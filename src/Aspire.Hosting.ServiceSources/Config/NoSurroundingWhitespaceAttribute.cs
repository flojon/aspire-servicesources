namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Marks a developer-config field whose value is handed to something outside this process exactly
/// as written, so whitespace at either end of it is part of the name being looked up.
/// </summary>
/// <remarks>
/// An opt-in rather than a rule for the whole file, because this file deliberately passes values
/// through as the developer wrote them: whitespace may be real in a <c>local.path</c> or in an
/// argument of a <c>prepare.command</c>, and trimming those would be rewriting what someone meant.
/// It is only for a value that names a thing on the other side of a CLI, where a surrounding space
/// cannot be part of the name in practice.
/// <para>
/// Refused rather than trimmed away, which is the substance of
/// <see href="https://github.com/flojon/aspire-servicesources/issues/236">#236</see>. Trimming is
/// right nearly every time and silent when it is wrong: a kubectl context name is an arbitrary key
/// in the developer's own kubeconfig, so <c>" padded "</c> can be a context that really exists, and
/// quietly trimming it would select a different one — a different cluster, a different user entry,
/// and whatever credential plugin that entry names — without saying so.
/// </para>
/// <para>
/// The receiver is required rather than assumed, because it is the fact that justifies the rule.
/// "Who gets this value as written?" is the question the next field to carry this has to answer,
/// and the message is built out of the answer instead of hardcoding one tool's name into a rule
/// whose name promises nothing about kubectl.
/// </para>
/// </remarks>
/// <param name="receiver">
/// What receives the value verbatim, named in the message as the developer would name it —
/// <c>kubectl</c>.
/// </param>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class NoSurroundingWhitespaceAttribute(string receiver) : Attribute
{
    /// <summary>What receives the value verbatim — <c>kubectl</c>.</summary>
    public string Receiver { get; } = receiver;

    /// <summary>
    /// A sentence appended to the message, for a field where the padding may have been deliberate.
    /// </summary>
    /// <remarks>
    /// Absent for nearly every field: a value that cannot legally carry surrounding whitespace has
    /// nothing to add beyond the spelling that works. It exists for the one field that can. A
    /// kubeconfig context name is an arbitrary key, so the developer reading the message may have
    /// meant what they wrote, and telling them to write the trimmed spelling would send them to a
    /// context that need not exist.
    /// </remarks>
    public string? IfDeliberate { get; init; }
}
