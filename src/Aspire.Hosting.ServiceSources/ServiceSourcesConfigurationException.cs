using System.Text;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources;

public sealed class ServiceSourcesConfigurationException : Exception
{
    /// <summary>
    /// Set to <c>1</c> or <c>true</c> to get the runtime's full dump — type names, every stack
    /// frame, the nested inner-exception blocks — instead of the summary. For diagnosing this
    /// package itself; the summary is what a misconfiguration needs.
    /// </summary>
    internal const string FullDetailEnvironmentVariable = "SERVICESOURCES_FULL_ERRORS";

    public ServiceSourcesConfigurationException(string message) : base(message)
    {
    }

    public ServiceSourcesConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    // One of two legitimate callers of the banned constructor (the other is
    // LocalKindConfig.RewriteIndentationAdviceForCodeOrigin, which only rewrites an already-escaped
    // message); the suppression here is scoped to these two lines so every other use still reports.
#pragma warning disable RS0030
    /// <summary>
    /// The only way this package builds a message: a raw <c>string</c> hole does not compile, so a
    /// caller-controlled name cannot reach a reader unescaped.
    /// </summary>
    internal static ServiceSourcesConfigurationException For(ServiceTextHandler message) =>
        new(message.Text);

    internal static ServiceSourcesConfigurationException For(ServiceTextHandler message, Exception innerException) =>
        new(message.Text, innerException);
#pragma warning restore RS0030

    /// <summary>
    /// Every message on this type is written to be read by the developer who has to act on it, and
    /// the one place it is most likely to be read is where an <c>AddService</c> call takes the
    /// AppHost down: the runtime prints <see cref="object.ToString"/> for an unhandled exception,
    /// so whatever this returns is the entire error the developer sees. The default renders the
    /// type name, the whole inner-exception chain and a stack trace per level — twenty-odd lines of
    /// libgit2 and MSBuild frames around a sentence naming the fix. None of those frames are
    /// actionable for a misconfiguration, so they are dropped and the causes kept as one line each,
    /// which is where the underlying library's own wording lives.
    /// </summary>
    public override string ToString() =>
        Describe(FullDetailRequested(Environment.GetEnvironmentVariable(FullDetailEnvironmentVariable)));

    /// <param name="fullDetail">
    /// Whether to return the runtime's own rendering, stack traces and all.
    /// </param>
    internal string Describe(bool fullDetail)
    {
        if (fullDetail)
        {
            return base.ToString();
        }

        var description = new StringBuilder(Message);

        // Flattened rather than indented per level: the chain is a single line of causation
        // (configuration error <- auth failure <- libgit2's own words), and the depth of the wrap
        // is an implementation detail of this package, not something to make the reader parse.
        string? previous = null;
        var wroteACause = false;
        for (var cause = InnerException; cause is not null; cause = cause.InnerException)
        {
            // A wrapper that only reclassifies its inner exception carries that exception's message
            // verbatim — GitAuthenticationFailedException does exactly this, to keep libgit2's own
            // wording — so printing every level would say the same sentence twice in a row. Only
            // adjacent repeats are collapsed: the same message recurring further down the chain is
            // a genuine second occurrence (a retry that failed the same way) and still worth seeing.
            if (cause.Message != previous)
            {
                description.Append(Environment.NewLine).Append("  caused by: ").Append(Raw.Cause(cause));
                previous = cause.Message;
                wroteACause = true;
            }
        }

        // A message with nothing wrapped underneath it is a configuration error this package
        // diagnosed itself, and there is no hidden detail to point at. Once something *is* wrapped,
        // the frames that were just dropped may be the whole diagnosis — several call sites wrap a
        // plain `catch (Exception)` around genuinely unexpected failures, where the message is
        // "failed to clone" and the trace is what says why. Without this line the summary is a dead
        // end: nothing in it names the switch that widens it, so a developer filing a bug report
        // has no way to know a fuller one exists.
        if (wroteACause)
        {
            description
                .Append(Environment.NewLine)
                .Append("  (set ")
                .Append(FullDetailEnvironmentVariable)
                .Append("=1 for the full exception detail, including stack traces)");
        }

        return description.ToString();
    }

    internal static bool FullDetailRequested(string? value) =>
        value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
