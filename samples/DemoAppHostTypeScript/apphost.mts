// TypeScript AppHost demonstrating AddService()'s "url" source, exported via Aspire's Type
// System (ATS) so it's callable from a guest-language AppHost. See servicesources.local.json.example.
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const inventory = await builder.addService('inventory');

// addService() returns a ResourceWithServiceDiscoveryPromise, so the handle can be passed
// straight into withReference() for service discovery.
await builder
    .addExecutable('probe', '/usr/bin/env', '.', [])
    .withReference(inventory);

await builder.build().run();
