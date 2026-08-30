# Aspire.Hosting.ServiceSources — Interactive Source Picker for Unconfigured Services

**Date:** 2026-08-28
**Status:** Design — not yet implemented.
**Complements:** GitHub issue #70 (per-service "Change source" command), which covers services
that already resolved. This design covers the case #70 does not reach — a service with no source
at all — and reuses #70's dialog machinery to do it.
**Depends on:** #62 (every source returns a real registered resource) — merged, and the hard
prerequisite #70 named.
**Related:** #130 / #136 (`UseDeferredCheckout()`) share this design's mechanism from the other
side — they defer the checkout of a service whose source is already known, where this defers the
*choice*; #134 (code-authored catalog) changes where "the set of services that exist" is read
from; #69 (developer config through `IConfiguration`) supplies a better persistence layer later;
#8 (CLI configuration UX) is served by the same `PromptInputsAsync` implementation; #43
(per-source field validation) is reused rather than duplicated; #54 (third-party sources) is
picked up automatically by deriving the source list from the registry.
**Corrected by:** the review on PR #128 — an earlier draft claimed a checkout could never precede
a prompt within one run. See *The prompt cannot precede a type choice within a single run*.

## Motivation

A developer cloning an AppHost repository for the first time has no `servicesources.local.json`.
Today that is a hard error, one service at a time:

```
Service 'orders' was not found in 'servicesources.local.json'.
```

There is nothing in the product to tell them what the file should contain, and the AppHost never
reaches the dashboard, so the dashboard cannot help. They read the README, hand-write JSON, and
run again.

The failure mode on the other side is worse. Once the file *does* exist and marks services
`"local"`, the first `AddService()` call clones every `"local"` entry in parallel before the
AppHost has said which services it wants (see `LocalCheckoutPrefetch`). A developer who wanted to
run one service against local source and the rest as containers pays a cold clone for all of them
unless they curate the file by hand first. #76 tracks that cost; this design removes the reason to
incur it, by making "which services are local" a question the developer answers in the dashboard
rather than a file they must author before anything runs.

The goal is that `aspire run` on a fresh clone reaches a working dashboard, shows which services
have no source, and asks.

## Findings that constrain the design

Established empirically: runtime behaviour against Aspire.Hosting 13.5.2 and Aspire CLI 13.5.1,
API surface also checked against the 13.6.0-pr.19577 assembly. Each was verified with a throwaway
AppHost, not inferred from documentation.

### The prompt cannot precede a *type* choice within a single run

Two premises hold, both verified rather than assumed: Aspire has no API to add resources to the
model after `Build()`, and none to change a running resource's type. `IInteractionService` is
resolved from the built application's service provider, and the dashboard starts inside
`RunAsync()` — both strictly after composition ends.

What follows from them is narrower than "the prompt must come first". It follows only when the
answer **changes the resource type**. The source choice this design asks about does exactly that —
`local` produces a `ProjectResource`, `container` a container, `url` nothing at all — so for a
genuinely unconfigured service the two-phase shape is correct: run one configures, run two works.
#70 reached the same conclusion independently.

**But the checkout itself is deferrable, and the earlier draft of this section wrongly said it was
not.** When the type is already fixed, a `ProjectResource` can be registered before its `.csproj`
exists, held back, and started later in the same run. Confirmed end to end on Aspire 13.5.2:

```
project exists at construction? False
STARTUP STATE (project still missing): NotStarted
ENDPOINT allocated at startup (deferred)? True url=http://localhost:45713
CHECKOUT DONE, project exists? True
START COMMAND success=True   -> Running, without an AppHost restart
```

The mechanism, and its one trap:

- `WithExplicitStart()` on a non-persistent lifetime makes DCP **withhold** the executable at
  startup rather than create it stopped (`ExecutableCreator.IsReadyToCreate` returns
  `!DcpModelUtilities.ShouldDeferCreateForExplicitStart(...)`). `DcpExecutor.StartResourceAsync`
  creates the DCP object on demand, so working directory, certificates and execution config all
  resolve *after* the checkout.
