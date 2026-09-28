# The `"repository"` source

Clone the service's own git repository, keep it on a ref, and run it. The catalog names the
repository and, for a .NET service, the project inside it:

=== "C#"

    ```csharp
    catalog.AddService("orders")
        .WithRepository("https://github.com/example/orders", defaultRef: "main")
        .WithProject("src/Orders.Api/Orders.Api.csproj");
    ```

=== "YAML"

    ```yaml
    services:
      orders:
        repository: https://github.com/example/orders
        project: src/Orders.Api/Orders.Api.csproj
        defaultRef: main
    ```

??? note "Renamed from `\"local\"`"
    `"local"` used to collide in spelling (though not in meaning) with
    `servicesources.local.json` — a different "local" (per-developer settings) entirely, and was the
    only source whose value didn't match its own catalog block (`url`↔`url:`, `container`↔`container:`,
    `kubernetes`↔`kubernetes:`, but `local`↔`repository:`). Same behavior either way — clone the
    catalog's `repository:` url, reconcile onto `ref`. A config still naming `source: "local"` fails
    with a specific error naming the rename and the fix, rather than a generic "unrecognized source"
    message.

    The per-developer *block* was the same collision one level down — `"local": { ... }` for the
    `"repository"` source's own settings — and is renamed the same way, to `"repository"`. Unlike
    the source value, `"local"` still works there: it's a deprecated alias, not a retired spelling,
    so a config written before this rename keeps resolving exactly as it does today, behind a
    one-time startup notice asking you to rename it. Setting both `"local"` and `"repository"` on
    one entry is a configuration error.

Requires `git` (2.7 or newer) on `PATH` for a managed checkout — the same "a tool you already
have" trade the `"kubernetes"` source makes with `kubectl`. Every git operation runs under your own
git, so your credential helper, SSH agent, `~/.gitconfig` and proxy settings apply unchanged.

Each developer picks it in `servicesources.local.json`, optionally with their own `ref`:

```json
{
  "services": {
    "orders": { "source": "repository" },
    "payments": {
      "source": "repository",
      "repository": { "ref": "feature/new-checkout" }
    }
  }
}
```

**Already have a clone?** Don't point `"repository"` at it — switch that service to the
[`"path"` source](path.md) with your own directory, which needs no git at all:

```json
{
  "services": {
    "payments": { "source": "path", "path": { "path": "/home/dev/code/payments" } }
  }
}
```

That works for any catalog entry, including one with no `path:` of its own. (`repository.path`
under `"repository"` still does the same thing, but is deprecated: it logs a startup notice with
the `"path"` spelling to paste instead.)

