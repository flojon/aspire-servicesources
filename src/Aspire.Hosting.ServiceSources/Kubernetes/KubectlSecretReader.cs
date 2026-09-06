using System.Diagnostics;
using System.Text;

namespace Aspire.Hosting.ServiceSources.Kubernetes;

/// <summary>
/// Reads a secret value by running <c>kubectl get secret</c> and decoding what it prints.
/// </summary>
/// <remarks>
/// One value per invocation rather than one fetch of the whole secret, because a connection string
/// naming two keys of one secret is rarer than the extra state a cache would have to be correct
/// about — and the values are credentials, which is a poor thing to hold longer than the call that
/// needs them.
/// <para>
/// <b>The key is addressed as <c>{.data.the\.key}</c>, with each <c>.</c> in the key escaped — not
/// as <c>{.data['the.key']}</c>.</b> The bracket form reads as the exact one and is not: kubectl's
/// jsonpath returns <em>empty</em> for it whenever the key contains a literal dot, while exiting 0,
/// which this reader can only report as "no such key". Measured against kubectl v1.24.3 rather than
/// reasoned about, because the two forms are easy to argue about and the failure is silent:
/// </para>
/// <code>
/// kubectl create secret generic p --from-literal=ca.crt=CERT --dry-run=client \
///     -o "jsonpath={.data['ca.crt']}"   # prints nothing, exit 0
///     -o 'jsonpath={.data.ca\.crt}'     # prints Q0VSVA==
/// </code>
/// <para>
/// The keys this matters for are the ones worth having: <c>.dockerconfigjson</c> is the key the API
/// itself gives a pull secret, and <c>ca.crt</c> and <c>tls.key</c> are TLS material. Every other
/// character a key may hold — letters, digits, <c>-</c> and <c>_</c> — needs no escape, verified the
/// same way.
/// </para>
/// <para>
/// <b>Nothing here escapes the key, because nothing here can.</b> A <c>'</c> would close the
/// quoting, and a jsonpath that fails to <em>execute</em> makes kubectl print the whole secret to
/// standard error. What keeps that shut is
/// <c>ConnectionStringTemplate</c>'s charset check at parse time, which refuses any key the
/// Kubernetes API could not carry — so the string arriving here has already been constrained to
/// <c>[-._a-zA-Z0-9]+</c>. That is a rule about what a developer may write, not merely about what a
/// cluster happens to hold; the difference is the whole of the protection, and this file relies on
/// it.
/// </para>
/// </remarks>
internal sealed class KubectlSecretReader : IKubernetesSecretReader
{
    /// <summary>
    /// How long one fetch may take before it counts as a failure.
    /// </summary>
    /// <remarks>
    /// Carried here rather than left to <c>kubectl</c>, which will wait far longer against an
    /// unreachable API server. This runs inside a parameter resolution: a fetch that hangs holds
    /// that resource with nothing to report, so the useful answer is a failure that names the
    /// timeout, and quickly enough that the dashboard shows it while the developer is still
    /// looking.
    /// </remarks>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    public string Read(string context, string @namespace, string secretName, string key)
    {
        var startInfo = new ProcessStartInfo("kubectl")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in Args(context, @namespace, secretName, key))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Start(startInfo, secretName, key);

        // Closed at once, the way every other process this package runs closes it: an exec
        // credential plugin that decides to prompt — `kubelogin`, `aws eks get-token` — must see the
        // end of stdin and fail rather than wait for a human who is not watching a parameter
        // resolve.
        process.StandardInput.Close();

        // Read both pipes before waiting. A secret's value is far smaller than a pipe buffer, but
        // kubectl's diagnostics on the error pipe need not be, and a process blocked writing to a
        // pipe nobody is draining would reach the timeout below rather than its own exit.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)FetchTimeout.TotalMilliseconds))
        {
            Terminate(process);

            throw new KubernetesSecretException(
                $"Reading key '{key}' from secret '{secretName}' took longer than "
                + $"{FetchTimeout.TotalSeconds:0} seconds and was cancelled. The cluster's API server may be "
                + $"unreachable from here, or context '{context}' may name one that no longer exists.");
        }

        if (process.ExitCode != 0)
        {
            var diagnostic = FirstLine(Drained(standardError));

            throw new KubernetesSecretException(
                $"Reading key '{key}' from secret '{secretName}' in namespace '{@namespace}' failed"
                + $" (kubectl exited {process.ExitCode})"
                + (diagnostic.Length == 0 ? "." : $": {diagnostic}"));
        }

        var encoded = Drained(standardOutput).Trim();

