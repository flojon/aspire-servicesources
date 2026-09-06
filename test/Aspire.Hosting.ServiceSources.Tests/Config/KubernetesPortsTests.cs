using Aspire.Hosting.ServiceSources.Config;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// How a backing service's <c>kubernetes.port</c> survives <see cref="ConfigurationBinder"/> when it
/// is written either as a number or as a block of named ports.
/// </summary>
/// <remarks>
/// Every case here is a behaviour of the binder rather than of this package, which is exactly why
/// they are pinned: the field is the first in the developer config whose value may be a scalar
/// <em>or</em> a map, the binder is reached through <c>Aspire.Hosting</c> rather than referenced
/// directly, and two of these behaviours are silent — a bad entry is dropped rather than reported,
/// and a bad scalar throws from the binder rather than from anything that names a backing service.
/// The validator's whole per-entry walk exists because of the first; the requirement that
/// validation run before binding exists because of the second.
/// <para>
/// Bound through the real binder against the real config type, not against a stand-in, since what
/// is being asserted is that <em>this</em> property on <em>this</em> type binds — a stand-in would
/// keep passing after the property's type changed.
/// </para>
/// </remarks>
public class KubernetesPortsTests
{
    private static KubernetesBackingServiceDeveloperConfig Bind(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build()
            .GetSection("kubernetes")
            .Get<KubernetesBackingServiceDeveloperConfig>() ?? new();

    [Fact]
    public void A_port_written_as_a_number_is_the_single_port()
    {
        var config = Bind(("kubernetes:port", "5432"));

        Assert.Equal(5432, config.Port!.SinglePort);
        Assert.Empty(config.Port);
    }

    [Fact]
    public void A_port_written_as_a_block_carries_every_named_port()
    {
        var config = Bind(("kubernetes:port:amqp", "5672"), ("kubernetes:port:management", "15672"));

        Assert.Null(config.Port!.SinglePort);
        Assert.Equal(5672, config.Port["amqp"]);
        Assert.Equal(15672, config.Port["management"]);
    }

    /// <remarks>
    /// Configuration keys are case-insensitive, so the casing that survives a merge is whichever
    /// layer wrote last — which means a template's <c>${port:AMQP}</c> and a file's <c>amqp</c> have
    /// to find each other. The comparer is set in the type's parameterless constructor, and this
    /// asserts the binder keeps it rather than replacing the instance.
    /// </remarks>
    [Fact]
    public void A_named_port_is_found_whatever_the_casing()
    {
        var config = Bind(("kubernetes:port:amqp", "5672"));

        Assert.True(config.Port!.TryGetValue("AMQP", out var port));
        Assert.Equal(5672, port);
    }

    /// <remarks>
    /// The one gesture a higher configuration layer has for dropping a value a lower one set. It has
    /// to keep working for this field, which is why the converter answers an empty value with null
    /// rather than with an empty map — an empty map would be a <em>configured</em> port block with
    /// no ports in it, which is a different thing and is refused.
    /// </remarks>
    [Fact]
    public void An_empty_value_leaves_the_field_unset()
    {
        var config = Bind(("kubernetes:port", ""));

        Assert.Null(config.Port);
    }

    /// <remarks>
    /// The behaviour the validator's per-entry walk exists for. The binder cannot convert the value,
    /// so it omits the entry entirely and the map binds one port short — the tunnel would forward
    /// fewer ports than the block names, with nothing to say so. Nothing downstream can report what
    /// it never receives, so it has to be caught before binding.
    /// </remarks>
    [Fact]
    public void A_named_port_that_is_not_a_number_is_dropped_by_the_binder()
    {
        var config = Bind(("kubernetes:port:amqp", "abc"));

        Assert.Empty(config.Port!);
    }

    /// <remarks>
    /// The mirror of the case above, and the reason validation runs ahead of binding: the binder's
    /// own complaint names a CLR type at a colon-separated key, from an exception no handler
    /// upstream treats as a configuration problem.
    /// </remarks>
    [Fact]
    public void A_port_that_is_not_a_number_throws_from_the_binder()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => Bind(("kubernetes:port", "abc")));

        Assert.Contains("abc", thrown.Message, StringComparison.Ordinal);
    }
}
