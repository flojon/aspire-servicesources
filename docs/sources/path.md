# The `"path"` source

Point a service at a directory that's already checked out beside the AppHost — the shape a
genuine monorepo has, where the AppHost and the services it depends on live in one repository, on
one commit, by construction. There's nothing to clone: the directory is already there.

=== "C#"

    ```csharp
    catalog.AddService("orders")
        .WithPath("services/orders")                      // relative to the AppHost directory
        .WithProject("src/Orders.Api/Orders.Api.csproj"); // same rules as every other source
    ```

=== "YAML"

    ```yaml
    services:
      orders:
        path: services/orders                     # relative to the AppHost directory
        project: src/Orders.Api/Orders.Api.csproj  # same field, same rules, as every other source
    ```

`servicesources.local.json`:
```json
{
  "services": {
    "orders": { "source": "path" }
  }
}
```

— or nothing in `servicesources.local.json` at all: see `defaultSource: path` below.

A `dotnet` service can pick its launch profile here the same way it can for `"repository"`, with
`dotnet: { launchProfileName: http }` in yaml or `AsDotnet(o => o.WithLaunchProfileName("http"))` in
code. The name is checked against the directory's `Properties/launchSettings.json` when the service
resolves. See [the `dotnet:` block](../guides/yaml-catalog.md#dotnet-choosing-a-launch-profile).

Non-.NET kinds work exactly as they do for `"repository"` — the `kind`/`project` machinery never
learns the directory wasn't cloned:

=== "C#"

    ```csharp
    catalog.AddService("frontend")
        .WithPath("services/frontend")
        .AsJavaScript(o => o.WithAppDirectory(".").WithRunScript("dev"));
    ```

=== "YAML"

    ```yaml
    services:
      frontend:
        path: services/frontend
        kind: javascript
        javascript:
          appDirectory: .
          runScript: dev
    ```

`path:` combines freely with `repository:`/`url:`/`container:`/`kubernetes:` on the same entry —
see [Combining sources on one catalog entry](index.md#combining-sources-on-one-catalog-entry):
one developer clones the service, another uses the copy they already
have, same catalog entry either way.

**Confinement depends on who wrote the value.** A catalog's own `path:` is committed, shared
configuration, so it's confined to the repository the AppHost lives in: no absolute path, and no
climbing out of that repository with `..`. It's still written relative to the AppHost directory, and
may climb *out of that directory* — the usual layout, with the AppHost in `src/MyApp.AppHost/` and
its services beside it, needs `path: ../Orders.Api`. The repository is the nearest directory at or
above the AppHost holding a `.git` directory or file. Your own override, set in
`servicesources.local.json`, is unconfined instead — it's your own machine and directory, and it
works even for an entry whose catalog declares no `path:` at all:

```json
{
  "services": {
    "orders": { "source": "path", "path": { "path": "/home/dev/code/orders" } }
  }
}
```

**A copy with no `.git`.** A source archive, or a container build context whose `.dockerignore`
leaves `.git` out, has nothing to find the repository by. There, set `ServiceSources:RepositoryRoot`
to the repository's root directory — absolute, or relative to the AppHost directory — from any
configuration layer:

```dockerfile
ENV ServiceSources__RepositoryRoot=/src
```

or `"repositoryRoot": "../.."` at the root of `servicesources.local.json`, or appsettings, user
secrets or the command line. It has to be the AppHost directory or one of the directories above it.
It is read only when no `.git` is found — a `.git` always wins, so the setting can never widen the
boundary a repository draws. With neither, the AppHost directory itself is the boundary, and a
`path:` that climbs out of it is refused with an error saying so and naming this setting.

If the resolved directory doesn't exist, resolution fails naming it and the AppHost directory it
was looked for under — there is nothing to clone, so a missing directory is the only thing a
`"path"` service can fail on before its kind is even consulted.

**No `ref`.** A directory that is not a separate checkout has no second commit for a ref to name:
there's no `path.ref`. A `repository.ref` is not read — `repository` (or its deprecated alias,
`local`) is the `"repository"` source's block, and like any block for a source that isn't selected
it survives but nothing reads it, so one left in a lower configuration layer doesn't stop a higher
layer switching the service to `"path"`.

**`prepare` runs as `once`, `always` or `never` — never `oncePerCommit`.** There's no separate
commit for this directory to move to on its own, so a `mode` left out means `once` here (re-run only
when the command changes) rather than the `oncePerCommit` it means for `"repository"`. A catalog
`prepare:` that writes `mode: oncePerCommit` is read as `once` too: that mode is right for the
`"repository"` source the same entry may also serve, so it isn't an error to reject for every
developer who picks `"path"`. A `path.prepare.mode: oncePerCommit` you write yourself is refused,
naming `once` and `always` as the alternatives.

A catalog-declared `path:`'s own `prepare:` block runs normally: there's no "someone else's
directory" here to protect. A `path.path` override is the exception, exactly like `repository.path`:
it points at your own working tree, which nothing establishes is a checkout of what the catalog
names, so the catalog's step is not run there — a startup notice shows the command, ready to paste
into your own `path.prepare` block. Only a `path.prepare` you declare runs in that directory. Two
services sharing one resolved `path` serialize their step rather than run it concurrently. The
marker lives where a `repository.path` override's does,
`<AppHostDirectory>/.servicesources/prepare/<service>.json`, keyed on the resolved path and the
command.

A service grouped into a `repositories:` entry doesn't inherit that repository's `prepare:` under
`"path"`: the group's step is written to run once at the root of its shared checkout, not in one
member's directory once per member. Only a `path.prepare` block the developer declares runs there.

**`defaultSource: path` is close to free.** Unlike [`defaultSource: repository`'s warning](../getting-started/quickstart.md#defaulting-a-source) —
about the clone every developer and CI trigger by default — a `"path"` service with nothing in
`servicesources.local.json` costs nothing extra when the directory is in-repo: it's already there,
by construction.

=== "C#"

    ```csharp
    catalog.AddService("orders")
        .WithPath("services/orders")
        .WithDefaultSource("path");
    ```

=== "YAML"

    ```yaml
    services:
      orders:
        path: services/orders
        defaultSource: path
    ```

**No interaction with [deferred checkout](repository.md#first-run-deferred-checkout).** Like `url`,
`kubernetes` and `container`, a `"path"` service is always resolved eagerly, with full
launch-profile fidelity — there's no clone to defer, so no "Preparing" state ever appears for one.

**No `repositories:` grouping either.** Grouping exists to avoid cloning one external repository
twice; a `"path"` service has nothing to clone, so there's no shared-checkout identity to opt into.
Two services naming the same resolved `path` are just two entries pointing at one directory.

**`repository.path` is deprecated.** It resolves a directory the same way — no clone, no ref —
reachable a second, less discoverable way, nested under a source whose other machinery it never
touches. It keeps working exactly as it does today, and a startup notice says once to use
`"source": "path"` with `"path": { "path": "..." }` instead — a JSON snippet with your own directory
in it, ready to paste. The one thing that doesn't carry over by itself is a `repository.prepare`
block: it isn't read under `"path"`, so the notice says to move it to `path.prepare` when you have
one. The catalog's own `prepare:` step still isn't run in your directory, under either spelling.
(The deprecated `local` alias for the `repository` block behaves the same way here too — a
service still written as `"local": { "path": "..." }` gets both notices: rename the block to
`"repository"`, and separately, move off `.path` onto the `"path"` source.)

