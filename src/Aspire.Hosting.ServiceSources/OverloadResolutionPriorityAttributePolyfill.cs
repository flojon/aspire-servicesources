#if !NET9_0_OR_GREATER

namespace System.Runtime.CompilerServices;

/// <summary>
/// The <c>net8.0</c> stand-in for .NET 9's own attribute, needed so the <c>WithCommand</c> shadow in
/// <see cref="Aspire.Hosting.ServiceSources.ServiceSourcesBuilderExtensions"/> can carry the same
/// overload priority Aspire's own overload does on every target framework this package builds for.
/// </summary>
/// <remarks>
/// Aspire.Hosting ships its own copy of this type for the same reason, but declares it
/// <see langword="internal"/>, so referencing the package is not enough — building the net8.0 leg
/// against Aspire's copy fails with CS0122. The compiler matches this attribute by full name, so an
/// <see langword="internal"/> declaration in this assembly is what the net8.0 leg binds to.
/// </remarks>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = false)]
internal sealed class OverloadResolutionPriorityAttribute(int priority) : Attribute
{
    public int Priority => priority;
}

#endif
