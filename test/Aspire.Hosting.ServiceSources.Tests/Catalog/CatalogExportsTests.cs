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
    public void NoTwoExportedMethodsInTheAssembly_ShareAGeneratedCapabilityId()
    {
        // Widened from a hardcoded { ServiceCatalogBuilder, ServiceDefinitionBuilder } pair to the
        // whole assembly: Stage 0's actual measured collision was cross-type —
        // ServiceCatalogBuilder.AddService vs. ServiceSourcesBuilderExtensions.AddService — which a
        // two-type list cannot catch if the explicit [AspireExport("addServiceToCatalog")] id were
        // ever accidentally removed. This reflects over every exported method in the assembly the
        // same way the ATS generator itself would discover them, rather than over a hand-picked
        // subset. Belt-and-suspenders against the Stage 0 regression (docs/superpowers/specs/
        // 2026-09-07-code-catalog-stage0-ats-probe-findings.md): build already fails with
        // ASPIREEXPORT013 on a real collision, but this asserts the intent directly rather than
        // relying on the analyzer alone catching a future one.
        var ids = ExportedMethods().Select(m => CapabilityId(m.DeclaringType!, m)).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The set of ids the guard above found, as evidence that the widened scan discovers exactly
    /// this stage's known exported surface — no more, no less. A different set here means either
    /// the reflection logic is wrong or there's a real assembly surface the fix-round brief wasn't
    /// told about, and should be investigated rather than silently reconciled by editing this list.
    /// </summary>
    [Fact]
    public void ExportedIds_MatchTheKnownSurface()
    {
        var ids = ExportedMethods().Select(m => CapabilityId(m.DeclaringType!, m)).ToHashSet(StringComparer.Ordinal);

        string[] expected =
        [
            "addService", "addBackingService", "asJava", "asJavaScript", "getServiceEndpoint", "useJava", "useJavaScript",
            "addServiceCatalog", "addServiceToCatalog",
            CamelCase(nameof(ServiceConfigurationExports.WithServiceEnvironment)),
            CamelCase(nameof(ServiceConfigurationExports.WithServiceEnvironmentFromParameter)),
            CamelCase(nameof(ServiceConfigurationExports.WithServiceEnvironmentFromEndpoint)),
            CamelCase(nameof(ServiceConfigurationExports.WithServiceReference)),
            CamelCase(nameof(ServiceConfigurationExports.WithServiceConnectionString)),
            CamelCase(nameof(ServiceConfigurationExports.WaitForService)),
            CamelCase(nameof(ServiceConfigurationExports.WaitForServiceCompletion)),
            CamelCase(nameof(ServiceConfigurationExports.WithServiceArg)),
            CamelCase(nameof(ServiceConfigurationExports.WithServiceHttpsEndpoint)),
            CamelCase(nameof(ServiceConfigurationExports.WithServiceHttpEndpoint)),
            $"{nameof(ServiceDefinitionBuilder)}.{CamelCase(nameof(ServiceDefinitionBuilder.WithRepository))}",
            $"{nameof(ServiceDefinitionBuilder)}.{CamelCase(nameof(ServiceDefinitionBuilder.WithUrl))}",
            $"{nameof(ServiceDefinitionBuilder)}.{CamelCase(nameof(ServiceDefinitionBuilder.WithContainer))}",
            $"{nameof(ServiceDefinitionBuilder)}.{CamelCase(nameof(ServiceDefinitionBuilder.WithKubernetes))}",
            $"{nameof(ServiceDefinitionBuilder)}.{CamelCase(nameof(ServiceDefinitionBuilder.WithPrepare))}",
            $"{nameof(ServiceDefinitionBuilder)}.{CamelCase(nameof(ServiceDefinitionBuilder.WithKind))}",
            "JavaKindOptionsBuilder.workingDirectory",
            "JavaKindOptionsBuilder.mavenGoal",
            "JavaKindOptionsBuilder.gradleTask",
            "JavaKindOptionsBuilder.jarPath",
            "JavaKindOptionsBuilder.wrapperPath",
            "JavaKindOptionsBuilder.args",
            "JavaKindOptionsBuilder.port",
            "JavaKindOptionsBuilder.scheme",
            "JavaScriptKindOptionsBuilder.appDirectory",
            "JavaScriptKindOptionsBuilder.appType",
            "JavaScriptKindOptionsBuilder.packageManager",
            "JavaScriptKindOptionsBuilder.port",
            "JavaScriptKindOptionsBuilder.portEnv",
            "JavaScriptKindOptionsBuilder.runScript",
            "JavaScriptKindOptionsBuilder.scriptPath",
            "JavaScriptKindOptionsBuilder.targetPort",
        ];

        Assert.Equal(expected.OrderBy(id => id, StringComparer.Ordinal), ids.OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every method in the assembly ATS would export: one carrying <c>[AspireExport]</c> directly,
    /// or a public instance method whose declaring type carries
    /// <c>[AspireExport(ExposeMethods = true)]</c> and that doesn't individually opt out with
    /// <c>[AspireExportIgnore]</c> — the same discovery the ATS generator itself would do.
    /// </summary>
    private static IEnumerable<MethodInfo> ExportedMethods()
    {
        var assembly = typeof(ServiceCatalogBuilder).Assembly;

        foreach (var type in assembly.GetTypes().Where(t => t.IsPublic))
        {
            var typeExport = type.GetCustomAttribute<AspireExportAttribute>();
            var typeExposesMethods = typeExport is { ExposeMethods: true };

            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    // Property accessors, operators — not something ATS exports as a method.
                    continue;
                }

                var methodExport = method.GetCustomAttribute<AspireExportAttribute>();

                if (methodExport is not null)
                {
                    yield return method;
                    continue;
                }

                if (typeExposesMethods
                    && method.IsPublic
                    && !method.IsStatic
                    && method.GetCustomAttribute<AspireExportIgnoreAttribute>() is null)
                {
                    yield return method;
                }
            }
        }
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
