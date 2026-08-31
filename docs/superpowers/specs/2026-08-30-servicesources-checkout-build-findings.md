# Is a fresh `"local"` checkout ever built before its resource starts?

**Status:** Findings — measured, no code changed. Settles GitHub issue #81.
**Date:** 2026-08-30
**Question:** Nothing in this repository builds a `"local"` checkout. Does anything *need* to?
#81 filed four sub-questions and could not answer any of them from the code alone.

## Answer

**Aspire builds it, on every start, correctly — for `kind: dotnet`.** The package needs no build
step for the case it ships today, and #81's staleness worry does not occur.

The mechanism #81 could not name from reading the code: a `dotnet` service is registered with
Aspire's own `AddProject`, and DCP launches that resource as

```
dotnet run --project <checkout>/<project>.csproj --configuration Debug --no-launch-profile
```

with **the checkout directory as the process's working directory**. So the build is not a step
Aspire performs on our behalf and could stop performing — it is `dotnet run`'s own implicit
incremental build, at resource-launch time, on every start.

Three consequences follow, and all three were confirmed by experiment rather than derived:

1. A cold checkout compiles on first run. No `bin/` needs to pre-exist.
2. A moved `ref` cannot leave stale binaries behind: git stamps the files it rewrites with the
   current time, so the next start's incremental build recompiles them.
3. The `global.json` barrier written into `.servicesources/` is fully effective for a run Aspire
   launches, because `sdk.version` resolves from the working directory and that directory is
   inside the checkout.

