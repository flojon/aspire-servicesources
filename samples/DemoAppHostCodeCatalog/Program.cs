using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

// Registers the "java" local kind — same call as the yaml-based sample, needed before the first
// AddService() either way. builder.UseJava() is unaffected by where the catalog comes from.
builder.UseJava();

// The whole catalog, declared here instead of in servicesources.yaml. Must come before the first
// AddService() call, which is where it's read.
builder.AddServiceCatalog(catalog =>
{
    catalog.AddService("orders")
        .WithRepository(
            "https://github.com/dotnet/aspire-samples",
            project: "samples/health-checks-ui/HealthChecksUI.ApiService/HealthChecksUI.ApiService.csproj",
            defaultRef: "main");

    catalog.AddService("inventory")
        .WithUrl("https://httpbin.org");

    catalog.AddService("payments")
        .WithContainer("nginxdemos/hello", port: 80, defaultTag: "latest")
        .WithKubernetes("payments", port: 8080);

    // "catalog" (kind: java) is left uncommented here, unlike the yaml sample, because
    // AddServiceCatalog costs nothing extra to declare it — it's servicesources.local.json (below)
    // that decides whether it actually clones anything, exactly as in the yaml sample.
    //
    // AsJava is the typed alternative to WithKind("java", <dictionary>) — Stage 1 shipped only the
    // dictionary form because JavaKindOptions was still internal with no public handle over it.
    // WithPrepare demonstrates the code-authoring equivalent of yaml's prepare: block; it's declared
    // here for the same reason the "catalog" service itself is never actually run below — this repo
    // (spring-petclinic) doesn't need a prepare step, so this exists purely to show the call.
    catalog.AddService("catalog")
        .WithRepository("https://github.com/spring-projects/spring-petclinic", defaultRef: "main")
        .AsJava(o => o.MavenGoal("spring-boot:run").Port(8080))
        .WithPrepare(["./mvnw", "-q", "dependency:go-offline"], mode: "once");
});

// "local" source: clones (or uses an existing checkout of) a real project and runs it via
// Aspire's own project orchestration. See servicesources.local.json.example.
//
// AddService returns a builder over the real resource, so the AppHost can inject configuration
// the yaml/json files can't express — here a value from the AppHost's own graph.
var orders = builder.AddService("orders")
    .Configure<IResourceWithEnvironment>(r => r.WithEnvironment("DEMO_INJECTED_BY_APPHOST", "true"));

// "url" source: resolves straight to a fixed, already-known URL — no resource for Aspire to
// run. See servicesources.local.json.example. This one runs out of band, so any Configure call
// on it would be skipped with a logged warning, and a container cannot reference it.
var inventory = builder.AddService("inventory");

// "container" source: runs a published container image locally via Aspire's own
// container-runtime integration. See servicesources.local.json.example.
var payments = builder.AddService("payments");

// The "catalog" service above is kind: java. To run it, uncomment below AND add
//   "catalog": { "source": "local" }
// to servicesources.local.json. Both steps are needed, and deliberately: the first AddService call
// clones every "local" entry in that file up front, so listing catalog there by default would clone
// Spring PetClinic on every run of this sample even with the line below commented out. Unlike the
// services above it also needs a JDK, since it builds the checkout with the repo's Maven wrapper.
// var catalog = builder.AddService("catalog");

builder.Build().Run();
