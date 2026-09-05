using System.ComponentModel;
using System.Globalization;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// What a backing service's <c>kubernetes.port</c> binds to: either one unnamed port, written as a
/// number, or a block of named ports, each of which the tunnel forwards and a connection string can
/// reach as <c>${port:&lt;name&gt;}</c>.
/// </summary>
/// <remarks>
/// A dictionary that may instead hold a single port, rather than the two separate fields the shape
/// reads like, because the developer writes <em>one</em> key and a key binds to one property.
/// Exactly one of <see cref="SinglePort"/> and the entries is ever populated.
/// <para>
/// <b>The single port is not stored as a one-entry map</b>, which would be tidier and is wrong:
/// <c>${port}</c> is accepted against a port written as a number and refused against a block of one
/// named port, so "written as a number" has to survive binding as a fact of its own rather than be
/// inferred from a count.
/// </para>
/// <para>
/// <b>Why a <see cref="Dictionary{TKey,TValue}"/> carrying a <see cref="TypeConverter"/> is the
/// shape, rather than a choice of shapes.</b> The developer config is bound in one
/// <c>Get&lt;Dictionary&lt;string, BackingServiceDeveloperConfig&gt;&gt;()</c> call, so this has to
/// survive the standard binder unaided, and the binder is value-first: a section carrying a value is
/// offered to the type's converter before its children are looked at, and a section with children
/// and no value goes to the dictionary walk. Measured against this repo's pinned binder, a plain
/// <c>Dictionary&lt;string, int&gt;</c> given a number binds to <see langword="null"/> <em>silently</em>,
/// and a non-dictionary class cannot bind children whose keys are names the developer invented. A
/// dictionary with a converter is the one shape that catches both spellings.
/// </para>
/// <para>
/// The comparer is <see cref="StringComparer.OrdinalIgnoreCase"/> because configuration keys are:
/// the casing that survives a merge across layers is whichever provider wrote last, so a template's
/// <c>${port:AMQP}</c> has to find a file's <c>amqp</c>. The binder preserves the comparer, since it
/// populates the instance this type's parameterless constructor built rather than replacing it.
/// </para>
/// <para>
/// Named <see cref="SinglePort"/> rather than <c>Single</c>: a property called <c>Single</c> on a
/// type that is an <see cref="IEnumerable{T}"/> shadows <see cref="Enumerable.Single{T}"/>, so
/// <c>ports.Single()</c> would stop compiling for every later reader.
/// </para>
/// </remarks>
[TypeConverter(typeof(KubernetesPortsConverter))]
internal sealed class KubernetesPorts : Dictionary<string, int>
{
    /// <summary>The constructor the binder uses for a block of named ports.</summary>
    public KubernetesPorts() : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    private KubernetesPorts(int singlePort) : this() => SinglePort = singlePort;

    /// <summary>
    /// The one port, when <c>port</c> was written as a number rather than as a block of named ports;
    /// <see langword="null"/> when it was written as a block.
    /// </summary>
    /// <remarks>
    /// Get-only, so configuration cannot put a value at it —
    /// <see cref="DeveloperConfigField.IsConfigurable"/> excludes a property that has no setter,
    /// which is what keeps this from being advertised as a key a developer may write.
    /// </remarks>
    public int? SinglePort { get; }

    /// <summary>One unnamed port, as the converter builds it.</summary>
    internal static KubernetesPorts Of(int singlePort) => new(singlePort);

    /// <summary>
    /// Whether <paramref name="value"/> is a port number as configuration spells one.
    /// </summary>
    /// <remarks>
    /// The one parse, shared by <see cref="KubernetesPortsConverter"/> and by the validator's walk
    /// over a port block, so that the two cannot disagree about what a number is. They have to
    /// agree exactly: a value the validator accepts and the converter rejects reaches the binder,
    /// which answers it by naming a CLR type at a colon-separated key.
    /// <para>
    /// <see cref="NumberStyles.Integer"/> against
    /// <see cref="CultureInfo.InvariantCulture"/> is not a preference — it is what the binder itself
    /// does to a named entry, through <c>Int32Converter.ConvertFromInvariantString</c>. A stricter
    /// parse here would refuse a value the binder goes on to accept for an entry, and the two halves
    /// of one block would then disagree about the same text. A negative number parses and is refused
    /// later, by the range check, which is where a port that is a number but not a port has always
    /// been answered.
    /// </para>
    /// </remarks>
    internal static bool TryParsePort(string value, out int port) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
}

/// <summary>
/// Reads a <c>port</c> written as a number. A <c>port</c> written as a block of named ports never
/// reaches this: the binder hands a section with children to the dictionary walk instead.
/// </summary>
internal sealed class KubernetesPortsConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    /// <remarks>
    /// An empty value answers <see langword="null"/>, which leaves the field unset — the one gesture
    /// a higher configuration layer has for dropping a value a lower one set, and one this field has
    /// to keep. An empty <em>block</em> is a different thing and is refused by the validator, since
    /// a port block nobody put a port in forwards nothing.
    /// <para>
    /// Anything else unparseable throws rather than answering <see langword="null"/>. The validator
    /// refuses it first, so this is unreachable through configuration — and it is left throwing so
    /// that a route into the binder nobody predicted fails loudly instead of quietly unsetting the
    /// field.
    /// </para>
    /// </remarks>
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string text)
        {
            return base.ConvertFrom(context, culture, value);
        }

        if (text.Length == 0)
        {
            return null;
        }

        return KubernetesPorts.TryParsePort(text, out var port)
            ? KubernetesPorts.Of(port)
            : throw new FormatException(
                $"'{text}' is neither a port number nor a block of named ports.");
    }
}
