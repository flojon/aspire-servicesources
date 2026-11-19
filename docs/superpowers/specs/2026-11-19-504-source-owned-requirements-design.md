# One source-owned requirements model for the picker, the validator and Resolve (#504)

**Date:** 2026-11-19
**Status:** Approved (2026-11-19; the human decided every question in section 7)
**Resolves:** #504 (and decides #495's `IsOfferedFor` and #497's `CanResolve` together with it).
**Out of scope:** #497's already-fixed items (`SourceProbe` exception handling, `RequireKubernetesBlock`
returning its block with `!`, the "a, b or c" remedy text), #513, validating a catalog `path:` against the
filesystem in the picker (Resolved Question 1).
**Measured against:** reading the repo at `origin/main` `daa515b`. Nothing here was run; the plan's tests
re-check the claims made here.

## 1. Problem and what the code does today

What a source needs before it resolves is written down in five places that are kept in step by hand:

| Fact | Where it is written today |
|---|---|
| Which settings the dialog asks for | `SourceFollowUp.FieldsFor`, a `switch` on the source's *name* |
| Whether the picker offers the source | `IServiceSource.IsOfferedFor` (default `true`; overridden by `LocalProjectSource`, `ContainerSource`, `KubernetesSource` only) |
| Whether an error message may suggest switching to it | `IServiceSource.CanResolve` (default `false`; overridden by `ContainerSource`, `KubernetesSource`, `UrlSource`) |
| What the save validator checks | `PickerAnswers.ThrowIfUnresolvable`, a `switch` on the source's name that calls `UrlSource.ResolveUrl`, `KubernetesSource.Validate`, `PathSource.ResolveOverride` |
| What `Resolve` refuses | each source's own `Resolve` |

Consequences, all present in the code:

- A new required setting on a source is not asked for by the dialog and not checked by the validator. The
  save succeeds and the next AppHost start throws. (`ContainerSource` and `LocalProjectSource` are absent
  from `ThrowIfUnresolvable` entirely, so a container or repository answer is never resolve-checked.)
- `ContainerSource.TryGetImageAndPort` / `ContainerConfigProblem` repeat the image, port-present and
  port-range checks of `ResolveContainerConfig`; only "None or not" is ever read.
- `"repository" or "path" or "disabled"` is written in `LocalProjectSource.SourceSwitchRemedy` and again as
  `neverOffered` in `LocalProjectSourceTests`, beside `SourceChoices.DisplayOrder`.
- `CanResolve` defaults to `false`; the only guard is a reflection test (`EveryRegisteredSource_OverridesCanResolve_...`),
  and "`CanResolve` implies `IsOfferedFor`" is tested only on hand-picked samples.
- `PickerAnswers.Validate` (run on the dialog answers, `SaveOneAsync`) and `PickerAnswers.ValidateEntry` (run
  by the writer on the merged file entry) each match the offered source and run `CheckSection`, so a save
  validates twice. `Validate` rejects a source that is not offered; `ValidateEntry` falls back to the raw
  source name with `?? source`.

## 2. Decisions

The human confirmed on 2026-11-19 only the ground rules: `IServiceSource` is internal, `IsOfferedFor` and
`CanResolve` are in the unreleased 0.9.0, and breaking changes to it, including abstract members that force
an edit to every source, are acceptable. The decisions below were proposed by the spec author; the human approved them, with the answers recorded in section 7.

### D1. One method on `IServiceSource` returns the requirements; the two predicates are removed

```csharp
SourceRequirements Requirements(SourceInput input);   // abstract: no default
bool IsSwitchRemedy { get; }                          // abstract: no default
```

`IsOfferedFor` and `CanResolve` are **deleted from the interface**, not kept as defaulted members. They
become two read-only properties of the returned value (D2), so there is nothing a source can forget to
override and nothing for them to drift against. `DeclaredResourceType` is unrelated and unchanged.

```csharp
/// What a source is asked about. Resolve's own inputs, minus the builder.
internal sealed record SourceInput(
    string ServiceName, ServiceDefinition Definition, ServiceDeveloperConfig Config,
    string AppHostDirectory, RepositoryDeveloperConfig? RepositoryConfig = null);
```

