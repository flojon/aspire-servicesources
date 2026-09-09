// TypeScript AppHost demonstrating the catalog authored in code — no servicesources.yaml. See
// samples/DemoAppHostTypeScript/apphost.mts for the yaml-based equivalent.
import { createBuilder, PrepareMode } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addServiceCatalog(async (catalog) => {
  const orders = await catalog.addServiceToCatalog('orders');
  await orders.withRepository('https://github.com/dotnet/aspire-samples', {
    project: 'samples/health-checks-ui/HealthChecksUI.ApiService/HealthChecksUI.ApiService.csproj',
    defaultRef: 'main',
  });

  // Two sources are described here on purpose, the same reason
  // samples/DemoAppHostTypeScript/servicesources.yaml gives inventory both a url: and a
  // container: block: a "url"-sourced service runs out of band with no Aspire resource, so the
  // payments container below can't reference it (#72) unless inventory also resolves through
  // "container". servicesources.local.json picks the actual source; apphost.mts never changes.
  const inventory = await catalog.addServiceToCatalog('inventory');
  await inventory.withUrl('https://httpbin.org');
  await inventory.withContainer('nginxdemos/hello', 80, { defaultTag: 'latest' });

  const payments = await catalog.addServiceToCatalog('payments');
  await payments.withContainer('nginxdemos/hello', 80, { defaultTag: 'latest' });
  await payments.withKubernetes('payments', { port: 8080 });

  // Declared only — never passed to builder.addService() below, the same choice Task 13's C#
  // sample makes for its "catalog" service: running it needs a JDK to build the checkout with
  // the repo's Maven wrapper, so this only demonstrates WithKind as a catalog-authoring surface.
  //
  // asJava/withPrepare are Stage 2's typed alternatives to the untyped withKind bag Stage 1 shipped
  // — see samples/DemoAppHostCodeCatalog/Program.cs for the identical shape in C#.
  const catalogService = await catalog.addServiceToCatalog('catalog');
  await catalogService.withRepository('https://github.com/spring-projects/spring-petclinic', {
    defaultRef: 'main',
  });
  // asJava is the typed alternative to withKind('java', { options: {...} }) — Stage 1 shipped only
  // the untyped bag because JavaKindOptions had no public handle yet. The lambda nested inside the
  // addServiceCatalog lambda is exactly the shape Stage 0 measured crossing ATS.
  await catalogService.asJava(async (o) => {
    await o.mavenGoal('spring-boot:run');
    await o.port(8080);
  });
  await catalogService.withPrepare(['./mvnw', '-q', 'dependency:go-offline'], {
    mode: PrepareMode.Once,
  });
});

const inventory = await builder.addService('inventory');

const payments = await builder
  .addService('payments')
  .withServiceEnvironment('DEMO_INJECTED_BY_APPHOST', 'true')
  .withServiceReference(inventory)
  .withServiceHttpsEndpoint();

const probeScript =
  'console.log("INVENTORY_URL=" + process.env.INVENTORY_URL);' +
  'console.log("services__inventory__http__0=" + process.env.services__inventory__http__0);';

await builder
  .addExecutable('probe', process.execPath, '.', ['-e', probeScript])
  .withEnvironment('INVENTORY_URL', inventory.getServiceEndpoint())
  .withReference(inventory);

await builder.build().run();
