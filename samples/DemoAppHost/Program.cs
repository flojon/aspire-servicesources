using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

// "local" source: clones (or uses an existing checkout of) a real project and runs it via
// Aspire's own project orchestration. See servicesources.local.json.example.
var orders = builder.AddService("orders");

// "url" source: resolves straight to a fixed, already-known URL — no resource for Aspire to
// run. See servicesources.local.json.example.
var inventory = builder.AddService("inventory");

// "container" source: runs a published container image locally via Aspire's own
// container-runtime integration. See servicesources.local.json.example.
var payments = builder.AddService("payments");

// Registers the "java" local kind, so a service whose catalog entry says `kind: java` can be
// cloned and run via the Aspire Community Toolkit's Java integration. Inert until some service
// actually resolves to it, so it costs nothing to leave in.
builder.UseJava();

// The "catalog" service in servicesources.yaml is `kind: java`. Uncomment to run it — unlike the
// services above it needs a JDK on the machine, since it builds and runs the checkout with the
// repository's own Maven wrapper.
// var catalog = builder.AddService("catalog");

builder.Build().Run();
