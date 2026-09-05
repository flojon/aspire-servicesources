namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// How a value read out of configuration is rendered back into a message.
/// </summary>
/// <remarks>
/// Shared rather than private to <see cref="DeveloperConfigValidator"/>, which is where it started
/// and where it was the only thing echoing developer-written text. A backing service's
/// <c>kubernetes.port</c> block changed that: its keys are names the developer invents, and they are
/// quoted by the validator, by <c>KubernetesBackingServiceSource</c>'s refusals and by a health
/// check's description — three files, one rule.
/// </remarks>
internal static class ConfiguredValue
{
    /// <summary>
    /// A value as a quoted literal with its whitespace spelled out, so that a character which
    /// looks like a space — a tab, a newline, U+00A0 — is distinguishable from one.
    /// </summary>
    /// <remarks>
    /// The plain space is left as itself: it is the character a reader assumes, so escaping it
    /// would add noise to the common case and nothing else. Everything else whitespace gets its
    /// code point, which is what a developer needs in order to find it in the file.
    ///
    /// Every message that echoes a value or a developer-invented key goes through this, rather than
    /// only the ones about whitespace. A message is read by someone who cannot see what they typed,
    /// and which messages a whitespace value can reach is not a thing to work out per message: it
    /// was reaching one of them unescaped for exactly as long as it took to notice.
    /// <para>
    /// It also keeps a newline in a name from forging a line of its own. These messages are relayed
    /// into <c>~/.aspire/logs</c> and routinely pasted into issues, and a port named
    /// <c>"amqp\n\nBacking service 'x': all is well."</c> would otherwise read as two sentences from
    /// this package rather than as one name.
    /// </para>
    /// </remarks>
    public static string Escaped(string? value) =>
        value is null
            ? "''"
            : $"'{string.Concat(value.Select(c => c switch
            {
                ' ' => " ",
                '\t' => "\\t",
                '\n' => "\\n",
                '\r' => "\\r",
                _ when char.IsWhiteSpace(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            }))}'";
}
