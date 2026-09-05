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
/// <b>The key is addressed as <c>{.data['key']}</c>, not <c>{.data.key}</c>.</b> Kubernetes allows
/// <c>.</c> in a secret key — <c>.dockerconfigjson</c> is the common one — and the dotted form
/// silently descends into a field that is not there rather than failing, so it returns empty for a
/// key that exists. The bracket form is exact. A key cannot contain <c>'</c>: the API restricts
/// keys to <c>[-._a-zA-Z0-9]+</c>, which is also why this does not have to escape one.
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
            var diagnostic = standardError.GetAwaiter().GetResult().Trim();

            throw new KubernetesSecretException(
                $"Reading key '{key}' from secret '{secretName}' in namespace '{@namespace}' failed"
                + $" (kubectl exited {process.ExitCode})"
                + (diagnostic.Length == 0 ? "." : $": {diagnostic}"));
        }

        var encoded = standardOutput.GetAwaiter().GetResult().Trim();

        // jsonpath prints nothing for a path that matches nothing, and exits 0 while doing it — so
        // an empty result is the shape "no such key" arrives in, not a secret holding an empty
        // value. The two are worth separating in the message, since one is a typo in the connection
        // string and the other is a cluster the developer would have to go and look at.
        if (encoded.Length == 0)
        {
            throw new KubernetesSecretException(
                $"Secret '{secretName}' in namespace '{@namespace}' has no key '{key}', or the key holds no "
                + "value. `kubectl get secret " + secretName + " -o jsonpath='{.data}'` lists the keys it does have.");
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
    /// The command line one fetch runs, kept separate so a test can assert it without a cluster.
    /// </summary>
    internal static string[] Args(string context, string @namespace, string secretName, string key) =>
        [
            "get",
            "secret",
            secretName,
            "--context",
            context,
            "--namespace",
            @namespace,
            "--output",
            $"jsonpath={{.data['{key}']}}",
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
