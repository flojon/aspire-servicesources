# Aspire.Hosting.ServiceSources — Interactive Source Picker for Unconfigured Services

**Date:** 2026-08-28
**Status:** Implemented. Reconciled with main on 2026-10-18 (see *Since this was
written*).
**Complements:** GitHub issue #70 (per-service "Change source" command), which covers services
that already resolved. This design covers the case #70 does not reach — a service with no source
at all — and reuses #70's dialog machinery to do it.
**Depends on:** #62 (every source returns a real registered resource) — merged, and the hard
prerequisite #70 named.
**Related:** #130 / #136 / #216 made deferred checkout the default — they defer the checkout of a
service whose source is already known, where this defers the *choice*; #271 / #428 added the
`BeforeStartEvent` prompt that picks among *declared* cold checkouts, persisted in
`.servicesources/selection.json`; #134 (code-authored catalog) and #69 (developer config through
`IConfiguration`) have merged and change where this design reads and writes; #8 (CLI configuration
UX) is served by the same `PromptInputsAsync` implementation; #43 (per-source field validation) is
reused rather than duplicated; #54 (third-party sources, still open) would be picked up by
deriving the source list from the source dictionary once that can be extended.
**Corrected by:** the review on PR #128 — an earlier draft claimed a checkout could never precede
a prompt within one run (see *The prompt cannot precede a type choice within a single run*), raised
the prompt from an event that deadlocks behind `WaitFor` (see *Interactions raised at startup*),
and described `CI` and `SERVICESOURCES_INTERACTIVE` as new when #428 already owns them.

## Since this was written

- **#76 is closed and deferred checkout is the default (#216, #404).** Nothing clones before the
  AppHost has started, so the cost this design originally also set out to remove is gone. The
  remaining motivation is the first-run failure below.
- **#428 shipped a pick among declared services.** A `BeforeStartEvent` prompt asks which cold
  checkouts to run, skips the rest and remembers the answer in `.servicesources/selection.json`.
  It cannot help a service that has no source at all, because `AddService()` throws before the
  prompt exists. This design covers that gap; the two prompts must read as one flow, not two.
- **#134 and #69 have merged**, so *Persistence and restart* was re-derived against them
  (2026-10-18); the "two config files read directly" description it started from no longer holds.
- **`UseDeferredCheckout()` and `UseJava()` are now obsolete no-ops**, so the opt-in family named
  below is not a family to join.

## Motivation

A developer cloning an AppHost repository for the first time has no `servicesources.local.json`.
Today that is a hard error, one service at a time:

```
Service 'orders' was not found in 'servicesources.local.json'.
```

There is nothing in the product to tell them what the file should contain, and the AppHost never
reaches the dashboard, so the dashboard cannot help. They read the README, hand-write JSON, and
run again.

