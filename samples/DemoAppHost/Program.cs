using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

// Lets the smoke test exercise the eager fallback path deliberately (e.g. to check that a
// prepare step's output reaches the AppHost's own stdout, which is only true on that path — see
// scripts/smoketest-local-source.sh). Not something a real AppHost needs: deferred is the
// default since 0.7.0, and this line has no effect when the variable is unset.
if (Environment.GetEnvironmentVariable("SERVICESOURCES_DEMO_CHECKOUT_TIMING") is { } checkoutTiming
    && Enum.TryParse<CheckoutTiming>(checkoutTiming, ignoreCase: true, out var timing))
{
    builder.SetCheckoutTiming(timing);
}

// "java" is a built-in local kind — a service whose catalog entry says `kind: java` clones and
// runs via the Aspire Community Toolkit's Java integration with no registration call needed.

// "local" source: clones (or uses an existing checkout of) a real project and runs it via
// Aspire's own project orchestration. See servicesources.local.json.example.
//
// AddService returns a builder over the real resource, so the AppHost can inject configuration
// the yaml/json files can't express — here a value from the AppHost's own graph.
var orders = builder.AddService("orders")
    .WithEnvironment("DEMO_INJECTED_BY_APPHOST", "true");

// "url" source: resolves straight to a fixed, already-known URL — no resource for Aspire to
// run. See servicesources.local.json.example. This one runs out of band, so any Configure call
// on it would be skipped with a logged warning, and a container cannot reference it.
var inventory = builder.AddService("inventory");

// "container" source: runs a published container image locally via Aspire's own
// container-runtime integration. See servicesources.local.json.example.
var payments = builder.AddService("payments");

// The "catalog" service in servicesources.yaml is `kind: java`. To run it, uncomment below AND add
//   "catalog": { "source": "local" }
// to servicesources.local.json. Both steps are needed: AddService is what actually resolves and
// runs it. Unlike the services above it also needs a JDK, since it builds the checkout with the
// repo's Maven wrapper.
// var catalog = builder.AddService("catalog");

builder.Build().Run();