`RepositoryConfig` is there because `PathSource.Resolve` lets a group's `path` entry supply a service's
directory (`GroupPathSource.Redirects`), so "no directory declared" is only a requirement when no redirect
applies. Callers with no group in hand (the picker, today) pass `null`, which is what they effectively do now.

### D2. The model: fields, a problem, and two derived views

```csharp
internal sealed record SourceRequirements(IReadOnlyList<FollowUpField> Fields, SourceProblem? Problem)
{
    public bool Resolves => Problem is null;                       // was CanResolve
    public bool Offered  => Problem is null                         // was IsOfferedFor
        || (Problem.MissingSetting is { } key && Fields.Any(f => f.Key == key));
}

internal sealed record SourceProblem(ServiceSourcesConfigurationException Exception)
{
    public string Message => Exception.Message;
    public string? MissingSetting => /* read from Exception.Data, see D4 */;
}
```

- `Fields` are the settings the dialog must still ask for, given the catalog entry (what `FieldsFor` returns
  now, `FollowUpField` unchanged). It depends on `Definition` only.
- `Problem` is the **first** reason `Resolve` would refuse the entry as it stands, with no side effect and
  no exception escaping. Its message is the one `Resolve` throws, because it is that exception.
- `Resolves` means "works as it stands" (the `CanResolve` meaning). `Offered` means "the picker may offer
  it": it resolves, or the only thing wrong is a setting the dialog asks for. That is the `IsOfferedFor`
  meaning, now a consequence of the model: an entry the dialog could repair is offered; an entry whose
  catalog value is itself unusable (`container.port` of 70000, a catalog `url` with an `ftp` scheme) is not,
  because no answer the dialog collects replaces a catalog value.
- `Resolves` implies `Offered` by construction (`Problem is null` is the first disjunct). This replaces the
  hand-sampled `EverySourceThatCanResolve_IsAlsoOfferedFor` test.

**Ordering rule for sources.** Because `Problem` is only the first problem, a `Check` must raise problems
that no dialog answer can fix (catalog facts: a missing `container.image`, an unsupported catalog scheme)
before it raises a missing *askable* setting. Otherwise `Offered` would say yes to an entry that still fails
after the answers are in. A catalog fact that can only fail *after* an askable setting (a catalog
`kubernetes.port` of 70000 with no `context` yet) is still offered, exactly as today; the save-time check
reports it once the answers exist. The model does not claim to be exact there.

### D3. `Resolve` cannot skip the requirements: a template base class

```csharp
internal abstract class ServiceSource<TChecked> : IServiceSource
{
    protected abstract IReadOnlyList<FollowUpField> FieldsFor(ServiceDefinition definition);
    protected abstract TChecked Check(SourceInput input);   // throws ServiceSourcesConfigurationException
    protected abstract IResourceBuilder<ServiceResource> Build(
        IDistributedApplicationBuilder builder, SourceInput input, TChecked checkedValues);
    public abstract bool IsSwitchRemedy { get; }

    public sealed IResourceBuilder<ServiceResource> Resolve(builder, serviceName, definition, config, repositoryConfig)
        => Build(builder, input, Check(input));

    public SourceRequirements Requirements(SourceInput input) =>
        new(FieldsFor(input.Definition), SourceProblem.Capture(() => Check(input)));
}
```

Every one of the six sources derives from it, so the compiler enforces: a source that omits `Check`,
`FieldsFor`, `Build` or `IsSwitchRemedy` does not build; `Resolve` is `sealed`, so a source cannot bypass
`Check`; `Requirements` runs the same `Check` `Resolve` runs. "The dialog and the validator cannot disagree
with `Resolve`" is then true by construction for everything in `Check`, not by a test that compares copies.

`TChecked` is whatever `Check` computed that `Build` needs, so nothing is parsed twice: container's
`(image, tag, port, scheme)`, URL's `Uri`, kubernetes' `(scheme, context, remotePort, block)`, path's resolved
override directory, and an empty `NoValues` struct for `repository` and `disabled`.

