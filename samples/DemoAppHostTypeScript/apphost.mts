// TypeScript AppHost demonstrating AddService(), exported via Aspire's Type System (ATS) so it's
// callable from a guest-language AppHost. See servicesources.local.json.example.
//
// Known issue: this file currently cannot compile due to an upstream Aspire CLI TypeScript
// codegen bug (confirmed on Aspire CLI 13.4.6/13.5.0) — see the README's "Known issue" section
// under the Sample heading for details.
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// "url" source: a fixed, already-known URL.
const inventory = await builder.addService('inventory');

// "container" source: a published image run locally. Because this one actually runs here, the
// AppHost can configure it — the guest-language equivalent of C#'s Configure<T>(). Each shape has
// its own method name: ATS drops overloads, and a generic method loses its type parameter.
const payments = await builder
  .addService('payments')
  .withServiceEnvironment('DEMO_INJECTED_BY_APPHOST', 'true')
  .withServiceReference(inventory);

await builder.build().run();