The original draft also counted the eager clone of every `"repository"` entry (#76) as a motivation.
That is resolved by deferred checkout, and #428 lets the developer decline services at start. What
remains is that choosing a source is still a file the developer must author before anything runs;
this design makes it a question answered in the dashboard.

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
`repository` produces a `ProjectResource`, `container` a container, `url` nothing at all — so for a
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
| | Source *type*: `repository` / `container` / `url` / `kubernetes` / `path` |

This is the finding #130 acted on; deferred checkout is now the default (#216). It is the reason
this section must not be read as a blanket impossibility — it had already been cited as one.

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

### Interactions raised at startup do not block startup, and they wait — from the right event

`IInteractionService` resolves once the application is built. A `PromptInputsAsync` started and
deliberately not awaited leaves the application free to finish starting — verified:
`Distributed application started` was logged while the prompt was outstanding. The prompt then
stays pending until a dashboard client answers it, rather than failing because no browser was
attached at the moment it was raised.

**The event matters, and the first draft chose the wrong one.** It raised the prompt from
`AfterResourcesCreatedEvent`, and that experiment had no consumer waiting on the service. That
event sits behind the whole `WaitFor` graph: a resource with an unsatisfied wait is not created
until the wait resolves, and a placeholder never reaches `Running`. A consumer that did
`WaitFor(orders)` would keep the event from firing, so the prompt that could unblock it would never
appear. #428 hit the same trap and moved to `BeforeStartEvent`, running the dialog on a task nobody
awaits (`DeferredCheckout`, the comment above its subscription); this design does the same. The
placeholder must also not hold a consumer back, and that mechanism is **not yet verified for a
registered resource**. `url` and `disabled` get it from `UnregisteredServiceStartupGuard`, which strips
the consumer's `WaitAnnotation` at `BeforeStartEvent` because their facade is unregistered
(`ServiceResource` deliberately omits `IResourceWithoutLifetime`, as the marker would also suppress
`WaitFor` on `container`/`kubernetes`/`repository`). The placeholder is registered, so either it
carries `IResourceWithoutLifetime` — which should make Aspire ignore the wait, to be measured — or the
guard's source list grows to include it. Both halves are needed: the event choice keeps the prompt
reachable, and one of these keeps the consumer starting. The wait-safety test below pins whichever is
chosen.

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

Called before the first `AddService()`. `UseJava()` and `UseDeferredCheckout()` are obsolete, so
the name is settled on its own merits against the surviving `Use*` methods. Without it, a service
missing from `servicesources.local.json` throws exactly as it does today, so the change is
non-breaking for every existing consumer. The opt-in exists only because the placeholder has to be
decided during composition, before `IInteractionService` exists; #428's source-pick prompt needs no
opt-in because it runs after the application is built.

**The `CI` and `SERVICESOURCES_INTERACTIVE` rules are not new: `InteractivePrompts`
(`DeferredCheckout.cs`) already owns them for #428's prompt, and this design reuses that one
decision.** One setting therefore governs both prompts, so `SERVICESOURCES_INTERACTIVE=false`
silences the source-pick prompt and turns the placeholder back into the exception together. That
is intended: a CI job that opted out of one question should not be asked the other.

| `SERVICESOURCES_INTERACTIVE` | Behaviour after `UseInteractiveServiceConfiguration()` |
|---|---|
| unset or unrecognised | Interactive unless `CI` is set |
| `1` / `true` / `yes` | Interactive regardless of `CI` |
| `0` / `false` / `no` | Strict regardless of `CI` |

`CI` counts as set when it is anything but empty or a false value (`false`, `0`, `no`). The override
has no effect without the opt-in — nothing changes for an AppHost that never called it — and none in publish
mode, where a manifest cannot describe the placeholder and a missing source keeps failing fast.

Strict mode is today's behaviour verbatim: `ServiceSourcesConfigurationException` naming the
service and the file.

### The placeholder resource

An unconfigured service resolves to a registered resource behind the same `ServiceResource` facade
every other source returns (#313), so the `AddService("orders")` line compiles and runs, and the
configuration calls chained off it (`WithEnvironment`, `WithReference`, `WaitFor`) are skipped with a
warning or ignored rather than failing. Two calls are the exception, below:

```csharp
internal sealed class UnconfiguredServiceResource(string name)
    : Resource(name), IResourceWithServiceDiscovery, IResourceWithoutLifetime;
```

registered with `WithInitialState` and handed to `ResolvedService.Bridge` under the source name
`"unconfigured"`, which is out of band (`Reachability.OutOfBandSources`), so `WithEnvironment` and
similar calls on the facade are skipped with a warning. The registered resource, not the facade, is
what the dashboard shows:

- `ResourceType = "Service (unconfigured)"`
- `State = new ResourceStateSnapshot("Not configured", KnownResourceStateStyles.Info)`
- a short `Reason` property naming the configuration key to set — no file path, and nothing that
  assumes how the source gets chosen, because the `Configure` command (below) changes that. The full
  `NotConfiguredError` text, near-miss hints included, goes to the log once instead.

It has no endpoints and starts no process. `ServiceSourceAnnotation(name, "unconfigured")` is how
the rest of the package recognises it, and a `Configure` command (below) is added to it.

Critically, an unconfigured service is **not** a `"repository"` service, so `LocalCheckoutPrefetch`
never sees it and nothing is cloned for it. This is the mechanism by which the design stops
paying for repositories the developer never chose.

Because the placeholder has no endpoints, `GetServiceEndpoint()` and `Unwrap<T>()` on it still
throw at composition. Their messages say that no source has been chosen rather than that the
service "runs out of band"; making them tolerant instead is left for later, since silently
returning a dangling endpoint reference is worse than a clear stop.

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
checkout would occupy, using the deferred-checkout mechanism that is now the default. If the
developer then answers `repository` with a managed checkout, it clones and starts in the same run —
no restart at all.

It is recorded as an option rather than the recommendation because of what it costs when the guess
is wrong. Any other answer — `container`, `url`, `kubernetes` or `disabled` — leaves a phantom
`NotStarted` project resource in the dashboard for a service that is not a project, and a self-managed
`path` answer both leaves one and still needs a restart because it changes the frozen path. It
trades dashboard clarity for a faster first run, and it only ever helps `kind: dotnet` (the default
kind, which requires a `project`). Worth
revisiting now that deferred checkout has settled in practice.

### Push, then pull

**Push.** One subscription to `BeforeStartEvent`, beside #428's, never `AfterResourcesCreatedEvent`
(see *Interactions raised at startup*). If any placeholders exist and `IInteractionService.IsAvailable`,
it starts — without awaiting — a single `PromptInputsAsync` carrying one `InputType.Choice` input per
unconfigured service. Asking for every service in one form is what keeps a ten-service fresh clone to
one dialog instead of ten.

The choice list is derived from the source dictionary `AddService` resolves through, which is a
private, closed set today: `repository`, `container`, `url`, `kubernetes`, `path` and `disabled`.
`disabled` is the way to say "I don't want this service" and stops it being asked about again. A
third-party source (#54, still open) would appear without touching this code once that dictionary
can be extended; until then there is nothing to derive beyond those six.

**The list is filtered per service, not offered whole.** A source is only a valid answer where the
service's catalog entry declares what it needs, and picking one without it would throw at the next
run — the failure this design exists to remove. The rules, read off each source's `Resolve`:

| Source | Offered when |
|---|---|
| `repository` | the catalog entry names a repository to clone (`LocalGitCheckout.HasRepositoryToClone`) |
| `container` | it has a `container` block with an `image` and a `port` (`ContainerSource.ResolveContainerConfig`) |
| `kubernetes` | it has a `kubernetes` block naming a `service` (`KubernetesSource.RequireKubernetesBlock`) |
| `url`, `path`, `disabled` | always — a developer's own `url`, `path` or nothing at all is enough |

`AddService`'s `DeclaredResolutions` cannot be reused for this, which an earlier draft claimed. It is
private; it keeps only sources whose `DeclaredResourceType` is non-null, so `url` and `disabled`
(which do not override it) never appear; and `repository` and `path` always answer, since a
developer's own path makes them selectable, so they never filter. It answers "what type would this
resolve to if chosen", not "can the picker offer it". The picker needs its own per-source predicate,
a default interface method beside `DeclaredResourceType`, so a third-party source (#54) states its own
precondition instead of being hard-coded here.

A service whose chosen source needs more than its name then gets a short follow-up
`PromptInputsAsync`, but far fewer sources need one than the first draft assumed. What a *developer*
may set per source is fixed by the config types (`ServiceDeveloperConfig` and its blocks): `path`
requires a `path` unless the catalog entry has a `path:`; `url` requires a `url` when the catalog
entry has none (`UrlSource.ResolveUrl` throws otherwise) and otherwise only overrides it; `repository` takes an optional `ref`
and `path`; `container` takes only an optional `tag` — its image comes from the catalog, so it is not
asked for; `kubernetes` requires a `context` always and a `port` when the catalog entry has none
(`KubernetesSource.BuildPortForwardArgs` throws otherwise), and takes optional `namespace` and
`scheme`; `disabled` takes nothing. The common answer — `repository`, `container`, `disabled` —
therefore needs no follow-up at all, while `kubernetes` always has one. This two-step shape is forced: `PromptInputsAsync` takes a fixed input list, so a field cannot
appear in response to an earlier answer in the same form. #70 weighed the alternative — every field at
once, with `ValidationCallback` rejecting mismatches — and judged sequential prompts read better and
behave correctly in the CLI; this design follows that. Validation reuses the #43 rules: the raw
answers are loaded into an in-memory configuration section for the entry and run through
`DeveloperConfigValidator`, so no typed object has to be built from a half-filled form.

If `IsAvailable` is false, nothing is prompted and the AppHost logs one warning naming every
unconfigured service and the key to set for each.

**When nobody answers.** #428's prompt starts every undecided service and saves nothing when it times
out, is closed, or cannot be shown. The same fallback is meaningless here, because a placeholder has
nothing to start. So this prompt has no deadline — it stays pending until answered or the host stops,
which is the behaviour measured earlier — and a closed dialog leaves the placeholders as they are,
logs one line naming them, and relies on the `Configure` command below as the way back in. It does not
re-raise the dialog.

The two prompts can both appear in one run: after a partial first configuration, the unconfigured
services get this dialog and the configured, undecided cold checkouts get #428's. They answer
different questions and neither can be folded into the other — a placeholder cannot be a checkout
candidate until a source exists, which is next run — so the dialogs are titled by their question. Their order is not controlled: each runs on its own
task from its own `BeforeStartEvent` handler, and handler order is subscription order, which depends
on whether the first deferred service or the first placeholder is added first. Whether Aspire shows
two pending prompts together or one after the other has not been verified here, and should be checked
before this ships. When no service has a source, only this dialog appears; once every service has one,
only #428's can; a partly configured file, or a catalog `defaultSource` on some services, gives both.

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

As first drafted, "unconfigured" meant *present in `servicesources.yaml`, absent from
`servicesources.local.json`*. #134 made the catalog authorable in code and #69 moved developer
config onto `IConfiguration`, so neither half is necessarily a file any more. The concept
survives — a service the AppHost knows about but the developer has not chosen a source for — but
the implementation must read "services that exist" from the catalog and "chosen sources" from
developer config as they now work, and write answers where #69 says developer config lives.

**Re-derived against main (2026-10-18):**

- **"Unconfigured" is one decision point.** `ServiceSourcesConfigCache.ResolveService` throws
  `DeveloperConfiguration.NotConfiguredError` in exactly two cases: no entry for the service, or an
  entry whose `source` is blank. The placeholder replaces that throw when interactive mode is on;
  strict mode keeps it verbatim. A service that is missing from the *catalog* still throws — that is
  an AppHost bug, not something a developer can answer in a dialog.
- **Developer config is a layered `IConfiguration`.** `servicesources.local.json` is only the
  lowest-precedence layer (`DeveloperConfigFileSource`); appsettings, user secrets, environment and
  the command line override it, and a catalog `defaultSource` (#158) sits below the file. "Unconfigured"
  therefore means *no layer* supplied a source. The picker writes to the file because it is the only
  layer this package owns, and must say so when a higher layer would shadow the answer.
- **A catalog `defaultSource` is already a configured service.** It never reaches the placeholder
  path, and `LoadedConfig.DefaultedServiceNames` keeps it out of the eager prefetch.
- **`AddService()` returns a `ServiceResource` facade, never the registered resource (#313).** The
  placeholder is therefore not the returned object: it is the *real* registered resource behind a
  `ResolvedService.Bridge`, like `ServiceContainerResource`, with a new `"unconfigured"` source name
  in the reachability table so `WithEnvironment` / `WithReference` on the facade are skipped with a
  warning, as for `"disabled"`. `DisabledSource` registers nothing; the placeholder must, because it
  carries the dashboard state and the `Configure` command.
- **Keep `NotConfiguredError`'s diagnostics.** Its near-miss notes (misspelled root key, misspelled
  service name) are what make the typo'd-key consequence below tolerable. They are logged as one
  notice when the placeholder is created, not shown in the dashboard property, which stays short and
  free of local file paths.

Answers and #428's `selection.json` are different facts (which source a service uses, versus
which declared services to run this time) and stay in separate stores. The prompts should not be
raised twice: once a service is configured, #428's prompt takes over for it on the next run.

The file written is the one beside this AppHost, which in a linked git worktree is that worktree's own
copy. `WorktreeSeed` copies the main worktree's file in once on first run and never writes back, so
an answer given in a linked worktree does not reach the main one, which asks again in its own time.

**A higher layer can shadow the answer, but only by blanking the key.** An unconfigured service is one
where no layer supplies a non-blank `source`, so a value from appsettings, user secrets or the
environment cannot be in the way. What can is a higher layer that sets the key to blank — the one
gesture configuration offers for dropping a value a layer below set, and the reason
`ResolveService` treats a blank source as absent. Before saving, the picker asks each configuration
provider other than the file's whether it holds `ServiceSources:Services:<name>:source` with a blank
value. If one does, the answer is still saved, and the notification says it will not take effect
until that layer stops blanking the key, naming the provider.

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
  service throws the current exception with the current message; so does publish mode.
- Decision matrix: the combinations of opt-in, `CI` (including `false` and `0`) and
  `SERVICESOURCES_INTERACTIVE` (including an unrecognised value), asserted against
  `InteractivePrompts` so the two prompts cannot drift.
- Source-option derivation: before per-service filtering, the choice list equals the source
  dictionary's keys; the filtered list is what the per-source predicate allows.
- Wait safety: a consumer that `WaitFor`s a placeholder starts, and the prompt is still raised
  (the `AfterResourcesCreatedEvent` deadlock).
- Config merge round-trip: writing a chosen source into a file with existing entries preserves
  them, and the result reloads through `DeveloperConfigLoader` unchanged.
- Consumer safety: a resource that references an unconfigured service composes and the
  application starts.
- Closed dialog and host stop: neither starts anything or re-raises the dialog, and a closed one
  logs one line naming the placeholders. There is no timeout path to test, because the prompt has no
  deadline.
- Blank shadowing: a blank value for the key in a provider above the file produces the warning;
  no such provider produces none.
- Per-service choices: a service whose catalog entry declares no `container` block is not offered
  `container`; none without a repository is offered `repository`; none without a `kubernetes.service`
  is offered `kubernetes`; `url`, `path` and `disabled` are always offered.
- Follow-up prompt: `kubernetes` asks for `context` (and `port` when the catalog has none); `path`
  asks for `path` unless the catalog has one; `url` asks for `url` unless the catalog has one;
  `repository`, `container` and `disabled` ask for nothing.
- `Configure` command: invoking it on a placeholder runs the same prompt and save as the push path.
- Two prompts in one run: a partly configured file raises both dialogs, each for its own services
  only.
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
- Progress reporting during checkouts — #130 / #136 (shipped). Note that deferral is what makes this
  tractable at all: a checkout that runs before `Build()` has nowhere to report to, since the CLI
  never renders AppHost console output, while one that runs after the dashboard is up can publish
  real resource state. Nothing here needs to solve it.
