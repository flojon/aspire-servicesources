using System.Reflection;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Drives Aspire's <c>withEndpointCallback</c> / <c>withHttpsEndpointCallback</c> capabilities the
/// way a guest-language AppHost's generated SDK does — by invoking the <c>internal</c> generic on
/// <c>Aspire.Hosting.ResourceBuilderExtensions</c> closed over <see cref="ServiceResource"/>.
/// </summary>
/// <remarks>
/// Reflective because neither half can be named from C#: the methods are <c>internal</c> to
/// <c>Aspire.Hosting.dll</c>, and so is their callback's parameter type,
/// <c>EndpointUpdateContext</c>. That is also why this package cannot shadow them, which is the
/// whole reason the detector exists.
/// <para>
/// The callback is written as an <see cref="Action{T}"/> over <see cref="object"/> and re-bound to
/// the real context type with <see cref="Delegate.CreateDelegate(Type, object, MethodInfo)"/>, whose
/// relaxed parameter matching accepts the wider parameter. Properties are then set by reflection,
/// which needs no compile-time name for the type at all.
/// </para>
/// <para>
/// Distinct from <c>OverloadResolutionProbe.cs</c>, which solves a C# <em>overload resolution</em>
/// problem with an ordinary call site in a neutral namespace. Nothing about namespaces helps here.
/// </para>
/// </remarks>
internal static class GuestLanguageEndpointCallbacks
{
    private static readonly Type Extensions = typeof(DistributedApplication).Assembly
        .GetType("Aspire.Hosting.ResourceBuilderExtensions", throwOnError: true)!;

    /// <summary>
    /// <c>Aspire.Hosting/withEndpointCallback</c>: updates <paramref name="endpointName"/> if it
    /// exists, and otherwise creates it and adds it straight to the facade's collection.
    /// </summary>
    public static void EndpointCallback(
        IResourceBuilder<ServiceResource> service, string endpointName,
        params (string Property, object? Value)[] writes)
    {
        var method = Closed("WithEndpointCallback");
        method.Invoke(null, [service, endpointName, Callback(method, writes), true]);
    }

    /// <summary>
    /// <c>Aspire.Hosting/withHttpsEndpointCallback</c>: for a service whose source already
    /// registered an endpoint of this name, always the ungated in-place update branch.
    /// </summary>
    public static void HttpsEndpointCallback(
        IResourceBuilder<ServiceResource> service, string? name,
        params (string Property, object? Value)[] writes)
    {
        var method = Closed("WithHttpsEndpointCallback");
        method.Invoke(null, [service, Callback(method, writes), name, true]);
    }

    private static MethodInfo Closed(string name) =>
        Extensions
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name && candidate.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(ServiceResource));

    private static Delegate Callback(MethodInfo closed, (string Property, object? Value)[] writes)
    {
        // Read off the signature rather than by name: the context type is internal, so a literal
        // namespace here would be an unverifiable guess.
        var contextType = closed.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Single(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Action<>))
            .GetGenericArguments()[0];

        Action<object> write = context =>
        {
            foreach (var (property, value) in writes)
            {
                var setter = contextType.GetProperty(property)
                    ?? throw new InvalidOperationException(
                        $"'{contextType.Name}' has no property '{property}'.");
                setter.SetValue(context, value);
            }
        };

        return Delegate.CreateDelegate(
            typeof(Action<>).MakeGenericType(contextType), write.Target, write.Method);
    }
}
