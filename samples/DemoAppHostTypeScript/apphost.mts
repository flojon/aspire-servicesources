// TypeScript AppHost demonstrating AddService(), exported via Aspire's Type System (ATS) so it's
// callable from a guest-language AppHost. See servicesources.local.json.example.
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// Both services resolve through the "container" source: a published image run locally. Because
// they actually run here, the AppHost can configure them — the guest-language equivalent of C#'s
// Configure<T>().
//
// servicesources.yaml also describes a "url" source for inventory, and flipping it there is a
// one-line config edit that this file doesn't see. Don't do it for this sample, though: a
// "url" service runs out of band with no resource for Aspire to run, so a *container* consumer
// like payments can't reference it (issue #58) and the AppHost would refuse to start. A project
// or executable consumer can.
const inventory = await builder.addService('inventory');

// Each configuration shape has its own method name: ATS drops overloads, and a generic method
// loses its type parameter.
const payments = await builder
  .addService('payments')
  .withServiceEnvironment('DEMO_INJECTED_BY_APPHOST', 'true')
  .withServiceReference(inventory);

// addService() returns a ResourceWithServiceDiscoveryPromise, so the handle also goes straight
// into Aspire's *own* withReference() — the wrapper type microsoft/aspire#19577 fixed, and the
// reason this sample compiles at all on 13.6.0+. Distinct from payments' withServiceReference()
// above, which is this package's own ATS export; this line exercises the native Aspire path.
await builder
    .addExecutable('probe', '/usr/bin/env', '.', [])
    .withReference(inventory);

await builder.build().run();