`IServiceSource` stays (the registry type and the test seam); the base class is a helper. The alternative,
keeping each source's `Resolve` hand-written and calling its own `Check` by convention, was Resolved Question 2 (decided: base class).

### D4. How a source says "this setting is missing" without a new public member

`Check` keeps throwing `ServiceSourcesConfigurationException` through the existing helpers
(`ResolveUrl`, `ResolveContainerConfig`, `ResolveForwardTarget`, `EndpointScheme.Resolve`, ...), which stay
the single copy of each rule and keep their current direct callers in tests. A helper that throws because a
*setting is absent* tags the exception with the key it is missing:

```csharp
// internal, beside AsUnsettledResourceType; no PublicAPI change
internal ServiceSourcesConfigurationException AsMissingSetting(string key);
internal string? MissingSetting { get; }     // reads Data["ServiceSources.MissingSetting"]
```

The key is the same string a `FollowUpField.Key` uses (`"kubernetes:context"`). A missing setting no dialog
field covers (`container:image`, `kubernetes:service`, `repository:url`) is tagged too, and is simply not in
`Fields`, which is what makes the source not offered. `SourceProblem.Capture` catches **only**
`ServiceSourcesConfigurationException`; anything else is a bug and propagates (see D7 for the two call
sites that must not let it escape).

The tag holds a constant key, never a user value, so it cannot carry a secret into a log.

### D5. The container rules have one copy

`ContainerSource.TryGetImageAndPort` and the `ContainerConfigProblem` enum are deleted.
`ResolveContainerConfig` (image present, port present, port in range) is the only copy; it tags the first two
as missing settings (`container:image`, `container:port`, neither askable). `Check` is
`ResolveContainerConfig` followed by the scheme resolution already in `Validate`; `Build` is today's `Resolve`
body after validation. `ContainerSource.Validate`, `IsOfferedFor` and `CanResolve` go.

### D6. The picker's exclusions become data: `IsSwitchRemedy`

`IsSwitchRemedy` means "an error message about another source may suggest switching to this one".

| Source | `IsSwitchRemedy` | Why |
|---|---|---|
| `container`, `url`, `kubernetes` | `true` | They are today's `CanResolve`-overriding sources and the ones `SourceSwitchRemedy` lists. |
| `repository` | `false` | It is the source whose message is being written. |
| `path` | `false` | It has its own remedy sentence (`PathSourceRemedy`), which explains `path.path`. |
| `disabled` | `false` | Turning a service off is not a fix. |

`SourceSwitchRemedy` loops `SourceChoices.DisplayOrder`, skips `!source.IsSwitchRemedy`, and keeps those whose
`Requirements(input).Resolves`. The `name is "repository" or "path" or "disabled"` test and the test's
`neverOffered` array are deleted; the test reads the flag. `DisplayOrder` stays in `SourceChoices` as the one
ordering list (it is order, not an exclusion, and `DisplayOrder_CoversEverySource` keeps it complete).
Putting order on the source is not worth a second abstract member.

### D7. Where an unexpected exception is contained

`SourceProbe` today swallows *any* exception because the probe runs inside another error's construction.
That stays, but at the two call sites that evaluate **other** sources, not inside each source:

```csharp
SourceProbe.Evaluate(IServiceSource source, SourceInput input)   // never throws
    // returns source.Requirements(input), or SourceRequirements.Unknown on any other exception:
    // no fields, Problem = "could not tell", so neither Resolves nor Offered.
```

Used by `SourceSwitchRemedy` (the remedy text of `LocalProjectSource`) and by `SourceChoices.For` (the picker's
offer list, which builds `new SourceInput("", definition, new ServiceDeveloperConfig(), "")`: with an empty
config no `Check` reaches the filesystem, and the service name only appears in a message nobody reads), so a validator bug costs one source its place in a list, not the dialog or the diagnostic.
`PickerAnswers`, which validates the one source the developer chose, calls `Requirements` directly and keeps
its current behaviour: an unexpected exception reaches `SaveOneAsync`'s catch and is reported as a failed
save.

### D8. `repository` and `path`: what `Check` covers

