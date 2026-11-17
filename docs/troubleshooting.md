# Troubleshooting

## When configuration is wrong

Every problem this package detects — a missing project file, an unregistered `kind`, a checkout it
won't overwrite, a clone it can't authenticate — is raised as a `ServiceSourcesConfigurationException`
whose message names the service, what failed, and what to do about it. Because these are raised
from `AddService()`, they usually reach you as an unhandled exception that takes the AppHost down
before Aspire starts, so that message *is* the error output. It prints as the message plus one
line per underlying cause:

```
Unhandled exception. Service 'reportdata': failed to clone repository 'https://github.com/acme/reportdata' into
'/src/report-service/src/Report.AppHost/.servicesources/checkouts/reportdata' — authentication failed, or the
repository is not visible to the credentials in use. Configure credentials via a git credential helper (`git
credential fill` must resolve them for this host) or the SERVICESOURCES_GIT_USERNAME/SERVICESOURCES_GIT_TOKEN/
SERVICESOURCES_GIT_HOST environment variables.
  caused by: unexpected http status code: 404
  (set SERVICESOURCES_FULL_ERRORS=1 for the full exception detail, including stack traces)
```

## Long paths on Windows

On Windows, git is run with `core.longpaths=true`, so a repository with deeply nested files can be
cloned and checked out under the AppHost's directory. Windows itself must still allow long paths for
the tools that later read those files (`LongPathsEnabled`), and a shorter AppHost location helps
either way. Clones are staged in `.servicesources/checkouts/.incoming-<8 hex>` and renamed into
place; a staging directory left by a clone that was killed is removed the next time any clone starts
in that folder (the clone's log says so), so you can also delete it by hand. Each managed clone also
has `core.longpaths=true` in its own git config, so plain `git status` and your IDE work in it too.

### Deep AppHost directories: the git directory moves out of the checkout

git refuses to run in a repository whose git directory path is longer than 220 characters, failing every
command with `fatal: '$GIT_DIR' too big`, whatever `core.longpaths` or `LongPathsEnabled` say. The limit
is `PATH_MAX - 40` in git's `setup.c`, with Git for Windows' `PATH_MAX` of 260, so it is fixed when git is
built and cannot be configured. It was measured on git 2.52.0.windows.1 only; other versions are not known
to behave the same. For a checkout the git directory is the checkout path plus 5 characters (`\.git`), so a
checkout path of 215 characters or fewer works in place and 216 or more does not.

When the AppHost directory is deep enough that it would not fit (more than 170 characters),
the clone is made with `git clone --separate-git-dir`. The working tree stays at
`<AppHost>\.servicesources\checkouts\<name>`, and its `.git` is a file that points at a short git
directory under `%LOCALAPPDATA%\ServiceSources\git`. Plain `git` and your IDE work in the checkout
as usual. A shallower AppHost is cloned exactly as before, with `.git` inside the checkout.

- A git directory that no checkout refers to any more (the checkout was deleted, or its AppHost
  moved) is removed the next time a deep clone runs once it has gone 14 days without use. Each one is a
  full clone, so once you have deleted a checkout you will not need again you can remove its folder
  under `%LOCALAPPDATA%\ServiceSources\git` yourself (the `servicesources-owner.json` inside each
  folder names its checkout); never remove the folder of a checkout that still exists. A checkout in use keeps its own git directory alive.
- A checkout whose AppHost directory was moved or renamed is not picked up again: its `.git` file
  names the old location, and it is refused with a message that says so. Delete the checkout
  directory and re-run to clone it again.

### `cannot clone into …: … at least N characters shorter`

Even with the git directory elsewhere, git for Windows cannot find a repository whose `.git` file path
passes 259 characters, so a working tree's own directory path is limited to 254 characters at the very
most. The limit used here is 235, which leaves a margin (the clone is staged under `.incoming-<8 hex>`, a 19-character
segment including the backslash, so for a checkout name of up to 18 characters the staging path is
the one that counts). For such a name that is an AppHost directory of 190 characters. Beyond it the error
names the length and how many characters to remove; move the AppHost, or the worktree it is in, to a
shorter path. If `%LOCALAPPDATA%` is unavailable or its `ServiceSources\git` folder would itself be
too long, a deep AppHost is refused with the `'$GIT_DIR' too big` wording instead (it needs a path of
at most 215 characters, so that git can keep its git directory in the checkout).

### A checkout made before the git directory moved out is too deep to maintain

An existing checkout is never moved. If its git directory is still inside it and its path is too deep
for git (a checkout path past 215 characters), a start logs a notice: every git command in it fails
with `'$GIT_DIR' too big`.
If it has local changes or unpushed commits, move `.servicesources\checkouts\<name>` out of the AppHost; otherwise delete it.
Then re-run, and it is cloned again with a short git directory (this needs an AppHost path within the limit above, 190 characters for a checkout name of 18 characters or fewer).

## Getting the full exception detail

The stack frames behind that message are this package's own plumbing and don't help with a
misconfiguration, so they're left out — and the last line says how to get them back, because for a
failure this package didn't anticipate they are the diagnosis. When you need them — you suspect a
bug in this package rather than in your configuration, and want to file it — set
`SERVICESOURCES_FULL_ERRORS=1` to get the runtime's complete dump, type names, inner-exception
blocks, stack traces and all.

## Reporting a problem

Open an issue at
[flojon/aspire-servicesources](https://github.com/flojon/aspire-servicesources/issues), with the
full exception detail above.