- A managed checkout is cloned once into
  `<AppHostDirectory>/.servicesources/checkouts/<serviceName>/`, and reconciled to the
  configured `ref` (or the catalog's `defaultRef`) on every run. Uncommitted edits are never
  discarded — if the checkout is dirty and the ref changed, resolution fails loudly instead of
  overwriting your work. Anything you put at that path yourself that isn't a plain clone — a linked
  `git worktree`, or a clone made with `--separate-git-dir` — is refused with an explanation rather
  than replaced; point at it with the [`"path"` source](path.md) instead. A directory there with no
  `.git` entry at all is treated as debris from an interrupted clone and **deleted**, so don't
  hand-place a plain directory as a quick override — use `"path"` for that too. The `.servicesources/` directory gitignores itself
  on first use — no need to add it to your own `.gitignore` — and shields what it holds from your
  AppHost repository's build settings (see below).

  `ref`/`defaultRef` accept a commit SHA, not only a branch or tag — and a SHA is how a team turns
  "whatever's at the tip the first time each developer clones" into a reviewed checkout, since a
  resolved service can build and run code the checkout's own repository controls (see
  [`SECURITY.md`](https://github.com/flojon/aspire-servicesources/blob/main/SECURITY.md)). It costs a catalog edit per bump; that's the actual trade.
- Keep the file to the services you actually add — unless you use
  [`UseDeferredCheckout()`](#first-run-usedeferredcheckout), which removes the reason to.
  `AddService()` has to hand back the real resource, so it can't wait until the AppHost has finished
  composing to find out which services it wants: an entry whose *first* checkout an `AddService()`
  call would have to block on is cloned on the first call, in parallel with the others, before the
  AppHost has said which ones it wants. Entries you never add cost network and disk for that first
  clone. The AppHost logs which ones those were at startup — and warns if one of them failed, since
  nothing else would ever tell you — so you know what to drop.

  Nothing else is speculated over. A checkout that already exists — every service on every run
  after the first — is resolved only for the services you add, and so is a `path` override. And a
  service whose first checkout is *deferred* is cloned only when you add it: a deferred
  registration blocks on nothing, so its clone no longer has to be started ahead of demand to run
  alongside the others. With `UseDeferredCheckout()` on, a config listing ten `"repository"` services in
  front of an AppHost that adds two downloads two (#76).

  Either way, only the services you actually add are reconciled to their configured `ref`: a
  checkout that already exists is never touched on behalf of an entry you don't `AddService()`, so
  work in progress on a branch there is safe.

## `prepare`: a checkout that has to bootstrap itself

A managed checkout is assumed to be runnable the moment it is cloned. That holds for a `dotnet`
project, and for a Maven/Gradle service whose wrapper builds it as part of running — but not for a
repository whose runnable artifact or data asset is produced by a script the repository commits and
then gitignores. Such a checkout resolves cleanly and then fails, because the thing the catalog
names isn't there.

`prepare` is a command run **inside the materialized checkout, before the kind is allowed to judge
it**:

=== "C#"

    ```csharp
    catalog.AddService("routing")
        .WithRepository("https://github.com/example/routing")
        .AsJava(o => o
            .WithJarPath("graphhopper-web-11.0.jar")
            .WithArgs(["server", "gh-config-local.yml"])
            .WithPort(8989))
        .WithPrepare(
            ["./prepare.sh"],
            windowsCommand: ["pwsh", "-File", "prepare.ps1"], // optional; replaces command on Windows
            mode: PrepareMode.OncePerCommit);                 // the default | Once | Always | Never
    ```

=== "YAML"

    ```yaml
    services:
      routing:
        repository: https://github.com/example/routing
        kind: java
        prepare:
          command: ["./prepare.sh"]
          windowsCommand: ["pwsh", "-File", "prepare.ps1"]   # optional; replaces command on Windows
          mode: oncePerCommit                                # the default | once | always | never
        java:
          jarPath: graphhopper-web-11.0.jar
          args: ["server", "gh-config-local.yml"]
          port: 8989
    ```

- `command` is a **list, not a string**. There is no shell, so there are no quoting or
  word-splitting rules to get wrong and an argument containing spaces needs no escaping. The first
  element is either a path inside the checkout (`./prepare.sh`, `scripts/bootstrap`) or a bare
  program name resolved through `PATH` (`make`, `npm`, `bash`). A path is confined to the checkout:
  an absolute one, or one that climbs out with `..`, is rejected by name — the same rule
  `java.jarPath` follows, and for the same reason, since the catalog is shared configuration you
  clone rather than write.
- `windowsCommand` replaces `command` when the AppHost runs on Windows. It exists because one
  catalog is committed and shared by a team across platforms, so each value can only be spelled one
  way, and `./prepare.sh` isn't executable on Windows. Leave it out for a program that exists as an
  executable everywhere (`make`, `python`, `dotnet`) — the command runs there unchanged.

  **`npm` is not one of those, and it's the case to know about.** There is no `npm.exe`, only
  `npm.cmd`; nothing here goes through a shell, and Windows resolves a bare name on `PATH` by
  appending `.exe` rather than by walking `PATHEXT`. So `["npm", "ci"]` starts fine on Linux and
  macOS and fails to start on Windows, and wants `windowsCommand: ["npm.cmd", "ci"]` beside it. The
  same goes for `yarn`, `pnpm` and `tsc`. The launch failure names this as the likely cause.
- The command runs with the checkout as its working directory, with no shell between it and the
  tool, and both its streams are relayed line by line as they arrive, tagged with the service.
  **A tool that prints a progress meter floods this** — a plain `curl` download, for one — since
  every carriage-return-terminated update becomes its own line; prefer `curl -sS` or
  `--no-progress-meter` (or the equivalent for whatever tool the command runs):

  ```
  [prepare routing] Downloading graphhopper-web-11.0.jar...
  [prepare routing] Importing sweden-latest.osm.pbf
  [prepare routing] done in 4m12s
  ```

  On the run that creates the checkout under
  [`UseDeferredCheckout()`](#first-run-usedeferredcheckout) those lines are the service's own
  resource log, visible in the dashboard, and the service shows a **Preparing** state while the step
  runs — which is what a four-minute import needs, so that it reads as an initialization phase
  rather than as a hang. Every other run reports to the AppHost's standard output: under
  `dotnet run` that is your terminal, and under `aspire run` the CLI relays it, live, into its own
  log under `~/.aspire/logs/` rather than printing it. A capped copy — its first and last lines,
  with anything in between marked as elided — also lands in the same resource log once the
  dashboard exists, so the record ends up where the service is even on a run the console alone
  would have hidden it from.
- A non-zero exit fails the service, naming it, the resolved command, the exit code and the tail of
  the output. During composition that fails the AppHost, exactly as a bad `repository` or a missing
  `project` does; on a deferred first run it costs that one service and nothing else.
- **Ctrl-C stops it.** On a deferred first run the shutdown signal reaches the command's own process
  tree, so interrupting a long import ends the import rather than leaving it — and its children —
  running with no AppHost left to belong to. There is no timeout: a legitimate bootstrap can take an
  hour, and there is no defensible default to cut it off at.
- **`aspire publish` doesn't run it.** Publish composes the model, writes the manifest and exits,
  and a bootstrap produces what a service needs in order to *run* — so a manifest doesn't depend on
  it, and paying a multi-gigabyte download on every CI publish to emit one would be nothing but
  cost. The block is still validated there, so a typo'd mode or a command pointing outside the
  checkout still fails a publish. The skip is reported, because it has one consequence worth naming:
  the step runs *before* the kind judges the checkout, so a service whose committed files aren't
  enough for its kind on their own — a generated `.csproj`, a generated project directory — is
  reported as missing them. Run the AppHost once to materialize the checkout, then publish.

**Choosing a mode.** All four answer one question — how often does this run — and the guards nest:

| Mode | Re-runs when | For |
| --- | --- | --- |
| `oncePerCommit` *(default)* | the command changes, or the checkout moves to another commit | a bootstrap **defined by a script the repository commits** |
| `once` | the command changes | an expensive bootstrap **independent of the commit** |
| `always` | every start | an incremental script that decides its own work |
| `never` | — | opting out of a step the catalog declared |

**"the checkout moves to another commit" is more concrete than it sounds.** A warm managed checkout
is never fetched or moved on your behalf — the commit only moves when you move it — so under the
default `oncePerCommit`, **`git pull` in a service checkout is what causes `prepare` to run again on
the next AppHost start.** That's an ordinary, frequent action, and nothing about running it reads as
"approve a script to run on my machine"; see [`SECURITY.md`](https://github.com/flojon/aspire-servicesources/blob/main/SECURITY.md) for why that's worth
knowing rather than just worth stating.

The `once` vs `oncePerCommit` question is **"does the repository define this step?"** rather than
"how often" — and the test is where the *version* is written, not where the expensive part is.

The example above is the default for that reason, even though its `java.jarPath` looks pinned.
`prepare.sh` is committed in that repository and hardcodes the GraphHopper version inside itself, so
a team bumping it to 12 moves the commit while the catalog's command stays `["./prepare.sh"]`. Under
`once` the marker would never invalidate, and every developer would keep serving the 11 jar until
someone thought to delete a marker they have never heard of. The catalog's `jarPath` naming a
version is what makes `once` *look* right here; the download is pinned in the script.

Reach for `once` when the command itself carries everything that decides what it produces, so a
commit cannot change the answer — `["./fetch-model.sh", "--release", "v4.2"]`, or a step whose whole
input is a URL the catalog spells out. Then a developer committing a one-line README fix shouldn't
pay the download, and under `once` they don't. The default errs the other way on purpose: an
unexpected re-run is annoying and immediately visible, where a stale artifact is invisible and
surfaces later as a confusing runtime failure.

**The command must be safe to re-run.** Under `always` that's self-evident. Under the two guarded
modes it is equally required and less obvious: the completion is recorded **only on success**, so a
step that fails halfway runs again from the beginning on the next start, against a checkout that
already holds whatever the first attempt managed to produce. A command that can't tolerate that is
incorrect under every mode.

Nothing here detects that a step's *outputs* are stale — at most that the checkout moved. That is
deliberate: nothing in the catalog says what your script reads and writes, hashing the working tree
is expensive and answers wrongly in both directions, and re-running whenever the tree is dirty would
pay the full bootstrap on every start for exactly the developer `"repository"` exists to serve.
Incremental rebuild is a solved problem with real dependency graphs behind it — `make`, Gradle,
MSBuild, `npm ci` — so `mode: always` with a command that guards itself delegates to them:

```sh
[ -f graphhopper-web-11.0.jar ] && [ -d data ] && exit 0
```

**How completion is recorded, and how to force a re-run.** A managed checkout keeps a marker at
`<checkout>/.git/servicesources-prepare.json`, holding a hash of the resolved command and the commit
it ran against. Inside `.git` deliberately: it's invisible to the service repository's `git status`,
so it can't be committed by accident, and it dies with the checkout — so a deleted-and-recloned
checkout re-prepares even at the same commit. Delete the file to force a re-run. Each service's
checkout keeps its own, so two services from one repository each pay for their own bootstrap; that
is correct rather than merely tolerable, since a jar downloaded into one clone is not in the other.

Every decision to run says why — no completion recorded, the command changed, the commit moved, the
commit couldn't be determined, or the mode is `always`. A decision to *skip* says nothing: that's
the ordinary case, and the marker already records it.

**Your own directory declares its own step.** A service pointed at your own directory — the
[`"path"` source](path.md) with `path.path`, or the deprecated `repository.path` — **never inherits the catalog's
`prepare` block**. Nothing establishes that your directory is even a checkout
of the repository the catalog names — `path` is validated by "does it exist" and nothing else — so a
catalog command like `["npm", "ci"]` would run perfectly happily in a tree that has nothing to do
with it. And it's your working tree, holding your in-flight work, where a repository's own bootstrap
script is entitled to run `git clean`. So the command that runs there has to be one you wrote:

```json
{
  "services": {
    "routing": {
      "source": "path",
      "path": {
        "path": "/home/dev/code/routing",
        "prepare": { "command": ["./prepare.sh"] }
      }
    }
  }
}
```

A catalog block on such a service is **ignored, not rejected** — it's the team's field and applies
correctly to every developer on a managed checkout, so your local override must not turn it into a
failure. Instead a notice at startup names the service and the command that was not run, verbatim,
so you can paste it into the file above. It repeats on every start until you declare a block of your
own, and **any** declared block silences it — including `{ "prepare": { "mode": "never" } }`, which
is how you say that nothing should run there. (A `mode` with no `command` is otherwise rejected on a
`path` service: you'd have written the half of the block that can't stand alone, and there is no
catalog command for it to apply to.) The marker for such a checkout lives in the tool's own tree, at
`<AppHostDirectory>/.servicesources/prepare/<service>.json`, keyed on the resolved path as well as
the command — writing into a directory this tool doesn't own is the one thing `path` promises never
to happen.

**This notice is not a general consent rule, and it's easy to read it as one.** It exists because a
`path` checkout is *your* working tree — the axis it protects is blast radius, not letting a
catalog command mutate a directory this tool doesn't own. A managed checkout under
`.servicesources/checkouts/` is exactly the opposite: fully tool-owned, so nothing here asks before
its `prepare` step (or the `dotnet`/`javascript`/`java` build that follows it) runs the first time —
yet that script is foreign code in the sense that matters for [`SECURITY.md`](https://github.com/flojon/aspire-servicesources/blob/main/SECURITY.md): written
and reviewed by the service repository, not by you. Meeting this notice on a `path` service is not a
sign that a managed one asks first too.

**Overriding a catalog step per developer.** Your block is merged over the catalog's **per field**,
with one exception: `mode` overrides on its own, and `command`/`windowsCommand` are replaced
*together* if you supply either. Splitting the pair is never what anyone means — you'd run your own
command on Linux and the team's on Windows.

| You write in `servicesources.local.json` | Effect |
| --- | --- |
| `{"mode": "never"}` | the catalog's step is disabled; nothing runs |
| `{"mode": "always"}` | the catalog's command runs on every start |
| `{"command": ["make", "bootstrap"]}` | the catalog's `command` **and** `windowsCommand` are both replaced, so `make` runs on Windows too. Mode kept |
| `{"command": [...], "windowsCommand": [...]}` | both replaced, mode kept |
| *(absent)* | the catalog's block stands |

A block with no catalog block behind it stands on its own: you may introduce a step the catalog never
declared.

**Two steps can run at once.** Eagerly-resolved services prepare one after another, because
`AddService()` is serial. Deferred ones don't: `UseDeferredCheckout()` gives each service a task of
its own precisely so that one slow checkout isn't the start of every other, and serializing the step
inside it would put that coupling straight back. So a prepare command has to tolerate running
alongside a *different* service's command. What those two share is the machine and whatever package
caches they use — never a working tree, since managed checkouts are per-service clones and the one
arrangement that shares a tree (two services on one `path`) never defers. Most caches are built for
that (`~/.nuget/packages`, npm's); a Maven local repository is not, so a step that resolves into
`~/.m2` and must not overlap with another should take its own lock. A service never prepares
concurrently with *itself*.

**What this deliberately isn't.** One command, one marker, per service: no task runner, no ordering
between steps, no caching of produced artifacts across developers, no timeout (Ctrl-C works on a
deferred first run, and a country-sized routing graph has no defensible default) and no injected
environment variables.
`prepare` also belongs to the `"repository"` *service* source; the `"local"` source a
[backing service](../guides/backing-services.md) can have means something else
entirely, with no repository and so nothing to bootstrap.

## Aspire builds a checkout, on every start

Nothing in this package compiles a checkout, and nothing needs to. A `dotnet` service is
registered with Aspire's own `AddProject`, and Aspire launches that resource with `dotnet run`,
whose working directory is the checkout itself. The build you would otherwise have to arrange is
that command's own implicit incremental build.

So a checkout cloned for the first time compiles when the resource starts — a cold clone with no
`bin/` needs nothing done to it first — and a checkout whose `ref` you change is recompiled on the
next run rather than served from the previous ref's binaries. That last one is worth stating
outright, because the failure it *doesn't* have would be a quiet one: a service answering with
code you moved away from.

Two things to know when it goes wrong:

- **The compiler's output isn't in the AppHost's console.** It goes to that resource's console in
  the dashboard, like any other project resource, so the reason a checkout wouldn't compile is one
  click away rather than in the terminal you launched from. What the AppHost's console does say is
  that the service isn't running (#150):

  ```text
  fail: Aspire.Hosting.ServiceSources[0]
        Service 'orders' is configured as 'repository' and its resource is not running: it reported
        'Finished' with exit code 1. This console does not carry that resource's output, so
        nothing here says why — its own console in the Aspire dashboard does, at the dashboard
        URL logged above. A 'repository' service runs from a checkout rather than from a project
        added to this AppHost, and the build of that checkout writes to those same logs — so a
        failure to compile is reported nowhere else at all.
  ```

  One line per failing resource instance, for every source rather than `"repository"` alone, whenever
  it reports `FailedToStart` or ends with a non-zero exit code. A replicated service gets one line
  per replica that failed, naming which one and its own exit code; an unreplicated one gets a
  single line and no instance id.

  It errs towards saying nothing rather than crying wolf, because a channel that sometimes lies is
  one you learn to ignore — which is the problem it exists to fix. So none of these are reported:
  a terminal state whose exit code was never reported, an orderly Ctrl-C, a resource you stopped
  yourself from the dashboard, and `RuntimeUnhealthy` — the last says your *container runtime* is
  unreachable, not that this service failed, and an AppHost started before Docker has finished
  booting reports it for every container-backed service and then starts them all normally once the
  runtime answers. A service you restart and that fails again *is* reported again. The line says
  only *that* the service isn't running and where to look: what went wrong belongs to the process
  Aspire launched, whose output this package doesn't own. On a run with no dashboard — a
  `DistributedApplicationTestingBuilder` host, or an AppHost that turned it off — the line points
  at the resource's own logs instead of naming a dashboard that isn't there.

  `"repository"` is where this matters most, and why it was asked for there. You never added the
  project — you wrote a name in `servicesources.local.json` — and you didn't choose where its code
  lives, so a resource that quietly fails to appear is one you may not know to look for. The same
  reasoning already covers a clone that fails for a service nothing waits on; this is the step
  after it.

- **Two `path` services in one repository can collide.** If both point into the same repository
  and their projects share a `ProjectReference`, Aspire starts both at once, and two builds write
  that shared project's `bin/`/`obj/` simultaneously — which fails intermittently, with an
  `MSB4018` or `CS2012` naming a file "being used by another process"
  ([microsoft/aspire#15190](https://github.com/microsoft/aspire/issues/15190)). Managed checkouts
  can't hit this: each service gets its own clone under `.servicesources/checkouts/<serviceName>/`,
  so there is no shared output directory even when two services come from one repository.

Launching the AppHost from an IDE is the one case this doesn't cover. An IDE that starts project
resources itself, to attach a debugger, builds them the way it builds anything else — and a
project reached by a path isn't in your solution, so it may not be built at all
([microsoft/aspire#2154](https://github.com/microsoft/aspire/issues/2154), open upstream).

## First run: `UseDeferredCheckout()`

On a cold clone, `AddService()` blocks until the checkout it needs is on disk. Composition
hasn't finished, so the AppHost hasn't started, so there is no dashboard to look at while
several repositories clone — and a checkout that fails throws out of composition and takes the
whole AppHost down with it, including the services that were fine.

`builder.UseDeferredCheckout()` moves that wait past startup for the case where it hurts: a
`"repository"` service whose *managed* checkout doesn't exist yet. The resource is registered against
the path its checkout will have, held back with Aspire's own explicit-start behaviour, cloned
while the AppHost runs, and started when its checkout lands:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.UseDeferredCheckout();

var orders = builder.AddService("orders").WithHttpEndpoint();
```

The dashboard comes up immediately, checkout progress and failure become resource state you can
see, and one bad clone costs one service instead of the run. The clones stay parallel: a deferred
service's clone starts at its own `AddService()` call and blocks nobody, so several of them still
run at once — the wall-clock is the slowest clone, not the sum. The one thing that still clones in
turn is a third-party `kind` handler that declares deferral support and then declines it for a
particular service; the built-in `dotnet`, `java` and `javascript` kinds never do. If you maintain
a kind of your own, [Implementing a kind](../guides/non-dotnet-services.md#implementing-a-kind) covers the two members that opt into
this and what declining late costs.

It also stops the AppHost downloading repositories it doesn't use. Without deferral the clones have
to start before the AppHost has said which services it wants, so every `"repository"` entry with no
checkout yet is cloned; a deferred one is cloned only when it is added (#76).

The wait is one you can watch. git's own progress becomes the service's state — the phase it is
in, that phase's percentage, and the bytes transferred while a pack is arriving
(`Receiving objects 48% · 18.54 MiB`) — with every line git writes going to the service's console
logs as it arrives. A failure lands in the same two places, plus one line in the AppHost's own
console saying that service isn't running — the same line
[a checkout that won't compile gets](#aspire-builds-a-checkout-on-every-start), because both are
read off the resource's state rather than off any one failure path. Nothing appears for a
repository small enough that git reports nothing, which is normal rather than a sign of a stall.

**What a cold checkout costs, and what it doesn't.** This part is about the `dotnet` kind. The
`java` and `javascript` kinds have no launch profile and read nothing out of the repository while
composing, so deferral costs them nothing at all — skip to *Scoped deliberately narrowly* below.
(One `javascript` exception, covered there: `appType: node` and `appType: bun` are deferred only
when the catalog guarantees a `package.json`.)

Aspire reads a project's launch profile
while composing the AppHost and turns it into endpoints, environment variables and command-line
arguments there and then. A deferred service has no repository on disk at that point, so all
three come out empty — and nothing re-runs the step.

Environment is put back for you. Once the clone lands, the profile's `environmentVariables` are
applied to the resource before it starts, and only where the AppHost hasn't already set the same
key, so `WithEnvironment` and `WithReference` still win. That matters more than it sounds:
`Host.CreateDefaultBuilder` takes the environment name from `DOTNET_ENVIRONMENT`, which most
repositories set in the launch profile and nowhere else, so without this a deferred service runs
as `Production` while every warm run of it runs as `Development`.

Values are expanded, and the service's own `DOTNET_LAUNCH_PROFILE` is set to the profile it was
started under — both as Aspire does them on a warm run. The profile read is whichever one Aspire
itself will select, which is the same selection it makes for the service's command-line arguments
once the checkout has landed: the profile your AppHost was launched under, when the service has
one by that name, and otherwise the first launchable profile in the file. So the process never
ends up with one profile's environment and another's arguments.

Endpoints can't be, because ports are allocated during composition and the spec is frozen. So a
deferred service carries only the endpoints you declare:

```csharp
var orders = builder.AddService("orders").WithHttpEndpoint();
```

You are not asked for that line up front, and a service that doesn't need it isn't refused — a
run-to-completion worker has no `applicationUrl` on either path, so demanding one would mean
declaring an endpoint it never listens on. Instead the real launch profile is read once the
checkout lands and the shortfall is reported then, quoting the `applicationUrl` it actually
found: the project still binds that URL itself and runs, but Aspire allocated no endpoint, so
the port isn't moved off a collision, nothing proxies it, service discovery can't resolve the
service and the dashboard won't link it. Add the line and the next run is whole; it is correct
on a warm checkout too, where it updates the endpoint the profile already created rather than
adding one.

Scoped deliberately narrowly, so the blast radius is first-run-only:

- Only a checkout that doesn't exist yet. A warm checkout — every run after the first — takes
  the existing eager path unchanged, with full launch-profile fidelity.
- Only managed checkouts. Your own directory (the [`"path"` source](path.md)) has nothing to clone.
- Only the `"repository"` source, and within it only the kinds that own a managed checkout: `dotnet`,
  `java` and `javascript`. The other sources — `url`, `kubernetes`, `container` and
  [`path`](path.md) — never clone a repository, so they have nothing to defer.
- Only run mode. `aspire publish` and manifest generation clone first as they always have; a
  manifest written from a repository that isn't on disk would describe a project without its
  endpoints or its profile environment.

The `java` and `javascript` kinds get the same treatment for free, and without the endpoint
caveat above: `java` requires `port` in its kind block, and a `javascript` service always gets an
`http` endpoint with a port Aspire allocates when the block doesn't name one. Both come from the
committed catalog, so a deferred `java` or `javascript` service is identical to a warm one. The
checks that do need the working tree — `workingDirectory` and the `mvnw`/`gradlew` wrapper for
`java`, `appDirectory`/`package.json`/`scriptPath` for `javascript` — simply move to just after
the clone, which is where the docs already said they happened. For `javascript`, the separate
resource that runs `npm install` is held back with the app and started ahead of it.

`appType: node` and `appType: bun` are the one exception, and they opt out rather than guess.
Aspire's `AddNodeApp`/`AddBunApp` attach a package manager — and with it the `npm install`
resource the app waits on — only if they can see a `package.json` in the app directory, so what a
warm run builds depends on what the repository holds, and a checkout that hasn't landed can't be
looked at. They are deferred only where the answer is already known: `runScript` is set (which
requires a `package.json` anyway), or `packageManager` names one. Otherwise that one service
resolves eagerly, exactly as it does without `UseDeferredCheckout()`. Every other `appType` runs a
`package.json` script by definition and is deferred unconditionally.

Off by default: a service that used to be running by the time `Build()` returned is started
after it instead, which is visible to anything in your AppHost that assumed otherwise. Call it
before your first `AddService()`, which is where the decision is made — the same ordering
`AddServiceCatalog()` requires. A call made after any service has already resolved throws
`ServiceSourcesConfigurationException` naming the service, rather than silently having no effect
on it.

## Managed checkouts don't inherit your AppHost repository's build settings

A managed checkout is cloned *inside* your AppHost's repository, and MSBuild, NuGet, the .NET SDK
host and the compiler's analyzer configuration all find their settings by walking **up** from each
project or source file. Left alone, that means another team's repository gets built under rules
written for yours — most visibly as `NU1008` on every pinned `PackageReference` when your
repository turns on central package management, and least visibly as your `packageSourceMapping`
confining that repository's restores to your feeds (a leak that hides behind a warm
`~/.nuget/packages` and only surfaces on a clean machine or in CI).

So alongside the `.gitignore`, `.servicesources/` gets six tool-managed files, plus an empty
`.mvn` directory, that end those walks there:

| File | Content | Stops |
| --- | --- | --- |
| `Directory.Build.props`, `Directory.Build.targets` | `<Project />` | your repository's build customisation |
| `Directory.Packages.props` | `ManagePackageVersionsCentrally=false` | your repository's central package management |
| `nuget.config` | `<packageSourceMapping><clear /></packageSourceMapping>` | your repository's package source mapping (see the note below) |
| `.editorconfig` | `root = true` | your repository's code style and analyzer severities |
| `global.json` | `{}` | your repository's SDK pin and `msbuild-sdks` versions |
| `.mvn/` | empty directory | your repository's `.mvn/maven.config`, `jvm.config` and `extensions.xml`, and Maven's own root-directory detection, for a **jar**-run Java service (one without its own `mvnw`, which already stops the walk at the checkout) |

Each of the six files is written with a comment saying what it is and why it's there, since you'll
find them on disk with no git history to explain them, and all six are rewritten whenever their
content is out of date, so upgrading the package updates them. `.mvn/` is the exception: Maven
only asks whether the directory exists, so there is no content to write a comment into or to keep
up to date — the directory is created once and then left alone, including anything Maven or a
developer later adds inside it (a `maven-wrapper.properties` for a service that grows its own
`mvnw`, for instance).

Each barrier drops a constraint the checkout never opted into, and only supplies what a checkout
lacks. A checkout carrying its own `Directory.Build.props`, `Directory.Packages.props`,
`.editorconfig`, `global.json` or `.mvn/` is found first and keeps its own settings, including
central package management if that's how that repository builds.

Four of these are worth a note:

- **`.editorconfig` needs its own barrier** rather than riding on the `Directory.Build.props` one,
  because analyzer severity written as `dotnet_diagnostic.<id>.severity = error` comes from the
  `.editorconfig` itself — not from `EnforceCodeStyleInBuild` or `TreatWarningsAsErrors`. Without
  it, your repository's code style raises the checkout's own analyzers to errors.
- **`global.json` has two halves that resolve from different anchors**, so the barrier covers one
  of them completely and the other conditionally. `msbuild-sdks` resolves by walking up from the
  *project*, so it is stopped outright. `sdk.version` resolves by walking up from the *current
  working directory*, so it is stopped only for a build or run launched from inside the checkout —
  which is the working directory Aspire gives a project resource. A build you launch with the
  AppHost directory as its working directory still sees your repository's SDK pin.
- **`nuget.config` is the one barrier that isn't purely permissive, and the one that doesn't stop
  the walk.** NuGet merges every config from the drive root down rather than stopping at the
  nearest, so this file can only override the section it names. It names `packageSourceMapping`,
  and NuGet's `<clear />` discards *every* mapping accumulated before it — your user-level
  `~/.nuget/NuGet.Config` and machine-level ones included, not just your repository's. Inside a
  checkout, package source mapping is therefore off unless the checkout brings its own, while every
  inherited source stays reachable; a package that reaches your global packages folder that way is
  then served from it to restores that *do* have mapping in force, including your AppHost's own,
  because that folder isn't itself subject to mapping.

  The default is this way round because a mapping the checkout was never written against fails its
  restore outright, naming a source rather than the inherited rule behind it. If you'd rather keep
  the mapping enforced inside checkouts and deal with those failures, set
  `SERVICESOURCES_KEEP_PACKAGE_SOURCE_MAPPING=1`: the file isn't written, an existing one is
  removed, and the other five barriers are unaffected.
- **The rest of your `nuget.config` still reaches checkouts** for the same merging reason — your
  `packageSources`, `disabledPackageSources`, `packageSourceCredentials` and `config` sections
  among them. A repository that clears `packageSources` and adds only its own feed — the most
  common customisation there is — therefore still restricts what a checkout can restore, and like
  the mapping leak it hides behind a warm `~/.nuget/packages`. Clearing `packageSources` from here
  isn't the answer: a checkout that legitimately needs your private feed would stop building.

Two upward searches are deliberately left alone, because neither has a neutral value that isn't
also a decision: `Directory.Build.rsp` (MSBuild takes the first one found walking up from the
project, so your repository's response-file arguments still apply) and `.config/dotnet-tools.json`
(which affects `dotnet tool` run inside a checkout). Open an issue if either bites you.

## Some JavaScript and Gradle leaks can't be barriered

The barrier pattern above only works for a tool that **stops at the nearest file it finds while
walking up**. Several of the JavaScript and Gradle mechanisms a `javascript` or `java` service
depends on don't work that way, and no file placed in `.servicesources/` fixes them:

| Mechanism | Why a barrier doesn't work |
| --- | --- |
| npm's `.npmrc` | Not actually a leak: npm's local config comes from the *closest* ancestor holding `package.json` or `node_modules`, which is the checkout itself, so your repository's `.npmrc` never reaches it. |
| pnpm's `pnpm-workspace.yaml` | The nearest ancestor wins, so it looks barrierable — but a `packages: []` file at `.servicesources/` doesn't terminate the walk the way an empty `Directory.Build.props` does. It makes `.servicesources` a workspace root with zero matching projects, and `pnpm install` inside the checkout then reports `Scope: all 0 workspace projects`, exits `0`, and installs nothing — a silent no-op that is worse than the leak it would replace. Neither a checkout-level `.npmrc` (`ignore-workspace=true`) nor the equivalent environment variable changes this; only the `--ignore-workspace` CLI flag does, and this tool does not control how the install command is invoked. |
| Yarn Berry's `.yarnrc.yml` | Yarn *merges* rcfiles from the cwd and every ancestor rather than stopping at the nearest one, and there is no `root: true` equivalent to end the merge. Neutralizing it would mean enumerating every setting (`yarnPath`, `npmRegistryServer`, `nodeLinker`, `npmScopes`, …) and re-stating a default for each. |
| Node's `node_modules` resolution | Node consults every ancestor's `node_modules` in turn; an empty directory does not stop the search the way an empty file stops MSBuild. A dependency your repository happens to have installed above the checkout can resolve into a service that never declared it — passing on your machine and failing in the service's own CI. |
| Gradle's settings-file search | Searched in the cwd and every ancestor up to the filesystem root, stopping at the first hit — so a single-project repository carrying `gradlew` and `build.gradle` but no `settings.gradle` is captured by your repository's. A barrier gains nothing here: Gradle reports *"not part of the build defined by settings file … must have its own settings file"* regardless of what a barrier file said, so the failure is loud and already names the fix. |

Gradle is the one loud failure here, naming its own fix. The other three are not: Yarn silently
applies whatever your repository's `.yarnrc.yml` says (a registry, a linker mode, a scope) with no
error at all; `node_modules` silently resolves a dependency the checkout never declared, the same
"works on your machine, fails in CI" shape as the NuGet gap above; and pnpm's is the quietest of
all — a full install that reports success while installing nothing. Open an issue if one of them
costs you real time.

## Several services from one repository

Group them: declare the repository once and have each service join it, and every member shares
one managed checkout automatically — cloned once, reconciled onto one ref, at
`.servicesources/checkouts/<repository name>/`.

=== "C#"

    ```csharp
    var monorepo = catalog.AddRepository("monorepo", "https://github.com/example/monorepo", defaultRef: "main");

    catalog.AddService("orders")
        .WithSharedRepository(monorepo)
        .WithProject("src/Orders.Api/Orders.Api.csproj");

    catalog.AddService("payments")
        .WithSharedRepository(monorepo)
        .WithProject("src/Payments.Api/Payments.Api.csproj");
    ```

=== "YAML"

    ```yaml
    repositories:
      monorepo:
        repository: https://github.com/example/monorepo
        defaultRef: main
    services:
      orders:
        repositoryRef: monorepo
        project: src/Orders.Api/Orders.Api.csproj
      payments:
        repositoryRef: monorepo
        project: src/Payments.Api/Payments.Api.csproj
    ```

A grouped repository's own `ref` and `prepare` step (see [`prepare`](#prepare-a-checkout-that-has-to-bootstrap-itself)) belong to the group, not to any
one member — set them on the handle `AddRepository` returns (`WithPrepare`), or on the
`repositories:` entry in yaml, and a developer overrides the ref for everyone under
`ServiceSources:Repositories:<name>:ref` in `servicesources.local.json` rather than a member's own
`repository.ref`, which a grouped service can no longer set.

Two services naming the same `repository:` URL **without** joining a `repositories:` entry are
not grouped by that alone — each still gets its own checkout, cloned and reconciled separately,
and ServiceSources warns about it at startup. That is a suggestion, not an error: an existing
catalog written this way keeps working, and the warning goes away the moment you group them.

**The per-service escape from a group** is the [`"path"` source](path.md) with your own
`path.path`, the same override an ungrouped service has: point one developer's own working copy of
one member at a directory you manage yourself. There is no repository-level equivalent — it
redirects one service at a time, never a whole group's shared checkout in one setting.

**None of this applies to the [`"path"` source](path.md).** Grouping exists to avoid cloning
one external repository twice, and a `"path"` service has nothing to clone — two of them naming
the same resolved directory are just two entries pointing at it, no `repositories:` entry needed.