`Check` covers every configuration check that is a pure function of `SourceInput` and the filesystem under
`AppHostDirectory`: no builder, no network, no process, no git.

- `LocalProjectSource.Check` is `RequireRepositoryToCheckOut` (already the first thing `Resolve` does). It
  tags the missing repository as non-askable. Its remedy text probes the `IsSwitchRemedy` sources through
  `SourceProbe.Evaluate`. A flagged source's `Check` must never itself build that remedy; only `repository`
  does, and it is not flagged, so the probe cannot recurse.
- `PathSource.Check`: a `path.path` override is resolved against `AppHostDirectory` and must exist
  (`ResolveOverride`, the check the picker already makes); with no override, a blank catalog `path:` and no
  group redirect is the missing setting `path:path`, with `RequireCatalogPath`'s message. The override's
  resolved directory is the `TChecked`, so `Build` does not resolve it twice. Everything that needs the
  builder (`ValidateCatalogPath` reads `ConfiguredRepositoryRoot(builder)`), the kind handler and the project
  file stays in `Build` and is **not** a requirement; neither is the group directory a redirect supplies
  (`GroupPathSource.ResolveDirectory`). That is the one place the model does not reach, and it is
  stated rather than hidden (Resolved Question 1).
- `DisabledSource`: `FieldsFor` empty, `Check` returns `NoValues`.

### D9. `PickerAnswers` has one path

```csharp
public static string? Validate(string service, string source, JsonObject entry,
    ServiceDefinition definition, string appHostDirectory);
```

`ValidateEntry` is deleted and `Validate`'s fields-dictionary form with it. The one path:

1. Match `source` against `SourceChoices.For(definition)` ignoring case. No match is a rejection
   (`Source '...' is not available for service '...'`), in the save flow too, which `ValidateEntry` used to
   skip with `?? source`. The message is built through `Raw`/`Name` rather than interpolating the answer as a
   bare string, since the source name comes from the dialog.
2. Bind the entry through the same `ConfigurationRoot` the file would load as, run
   `DeveloperConfigValidator.ValidateAll`, bind `ServiceDeveloperConfig`.
3. `Requirements(new SourceInput(...)).Problem` is the result; nothing is switched on the source's name.

`SaveOneAsync` stops calling `Validate` on the bare answers and no longer sets `fields` aside for a second
check: the writer's `validateMerged` callback is the only validation of a save, and it validates the merged
entry, so settings already in the file count (a `url.url` already saved satisfies the requirement). The
"required field missing" message of the old pre-check is replaced by the source's own `Check` message (for
example "source 'kubernetes' requires 'kubernetes.context' in servicesources.local.json."). The dialog's own
`Required` flag still stops an empty required field before anything is written.

The dialog reads `SourceChoices.For(definition)` for its options and `Requirements(input-with-empty-config).Fields`
for its follow-up fields; `SourceFollowUp` is deleted (its `FollowUpField` record moves beside
`SourceRequirements`).

## 3. Source changes at a glance

| File | Change |
|---|---|
| `IServiceSource.cs` | Add `Requirements`, `IsSwitchRemedy`; remove `IsOfferedFor`, `CanResolve`. |
| new `ServiceSource.cs` | `ServiceSource<TChecked>`, `SourceInput`, `SourceRequirements`, `SourceProblem`, `NoValues`. |
| `ServiceSourcesConfigurationException.cs` | `AsMissingSetting`, `MissingSetting` (internal). |
| `ContainerSource`, `KubernetesSource`, `UrlSource`, `DisabledSource`, `PathSource`, `LocalProjectSource` | Derive from `ServiceSource<T>`; `Resolve` splits into `Check` and `Build`; predicates and their private copies deleted. |
| `SourceChoices` | `For` reads `Offered` through `SourceProbe.Evaluate`; `DisplayOrder` stays. |
| `SourceFollowUp` | Deleted. |
| `PickerAnswers` | One `Validate`; `ThrowIfUnresolvable`, `ValidateEntry`, `CheckSection`'s source switch removed. `ShadowingProvider` untouched. |
| `UnconfiguredPrompt` | Reads fields from `Requirements`; one validation. |
| `SourceProbe` | `Resolves(Action)` becomes `Evaluate(source, input)`. |
| `ServiceSourcesBuilderExtensions` | Registry unchanged; its summary comment on the six sources is the only edit, if any. |

