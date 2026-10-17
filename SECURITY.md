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

A `repository.path` service (an existing checkout you point the tool at, rather than one it clones and
manages) already refuses to inherit the catalog's `prepare` block: it prints the resolved command
at startup and asks you to paste it into `servicesources.local.json` yourself, saying plainly that
nothing runs in a directory this tool doesn't own unless you asked for it there. That's real
consent, and it already ships.

But it's drawn on **blast radius** — don't let a catalog command mutate a directory this tool
doesn't own — not on **provenance**, which is what this document is about: whether the code being
run is one you or your team reviewed. The two axes come apart exactly where they matter most: a
managed checkout under `.servicesources/checkouts/` is fully tool-owned, so nothing about that
notice applies to it. The one thing that asks before a managed checkout's `prepare` step runs is a
**changed command**, described below; the first run — and the `dotnet`/`javascript`/`java` build that
follows — is not asked about. Yet that checkout's script is the foreign code most worth being asked
about. A developer who has met the `path` notice and reasonably concludes some general "this tool
asks before running anything foreign" rule exists would be wrong.

### The one exception: a `prepare` command that changed

When the catalog's `prepare` command for a warm managed checkout is not the one that last succeeded
there, the dashboard asks before running it, showing the command. This is the one re-run whose
command line differs from the one you ran or accepted before. It compares the command line, not the
scripts that command invokes, which change with a moved commit, so pinning `defaultRef` is still the
lever. Everything else that re-runs a step — `always` mode (so a catalog that switches to `always`
with a new command is not asked about), a first use, a moved commit, a re-pointed path — runs
unprompted.

It is a consent gate, not a picker, so it fails closed: **only an explicit "Run" runs the step.**
Closing the dialog, no answer within 5 minutes, or a dialog that cannot be shown all decline, and
the service shows `Skipped` rather than starting; restart the AppHost to be asked again. That
deliberately differs from the cold-checkout source pick, where an unanswered dialog starts every
service. Anything waiting on a skipped service keeps waiting. With no dashboard to ask through (`aspire run` without one, CI), nothing can ask, so the
step runs unprompted and the log says why. `aspire publish` never runs the step. `SetCheckoutTiming(CheckoutTiming.Eager)`
and a kind that cannot be deferred (a custom kind, some `javascript` app types, or one whose
`ResolveDeferred` returns null) also keep running a changed command unprompted. Services sharing a
checkout share one completion marker, so one such member runs and records the changed command and the
deferred members then find nothing changed: it removes the prompt for the whole repository.

The cost: that one run is registered before the checkout is read, like a first-run clone, so it
loses composition-time launch-profile fidelity (a `dotnet` service should declare its own
endpoints, as for any deferred service).

## The lever: pin `defaultRef` / `repository.ref` to a commit

`defaultRef` (in the catalog) and `repository.ref` (a developer's own override) both accept a commit
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

See [`prepare`](docs/sources/repository.md#prepare-a-checkout-that-has-to-bootstrap-itself) for the
full behaviour of that step, including how a developer overrides or disables a catalog's block
entirely (`{ "prepare": { "mode": "never" } }`).

## The `"path"` source: no second repository to trust

Everything above is about `"repository"`: a **second** repository, controlled by a different team,
that this tool clones and builds on your machine. A catalog-declared
[`"path"`](docs/sources/path.md) service has none of that shape. It names a directory that is
already part of this repository — same commit, same code review, same CI as the AppHost itself.
There is no second repository to pin a ref against, because there is no second repository at all;
the trust question this document exists to raise doesn't arise for it.

A developer's own `path.path` override is the one case that keeps the old story unchanged: it
points at a directory that isn't necessarily part of this repository at all — your own out-of-tree
clone of something else, managed however you already manage it. That's exactly what `repository.path`
is today, under a new, first-class name; nothing about the risk moved when the name did. As with
`repository.path`, the catalog's `prepare` step is never run in that directory — only a `path.prepare`
step you declare yourself.

## Reporting a vulnerability

Open an issue on this repository.
