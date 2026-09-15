# Aspire.Hosting.ServiceSources — the "must be called before the first `AddService`" ordering contracts (#314)

**Date:** 2026-09-15
**Status:** Investigation. Answers what remains of #314's "runtime ordering contracts" checklist item
and what it would take to close each remaining piece; not itself a design for any one fix.
**Investigates:** #314's unchecked item *"Remove the 'must be called before the first `AddService`'
requirement, or make it unnecessary"*.
**Read out of the code at** `2f71199` (main, post-#314's own merge). Line numbers are that commit's.

---

## Where this stands

#314 named five builder-level entry points carrying this contract: `UseDeferredCheckout`,
`AddLocalKind`, `AddServiceCatalog`, `UseJava` and `UseJavaScript`. Two are already closed:

**`UseJava`/`UseJavaScript` are done.** `e461550`/`a1b1492` ("Make java/javascript built-in local
kinds") gave `LocalKindRegistry.TryGet` a lazy built-in fallback:

```csharp
// Sources/LocalKindRegistry.cs:71-85
public bool TryGet(string kind, out ILocalResourceKind? handler)
{
    if (_handlers.TryGetValue(kind, out handler)) return true;
    handler = kind switch
    {
        JavaLocalResourceKind.KindName => DefaultJava.Value,
        JavaScriptLocalKind.KindName => DefaultJavaScript.Value,
        _ => null,
    };
    return handler is not null;
}
```

Both handlers are dependency-free, zero-configuration types living unconditionally in this assembly
— the same way `"dotnet"` already worked. That precondition is exactly why this fix was available:
"always resolvable by name, on demand" costs nothing when there's a known, constructible default to
resolve to. `UseJava()`/`UseJavaScript()` still work (explicit registration is checked first, e.g. to
substitute a test double), they're just no longer *required* before the first `AddService`.

The other three remain, and they don't share that precondition — each is examined below.

## 1. `AddLocalKind` for a genuinely third-party kind

**Mechanism.** `Register` (`ServiceSourcesBuilderExtensions.cs:209-222` →
`Sources/LocalKindRegistry.cs:41-69`) has **no ordering check of its own** — it only rejects
re-registering `"dotnet"` and duplicate names. The constraint is enforced entirely from the other
side: `LocalProjectSource.Resolve` looks the kind up eagerly, before doing any checkout work
(`Sources/LocalProjectSource.cs:49`, `ResolveKindHandler` at `:240-257`), and throws if nothing is
registered and no built-in fallback matches:

```csharp
// Sources/LocalProjectSource.cs:245-254
if (!registry.TryGet(definition.Kind, out var handler) || handler is null)
{
    throw new ServiceSourcesConfigurationException(
        $"Service '{serviceName}': kind '{definition.Kind}' is not registered. " + …
        "Register it with builder.AddLocalKind(name, handler) before the first AddService call.");
}
```

**Why the Java/JavaScript fix doesn't generalize.** That fix works by substituting a *known* default
for two specific names. A kind implemented outside this package (someone's own `ILocalResourceKind`)
has, by definition, no default this package could construct — there is nothing to fall back to for a
name core has never heard of. The ordering requirement here isn't an implementation shortcut, it's
the inherent cost of an open registry resolved eagerly: *something* has to tell core about a custom
kind before core can use it, and "eagerly" is what fixes the "before" to "before the first use."

**What would actually remove it:** a different registration model entirely — e.g. discovering
`ILocalResourceKind` implementations via assembly scanning or DI registration instead of an
imperative `AddLocalKind` call, so there's no "call this line before that line" for the AppHost author
to get wrong. That's a materially bigger change (a new extensibility mechanism, not a tweak to this
one), out of scope for this investigation, and not clearly a win — an imperative call is also the
more Aspire-idiomatic shape for "opt this package into something," which is the same instinct that
made `AddServiceCatalog`/`AddRepository` explicit calls rather than convention-based discovery.

**Recommendation:** leave as is. This one is a structural cost of an open, eagerly-resolved registry,
not a bug or an oversight — the existing error message already tells the reader exactly what to do
and why (`ResolveKindHandler`'s message, plus `DescribeNearMatch` for a casing typo).

## 2. `AddServiceCatalog`

**Mechanism.** A real "too late" latch, not just an indirect consequence:

```csharp
// Config/ServiceSourcesConfigCache.cs:56-63
if (_frozen)
{
    throw new ServiceSourcesConfigurationException(
        "AddServiceCatalog(…) was called after the service catalog had already been read, so " +
        "its entries could not be seen. Because a service is resolved as it is added, the " +
        "catalog must be declared before the first AddService(…) — near the top of the AppHost, " +
        "next to UseDeferredCheckout() and UseJava().");
}
```

`_frozen` is set by `CodeCatalogAccumulator.Freeze()` (`:76-83`), called from
`LoadedConfig.Load` (`:255-258`) — which runs on the **first** `AddService()` call anywhere in the
AppHost, because that's what triggers `ServiceSourcesConfigCache.ResolveService` →
`LoadedFor` (`ServiceSourcesBuilderExtensions.cs:91`).

**Why it's global rather than per-service.** `LoadedConfig.Load` doesn't just look up one service —
it builds and validates the *whole* merged catalog in one pass: code/yaml duplicate-name detection,
repository name collisions (finding checks at `:296-337`), the ungrouped-service/repository namespace
check (`:382-415`), and the shared-repository-but-ungrouped warning (`:430-459`). All of that is
catalog-wide by nature — a duplicate or a namespace collision can only be found by looking at every
entry together — and none of it happens before the first `AddService` call needs an answer for the
one service it's asking about.

**Why this is harder than the Java/JavaScript case (confirming #314's own assessment).** There's no
default to fall back to here — `AddServiceCatalog`'s whole job is telling core about services nobody
could know about in advance. The real question is *when* the catalog has to be complete, and the
honest answer today is "by the time anything asks it a question," which happens to be the first
`AddService()` call because nothing else asks first.

**Options considered:**

- **Per-service freeze instead of global.** Defer the catalog-wide invariant checks (duplicates,
  namespace collisions, the shared-repo warning) until a later point — e.g. `BeforeStartEvent`, or
  `builder.Build()` — and let `AddServiceCatalog` keep contributing entries as long as the *specific*
  service a given `AddService()` call names hasn't been resolved yet. This changes the contract from
  "declare everything before the first `AddService` call, anywhere" to "declare a service's own entry
  before calling `AddService` for it" — narrower, and closer to how most AppHosts are already written
  (catalog entry, then the matching `AddService` call, repeated per service) — but it is still an
  ordering constraint, just a smaller one. It also reopens every one of the catalog-wide checks above:
  each would need to run against a partial catalog at resolution time and again, fully, once
  composition is known to be finished — two passes instead of one, and a real chance of the first pass
  reporting something the second pass would have resolved differently (e.g. a duplicate that a later
  `AddServiceCatalog` call was about to correct by removing the first declaration — which the current
  design doesn't actually support either, but a partial-catalog version would need to decide about).
- **Two-phase build (never resolve until `Build()`).** This is the shape `AddService` had *before*
  `#62` — a facade returned immediately, the real resource created later. `#62`'s own commit message
  records why that was removed: DCP had nowhere to attach a `Service` object for a container consumer
  to reference, and the AppHost had nowhere to put its own configuration on the returned handle.
  Reintroducing it to solve this would trade a solved problem for the one this ordering contract
  causes — not a net win, and explicitly the direction #62 moved away from.
- **Compile-time enforcement (Roslyn analyzer).** Flag an `AddServiceCatalog` call that appears, in
  the same method body, after an `AddService` call. This is what #314's own framing asks for most
  literally — "expressed nowhere in the type system" — and doesn't require touching the resolution
  model at all. It's necessarily incomplete (it can't see across methods, conditionals, or files), but
  it converts the *common* mistake — writing the calls in the wrong order in `Program.cs` — from a
  runtime exception into a build-time squiggle, for free relative to the alternatives above.

**Recommendation:** the per-service freeze is the only option that actually changes the runtime
contract, and it's a real project — it touches every catalog-wide invariant check in `LoadedConfig.Load`
and needs a design decision about the two-pass duplicate-detection problem above. Given that, and
that #314 already flagged this as "harder... changes what `AddService` builds," a Roslyn analyzer is
the cheaper, lower-risk piece worth doing on its own regardless of whether the deeper change ever
happens — it's additive, can't regress existing behavior, and covers the mistake this ordering
contract is actually protecting against in the AppHost shape almost everyone writes (one `Program.cs`
method, top to bottom).

## 3. `UseDeferredCheckout`

**This one is not what its own doc comment claims.** The XML doc says *"Must be called before the
first `AddService`, which is where the decision is made"* (`ServiceSourcesBuilderExtensions.cs:133`),
implying the same kind of contract as the other two. It isn't — `Enable()` just flips a bool with no
latch at all:

```csharp
// Sources/DeferredCheckout.cs:145-151
public void Enable()
{
    lock (_gate) { _enabled = true; }
}
```

and `ShouldDefer` (`:166-204`) reads `_enabled` fresh on every call, made from
`LocalProjectSource.Resolve` for each service as it's added. So calling `UseDeferredCheckout()` late
doesn't throw — it silently has no effect on whichever services were already resolved before that
line ran; every `AddService()` call after it still gets the deferred behavior. There is no error
message anywhere for this case because nothing detects it.

**This is arguably worse than #2's loud failure, not better.** An AppHost author who reorders their
calls and puts `UseDeferredCheckout()` after a couple of `AddService()` calls gets no signal at all —
just a puzzling first-run experience where some services block on their clone and others don't, with
no message pointing at why. Fixing *that* (making a late call detectably wrong, one way or another) is
a smaller, more self-contained piece of work than #2's catalog-wide redesign, and is not gated on
deciding anything about `AddServiceCatalog`.

**Recommendation:** treat this as a correctness gap independent of the rest of #314's checklist item
— either make `Enable()` itself detect "at least one service has already resolved on this builder"
and throw the same way `AddServiceCatalog` does (simplest, consistent with the sibling contracts, and
truthful to the existing doc comment), or soften the doc comment to describe the actual per-service
semantics if silent partial application is judged acceptable. The former is recommended: a silent
partial application of an opt-in whose whole point is dashboard-visible behavior is the kind of thing
that should fail loudly rather than differ by service based on line order.

---

## Summary

| Entry point | Status | What closing it needs |
| --- | --- | --- |
| `UseJava` / `UseJavaScript` | **Done** (#314, `e461550`) | — |
| `AddLocalKind` (custom kinds) | Open, structural | A different registration model (assembly scanning/DI) — bigger scope, not clearly a win; recommend leaving as is |
| `AddServiceCatalog` | Open, hard (per #314's own note) | Either a per-service freeze (real redesign of `LoadedConfig.Load`'s catalog-wide checks) or, cheaper, a Roslyn analyzer that catches the common ordering mistake at compile time |
| `UseDeferredCheckout` | Open, and mis-documented | Independent, smaller fix: make a late `Enable()` call detectably wrong instead of silently partial |

Nothing here is a one-line change. The two-fifths already closed (`UseJava`/`UseJavaScript`) worked
because a known, zero-config default existed to fall back to; none of the three remaining contracts
has that shape. The cheapest real progress available without a larger design commitment is the
Roslyn analyzer for `AddServiceCatalog` (and it could cover `AddLocalKind`/`UseDeferredCheckout`
too, catching the same-method-body ordering mistake for all three at once) plus fixing
`UseDeferredCheckout`'s silent-degrade bug, both independent of any decision about relaxing
`AddServiceCatalog`'s resolution model itself.