        // jsonpath prints nothing for a path that matches nothing, and exits 0 while doing it — so
        // an empty result is the shape "no such key" arrives in, not a secret holding an empty
        // value. The two are worth separating in the message, since one is a typo in the connection
        // string and the other is a cluster the developer would have to go and look at.
        if (encoded.Length == 0)
        {
            throw new KubernetesSecretException(
                $"Secret '{secretName}' in namespace '{@namespace}' has no key '{key}', or the key holds no "
                + $"value. `kubectl get secret {secretName} --context {context} --namespace {@namespace} "
                + "--output jsonpath='{.data}'` lists the keys it does have — with the context and the namespace "
                + "this read used, which are not necessarily the ones kubectl is pointed at right now.");
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException ex)
        {
            // Reachable only if the API returned something that is not the base64 it documents, so
            // this is a broken assumption rather than a developer's mistake. It still gets a
            // message rather than a raw FormatException, because what the dashboard shows against a
            // failed parameter is the message.
            throw new KubernetesSecretException(
                $"Key '{key}' of secret '{secretName}' did not decode as base64, which is how the Kubernetes API "
                + "stores every secret value.",
                ex);
        }
    }

    /// <summary>
    /// How long to keep waiting for kubectl's output once kubectl itself has exited.
    /// </summary>
    /// <remarks>
    /// Bounded for the reason <c>ProcessPrepareCommandRunner.StreamDrainTimeout</c> gives, which was
    /// measured rather than reasoned about: a redirected stream ends when the last handle to its
    /// write end closes, not when the process handed it exits, so an exec credential plugin that
    /// outlives kubectl holds this pipe open. <see cref="Process.WaitForExit(int)"/> bounds the
    /// process and says nothing about the pipe, and an unbounded read after it would be the very
    /// hang <see cref="FetchTimeout"/> exists to prevent, reintroduced one line later.
    /// </remarks>
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>What a stream produced, for as long as that is worth waiting for.</summary>
    private static string Drained(Task<string> stream) =>
        stream.Wait(StreamDrainTimeout) ? stream.Result : "";

    /// <summary>
    /// The first line of kubectl's diagnostic, which is the line that says what went wrong.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not the whole of it.</b> kubectl's jsonpath printer, when a template fails to
    /// execute, appends the entire object it was given — for <c>get secret</c> that is every key of
    /// the secret, base64-encoded, which is not redaction. This message reaches the dashboard and
    /// <c>~/.aspire/logs</c>, which is a file people paste into issues. The first line carries the
    /// error; the rest carries the secret.
    /// <para>
    /// The key charset checked at parse time already stops a template from failing that way, so this
    /// is the second of two locks on the same door.
    /// </para>
    /// </remarks>
    private static string FirstLine(string diagnostic)
    {
        string? first = null;

        foreach (var line in diagnostic.Split('\n'))
        {
            var text = line.Trim();

            if (text.Length == 0 || IsNoise(text))
            {
                continue;
            }

            // The first line that says something is the answer; the first line at all need not be.
            first ??= text;

            if (text.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
        }

        return first ?? "";
    }

    /// <summary>
    /// Whether a line of kubectl's standard error is a warning rather than the diagnostic.
    /// </summary>
    /// <remarks>
    /// klog writes <c>W0905 12:00:00.000000 1 loader.go:221] Config not found: …</c> before the
    /// error, and an API server can prepend <c>Warning:</c> headers of its own. Taking line one
    /// blindly replaces the diagnostic with noise on any cluster that emits either.
    /// </remarks>
    private static bool IsNoise(string line) =>
        line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)
        || (line.Length > 5 && line[0] is 'W' or 'I' && char.IsAsciiDigit(line[1]) && char.IsAsciiDigit(line[4]));

    /// <summary>
    /// The command line one fetch runs, kept separate so a test can assert it without a cluster.
    /// </summary>
    internal static string[] Args(string context, string @namespace, string secretName, string key) =>
        [
            "get",
            "secret",
            "--context",
            context,
            "--namespace",
            @namespace,
            "--output",
            // Each '.' in the key escaped, so it selects a field whose name contains a dot rather
            // than descending through one. Nothing else in a key needs escaping — the parser admits
            // only letters, digits, '-', '.' and '_'.
            $"jsonpath={{.data.{key.Replace(".", "\\.", StringComparison.Ordinal)}}}",
            // Last, immediately before the name. A bare '--' ends option parsing for everything
            // after it — kubectl uses pflag, where it is not a one-argument escape — so putting it
            // ahead of the flags would hand '--context' and its value to kubectl as secret names
            // and quietly drop the context, the namespace and the output format, leaving the fetch
            // to run against whatever cluster the developer's kubeconfig currently points at.
            // Here it does the one job it is for: the name that follows is a name whatever it
            // starts with. The parser already refuses a name beginning with '-'; this is the
            // second lock.
            "--",
            secretName,
        ];

    private static Process Start(ProcessStartInfo startInfo, string secretName, string key)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new KubernetesSecretException(
                    $"Reading key '{key}' from secret '{secretName}' could not start kubectl.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The same failure the port-forward hits, but this one surfaces against a parameter
            // rather than in a resource's log, so it has to say what it was trying to do.
            throw new KubernetesSecretException(
                $"Reading key '{key}' from secret '{secretName}' could not start kubectl. It has to be on PATH for "
                + "a '${secret:...}' placeholder to resolve.",
                ex);
        }
    }

    private static void Terminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It exited between the timeout expiring and the kill. Nothing to clean up, and the
            // timeout is still the right thing to report.
        }
    }
}