- Plain `AddProject(name, missingPath)` **throws** during composition — `WithProjectDefaults` →
  `GetEffectiveLaunchProfile(throwIfNotFound: true)` → `LaunchProfileExtensions.GetLaunchSettings`,
  which does `File.Exists(ProjectPath)`. Two escapes: `launchProfileName: null` (which sets
  `ExcludeLaunchProfile`), or a custom `IProjectMetadata` supplying a non-null `LaunchSettings`.
  Both lose the endpoints Aspire would synthesise from `applicationUrl`, so endpoints must be
  declared instead of discovered.
- Endpoints are still allocated at startup for a deferred resource, because DCP `Service` objects
  are created for every resource independently of the executable. `WithReference()` keeps working
  from the first second.

**The real boundary is the absolute `.csproj` path, not the run.** `ExecutableCreator` bakes it
into the executable's working directory and `--project` argument during
`PrepareProjectExecutablesAsync`, which runs before the dashboard is up; mutating
`IProjectMetadata.ProjectPath` afterwards changes nothing.

| Deferrable within one run | Needs a restart |
| --- | --- |
| The initial clone (repository known, not yet on disk) | Choice of repository |
| `ref` / branch selection, including switching refs | `project` within the repo, or a self-managed `path` |
| | Source *type*: `local` / `container` / `url` / `kubernetes` |

This is the finding #130 acts on, implemented as `builder.UseDeferredCheckout()` in #136. That work
is the reason this section must not be read as a blanket impossibility — it had already been cited
as one.

### `IsAvailable` cannot gate the composition-time decision

The natural instinct is to make `AddService()` throw when interactions are unavailable and
produce a placeholder when they are. This is not possible: `AddService()` runs before the service
provider exists, so `IInteractionService` — and therefore `IsAvailable` — cannot be consulted at
the moment the decision is made. The switch must be something readable during composition:
an explicit call, configuration, or the environment.

### `IsAvailable` does not distinguish CI anyway

Measured `IsAvailable == true` under both `dotnet run` and `aspire run`. It reflects whether the
dashboard is enabled and whether a non-interactive scope is active — not whether a human is
present. A CI job running the AppHost with a dashboard nobody opens reports `true`. Detecting
"nobody is watching" needs the environment, not this flag.

### Interactions raised at startup do not block startup, and they wait

`IInteractionService` resolves from `AfterResourcesCreatedEvent`. A `PromptInputsAsync` started
there and deliberately not awaited leaves the application free to finish starting — verified:
`Distributed application started` was logged while the prompt was outstanding. The prompt then
stays pending indefinitely until a dashboard client answers it, rather than failing because no
browser was attached at the moment it was raised. This is exactly the semantics the push half of
the design needs.

### A consumer can reference an endpoint-less placeholder safely

A resource implementing `IResourceWithServiceDiscovery` with no endpoints can be registered,
given a custom state, and passed to another resource's `WithReference()`. Composition and startup
both complete with zero exceptions. The consumer simply receives no `services__*` environment
variables for that name and fails on its first call to it — a runtime failure at the point of
use, not a startup failure.

### Upstream hot reload will make the restart cheap, not unnecessary

Three open upstream issues target restart cost — microsoft/aspire#13839 (AppHost hot reload
architecture for `aspire run`), #13924 (hot reload like `dotnet watch`) and #7695 (only restart
impacted resources). #13839 is a worked architecture proposal rather than a wish: it moves DCP and
dashboard ownership from the AppHost to the CLI so both survive an AppHost restart.

None of them make the app model mutable in place. The AppHost process still restarts and still
recomposes from scratch; what changes is what survives that restart. The two-phase shape this
design accepts is therefore not a bet against upstream — it holds either way.

What upstream would change is the appeal of the deferred stop-the-AppHost command: once the
dashboard session survives a restart, "save, restart, keep looking at the same dashboard" becomes
a genuinely good interaction rather than the disruptive one it is today. That is the trigger for
picking the follow-up back up.

### The AppHost's console is not a channel under `aspire run`

Measured: the CLI captures AppHost stdout and stderr and writes them only to
`~/.aspire/logs/cli_*.log`, tagging stderr as `[FAIL]`. The terminal shows a
`Connecting to AppHost...` spinner for the whole composition phase and never renders AppHost
console output, before or after the dashboard appears. Any progress or prompt written to the
console during composition is invisible where it matters most. This rules out "just ask on the
console before cloning" as an alternative to this design.

## Architecture

### Opt-in, read during composition