| # | #81's sub-question | Answer |
| --- | --- | --- |
| 1 | Cold, never-built checkout | **Builds.** Under an IDE's own launcher, untested — see *What is still open*. |
| 2 | Staleness across a `ref` change | **Does not occur.** Rebuilt on every start, both directions. |
| 3 | Build contention | **Real, but only for `path` services sharing build output.** Managed checkouts are structurally immune. |
| 4 | Kinds that need an explicit build | Unchanged; the seam is the prepare step ([#118](2026-08-28-servicesources-prepare-step-design.md)). |

## Method

A fixture rather than a real repository, so every variable is controlled:

- A local git repo with two refs, `refA` and `refB`, differing only in a `const string` the
  service returns from `/marker`. The constant is compiled in, so the HTTP response names the
  *compilation* that is running, not the working tree.
- An MSBuild target in the fixture's `.csproj`, `AfterTargets="Build"`, appending
  `$(MSBuildStartupDirectory)`, `$(NETCoreSdkVersion)` and a timestamp to a file **outside** the
  checkout, so the record survives a ref switch or a wiped working tree.
  `MSBuildStartupDirectory` is the working directory the build was launched from — the anchor
  `global.json`'s `sdk.version` resolves against.
- `DemoAppHost` pointed at the fixture over a `file://` URL, run as
  `dotnet run --project samples/DemoAppHost --no-build` **from the repository root**, so a build
  that inherited the launcher's working directory would be visibly distinguishable from one
  anchored at the checkout.
- Machine: three SDKs installed (8.0.424, 9.0.317, 10.0.400), Aspire AppHost 13.5.2, Linux/WSL2.

## Evidence

### 1. A cold checkout is built

`.servicesources/` deleted entirely, then one run. The checkout was cloned, `bin/` and `obj/`
appeared, the service answered `REF-A`, and the probe recorded exactly one build:

```
BUILD at 14:26:54.033 | startupDir=<apphost>/.servicesources/checkouts/orders | sdk=10.0.400
```

The process table during that run shows who did it, and with which working directory:

```
dotnet run --project <apphost>/.servicesources/checkouts/orders/FixtureSvc.csproj \
  --configuration Debug --no-launch-profile
```

### 2. A moved `ref` does not leave stale binaries

Both directions, with the previous ref's `bin/` in place and untouched between runs:

| `ref` before | `bin/` holds | `ref` after | `/marker` answered | Probe |
| --- | --- | --- | --- | --- |
| `refA` | `REF-A` | `refB` | `REF-B` | new build at 14:28:10 |
| `refB` | `REF-B` | `refA` | `REF-A` | new build |

The checkout's `HEAD` moved to `3e5ddc8`/`2790fb0` as expected and the output assembly's mtime
moved with it. #81's sub-question 2 — the one it called "ours in a way (1) isn't" — needs no
mechanism of our own.

### 3. The build's working directory is the checkout, so the `global.json` barrier holds

This settles the open observation left on #81 on 2026-08-28, which was reasoned but explicitly
not verified.

`{"sdk": {"version": "9.0.317", "rollForward": "disable"}}` placed in the **AppHost directory**,
the checkout wiped cold, then the same run repeated:

| Build launched from | Result |
| --- | --- |
| Aspire (working directory = the checkout) | built under **10.0.400**, resource started |
| the AppHost directory, by hand | **error NETSDK1045** — SDK 9 cannot target `net10.0` |

The first row is the barrier working: `hostfxr` walks up from the checkout, meets
`.servicesources/global.json`, and stops before reaching the pin. The second is the residual case
the README already documents — a build *you* launch from outside still sees your repository's pin.
Both halves of that README bullet are now measured rather than inferred.

### 4. Build contention is real, and narrower than #81 assumed

#81 listed contention as unestablished and gave no repro; the prepare-step design then recorded
that a prepare step is serialized by construction, which is true but covers only prepare steps.
**Aspire's own builds are parallel**, because DCP launches every project resource at once.

Fixture: one repository holding `Lib`, `SvcA` and `SvcB`, where both services carry a
`ProjectReference` to `Lib` — so both builds write the same `Lib/bin` and `Lib/obj`. Two services
resolved to it through the `path` override.

- Aspire-driven, outputs wiped between attempts: **1 of 2** attempts left `SvcA` with no probe
  line at all and a resource that never answered.
- The same two builds raced directly, five attempts: **2 of 5** failed.

```
error MSB4018: The "GenerateDepsFile" task failed unexpectedly.
System.IO.IOException: The process cannot access the file
'.../Lib/bin/Debug/net10.0/Lib.deps.json' because it is being used by another process.
```

Same family as [microsoft/aspire#15190](https://github.com/microsoft/aspire/issues/15190), which
reports `CS2012`/`MSB3491` on different files for the same cause.

**Managed checkouts cannot hit this.** Each service is cloned into its own
`checkouts/<serviceName>/`, so two services out of one repository — the #66 case — get two working
trees and two output directories with nothing shared to lock. The exposure is `path` services
pointing into a single repository, where the developer's own layout decides what is shared.

### 5. A checkout that fails to build is silent in the AppHost's console

Worth more than the four sub-questions, because it is the confusing failure mode #81 was filed to
protect against, and it is independent of whether anything rebuilds.

In the contention run above, `SvcA` never compiled and never started. The AppHost's own stdout
contained the version banner, the DCP start line, and the dashboard URLs — and nothing else. No
build, no error, no mention of the resource. The compiler output goes only to that resource's
console in the dashboard.

This also answers, in part, the open question the [prepare-step design](2026-08-28-servicesources-prepare-step-design.md)
flagged for implementation: whether `aspire run` buffers AppHost stdout. Aspire's *own* build
output does not travel through the AppHost console at all, so a prepare step that writes there is
already a different channel from the one a developer watches for a failing build.

## What is still open

**The IDE case, which is the one thing here that could not be run.** These findings are from
DCP's launcher. An IDE that takes over launching project resources in order to attach a debugger
builds them itself, and a project reached by path is not in the loaded solution — exactly
[microsoft/aspire#2154](https://github.com/microsoft/aspire/issues/2154), still open and
redirected to [#10920](https://github.com/microsoft/aspire/issues/10920). Nothing measured here
contradicts that; it establishes only that the gap, if present, belongs to the IDE's launcher and
not to `dotnet run`. Tracked on #83 with the rest of the upstream watch list.

Not attempted, and not worth attempting: driving VS or Rider headlessly to settle it. It wants a
person pressing F5 on a machine with the IDE installed.

## Reproducing

The fixtures were throwaway and are not committed. Rebuilding them is about twenty minutes:
a two-ref git repo whose service returns a compiled-in constant, an `AfterTargets="Build"` target
appending `$(MSBuildStartupDirectory)` and `$(NETCoreSdkVersion)` to a file outside the checkout,
and `DemoAppHost` pointed at it over `file://`. The contention fixture additionally needs two
services in one repository sharing a `ProjectReference`, resolved through `path`.
