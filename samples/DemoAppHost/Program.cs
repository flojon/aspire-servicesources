using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

// Registers the "java" local kind, so a service whose catalog entry says `kind: java` can be
// cloned and run via the Aspire Community Toolkit's Java integration. Must come before the first
// AddService call, which resolves eagerly; inert until a service actually resolves to it, so it
// costs nothing to leave in.
builder.UseJava();

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

// The "catalog" service in servicesources.yaml is `kind: java`. To run it, uncomment below AND add
//   "catalog": { "source": "local" }
// to servicesources.local.json. Both steps are needed, and deliberately: the first AddService call
// clones every "local" entry in that file up front, so listing catalog there by default would clone
// Spring PetClinic on every run of this sample even with the line below commented out. Unlike the
// services above it also needs a JDK, since it builds the checkout with the repo's Maven wrapper.
// var catalog = builder.AddService("catalog");

builder.Build().Run();