```csharp
builder.UseInteractiveServiceConfiguration();
```

Called before the first `AddService()`, joining the `Use*` opt-in family alongside `UseJava()`,
`UseJavaScript()` and `UseDeferredCheckout()` (#136) — the name should be settled against that
family rather than in isolation. Without it, a service missing from `servicesources.local.json`
throws exactly as it does today, so the change is non-breaking for every existing consumer.

The call is suppressed automatically when the standard `CI` environment variable is set — the
variable GitHub Actions, GitLab CI and Azure DevOps all define — so a committed opt-in does not
weaken CI. Because that is a heuristic and will be wrong on some runner, an explicit override
takes precedence in both directions:

| `SERVICESOURCES_INTERACTIVE` | Behaviour |
|---|---|
| unset | Interactive unless `CI` is set |
| `1` / `true` | Interactive regardless of `CI` |
| `0` / `false` | Strict regardless of `CI` |

Strict mode is today's behaviour verbatim: `ServiceSourcesConfigurationException` naming the
service and the file.

### The placeholder resource

An unconfigured service resolves to:

```csharp
internal sealed class UnconfiguredServiceResource(string name)
    : Resource(name), IResourceWithServiceDiscovery;
```

registered with `WithInitialState`:

- `ResourceType = "Service (unconfigured)"`
- `State = new ResourceStateSnapshot("Not configured", KnownResourceStateStyles.Info)`
- a property naming the catalog entry it came from, so the dashboard row explains itself

It has no endpoints and starts no process. It carries `ServiceSourceAnnotation(name, "(none)")`
so the rest of the package can recognise it, and a `Configure` command (below).

Critically, an unconfigured service is **not** a `"local"` service, so `LocalCheckoutPrefetch`
never sees it and nothing is cloned for it. This is the mechanism by which the design stops
paying for repositories the developer never chose.

**The authoring API here is the one upstream is most likely to move.**
microsoft/aspire#17722 (*Spec: Custom Resource Lifecycle Model*, open, no milestone) proposes
replacing `WithInitialState` with `WithResourceType` / `WithProperty` and a `ResourceContext`
exposing `SetStateAsync` / `SetPropertyAsync`. If it lands, this placeholder is the code that
changes — a contained change, and a reason to keep its construction in one place rather than
spread across the picker. The same proposal's first layer fixes microsoft/aspire#13647, where
`DCPExecutor` clobbers custom state published through `PublishUpdateAsync`. That should not reach
a plain `Resource` subclass, which DCP does not manage, but the placeholder publishes a custom
state and a custom property, so **confirm it survives a DCP watch cycle when implementing** rather
than assuming it does. Both are on the watch list in #83.

### Option not taken: speculatively registering a deferred project

The managed checkout root is `{appHostDirectory}/.servicesources/checkouts/{serviceName}`
(`LocalGitCheckout.cs`) — a pure function of the service name. Combined with `project` from the
committed catalog, the absolute `.csproj` path is therefore computable for an **unconfigured**
service too, with no developer config and no network access.

That opens a faster path than the placeholder. For a catalog entry whose `kind` is `dotnet`, this
design *could* speculatively register an explicit-start `ProjectResource` at the path a managed
checkout would occupy, using the mechanism #136 builds. If the developer then answers `local` with
a managed checkout, it clones and starts in the same run — no restart at all.

It is recorded as an option rather than the recommendation because of what it costs when the guess
is wrong. Any other answer — `container`, `url`, `kubernetes`, or `local` with a self-managed
`path` — leaves a phantom `NotStarted` project resource in the dashboard for a service that is not
a project, and the `path` answer still needs a restart because it changes the frozen path. It
trades dashboard clarity for a faster first run, and it only ever helps `kind: dotnet`. Worth
revisiting once `UseDeferredCheckout()` has settled in practice.

### Push, then pull

**Push.** One subscription to `AfterResourcesCreatedEvent`. If any placeholders exist and
`IInteractionService.IsAvailable`, it starts — without awaiting — a single `PromptInputsAsync`
carrying one `InputType.Choice` input per unconfigured service. Asking for every service in one
form is what keeps a ten-service fresh clone to one dialog instead of ten.

The choice list is derived from the registered sources rather than hard-coded, so a third-party
source (#54) appears without touching this code.

A service whose chosen source needs more than a name then gets a short follow-up
`PromptInputsAsync` — `local` needs `path`/`ref`, `container` needs an image, `url` needs a URL.
This two-step shape is forced: `PromptInputsAsync` takes a fixed input list, so a field cannot
appear in response to an earlier answer in the same form. #70 evaluated showing every field at
once with `ValidationCallback` rejecting mismatches and rejected it; this design follows that
conclusion. Validation reuses the #43 per-source field rules via `ValidationCallback`.

If `IsAvailable` is false, nothing is prompted and the AppHost logs one warning naming every
unconfigured service and the file to add them to.

**Pull.** Each placeholder also carries a `Configure` command, so a service added to
`servicesources.yaml` by a teammate after the developer's file already exists can be configured
from its own row without a restart-to-get-the-prompt loop. This is #70's per-service command,
scoped here to placeholders; extending it to already-resolved services is the remainder of #70
and stays out of scope.

### Persistence and restart

Answers are merged into `servicesources.local.json`, preserving entries the developer already
has. The file is created if absent. #69 may later move this onto `IConfiguration` and user
secrets; the merge logic is written behind a small interface so that swap does not rewrite the
dialog code.

Both ends of this design read the two config files directly — "unconfigured" means *present in
`servicesources.yaml`, absent from `servicesources.local.json`*, and answers are written back to
the latter. **#134 changes the first half of that**: it makes the catalog authorable in code and
`servicesources.yaml` optional, so "the set of services that exist" stops being a file this design
can read. The concept survives — a service the AppHost knows about but the developer has not
chosen a source for — but where both halves are read from has to follow #134 and #69 rather than
be fixed here.

Saving is followed by `PromptNotificationAsync` with `MessageIntent.Success` stating that the
choice is saved and takes effect on the next run. The UI must not imply the current run changed —
it did not. A dashboard command that stops the AppHost so the developer can immediately re-run is
a natural follow-up (`StopAppHostAsync` exists) and is deliberately out of scope here.

### Consequences accepted

- **A half-configured app can look broken.** A consumer referencing an unconfigured service
  starts and then fails on first call. This was chosen deliberately over refusing to start
  anything: being able to work on the eight services that *are* configured is worth more than
  protecting against a confusing error on the two that are not. The dashboard row naming the
  service "Not configured" is what makes the cause discoverable.
- **A typo'd key in `servicesources.local.json` now degrades instead of erroring.** Writing
  `"payment"` where `"payments"` was meant produces a placeholder rather than an exception. The
  dashboard shows it plainly, but the loud failure is gone. Accepted as the cost of the picker
  also covering services added to the catalog later.
- **Two runs to first light.** Inherent, per the findings above.

## Testing

The dialog itself needs a live AppHost and a dashboard client, so the logic is kept out of it:

- Placeholder construction: an unconfigured service yields a registered resource of the expected
  type and state, carries the annotation, and is absent from the prefetch set.
- Strict mode: with `SERVICESOURCES_INTERACTIVE=0`, or `CI` set and no override, an unconfigured
  service throws the current exception with the current message.
- Override precedence: the four combinations of `CI` and `SERVICESOURCES_INTERACTIVE`.
- Source-option derivation: registering an additional source makes it appear in the choice list.
- Config merge round-trip: writing a chosen source into a file with existing entries preserves
  them, and the result reloads through `DeveloperConfigLoader` unchanged.
- Consumer safety: a resource that references an unconfigured service composes and the
  application starts.
- State durability: the placeholder's custom state and property are still present after the
  application has started and DCP has published its own updates — the microsoft/aspire#13647
  check noted above.

One thin integration test covers that the subscription raises an interaction when placeholders
exist and raises none when they do not.

## Out of scope

- Changing the source of an already-resolved service — the rest of #70.
- A dashboard command to stop the AppHost after saving. Worth revisiting when upstream hot reload
  (microsoft/aspire#13839) lands, since a dashboard session that survives the restart is what makes
  it worth having.
- Moving persistence onto `IConfiguration` / user secrets — #69.
- Progress reporting during checkouts — #130 / #136. Note that deferral is what makes this
  tractable at all: a checkout that runs before `Build()` has nowhere to report to, since the CLI
  never renders AppHost console output, while one that runs after the dashboard is up can publish
  real resource state. Nothing here needs to solve it.
