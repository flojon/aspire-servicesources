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
        using System.Linq;
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

    // A method group has no call site for CA1857 to inspect, so both shapes below carried a runtime
    // string past [ConstantExpected] until Literal and Join took a trailing optional parameter. The
    // second is the one that mattered: it is Select(Raw.Escaped), already written in this package,
    // with one identifier changed. Unlike CA1857, this compilation can see them.

    [Fact]
    public void RawLiteral_CannotBeMethodGroupConverted() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{((Func<string, Raw>)Raw.Literal)(s)}b")""", "CS0123");

    [Fact]
    public void RawLiteral_CannotBeLinqProjected() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{Raw.Join(", ", new[] { s }.Select(Raw.Literal))}b")""", "CS0411");

    // Known open, pinned so that a future tightening has a red/green signal instead of a guess. The
    // trailing parameter closes single-argument delegate conversions only, and Enumerable.Zip takes a
    // two-argument one whose shape Literal now matches exactly. A ref struct would not close it
    // either: Func declares `allows ref struct` on net9 and later, so it would bite on net8.0 alone.
    // This shape costs an author a token that has no reason to exist, which is why it is tolerated;
    // if it ever starts being refused, this assertion failing is the intended signal.
    [Fact]
    public void RawLiteral_IsStillReachableThroughATwoArgumentZip() =>
        AssertCompiles("""ServiceSourcesConfigurationException.For($"a{new[] { s }.Zip(new[] { default(Raw.ConstantOnly) }, Raw.Literal).First()}b")""");

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

    /// <summary>
    /// The one way past the seam that is not a hole. A hand-built handler —
    /// <c>new ServiceTextHandler(0, 0)</c>, then <c>AppendLiteral(runtimeString)</c>, then
    /// <c>For(handler)</c> — reaches the message with an unescaped string, and neither the banned
    /// constructor nor any refusal above sees it.
    /// </summary>
    /// <remarks>
    /// Pinned by reflection for the same reason <see cref="ConstantExpectedIsEscalatedToAnError"/>
    /// is pinned against the csproj: the in-memory compilation here runs no analyzers, so the CA1857
    /// error that actually refuses this path cannot be demonstrated by compiling a snippet. The two
    /// together are the guard — the attribute is present, and the rule that enforces it is an error.
    /// Banning the two members outright was measured and rejected: RS0030 also fires on the
    /// compiler's own lowering of every <c>$"…"</c>, taking the deduplicated worklist from 154 to
    /// 1842 and burying the follow-up's scope.
    /// </remarks>
    [Fact]
    public void HandBuiltHandler_CannotAppendARuntimeString()
    {
        var appendLiteral = typeof(ServiceTextHandler).GetMethod(nameof(ServiceTextHandler.AppendLiteral));

        Assert.NotNull(appendLiteral);

        var value = Assert.Single(appendLiteral.GetParameters());

        Assert.Contains(
            value.GetCustomAttributes(inherit: false),
            attribute => attribute.GetType().Name == "ConstantExpectedAttribute");
    }

    [Fact]
    public void RawOrigin_RefusesAString() =>
        AssertRefused("""ServiceSourcesConfigurationException.For($"a{Raw.Origin(s)}b")""", "CS1503");

    /// <summary>
    /// Every way of making a <see cref="Raw"/> from a runtime string must escape it. Asserted
    /// behaviourally rather than against a list of blessed factory names: a name allowlist is the
    /// same fail-open recogniser this seam exists to replace, and would let a future
    /// <c>Raw.Trusted(string)</c> through as soon as someone added it to the list.
    /// </summary>
    [Fact]
    public void EveryStringTakingRawFactoryEscapesItsArgument()
    {
        const string Forgery = "orders'\"\nFATAL: forged";

        var factories = typeof(Raw)
            .GetMethods(System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.ReturnType == typeof(Raw))
            .Where(method => method.GetParameters().Any(p => p.ParameterType == typeof(string)));

        Assert.NotEmpty(factories);

        Assert.All(factories, factory =>
        {
            var constrained = factory.GetParameters()
                .Where(p => p.ParameterType == typeof(string))
                .All(p => p.GetCustomAttributes(inherit: false)
                    .Any(a => a.GetType().Name == "ConstantExpectedAttribute"));

            // A constant cannot carry a runtime value, so there is nothing to escape.
            if (constrained)
            {
                return;
            }

            var arguments = factory.GetParameters()
                .Select(p => p.ParameterType == typeof(string) ? (object?)Forgery : null)
                .ToArray();

            var rendered = factory.Invoke(null, arguments)!.ToString()!;

            Assert.DoesNotContain("\n", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("'", rendered.Replace("\\u0027", "", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("\"", rendered.Replace("\\\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
        });
    }
}
