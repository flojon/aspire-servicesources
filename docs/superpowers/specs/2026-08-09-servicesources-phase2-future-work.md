# Aspire.Hosting.ServiceSources — Phase 2 and Beyond

**Date:** 2026-08-09
**Status:** Reference / not yet designed. Companion to the [milestone 1a design](2026-08-09-servicesources-design.md), which this document does not duplicate — read that first for what's actually being built now.

This captures everything raised in the original project brief and the milestone 1a brainstorming session that was deliberately deferred. None of this is designed yet; each item needs its own brainstorming pass before implementation, ideally after milestone 1a has shipped and been used for real. Ordered roughly by expected priority, not obligation.

## Cluster source

The most-requested next source. Explicitly scoped out of milestone 1a because it's genuinely greenfield — no official Aspire building block exists for "consume an already-running service inside a cluster" (confirmed via source research: `Aspire.Hosting.Kubernetes` is deploy/publish-only, and `ExternalServiceResource` is sealed and lacks `IResourceWithEndpoints`).

Open questions to resolve before designing this:
- **Network reachability**: how does a developer's machine actually reach a service running in a dev cluster? Candidates: an assumed-already-configured direct network path (VPN/cluster-peered network), a managed `kubectl port-forward` process per service that ServiceSources starts/stops, or requiring an externally-reachable ingress/LoadBalancer URL per service. Each has different failure modes and setup burden.
- Multi-environment context selection (`dev-west`, `dev-east`, `qa`, `staging`) — config shape TBD, likely a top-level `cluster.context` similar to the original brief's sketch, but needs its own design pass once reachability is settled.
- Whether the `ServiceResource` facade pattern from milestone 1a (delegate to a real backing resource) extends cleanly here, or whether a cluster-backed service needs its own genuinely-registered resource type since there's no equivalent of `AddProject` to delegate to — this is a real open design question, not just an extension of the existing pattern.

## Deferred / parallel resolution

Milestone 1a resolves each service synchronously and in-order inside `AddService()`. This was a deliberate simplicity trade-off (see the design doc's Resolution Flow section for the reasoning) — the cost only shows up when multiple services need a genuinely cold clone+build simultaneously, which is mostly a first-run tax.

Phase 2 version: defer resolution to a `BeforeStartEvent`-subscribed hook that resolves all pending local services in parallel (`Task.WhenAll`) before DCP starts anything. Requires:
- The `ServiceResource` facade's `GetEndpoint()` to return a lazily-resolving value provider instead of delegating directly to an already-built resource, since the backing resource won't exist yet at the point `WithReference()` is called in `Program.cs`.
- Confirming exactly when Aspire's pipeline reads endpoint values relative to `BeforeStartEvent` completion — not verified during milestone 1a's research, called out there as an open risk.

## Repo update / freshness

Milestone 1a deliberately does **not** auto-update an already-cloned repo (no pull, no reset) — chosen specifically to avoid the destructive hard-reset-on-update behavior found in `Aspire.PolyRepo`'s `KeepUpToDate()`. A real "bring my local clone up to date" story is needed eventually:
- Likely an explicit command (`aspire-services update [service]`) rather than silent background mutation.
- Needs a real answer for dirty working trees — at minimum detect and refuse/warn rather than discard, unlike the prior-art behavior explicitly avoided here.

## Config discovery walk-up

Milestone 1a only checks the AppHost's own directory for `servicesources.yaml`/`servicesources.local.json`. Walking up parent directories (like `.gitignore`/`nuget.config` resolution) was considered and deferred — relevant for monorepos where config might live at the repo root rather than next to the AppHost. Needs a decision on which file "wins" if multiple exist at different directory levels before this is built.

## Container and external-endpoint sources

From the original brief's full source list — not discussed in depth during milestone 1a brainstorming beyond acknowledging they exist as future `IServiceSource` implementations. `ContainerSource` (run a published image instead of building from source) is probably the more immediately useful of the two; `ExternalEndpointSource` (point at an arbitrary URL) is closest in shape to Aspire's own (currently limited) `ExternalServiceResource`.

## Dependency / infrastructure resolution

From the brief: an imported service may depend on infrastructure (Redis, RabbitMQ, Postgres) or other services, and naively importing that infrastructure risks duplicate instances. The brief's own sketch:
```csharp
builder.AddService("orders")
    .RequiresRedis()
    .RequiresRabbitMQ()
    .RequiresService("identity");
```
Explicitly and repeatedly flagged in the brief as **not** a milestone 1 concern, and nothing in this session refined it further. Whoever picks this up should treat it as a fresh brainstorming topic, not an extension of the `IServiceSource` interface — it's a different kind of problem (composition/dedup across the whole app graph, not per-service source resolution).

## Auto/fallback source selection

```csharp
builder.AddService("orders").Auto();
```
Resolver picks from a priority list (existing local project → existing clone → running cluster service → container → clone-and-run) automatically. Brief explicitly defers this until the explicit source model (milestone 1a) works. Needs the cluster source to exist first anyway, since it's part of the fallback chain.

## CLI configuration UX

```
aspire-services configure
```
Interactive picker for which source each service uses, writing `servicesources.local.json`. Explicitly not required for v1 in the brief. Low technical risk once the config schema is stable — mostly a UX/tooling exercise, reasonable candidate for an early phase 2 pass since it directly improves the developer experience the whole package exists to deliver.

## Named profiles

`everything-local`, `hybrid`, `cluster-only`, `my-machine` — the brief mentions these as "potentially useful later" without detail. Would likely layer on top of `servicesources.local.json` (a profile = a named, swappable set of per-service source choices) rather than replace it. Undesigned.

## Central service registry

The brief notes service metadata "could eventually come from conventions or a central registry" instead of a local `servicesources.yaml`. Explicitly deferred in favor of the simple local/shared file for v1. No further detail to capture — genuinely open.
