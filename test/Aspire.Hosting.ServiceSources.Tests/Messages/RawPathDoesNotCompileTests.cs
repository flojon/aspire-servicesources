using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

/// <summary>
/// The ticket's deliverable is that the unescaped path stops compiling, so the proof has to be a
/// compilation rather than an assertion about output.
/// </summary>
public class RawPathDoesNotCompileTests
{
    private const string Preamble = """
        using System;
        using Aspire.Hosting.ServiceSources;
        using Aspire.Hosting.ServiceSources.Messages;

        internal static class Probe
        {
            internal static Exception M(string s, object o, char c) =>
        """;

    private static IReadOnlyList<Diagnostic> Compile(string expression)
    {
        var source = $"{Preamble}\n        {expression};\n}}";

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => MetadataReference.CreateFromFile(path));

        // The assembly name is what AssemblyInfo.cs grants InternalsVisibleTo, and it is the whole
        // reason an internal seam can be probed from here at all.
        var compilation = CSharpCompilation.Create(
            assemblyName: "Aspire.Hosting.ServiceSources.Tests",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    private static void AssertCompiles(string expression) =>
        Assert.Empty(Compile(expression));

    private static void AssertRefused(string expression, string expectedId)
    {
        var errors = Compile(expression);

        Assert.NotEmpty(errors);
        Assert.Contains(expectedId, errors.Select(d => d.Id));
    }

    // The controls: without these, every test below would also pass on a typo.

    [Fact]
    public void NameHole_Compiles() =>
        AssertCompiles("""ServiceSourcesConfigurationException.For($"Service '{new Name(s)}' failed.")""");

    [Fact]
    public void RawHoleAndInt_Compile() =>
        AssertCompiles("""ServiceSourcesConfigurationException.For($"{Raw.Literal("pre")} port {5}")""");

    [Fact]
    public void ConstantMessage_Compiles() =>
        AssertCompiles("""ServiceSourcesConfigurationException.For($"no holes at all")""");

    [Fact]
    public void InterpolatedChain_Compiles() =>
        AssertCompiles("""ServiceSourcesConfigurationException.For($"a '{new Name(s)}' " + $"and more")""");

    // The deliverable.

    [Fact]
    public void RawStringHole_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"Service '{s}' failed.")""", "CS1503");

    [Fact]
    public void MixedChainWithAPlainLiteralTail_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a '{new Name(s)}' " + "plain tail")""", "CS1503");

    [Fact]
    public void PreBuiltStringVariable_DoesNotCompile() =>
        AssertRefused("ServiceSourcesConfigurationException.For(s)", "CS1503");

    [Fact]
    public void ObjectHole_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{o}b")""", "CS1503");

    [Fact]
    public void CharHole_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{c}b")""", "CS0619");

    [Fact]
    public void AlignmentForm_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{new Name(s),-10}b")""", "CS1739");

    [Fact]
    public void FormatSpecifierForm_DoesNotCompile() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{new Name(s):N0}b")""", "CS1739");

    // The trust surface. Raw is the only thing in this design that is believed rather than checked,
    // so every way of making one is pinned here — a new string-taking factory added later fails
    // NoStringTakingRawFactoryExists rather than passing quietly.

    [Fact]
    public void RawConstructor_IsNotAccessible() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{new Raw("x")}b")""", "CS1729");

    [Fact]
    public void RawJoin_RefusesStringFragments() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{Raw.Join(", ", new[] { s })}b")""", "CS1503");

    // The two [ConstantExpected] parameters are NOT enforced by the compiler — measured, against the
    // prediction that they raise CS9244. Their only enforcement is analyzer CA1857, which this
    // in-memory compilation does not run, and which at its default warning severity would leave
    // Raw.Literal(runtimeString) as a one-token bypass in any build without -warnaserror. So the
    // package escalates it, and that escalation is the thing worth pinning.
    [Fact]
    public void ConstantExpectedIsEscalatedToAnError()
    {
        var csproj = Path.Combine(RepositoryRoot(), "src", "Aspire.Hosting.ServiceSources",
            "Aspire.Hosting.ServiceSources.csproj");

        Assert.Contains("<WarningsAsErrors>$(WarningsAsErrors);CA1857</WarningsAsErrors>",
            File.ReadAllText(csproj), StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
            && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void RawOrigin_RefusesAString() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{Raw.Origin(s)}b")""", "CS1503");

    [Fact]
    public void NoStringTakingRawFactoryExists()
    {
        var factories = typeof(Raw)
            .GetMethods(System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.ReturnType == typeof(Raw));

        Assert.All(factories, factory =>
        {
            var stringParameters = factory.GetParameters().Where(p => p.ParameterType == typeof(string));

            // A string parameter is only allowed where the compiler forces it to be a constant.
            Assert.All(stringParameters, parameter =>
                Assert.Contains(parameter.GetCustomAttributes(inherit: false),
                    a => a.GetType().Name == "ConstantExpectedAttribute"));
        });
    }
}
