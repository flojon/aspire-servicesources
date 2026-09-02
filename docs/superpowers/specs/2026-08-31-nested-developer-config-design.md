# Nested per-source developer config

Design for [#161]. Folds into [#157] before it merges.

## The problem

[#157]'s headline is that `servicesources.local.json` can be overridden without editing it: the
file becomes the lowest-precedence layer of the AppHost's own `IConfiguration`, and every standard
provider above it can override an entry. That promise holds when a higher layer changes a field
*within* a source. It breaks on the case a developer most wants it for — switching a service from
one source to another.

`IConfiguration` merges layers per key, not per object. Given a base entry that points a service at
a deployed instance:

```json
{ "services": { "common-auth": { "source": "url", "url": "http://from-local-json.invalid" } } }
```

and a higher layer that switches it back to a local checkout:

```json
{ "ServiceSources": { "Services": { "common-auth": {
      "source": "local",
      "path": "/…/.servicesources/checkouts/common-auth" } } } }
```

the effective entry is `source: local` + `path` + a stale `url`. The higher layer won on `source`
and contributed `path`, but `url` is a different key and nothing overwrote it.
`ServiceDeveloperConfigValidator` rejects any field the effective source's `RelevantFields` doesn't
list, so the run dies at composition:

```
Service 'common-auth': 'url' is not valid for source 'local' — remove it from
'ServiceSources:Services:common-auth', usually the service's entry in servicesources.local.json.
```

Since `local` (`path`, `ref`), `url` (`url`), `kubernetes` (`context`, `namespace`, `port`) and
`container` (`tag`) have disjoint field sets, this fires for anyone who had actually configured the
service before switching it — which is everyone who has used the file for its intended purpose.

There is no workaround from a higher layer. Blanking the stale field fails identically: the
validator tests `value is not null`, and the binder binds an empty environment variable as `""`,
which is not null. The only fix available today is to edit `servicesources.local.json` — the thing
[#157] exists to avoid.

**The README's own example is the broken case.** Its "Named profiles" section shows an
`appsettings.Cluster.json` switching `orders` to `kubernetes`. Anyone whose
`servicesources.local.json` has `orders` as `local` with a `path` hits the exception following the
documented example verbatim.

### Two findings from the investigation

**`port` already behaves the way we want.** It is `int?`, and the configuration binder maps an
empty string to `null` for nullable value types. So blanking a number from a higher layer already
works, while blanking a string does not. The file's own fields are inconsistent today.

**An empty `path` silently resolves the checkout to the AppHost directory.**
`LocalGitCheckout.PrepareRepoRoot` tests `config.Path is not null`, which is true for `""`;
`Path.GetFullPath("", appHostDirectory)` returns the AppHost directory itself; `Directory.Exists`
passes; and the method returns that directory as the service's checkout with
`NeedsReconciliation: false` — "used as-is: no clone, no checkout, no fetch, ever." Verified by
calling the real method: `RepoRoot` came back exactly equal to the AppHost directory, no exception.
This is reachable today, independently of [#161], because `path` is a relevant field for `local` so
the validator never inspects it. It survives [#148] unchanged.

## Decision

Restructure the developer config into **per-source sub-objects**, so that switching `source`
leaves the previous source's block present but unread. This is option 3 of the three the issue
proposed, chosen over relaxing the validator (option 1) and validating only against the effective
source (option 2).

Option 1 — treating null-or-whitespace as absent in the validator — was rejected as the primary
fix. It is not sufficient on its own: the validator would treat `""` as absent while every consumer
still sees it as present, and `LocalGitCheckout`'s `config.Path is not null` would then make the
documented "unset" gesture silently resolve the checkout to the AppHost directory. Its underlying
idea is kept, but as a normalization pass at binding rather than a rule in the validator — see
[Normalization](#normalization).

Option 2 — ignoring stale fields — would work functionally, since no consumer reads another
source's field, but it silently swallows typos, which the current strict behaviour is deliberately
there to catch.

### No backwards compatibility

The flat shape is not read at all. The owner's assessment is that the project has no users yet — it
was announced two days before this design — so a compatibility layer would cost two shapes in the
code, the validator, the docs and the test matrix indefinitely, in exchange for sparing an edit to a
gitignored per-developer file. The CHANGELOG policy is built for exactly this: while the version is below `1.0.0`, a
breaking change can ship in a minor release with a **Breaking** entry saying what breaks and how to
migrate.

A flat field must still *fail* rather than be silently ignored — this project has a stated allergy
to configuration that quietly does nothing (commit `4e36841` documents two such cases). That
failure is not a migration shim; it falls out of the unknown-key validation below at no extra cost.

## File shape

Only the block named by `source` is read. The others may sit there unread — that is the feature.

```json
{
  "services": {
    "orders": {
      "source": "local",
      "local":      { "path": "/src/orders", "ref": "main" },
      "url":        { "url": "https://orders.dev.example" },
      "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 },
      "container":  { "tag": "2026-06-01" }
    }
  }
}
```

A minimal entry stays minimal — no block is required:

```json
{ "services": { "orders": { "source": "local" } } }
```

Nesting is already the established shape on the catalog side: `servicesources.yaml` nests
`url: { url: … }`, `container: { image, port, defaultTag }` and `kubernetes: { service, port }`.
The developer config now mirrors it.

The catalog keeps its local-checkout fields (`repository`, `project`, `defaultRef`) flat at the
service root, and the developer config deliberately does **not** copy that. If `path` and `ref`
stayed flat here, switching `local` → `url` would leave a stale flat `path` and reproduce [#161]
exactly. The nesting has to be uniform to be a fix.

## Bound model

```csharp
internal sealed class ServiceDeveloperConfig
{
    public string Source { get; set; } = "";

    public LocalDeveloperConfig Local { get; set; } = new();

    public UrlDeveloperConfig Url { get; set; } = new();

    public KubernetesDeveloperConfig Kubernetes { get; set; } = new();

    public ContainerDeveloperConfig Container { get; set; } = new();
}

internal sealed class LocalDeveloperConfig
{
    public string? Path { get; set; }
    public string? Ref { get; set; }
}

internal sealed class UrlDeveloperConfig
{
    public string? Url { get; set; }
}

internal sealed class KubernetesDeveloperConfig
{
    public string? Context { get; set; }
    public string? Namespace { get; set; }
    public int? Port { get; set; }
}

internal sealed class ContainerDeveloperConfig
{
    public string? Tag { get; set; }
}
```

Blocks are **non-null, defaulted to an empty instance**. `source: local` with no `local:` block is
the common case — both `.example` files are exactly that — so consumers write `config.Local.Path`
with no `?.`, and there is no absent-versus-empty distinction to reason about, because the two mean
the same thing.

Names mirror the existing catalog-side `KubernetesMetadata` / `UrlMetadata` / `ContainerMetadata`,
with `DeveloperConfig` marking which side of the pair a type belongs to.

`config.Url.Url` reads oddly. It is what the catalog already does with `url: { url: … }`, and the
alternative — a bare string for the one single-field source — would break the uniformity that makes
the whole scheme work.

### Consumers

Mechanical, and confined to the field accesses:

| Site | Today | After |
| --- | --- | --- |
| `LocalGitCheckout.PrepareRepoRoot` | `config.Path`, `config.Ref` | `config.Local.Path`, `config.Local.Ref` |
| `LocalGitCheckout.ConfiguredReference` | `config.Ref` | `config.Local.Ref` |
| `UrlSource.ResolveUrl` | `config.Url` | `config.Url.Url` |
| `KubernetesSource` | `config.Context`, `config.Namespace`, `config.Port` | `config.Kubernetes.…` |
| `ContainerSource` | `config.Tag` | `config.Container.Tag` |

`LocalCheckoutPrefetch` filters on `entry.Value.Source` only and is unchanged.
`IServiceSource.Resolve` keeps its signature.

## Validation

`RelevantFields` is **deleted from `IServiceSource`**. The block types' properties are the valid key
set, so no source has to declare one, and the declaration cannot drift out of sync when a field is
added later. Its only consumer is the validator.

The validator becomes a single recursive unknown-key walk over the raw configuration section,
driven by the bound type's shape:

- A key at the entry root must match a property of `ServiceDeveloperConfig` — `source`, `local`,
  `url`, `kubernetes`, `container`.
- A key inside a block must match a property of that block's type.
- Matching is **case-insensitive**. Configuration keys are, so a `Local:Path` arriving from an
  environment-variable layer has to match `local:path`. (Today's `RelevantFields` is a
  `HashSet<string>` on the ordinal default comparer — a latent mismatch this check would otherwise
  trip over.)

The old cross-source check disappears. A field belonging to another source can no longer bind at
all, so the rule it enforced is now unviolatable by shape rather than by validation.

**Every block is checked, not only the effective one.** A stale block that is *valid* must not break
you — that is the whole point of [#161] — but a stale block containing a typo still should, because
you will switch to it eventually and would otherwise discover the typo then.

Because each field name is unique across the four block types, the error for a flat field can name
the block it belongs in:

```
Service 'orders': 'path' is not a valid key here. It belongs in the 'local' block: "orders": { ..., "local": { "path": ... } }.
```

The message names the block and nothing else. Naming a `source` to set alongside it would be advice
to change what the service resolves to — a stray `port` on a container-sourced entry belongs in the
`kubernetes` block, which is emphatically not a reason to make the service kubernetes-sourced.

Where a key matches no block's field, the message falls back to listing the valid keys at that
level.

Two further checks on the same walk catch a value written at the wrong level, in either direction.

A block name carrying a value rather than an object — `"url": "https://…"`, the old flat shape
written with a name that is also a block's — passes on its name alone, because the name genuinely is
valid at that level; only its scalar value gives it away. It gets a message of its own, saying the
key takes a block of settings rather than a value and naming the keys valid inside it:

```
Service 'orders': 'container' takes a block of settings, not a value: "orders": { ..., "container": { "tag": ... } }. Valid keys there are 'tag'.
```

It must not reuse the block-naming message above. That message is built by looking up which block
owns a *field* name, and three of the four block names — `local`, `kubernetes`, `container` — are not
field names, so the lookup finds nothing and the message falls back to listing valid keys. The report
then reads `'container' is not a valid key. Valid keys are 'container', …`, naming the key invalid
and valid in one breath. Only `url` escapes it, by the accident of being both a block name and a
field name — which is exactly the case that makes the fault easy to miss, since `url` is the field
in the [#161] repro and the natural one to test first.

The mirror case is a field carrying an object where a value goes — `"local": { "path": { … } }`, or
an object at `source`. Left unchecked this is worse than a silent ignore: the binder discards the
whole entry, and the developer is told the file configures no services while it plainly configures
one.

Both matter more than they look. This design forces a migration on every existing file, so a value
landing at the wrong level is the predictable mistake, and the silent acceptance it would otherwise
get is the thing this validation exists to rule out.

Reflection is acceptable here: `Directory.Build.props` sets no trimming or AOT properties, and the
configuration binder this code already calls is itself reflection-based.

### When it runs

Validation **moves from `AddService` to `DeveloperConfiguration.ReadFrom`**, and runs for every
entry rather than only for services the AppHost asks for.

It can move because it no longer needs a resolved `IServiceSource` to tell it which fields are
relevant — the check is now purely about shape, and `ReadFrom` already enumerates every entry once
per builder behind a lock, with the raw configuration in hand.

It *should* move because of when `LocalCheckoutPrefetch` clones. It runs inside the first
`AddService` call that resolves a `local`-sourced service — after that service's own
`ResolveService`, not before it — and starts a clone for *every* entry whose `source` is `local`,
including entries for services no `AddService` call ever names. Under lazy validation an entry with a typo inside its `local` block therefore
prefetches a managed checkout with the mistyped field silently absent, and errors only if the
AppHost happens to ask for that service by name. Validating at read time puts the error before the
work.

This makes a malformed entry fail the AppHost even when nothing uses that service. That is a
deliberate widening, and it is the right one for a shape error: the entry is malformed in a file the
developer owns, and the message names it precisely. Validation stays shape-only and never consults
the catalog, so an entry naming a service the catalog does not describe still passes — that case
remains `AddService`'s to report, and `LocalCheckoutPrefetch`'s comment about it still holds.

## Normalization

The same property map drives a second pass at bind time, in `DeveloperConfiguration.ReadFrom` after
`Get<Dictionary<string, ServiceDeveloperConfig>>()`: **any string property that is null or
whitespace becomes null**, across every block.

This does three things:

1. Makes `ServiceSources__Services__orders__Local__Path=` a working "unset", so a higher layer can
   drop a field it inherited — the within-block case that nesting alone does not fix. A base entry
   with `local: { path: … }` and a higher layer wanting a managed checkout at `local: { ref: … }`
   would otherwise merge to both and trip "'ref' cannot be combined with 'path'".
2. Closes the empty-`path` bug above, so a blank path is absent rather than the AppHost directory.
3. Makes strings consistent with `int? Port`, which the binder already maps from empty to null.

One deliberate semantic change: `"url": ""` under `source: url` now falls back to the catalog's
`url.url` instead of erroring "no url configured", because `UrlSource.ResolveUrl` computes
`config.Url.Url ?? metadata.Url?.Url` and the empty string no longer shadows the catalog. This is
the better behaviour — empty means unset means use the catalog — and gets its own test.

## Blast radius

- **`samples/DemoAppHost/servicesources.local.json.example`** and the TypeScript one. Both are
  bare `{ "source": … }` entries today, so they barely change; add a commented block example.
- **README.** The "Overriding `servicesources.local.json`" section, including the "Named profiles"
  example that is currently the broken case; the three other places documenting a flat field as an
  override (`path` as a quick override, `url` overriding the catalog, `tag` overriding
  `defaultTag`); and the environment-variable examples, which gain a block segment
  (`ServiceSources__Services__orders__Local__Ref=feature-x`). The common CI case
  `ServiceSources__Services__orders__Source=container` is unchanged, which is worth saying
  explicitly since it is the one people paste.
- **CHANGELOG.** A **Breaking** entry under `[Unreleased]`, per the stated policy, showing the old
  and new shape.
- **Tests.** Roughly thirteen `new ServiceDeveloperConfig` construction sites plus the JSON literals
  in the config tests.

## Testing

Test-driven, per the house workflow. The tests that matter:

- **The [#161] repro, through real layered `IConfiguration`.** Base layer sets `source: url` and a
  `url` block; a higher layer sets `source: local` and a `local` block. Resolves as local, does not
  throw, and the `url` block is unread.
- **A flat field at the entry root is rejected**, with a message naming the block it belongs under.
- **A typo inside an inactive block is still rejected**, while a valid inactive block is not.
- **Case-insensitive key matching** from an environment-variable layer.
- **Empty means absent**: `…__Local__Path=` yields a managed checkout, and specifically *not* the
  AppHost directory. This is the regression test for the verified bug.
- **`"url": ""` under `source: url`** falls back to the catalog's `url.url`.

## Sequencing

[#157] is 3 commits ahead of `main` and 2 behind, the two being [#152] and [#148]. [#148] rewrote
`LocalGitCheckout` and `LocalProjectSource`, which this design touches, so **[#157] must take
`main` before this work starts**. The file sets overlap only in `README.md` and `CHANGELOG.md`, so
the conflicts are text, not logic.

That step is the repository owner's to perform: it needs a rebase and force-push of a branch under
review, or a merge, and neither is an action this design's implementation should take on its own.

[#148]: https://github.com/flojon/aspire-servicesources/pull/148
[#152]: https://github.com/flojon/aspire-servicesources/pull/152
[#157]: https://github.com/flojon/aspire-servicesources/pull/157
[#161]: https://github.com/flojon/aspire-servicesources/issues/161
