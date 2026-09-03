using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The javascript and java kinds live in core but compile against hosting packages core references
/// with <c>PrivateAssets="all"</c>, so those assemblies reach no consumer and are not on disk here
/// either — this test project's output directory has neither. That is what makes this project the
/// place to assert the discipline the arrangement depends on: the third-party types may be reached
/// only from inside method bodies, never from a field, a base type, an interface, or a property.
/// Hoist one into a type's shape and the kind type stops loading for every AppHost, including the
/// ones that declare no service of that kind at all.
/// </summary>
public class GuestLanguageKindIsolationTests
{
    /// <summary>
    /// The two assemblies whose absence the rest of this class is asserting against. If a future
    /// change starts shipping them alongside core, every assertion below passes for the wrong
    /// reason, so this is checked rather than assumed.
    /// </summary>
    [Theory]
    [InlineData("Aspire.Hosting.JavaScript.dll")]
    [InlineData("CommunityToolkit.Aspire.Hosting.Java.dll")]
    public void TheGuestLanguageHostingAssembly_IsNotBesideThisTestAssembly(string fileName)
    {
        Assert.False(
            File.Exists(Path.Combine(AppContext.BaseDirectory, fileName)),
            $"'{fileName}' is in this test project's output, so the isolation tests here no longer "
            + "prove anything. Core references it with PrivateAssets=\"all\" precisely so it does "
            + "not flow to consumers; something has started flowing it.");
    }

    [Theory]
    [InlineData("Aspire.Hosting.ServiceSources.JavaScriptLocalKind")]
    [InlineData("Aspire.Hosting.ServiceSources.Java.JavaLocalResourceKind")]
    public void TheKindType_LoadsWithoutItsHostingAssembly(string typeName)
    {
        var type = typeof(LocalKindConfig).Assembly.GetType(typeName, throwOnError: true)!;

        // Creating an instance settles the base type, the interfaces and every field type: those
        // are resolved when the type is loaded, not when a method runs.
        var handler = Assert.IsAssignableFrom<ILocalResourceKind>(
            Activator.CreateInstance(type, nonPublic: true));

        // Method *signatures* resolve lazily per member, so enumerating them is a stricter check
        // than construction — and it is what a metadata-walking tool does. Aspire's TypeScript
        // generator reflects over this assembly in exactly this state to project [AspireExport].
        var members = type.GetMembers(
            BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.NotEmpty(members);
        Assert.NotNull(handler);
    }

    /// <summary>
    /// The whole config-validation surface has to work with the hosting assembly absent, so that a
    /// typo'd options block is reported as a typo rather than as a missing package. Only resource
    /// construction may need the assembly.
    /// </summary>
    [Theory]
    [InlineData("Aspire.Hosting.ServiceSources.JavaScriptLocalKind", "runScrip")]
    [InlineData("Aspire.Hosting.ServiceSources.Java.JavaLocalResourceKind", "mavenGaol")]
    public void ATypoedOptionsBlock_IsReportedWithoutTheHostingAssembly(string typeName, string typo)
    {
        var type = typeof(LocalKindConfig).Assembly.GetType(typeName, throwOnError: true)!;
        var handler = (ILocalResourceKind)Activator.CreateInstance(type, nonPublic: true)!;

        var rawConfig = new Dictionary<object, object> { [typo] = "dev" };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => handler.Validate("svc", rawConfig));

        Assert.Contains(typo, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="ILocalResourceKind.SupportsDeferredCheckout"/> is asked for services the AppHost
    /// may never add, so it must answer rather than fault — including with the hosting assembly
    /// absent, which is when every AppHost that declares no service of this kind asks it.
    /// </summary>
    [Theory]
    [InlineData("Aspire.Hosting.ServiceSources.JavaScriptLocalKind")]
    [InlineData("Aspire.Hosting.ServiceSources.Java.JavaLocalResourceKind")]
    public void SupportsDeferredCheckout_AnswersWithoutTheHostingAssembly(string typeName)
    {
        var type = typeof(LocalKindConfig).Assembly.GetType(typeName, throwOnError: true)!;
        var handler = (ILocalResourceKind)Activator.CreateInstance(type, nonPublic: true)!;

        // The answer itself is each kind's business; not throwing is this test's.
        _ = handler.SupportsDeferredCheckout(null);
    }
}
