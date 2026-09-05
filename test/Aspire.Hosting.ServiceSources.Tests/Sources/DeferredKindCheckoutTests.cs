using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// Deferral for the non-dotnet <c>"local"</c> kinds (#159): the part of the protocol core owns,
/// exercised through a stand-in <see cref="ILocalResourceKind"/> rather than through the java or
/// javascript satellites, so what is asserted here is what <em>any</em> kind can rely on.
/// </summary>
public class DeferredKindCheckoutTests
{
    private const string KindName = "stand-in";

    private sealed class FakeGitClient : IGitClient
    {
        private readonly Dictionary<string, ManualResetEventSlim> _blockUntil = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Exception> _failFor = new(StringComparer.Ordinal);

        public List<string> Cloned { get; } = [];

        /// <summary>Holds this repository's clone open until the returned gate is set.</summary>
        public ManualResetEventSlim BlockFor(string repositoryUrl) =>
            _blockUntil[repositoryUrl] = new ManualResetEventSlim(false);

        public void FailFor(string repositoryUrl, Exception exception) => _failFor[repositoryUrl] = exception;

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            lock (Cloned)
            {
                Cloned.Add(repositoryUrl);
            }

            if (_blockUntil.TryGetValue(repositoryUrl, out var gate))
            {
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            if (_failFor.TryGetValue(repositoryUrl, out var exception))
            {
                throw exception;
            }

            // What the stand-in kind's post-checkout validation looks for.
            Directory.CreateDirectory(Path.Combine(destinationPath, "app"));
        }

        public void Checkout(string repositoryPath, string reference)
        {
        }

        public void Fetch(string repositoryPath)
        {
        }

        public bool HasUncommittedChanges(string repositoryPath) => false;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => true;

        public string? GetOriginUrl(string repositoryPath) => null;
    }