`IBackingServiceSource` is a separate interface that does not implement `IServiceSource`; it is untouched.
`UnconfiguredSource` is not an `IServiceSource` (it says so) and is untouched. Every source changes: six
files, which is what the human accepted.

## 4. Attack surface and trust boundaries

- **The source name** arrives from the dashboard dialog and is matched against the offered list before any
  dictionary lookup (D9 step 1); a forged or stale name is rejected and never indexes `Sources`.
- **Answers are written to a file the next AppHost start loads.** The one validation now runs over the merged
  entry the file will contain, through the same binder and shape validator the load uses, so what is
  validated is what is loaded. Before, the dialog answers alone were checked first and the merged entry
  second with a differently-matched source.
- **`Requirements` runs validators for all six sources whenever the picker opens** (to compute `Offered`),
  and for the flagged sources inside an error's construction. Rule: `Check` has no side effect: no builder
  mutation, no port allocation (kubernetes' allocation stays in `Build`), no network, no process, no git, no
  write. The only I/O is the read-only existence probe of a developer-typed `path.path` under
  `AppHostDirectory`, which the picker already did on save; it runs only when a `path.path` is set, never
  for the empty config the offer list uses, and `path` is not `IsSwitchRemedy`, so an error message never
  triggers it.
- **Messages carry user values** (a URL, a path). They are the same exceptions `Resolve` throws today, built
  through `ServiceTextHandler`, so the existing escaping and `UrlSource.Redacted` credential redaction apply
  unchanged to the dialog and logs. `MissingSetting` is a constant key. Nothing new quotes a value.
- **A source added later** (the #495 plan mentions third-party sources, #54; `IServiceSource` is `internal`
  today, so nobody outside this assembly can add one) gets the same guarantees from the base class, but would run its `Check`
  inside the picker and error messages; the "no side effect" rule is documented on `ServiceSource<T>.Check`
  and is not machine-checked (Resolved Question 3).

## 5. Tests

New classes carry the trait their body earns (CLAUDE.md): `IO` where a test creates directories or builds a
host, none for pure ones; no `Timing` or `Concurrency` is expected.

1. **Closed-loop test, every source** (checklist item 3). A sample matrix of definitions (full, bare, each
   source's catalog value blank / invalid / valid) crossed with the empty config. For each registered source
   and sample where `Requirements.Offered`: answer every `Fields` entry from a sample-value table keyed by
   field key, bind the answers as a config, and assert `Problem is null`. A source whose `Check` requires a
   setting the dialog does not ask fails here. A field key with no sample value fails with "add a sample
   value", so a new field cannot slip past.
2. **Field keys are real config keys.** For every `Fields` entry, setting its key in a JSON entry and
   binding `ServiceDeveloperConfig` passes `DeveloperConfigValidator`, so a field cannot name a path that
   binds to nothing.
3. **`Resolves` implies `Offered`, over all sources** (checklist item 7): every registered source, every
   sample definition crossed with a set of configs (empty; each source's block filled; invalid values). By
   construction this holds, so the test pins the construction (`Offered` must keep its `Problem is null`
   disjunct), not a hand-picked sample.
4. **Compile-time contract.** No test: omitting `Check`, `FieldsFor`, `Build` or `IsSwitchRemedy` is a build
   error. The spec's evidence is a throwaway local edit that removes one and observes `CS0534`, recorded in
   the PR.
5. **`IsSwitchRemedy` is pinned**: exactly `container`, `kubernetes`, `url`; and every name in
   `ServiceSourcesBuilderExtensions.Sources` is in `DisplayOrder` (existing test).
6. **Agreement with `Resolve`, for the sources that resolve without side effects** (`url`, `container`,
   `kubernetes` with a fake port allocator, `disabled`): over the samples, `Problem.Message` equals the
   message `Resolve` throws, and `Problem is null` equals "`Resolve` returns". `repository` and `path` reach git
   and the filesystem in `Build`, so they rely on `Resolve` being `sealed` over `Check` (D3).
7. **`SourceChoicesTests` stay as the oracle** for `Offered`: every existing case keeps its expectation
   (`For_AnyDefinition_AlwaysOffersUrlPathDisabled`, `For_InvalidContainerBlock_OmitsContainer`,
   `For_FullyDeclaredDefinition_OffersAllSix`, ...). New cases: a catalog `url` with an `ftp` scheme is not
   offered; a catalog `kubernetes.scheme` the source rejects is not offered.
8. **`SourceFollowUpTests` move to `SourceRequirementsTests`**, asserting `Fields` and the missing-setting key
   per source with the same expectations as today.
9. **`PickerAnswersTests`**: the `Validate` cases are re-expressed over an entry; added: a not-offered source
   is rejected whether the entry came from the dialog or the merged file; a setting already in the file
   satisfies a requirement; the source name in the rejection is escaped.
10. **One validation per save** (checklist item 2): a recording `IDeveloperConfigWriter` in
    `UnconfiguredPromptTests` shows the prompt validates nothing before `SaveAsync` and passes a single
    `validateMerged` callback; with the real writer a bad answer yields exactly one failure message.
11. **`LocalProjectSourceTests`**: the two reflection and sample tests are replaced by tests 3 and 5; the
    remedy-text tests keep their expected strings (the three-source list is unchanged because the flagged
    set and the `Resolves` outcomes are).

## 6. Compatibility and release notes

`IServiceSource` is `internal`; `PublicAPI.Shipped.txt` / `Unshipped.txt` take no edit and the build's
`RS0016`/`RS0017` confirm it. `AsMissingSetting` / `MissingSetting` are `internal`. `IsOfferedFor` and
`CanResolve` were added in the unreleased 0.9.0, so nothing shipped changes.

Developer-visible behaviour changes, to be written into `CHANGELOG.md` (the plan amends the existing
unreleased picker entries rather than adding a new one, if they exist; the entries reference #495 and #497
only by number today):

- The picker no longer offers a source whose *catalog* value is unusable (a `url` whose scheme is not http or
  https, a catalog `kubernetes.scheme` the source rejects). It used to offer it and reject the save.
- A save rejected for a missing setting says what `Resolve` would say, not the dialog's
  "'kubernetes:context' (Kubernetes context) is required for source ..." line.
- A source name the picker did not offer is rejected on the merged-entry path as well.
- A `container` or `repository` answer is now resolve-checked at save, like `url`, `kubernetes` and `path`.

Message precedence when an entry has two problems can change by one step: `Check` runs first in `Resolve`, so
a missing required setting is reported before a kind or project error that used to come first in
`PathSource.Resolve`. The plan lists the existing tests that pin an order and keeps them green or updates
them with the reason.

## 7. Resolved Questions

The human decided each of these on 2026-11-19.

1. **`path`'s catalog `path:` is not validated in the picker in this ticket.** The picker checks only a typed
   `path.path`; the catalog value's confinement and existence stay in `Build` (they need
   `ConfiguredRepositoryRoot(builder)`). A follow-up may add a repository-root resolver to `SourceInput`. Until
   then a `path` answer for a service whose catalog directory is missing still saves and fails at the next start.
2. **Template base class (D3).** `ServiceSource<TChecked>` with abstract `FieldsFor`/`Check`/`Build`/`IsSwitchRemedy`
   and a sealed `Resolve`.
3. **A documented "no side effect in `Check`" rule is enough** (D8, section 4). No type-level purity requirement.
4. **The missing-setting wording is `Resolve`'s**, not the dialog's label-based message. The dialog's own
   `Required` flag still stops an empty required field before anything is written.
5. **Group-config for grouped `path` members stays out of scope.** `SourceInput.RepositoryConfig` is accepted but
   the picker passes `null`, so a grouped member under a `path` group is still asked for `path:path`. This is not
   a regression.
6. **Message precedence changes for entries with two problems are acceptable.** A test that pins the old order is
   updated, and the reason is stated in the commit that updates it.
