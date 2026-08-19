// TypeScript AppHost demonstrating AddService()'s "url" source, exported via Aspire's Type
// System (ATS) so it's callable from a guest-language AppHost. See servicesources.local.json.example.
//
// Known issue: this file currently cannot compile due to an upstream Aspire CLI TypeScript
// codegen bug (confirmed on Aspire CLI 13.4.6/13.5.0) — see the README's "Known issue" section
// under the Sample heading for details.
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// intentionally not wired to a consumer yet — see known issue above; would use
// builder.addContainer(...).withReference(inventory) once the codegen bug is fixed
const inventory = await builder.addService('inventory');

await builder.build().run();
