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
// like payments can't reference it (#72) and the AppHost would refuse to start. A project or
// executable consumer can.
const inventory = await builder.addService('inventory');

// Each configuration shape has its own method name: ATS drops overloads, and a generic method
// loses its type parameter.
const payments = await builder
  .addService('payments')
  .withServiceEnvironment('DEMO_INJECTED_BY_APPHOST', 'true')
  .withServiceReference(inventory);

// addService()'s declared return type is a bare Aspire interface, IResourceBuilder<
// IResourceWithServiceDiscovery>, rather than a concrete resource class — a shape the TypeScript
// generator emits no *Promise/*PromiseImpl wrapper pair for on its own (microsoft/aspire#19507).
// What supplies the pair here is the eight [AspireExport] configuration shims behind the
// withService* calls above: the generator emits it when the bare interface appears as an
// extension-method receiver, which those shims declare, so they carry it for addService too. With
// the wrapper types emitted, the resolved handle also flows into Aspire's *own* withReference(),
// distinct from payments' withServiceReference() above, which is this package's ATS export.
//
// probe prints what the AppHost injected for inventory and then exits, so it shows as Exited (not
// Running) next to the two containers — those two log lines are the whole point. node is the
// interpreter already running this AppHost, so process.execPath keeps it portable to Windows.
//
// The two lines are the point in a second sense: they are the portable and the non-portable way to
// name a resolved service's endpoint (#160). getServiceEndpoint() asks for "the endpoint this
// service exposes" and survives inventory being switched between the sources servicesources.yaml
// describes; the discovery variable spells a scheme into its own name, so it is only defined while
// inventory resolves to a source that produces an http endpoint. Were inventory on the "url" source
// with its https URL, that variable would be undefined while INVENTORY_URL still resolved — which
// is a contrast to reason about rather than run here, since payments references inventory and the
// AppHost would refuse to start for the reason noted above.
const probeScript =
  'console.log("INVENTORY_URL=" + process.env.INVENTORY_URL);' +
  'console.log("services__inventory__http__0=" + process.env.services__inventory__http__0);';

await builder
  .addExecutable('probe', process.execPath, '.', ['-e', probeScript])
  .withEnvironment('INVENTORY_URL', inventory.getServiceEndpoint())
  .withReference(inventory);

await builder.build().run();
