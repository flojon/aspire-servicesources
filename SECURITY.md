# Security

## Trust model: what a catalog entry can do

`servicesources.yaml` is shared, reviewed configuration, but a service entry in it is not itself
running code — it is an instruction to fetch and execute code that the **service repository**
controls, on every developer machine that resolves it. The catalog only names *which* code runs;
the service repository decides *what that code does*, and the two are usually reviewed by
different people — the second set is often not this AppHost's own team.

What runs, concretely:

- `prepare.command` runs a script the service repository commits.
- `kind: dotnet` builds and runs the checkout's project — MSBuild evaluates the repository's own
  `.targets` files and inline tasks.
- `kind: javascript` runs the package manager in the checkout, which runs the repository's own
  `package.json` lifecycle scripts.
- `kind: java`'s `mavenGoal`/`gradleTask` run the checkout's own wrapper script.

None of this is a bug to fix. Running another team's service locally is the point of this package
— refusing to build or run a checkout isn't a safer version of the feature, it's a different
product. What a developer adopting the package has had no single place to learn is the size of what
they are opting into, and the one lever that shrinks it.

## A consent notice already exists — and it doesn't cover this

A `local.path` service (an existing checkout you point the tool at, rather than one it clones and
manages) already refuses to inherit the catalog's `prepare` block: it prints the resolved command
at startup and asks you to paste it into `servicesources.local.json` yourself, saying plainly that
nothing runs in a directory this tool doesn't own unless you asked for it there. That's real
consent, and it already ships.

But it's drawn on **blast radius** — don't let a catalog command mutate a directory this tool
doesn't own — not on **provenance**, which is what this document is about: whether the code being
run is one you or your team reviewed. The two axes come apart exactly where they matter most: a
managed checkout under `.servicesources/checkouts/` is fully tool-owned, so nothing about that
notice applies to it, and nothing else asks before its `prepare` step — or the `dotnet`/`javascript`/`java`
build that follows it — runs for the first time. Yet that checkout's script is the foreign code most
worth being asked about. A developer who has met the `path` notice and reasonably concludes some
general "this tool asks before running anything foreign" rule exists would be wrong.

## The lever: pin `defaultRef` / `local.ref` to a commit

`defaultRef` (in the catalog) and `local.ref` (a developer's own override) both accept a commit
SHA, not only a branch or tag name. A catalog that pins a reviewed commit gets every developer a
reviewed checkout. A catalog that names `main` gets whatever is at the tip the first time each
developer clones — and, per the next section, on some runs after that too.

Pinning costs a catalog edit on every bump. That's the actual trade: a team that wants every
developer running exactly what was reviewed pays for it in commits to `servicesources.yaml`; a team
that trusts the service repository's own `main` doesn't need to.

## When re-execution actually happens

A warm managed checkout is not silently updated on every AppHost start. Git resolves a local branch
ref before touching the remote, so a checkout already sitting on local `main` compares equal to
itself and the existing checkout is reused without a fetch. The commit only moves when the
developer moves it.

Which means, under `prepare`'s default `oncePerCommit` mode: **`git pull` in a service checkout is
what causes its `prepare` step to run again on the next AppHost start.** Pulling a repository is an
ordinary, frequent action that nobody reads as "approve a script to run on my machine" — worth
knowing, since it's the actual trigger rather than anything more deliberate.

See [`prepare`](README.md#prepare-a-checkout-that-has-to-bootstrap-itself) in the README for the
full behaviour of that step, including how a developer overrides or disables a catalog's block
entirely (`{ "prepare": { "mode": "never" } }`).

## Reporting a vulnerability

Open an issue on this repository.
