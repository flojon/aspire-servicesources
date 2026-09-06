using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// How a developer-config field's shape is classified — value, list, block, or a value-or-map.
/// </summary>
/// <remarks>
/// The order these questions are asked in is load-bearing rather than incidental, and it is not
/// visible from any one of them: a list is a class, and a map is an <c>IEnumerable</c>, so each
/// shape is claimed by the question asked before it unless the questions are asked in the right
/// order. These pin the classifications; <c>PortBlockValidationTests</c> pins what is said about
/// each.
/// </remarks>
public class DeveloperConfigFieldTests
{
    [Fact]
    public void APortBlock_IsAValueOrMap() =>
        Assert.True(DeveloperConfigField.IsValueOrMap(typeof(KubernetesPorts)));

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(string[]))]
    [InlineData(typeof(KubernetesBackingServiceDeveloperConfig))]
    public void EveryOtherShape_IsNot(Type type) =>
        Assert.False(DeveloperConfigField.IsValueOrMap(type));

    /// <summary>
    /// A plain dictionary is <em>not</em> one, though it binds children the same way.
    /// </summary>
    /// <remarks>
    /// The converter is half the definition, not a decoration on it: without one the binder answers
    /// a scalar by leaving the field null and saying nothing, so such a type cannot carry the value
    /// spelling at all and must not be advertised as though it could.
    /// </remarks>
    [Fact]
    public void ADictionaryWithNoConverter_IsNot() =>
        Assert.False(DeveloperConfigField.IsValueOrMap(typeof(Dictionary<string, int>)));

    [Fact]
    public void AValueOrMap_NamesTheTypeItsEntriesBindTo() =>
        Assert.Equal(typeof(int), DeveloperConfigField.MapValueTypeOf(typeof(KubernetesPorts)));

    /// <summary>
    /// It is not a block, so nothing walks it for the fields it does not have.
    /// </summary>
    [Fact]
    public void AValueOrMap_IsNotABlockOfSettings() =>
        Assert.Null(DeveloperConfigField.BlockFieldsOf(typeof(KubernetesPorts)));

    /// <summary>
    /// The entry shape still sees <c>port</c> as a key of the <c>kubernetes</c> block, and still
    /// does not count that block's port field as a block of its own.
    /// </summary>
    /// <remarks>
    /// The pairing that matters: a classification that made <c>KubernetesPorts</c> a block would
    /// silently add its CLR members to the list of keys a developer is told they may write.
    /// </remarks>
    [Fact]
    public void TheBackingServiceShape_StillOffersPortAsAKeyOfTheKubernetesBlock()
    {
        Assert.Contains(
            "Port",
            DeveloperConfigShape.BackingService.BlockFields["Kubernetes"].Keys,
            StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            DeveloperConfigShape.BackingService.Blocks,
            block => block.PropertyType == typeof(KubernetesPorts));
    }
}
