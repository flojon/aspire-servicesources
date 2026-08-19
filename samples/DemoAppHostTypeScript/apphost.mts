import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const inventory = await builder.addService('inventory');

await builder.build().run();
