using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

/// <summary>
/// Design finding 10's guard: this package has no <c>PublicAPI.*.txt</c> or ApiCompat, and the
/// catalog authoring surface (<see cref="ServiceCatalogBuilder"/>, <see cref="ServiceDefinitionBuilder"/>)
/// just grew by roughly half with nothing automated watching for an accidental breaking mistake — a
/// generic method that can't project to guest languages, two exports sharing a generated capability
/// id, or a builder method that returns the wrong type and breaks fluent chaining. Mirrors
/// <c>ServiceConfigurationExportsTests</c>'s reflection shape, adapted to these two types: both are
/// marked <c>[AspireExport(ExposeMethods = true)]</c> at the class level, so — unlike
/// <c>ServiceConfigurationExports</c>'s individually-<c>[AspireExport]</c>-attributed static
/// methods — every public instance method here is an export whether or not it individually carries
/// the attribute.
/// </summary>
public class CatalogExportsTests
{
    [Fact]
    public void EveryPublicMethodOnServiceCatalogBuilder_IsNonGenericAndReturnsABuilderType()
    {
        foreach (var method in PublicInstanceMethods(typeof(ServiceCatalogBuilder)))
        {
            Assert.False(method.IsGenericMethodDefinition, $"{method.Name} must not be generic.");
            Assert.True(
                method.ReturnType == typeof(ServiceDefinitionBuilder),
                $"{method.Name} should return a builder type, returned {method.ReturnType}.");
        }
    }

    [Fact]
    public void EveryPublicMethodOnServiceDefinitionBuilder_IsNonGenericAndReturnsItself()
    {
        foreach (var method in PublicInstanceMethods(typeof(ServiceDefinitionBuilder)))
        {
            Assert.False(method.IsGenericMethodDefinition, $"{method.Name} must not be generic.");
            Assert.True(
                method.ReturnType == typeof(ServiceDefinitionBuilder),
                $"{method.Name} should return {nameof(ServiceDefinitionBuilder)}, returned {method.ReturnType}.");
        }
    }

    [Fact]
    public void NoTwoExportedCatalogMethods_ShareAGeneratedCapabilityId()
    {
        // Belt-and-suspenders against the Stage 0 regression (docs/superpowers/specs/
        // 2026-09-07-code-catalog-stage0-ats-probe-findings.md): build already fails with
        // ASPIREEXPORT013 on a real collision, but this asserts the intent directly rather than
        // relying on the analyzer alone catching a future one. Every public instance method on
        // both types is exported (ExposeMethods = true), not just the ones individually carrying
        // [AspireExport] — a filter that only looked at method-level attributes would silently
        // stop checking the five ExposeMethods-derived With* methods, the larger half of the
        // surface this test exists to guard.
        var ids = new List<string>();
        foreach (var type in new[] { typeof(ServiceCatalogBuilder), typeof(ServiceDefinitionBuilder) })
        {
            foreach (var method in PublicInstanceMethods(type))
            {
                ids.Add(CapabilityId(type, method));
            }
        }

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The capability id a method resolves to, per <c>AspireExportAttribute</c>'s own XML doc
    /// (`Aspire.Hosting.xml`, 13.5.2): an explicit <c>id</c> wins outright; a bare
    /// <c>[AspireExport]</c> with no id derives the camelCase method name; a method exposed only
    /// via its declaring type's <c>ExposeMethods = true</c> (no attribute of its own — the case for
    /// every <see cref="ServiceDefinitionBuilder"/> method today) derives
    /// <c>{TypeName}.{camelCaseMethodName}</c> instead of the bare name. <c>MethodName</c> plays no
    /// part — Stage 0 measured that it renames the generated SDK method without changing the
    /// colliding capability id.
    /// </summary>
    private static string CapabilityId(Type type, MethodInfo method)
    {
        var export = method.GetCustomAttribute<AspireExportAttribute>();
        if (export is not null)
        {
            return export.Id ?? CamelCase(method.Name);
        }

        return $"{type.Name}.{CamelCase(method.Name)}";
    }

    private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static IEnumerable<MethodInfo> PublicInstanceMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName); // exclude property accessors
}
