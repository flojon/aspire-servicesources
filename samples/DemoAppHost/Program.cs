using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

// "local" source: clones (or uses an existing checkout of) a real project and runs it via
// Aspire's own project orchestration. See servicesources.local.json.example.
//
// AddService returns a builder over the real resource, so the AppHost can inject configuration
// the yaml/json files can't express — here a value from the AppHost's own graph.
var orders = builder.AddService("orders")
    .Configure<IResourceWithEnvironment>(r => r.WithEnvironment("DEMO_INJECTED_BY_APPHOST", "true"));

// "url" source: resolves straight to a fixed, already-known URL — no resource for Aspire to
// run. See servicesources.local.json.example. This one runs out of band, so it accepts no
// configuration from the AppHost and cannot be referenced by a container.
var inventory = builder.AddService("inventory");

// "container" source: runs a published container image locally via Aspire's own
// container-runtime integration. See servicesources.local.json.example.
var payments = builder.AddService("payments");

builder.Build().Run();