    /// <summary>
    /// A kind that behaves the way java and javascript do: it can build its whole resource from the
    /// catalog, and the only thing it needs the working tree for is a check it hands back.
    /// <paramref name="withHelper"/> makes it add a second resource of its own alongside the
    /// service, which is the shape <c>Aspire.Hosting.JavaScript</c>'s <c>npm install</c> installer
    /// puts core in.
    /// </summary>
    private sealed class StandInKind(
        bool supportsDeferral = true,
        bool withHelper = false,
        bool deferralReturnsNull = false,
        bool helperWaitsForService = false,
        bool declineAfterAdding = false,
        bool withParameter = false) : ILocalResourceKind
    {
        public string? DeferredRepoRoot { get; private set; }

        public int SupportsDeferredCheckoutCalls { get; private set; }

        public int ResolveDeferredCalls { get; private set; }

        public bool SupportsDeferredCheckout(object? rawConfig)
        {
            SupportsDeferredCheckoutCalls++;
            return supportsDeferral;
        }

        public int ValidateCheckoutCalls { get; private set; }

        /// <summary>The repo root as it looked when the post-checkout validation actually ran.</summary>
        public bool CheckoutExistedWhenValidated { get; private set; }

        public bool ResolvedEagerly { get; private set; }

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
        {
            ResolvedEagerly = true;
            return Add(builder, serviceName, repoRoot);
        }

        public DeferredLocalResource? ResolveDeferred(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
        {
            ResolveDeferredCalls++;

            if (!supportsDeferral || deferralReturnsNull)
            {
                // A handler getting the contract wrong: registering first, deciding afterwards.
                if (declineAfterAdding)
                {
                    Add(builder, serviceName, repoRoot);
                }

                return null;
            }

            DeferredRepoRoot = repoRoot;

            return new DeferredLocalResource
            {
                Service = Add(builder, serviceName, repoRoot),
                ValidateCheckout = () =>
                {
                    ValidateCheckoutCalls++;
                    CheckoutExistedWhenValidated = Directory.Exists(Path.Combine(repoRoot, "app"));

                    if (!CheckoutExistedWhenValidated)
                    {
                        throw new ServiceSourcesConfigurationException($"'{repoRoot}/app' is not in the checkout.");
                    }
                },
            };
        }

        private IResourceBuilder<IResourceWithServiceDiscovery> Add(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot)
        {
            var helper = withHelper
                ? builder.AddExecutable($"{serviceName}-helper", "install", repoRoot)
                : null;

            // A resource with no lifetime, which a handler is equally entitled to add beside its
            // service and which DCP never creates.
            if (withParameter)
            {
                builder.AddParameter($"{serviceName}-token", "secret");
            }

            var service = builder
                .AddResource(new StandInResource(serviceName, Path.Combine(repoRoot, "app")))
                .WithHttpEndpoint(targetPort: 8080);

            // The inverted shape: a helper waiting on the service it sits next to, which core
            // cannot start in any order that works.
            if (helper is not null && helperWaitsForService)
            {
                helper.WaitFor(service);
            }

            return service;
        }
    }

    /// <summary>
    /// What a satellite kind hands back: an executable that is also service-discoverable, which
    /// plain <see cref="ExecutableResource"/> is not — <c>JavaAppExecutableResource</c> and
    /// <c>JavaScriptAppResource</c> are the real ones.
    /// </summary>
    private sealed class StandInResource(string name, string workingDirectory)
        : ExecutableResource(name, "run", workingDirectory), IResourceWithServiceDiscovery;

    private static string CreateAppHostDirectory(params string[] localServices)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        var yaml = string.Join("\n", localServices.Select(name =>
            $"  {name}:\n    repository: https://example.com/{name}.git\n    kind: {KindName}"));
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), $"services:\n{yaml}\n");

        var json = string.Join(",", localServices.Select(name => $"\"{name}\": {{ \"source\": \"local\" }}"));
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), $"{{ \"services\": {{ {json} }} }}");

        return dir;
    }

    private static ServiceMetadata Metadata(string name) =>
        new() { Repository = $"https://example.com/{name}.git", Kind = KindName };

    private static ServiceDeveloperConfig DevConfig(string? path = null) => new() { Source = "local", Local = new() { Path = path } };

    private static string ExpectedRepoRoot(string appHostDirectory, string serviceName) =>
        Path.Combine(appHostDirectory, ".servicesources", "checkouts", serviceName);

    private static bool IsHeldBack(IResource resource) =>
        resource.Annotations.OfType<ExplicitStartupAnnotation>().Any();

    private static IResource Named(IDistributedApplicationBuilder builder, string name) =>
        Assert.Single(builder.Resources, r => r.Name == name);

    [Fact]
    public void OptedIn_ColdCheckout_ReturnsWhileTheCloneIsStillRunning()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind();
        builder.AddLocalKind(KindName, kind);

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/frontend.git");

        // The complaint #159 is about: before this, a non-dotnet kind resolved eagerly and this call
        // sat here until the clone it never needed had finished.
        var service = new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        Assert.True(IsHeldBack(service.Resource));
        Assert.False(kind.ResolvedEagerly);
        Assert.False(Directory.Exists(ExpectedRepoRoot(dir, "frontend")), "the checkout should not exist yet");

        // The handler is told where the clone will land, which is the same pure function of the
        // service name that the checkout itself uses.
        Assert.Equal(ExpectedRepoRoot(dir, "frontend"), kind.DeferredRepoRoot);

        gate.Set();
    }

    [Fact]
    public void DeferredKind_KeepsTheEndpointsItDeclaredFromTheCatalog()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();
        builder.AddLocalKind(KindName, new StandInKind());

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/frontend.git");

        var service = new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        // This is why java and javascript can be deferred at all where dotnet needed a warning:
        // their endpoints come from the committed catalog, not from a file in the repository, so
        // deferral costs them nothing and a consumer's WithReference still resolves.
        var endpoint = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(8080, endpoint.TargetPort);

        gate.Set();
    }

    [Fact]
    public void KindThatDeclinesDeferral_FallsBackToTheEagerPathHavingAddedNothing()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind(supportsDeferral: false);
        builder.AddLocalKind(KindName, kind);

        var service = new LocalProjectSource(new FakeGitClient())
            .Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        // Returning null from ResolveDeferred is a kind saying "not me" — a kind that can only learn
        // its endpoints by reading the repository, say. It has to cost nothing but the eager path.
        Assert.True(kind.ResolvedEagerly);
        Assert.False(IsHeldBack(service.Resource));
        Assert.Single(builder.Resources, r => r.Name == "frontend");
        Assert.True(Directory.Exists(ExpectedRepoRoot(dir, "frontend")));
    }

    /// <summary>
    /// #76 for a satellite kind. <c>SupportsDeferredCheckout</c> is the question core can ask about
    /// a service nobody has added — it touches no filesystem and registers nothing — so the prefetch
    /// can ask it too, and leave a would-be-deferred service's clone to the registration that will
    /// actually want it.
    /// </summary>
    [Fact]
    public void OptedIn_KindThatCanDefer_ColdServiceTheAppHostNeverAdds_IsNotCloned()
    {
        var dir = CreateAppHostDirectory("frontend", "admin");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind();
        builder.AddLocalKind(KindName, kind);

        var git = new FakeGitClient();
        new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        Assert.Null(LocalCheckoutPrefetch.For(builder, git).UnusedCheckoutsMessage);

        Assert.True(
            SpinWait.SpinUntil(() => git.Cloned.Count > 0, TimeSpan.FromSeconds(30)),
            "the deferred service's own checkout was never cloned.");
        Assert.Equal(["https://example.com/frontend.git"], git.Cloned);
    }

    /// <summary>
    /// The converse, and the reason the prefetch has to ask rather than assume: a kind that cannot
    /// build its resource without reading the repository takes the eager path, where the clone
    /// blocks composition — so speculating for it is still what keeps the clones parallel.
    /// </summary>
    [Fact]
    public void OptedIn_KindThatCannotDefer_ColdServiceTheAppHostNeverAdds_IsStillCloned()
    {
        var dir = CreateAppHostDirectory("frontend", "admin");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind(supportsDeferral: false);
        builder.AddLocalKind(KindName, kind);

        var git = new FakeGitClient();
        new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        Assert.True(
            SpinWait.SpinUntil(() => git.Cloned.Count == 2, TimeSpan.FromSeconds(30)),
            "the speculative checkout for 'admin' never ran.");
        Assert.Contains("https://example.com/admin.git", git.Cloned);
    }

    [Fact]
    public void WithoutOptIn_ColdCheckout_StillResolvesEagerly()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);

        var kind = new StandInKind();
        builder.AddLocalKind(KindName, kind);

        var service = new LocalProjectSource(new FakeGitClient())
            .Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        // UseDeferredCheckout() stays the only way in, for every kind.
        Assert.True(kind.ResolvedEagerly);
        Assert.False(IsHeldBack(service.Resource));
    }

    [Fact]
    public void OptedIn_WarmCheckout_ResolvesEagerly()
    {
        var dir = CreateAppHostDirectory("frontend");
        Directory.CreateDirectory(Path.Combine(ExpectedRepoRoot(dir, "frontend"), "app"));
        Directory.CreateDirectory(Path.Combine(ExpectedRepoRoot(dir, "frontend"), ".git"));

        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind();
        builder.AddLocalKind(KindName, kind);

        var git = new FakeGitClient();
        var service = new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        // Every run after the first. Deferral only ever claims a checkout that is not there at all.
        Assert.True(kind.ResolvedEagerly);
        Assert.False(IsHeldBack(service.Resource));
        Assert.Empty(git.Cloned);
    }

    [Fact]
    public void OptedIn_PathOverride_ResolvesEagerly()
    {
        var dir = CreateAppHostDirectory("frontend");
        var checkout = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(checkout, "app"));

        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind();
        builder.AddLocalKind(KindName, kind);

        var service = new LocalProjectSource(new FakeGitClient())
            .Resolve(builder, "frontend", Metadata("frontend"), DevConfig(path: checkout));

        // 'path' is the developer's own directory: nothing to clone, so nothing to wait for.
        Assert.True(kind.ResolvedEagerly);
        Assert.False(IsHeldBack(service.Resource));
    }

    [Fact]
    public void EveryResourceTheHandlerAdded_IsHeldBack_NotJustTheOneItReturned()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();
        builder.AddLocalKind(KindName, new StandInKind(withHelper: true));

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/frontend.git");

        var service = new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        // The javascript case. Aspire.Hosting.JavaScript adds a separate installer resource to run
        // "npm install", which the app already waits for. Holding back only the app would leave DCP
        // starting the installer at startup against a directory that does not exist yet — so core
        // withholds everything the handler added, not only what it handed back.
        Assert.True(IsHeldBack(service.Resource));
        Assert.True(IsHeldBack(Named(builder, "frontend-helper")));

        gate.Set();
    }

    [Fact]
    public async Task BeforeStart_RunsTheCheckoutAndThenTheKindsOwnValidation()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind();
        builder.AddLocalKind(KindName, kind);

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/frontend.git");
        var service = new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        // Releasing the gate only now is what proves the host was not held while the clone ran.
        await PublishNotStartedAsync(services, service.Resource);
        Assert.Equal(0, kind.ValidateCheckoutCalls);
        gate.Set();

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        // The checks the handler could not run at composition time run exactly once, after the clone
        // and against the real working tree.
        Assert.Equal(1, kind.ValidateCheckoutCalls);
        Assert.True(kind.CheckoutExistedWhenValidated);
    }

    [Fact]
    public async Task HeldBackHelper_IsStartedToo()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();
        builder.AddLocalKind(KindName, new StandInKind(withHelper: true));

        var git = new FakeGitClient();
        var service = new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());
        var helper = Named(builder, "frontend-helper");

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        // Both have to reach NotStarted before the deferred task will act: it waits for each, because
        // starting the app while its installer is still withheld would leave the app's WaitFor
        // hanging on a resource nothing was ever going to create.
        await PublishNotStartedAsync(services, helper);
        await PublishNotStartedAsync(services, service.Resource);

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(Directory.Exists(Path.Combine(ExpectedRepoRoot(dir, "frontend"), "app")));
    }

    [Fact]
    public async Task ValidationThatFailsAfterTheClone_CostsOneServiceRatherThanTheHost()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind();
        builder.AddLocalKind(KindName, kind);

        var git = new FakeGitClient();
        git.FailFor("https://example.com/frontend.git", new InvalidOperationException("no such repo"));
        var service = new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, service.Resource);

        // The failure isolation #159 wants for the satellite kinds: a java clone that fails today
        // takes the whole AppHost down. Deferred, it is reported as this one service's state and
        // never as an exception on a task nobody awaits.
        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, kind.ValidateCheckoutCalls);
    }

    [Fact]
    public void HandlerThatThrowsFromResolveDeferred_IsReportedAgainstTheService()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();
        builder.AddLocalKind(KindName, new ThrowingKind());

        var exception = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new FakeGitClient()).Resolve(builder, "frontend", Metadata("frontend"), DevConfig()));

        // Same wrapping the eager path gives a handler that throws something it shouldn't: the
        // service and the kind are named, and the developer is pointed at Validate.
        Assert.Contains("frontend", exception.Message);
        Assert.Contains(KindName, exception.Message);
        Assert.Contains(nameof(ILocalResourceKind.Validate), exception.Message);
    }

    [Fact]
    public void KindThatDeclaresNoDeferralSupport_IsNeverAskedToResolveDeferred()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind(supportsDeferral: false);
        builder.AddLocalKind(KindName, kind);

        new LocalProjectSource(new FakeGitClient()).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        // The whole point of the cheap question: it is answerable without the expensive one being
        // asked, because the expensive one adds resources to the app model as a side effect.
        Assert.Equal(0, kind.ResolveDeferredCalls);
        Assert.True(kind.ResolvedEagerly);

        // Asked at all, so the eager path here is this kind declining deferral rather than deferral
        // never having been on the table — which is what makes the assertion above mean something.
        //
        // How many times is deliberately not asserted (#189). For a kind that declines, the prefetch
        // keeps the service in its set and starts the clone; that clone creates the checkout
        // directory, and the registration's own ShouldDefer reads exactly that directory. So whether
        // the registration gets as far as asking the kind depends on which of the two wins a race
        // the package is entitled to leave open — both branches reach the same eager resolution. A
        // total of 2 was pinning a scheduling accident, and reddened on whichever CI leg lost it.
        Assert.True(
            kind.SupportsDeferredCheckoutCalls > 0,
            "the kind was never asked whether it supports a deferred checkout.");
    }

    /// <summary>
    /// The property that makes the cheap question askable by more than one caller, and about
    /// services nobody has added: asking it registers nothing. Exercised through the prefetch alone
    /// — no <c>AddService</c> call, so the sweep over the developer configuration is the only caller
    /// that can ask, and what it asked is attributable rather than inferred from a total.
    /// </summary>
    [Fact]
    public void PrefetchAsksWhetherAKindSupportsDeferral_WithoutRegisteringAnything()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind();
        builder.AddLocalKind(KindName, kind);

        LocalCheckoutPrefetch.For(builder, new FakeGitClient());

        // One caller, one answer — and the expensive form, the one that adds resources, is not how
        // the prefetch found out.
        Assert.Equal(1, kind.SupportsDeferredCheckoutCalls);
        Assert.Equal(0, kind.ResolveDeferredCalls);
        Assert.False(kind.ResolvedEagerly);

        // Nothing reached the app model for a service the AppHost never mentioned.
        Assert.DoesNotContain(builder.Resources, r => r.Name == "frontend");
    }

    [Fact]
    public void KindThatDeclaresSupportThenDeclines_StillFallsBackToTheEagerPath()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var kind = new StandInKind(deferralReturnsNull: true);
        builder.AddLocalKind(KindName, kind);

        var service = new LocalProjectSource(new FakeGitClient())
            .Resolve(builder, "frontend", Metadata("frontend"), DevConfig());

        // A kind may only be able to decide once it has looked at everything, so null out of
        // ResolveDeferred stays honoured even after the cheap probe said yes.
        Assert.Equal(1, kind.ResolveDeferredCalls);
        Assert.True(kind.ResolvedEagerly);
        Assert.False(IsHeldBack(service.Resource));
        Assert.Single(builder.Resources, r => r.Name == "frontend");

        // And the checkout still lands. This is the one case where the two deferral questions
        // disagree, so the prefetch — which believed the first answer — started nothing for this
        // service (#76) and GetRepoRoot has to clone it inline through its "not in the prefetch
        // set" fallback. Serial rather than parallel, but the service must still be resolvable;
        // that fallback is what makes declining late safe rather than merely permitted.
        Assert.True(Directory.Exists(ExpectedRepoRoot(dir, "frontend")));
    }

    [Fact]
    public async Task LifetimeLessResourceTheHandlerAdded_IsNotHeldBackAndDoesNotStallTheStart()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();
        builder.AddLocalKind(KindName, new StandInKind(withParameter: true));

        var git = new FakeGitClient();
        var service = new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());
        var parameter = Named(builder, "frontend-token");

        // DCP never creates a parameter, so it never publishes the NotStarted the start task waits
        // for. Withholding one would leave that task waiting for a state that is not coming, and the
        // service would silently never start — so it is excluded rather than held back.
        Assert.False(IsHeldBack(parameter));
        Assert.True(IsHeldBack(service.Resource));

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        // Only the service's own NotStarted is published; the run completes anyway.
        await PublishNotStartedAsync(services, service.Resource);

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(Directory.Exists(Path.Combine(ExpectedRepoRoot(dir, "frontend"), "app")));
    }

    [Fact]
    public void KindThatRegistersAndThenDeclines_IsNamedRatherThanLeavingOrphans()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();
        builder.AddLocalKind(KindName, new StandInKind(deferralReturnsNull: true, declineAfterAdding: true));

        var exception = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new FakeGitClient()).Resolve(builder, "frontend", Metadata("frontend"), DevConfig()));

        // Nothing can come back out of the app model, so a handler that registers before deciding
        // leaves resources behind that then collide with the eager retry registering the same
        // service again. The handler is the only one who can fix it, so it is named.
        Assert.Contains("frontend", exception.Message);
        Assert.Contains(KindName, exception.Message);
        Assert.Contains(nameof(ILocalResourceKind.SupportsDeferredCheckout), exception.Message);
    }

    [Fact]
    public void HelperThatWaitsForTheServiceItSitsBeside_IsRefusedRatherThanLeftToHang()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();
        builder.AddLocalKind(KindName, new StandInKind(withHelper: true, helperWaitsForService: true));

        var exception = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(new FakeGitClient()).Resolve(builder, "frontend", Metadata("frontend"), DevConfig()));

        // Helpers are started before the service, so a helper waiting on the service can never be
        // satisfied — and the start loop awaits each in turn, so it would hang rather than fail.
        // A deadlocked task nobody awaits shows up as a service that simply never starts, which is
        // why this is named at registration instead.
        Assert.Contains("frontend-helper", exception.Message);
        Assert.Contains("WaitFor", exception.Message);
    }

    [Fact]
    public async Task CloneThatNeverLands_PaintsTheServiceAndItsHeldBackHelper()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();
        builder.AddLocalKind(KindName, new StandInKind(withHelper: true));

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/frontend.git");
        git.FailFor("https://example.com/frontend.git", new InvalidOperationException("no such repo"));
        var service = new LocalProjectSource(git).Resolve(builder, "frontend", Metadata("frontend"), DevConfig());
        var helper = Named(builder, "frontend-helper");

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        await PublishNotStartedAsync(services, helper);
        await PublishNotStartedAsync(services, service.Resource);

        gate.Set();

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        // Nothing was started, so both are the casualty: a held-back helper left sitting in
        // NotStarted reads as "still waiting" rather than as a resource nothing is coming for. The
        // service is painted too, even though this package moved it to "Checking out" itself —
        // which is why the skip below is keyed on what started, not on the state text.
        Assert.Equal(KnownResourceStates.FailedToStart, await StateOfAsync(services, service.Resource));
        Assert.Equal(KnownResourceStates.FailedToStart, await StateOfAsync(services, helper));
    }

    private sealed class ThrowingKind : ILocalResourceKind
    {
        public bool SupportsDeferredCheckout(object? rawConfig) => true;

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("never reached");

        public DeferredLocalResource? ResolveDeferred(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new InvalidOperationException("handler is broken");
    }

    /// <summary>
    /// Stands in for DCP, which publishes <c>NotStarted</c> when it withholds an explicit-start
    /// executable. That state is what each deferred task waits for before it touches the resource.
    /// </summary>
    private static Task PublishNotStartedAsync(IServiceProvider services, IResource resource) =>
        PublishStateAsync(services, resource, KnownResourceStates.NotStarted);

    private static Task PublishStateAsync(IServiceProvider services, IResource resource, string state) =>
        services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(state, null),
            });

    /// <summary>
    /// Reads a resource's current state through the only door the notification service opens: an
    /// update that captures the snapshot and hands the identical one back, so the read publishes
    /// nothing new.
    /// </summary>
    private static async Task<string?> StateOfAsync(IServiceProvider services, IResource resource)
    {
        string? state = null;

        await services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(resource, snapshot =>
            {
                state = snapshot.State?.Text;
                return snapshot;
            });

        return state;
    }
}
