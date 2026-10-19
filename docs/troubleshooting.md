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

On Windows, git is run with `core.longpaths=true`, and each managed checkout has it in its own
`.git/config` too, so a repository with deeply nested files can be cloned and checked out under the
AppHost's directory, and a plain `git status` or an IDE works in it. A checkout cloned by an earlier
version gets the setting the next time the AppHost starts, unless its config already sets
`core.longpaths` either way. Windows itself must still allow long paths for the tools that later read
those files (`LongPathsEnabled`), and a shorter AppHost location helps either way.

Clones are staged in `.servicesources/checkouts/.incoming-<8 hex>` and renamed into place; a staging
directory left by a clone that was killed is removed the next time any clone starts in that folder,
and the clone's progress log says so. You can also delete it by hand.

### "git's pack files there, … need paths of N characters"

```
Service 'orders': cannot clone into 'C:\\…\\.servicesources\\checkouts\\orders': git's pack files there,
including the temporary ones a repack writes, need paths of 271 characters
('C:\\…\\.servicesources\\checkouts\\.incoming-00000000\\.git\\objects\\pack\\.tmp-0000000000-pack-0000…0000.pack'),
and Windows limits them to 259 even with core.longpaths or LongPathsEnabled. Move the repository or
git worktree that holds the AppHost (the directory that holds '.servicesources') to a directory at
least 12 characters shorter.
```

git writes its pack files in a way that `core.longpaths` and `LongPathsEnabled` do not cover, so
past 259 characters a clone fails. The longest is the temporary pack a repack writes,
`.git\objects\pack\.tmp-<pid>-pack-<40 hex>.pack` (85 characters under the checkout, with a
10-digit process id). Repacks run when a clone borrows objects from the main worktree's checkout and
when `git gc --auto` maintains the checkout, so the check uses that length: any checkout it allows
can also do both. The clone is first staged in `checkouts\.incoming-<8 hex>`, so the path in the
message is whichever of that and `checkouts\<name>` is longer, with zeros standing in for the names
git will pick. With checkouts at `<AppHost>\.servicesources\checkouts\<name>`, that is an AppHost
directory longer than about 129 characters, or less for a checkout name over 18 characters. The check
runs before cloning, so nothing is downloaded.

Move the repository that holds the AppHost somewhere shorter, such as `C:\src\<repo>`. If it is a
linked worktree (made with `git worktree add`), move it with `git worktree move <old> <new>` rather
than by hand, which breaks the worktree's link. Move a main checkout by hand, then run
`git worktree repair` if it has linked worktrees.
Or clone the repository somewhere short yourself and point the service at it with the `path` source
(`"source": "path"` and `"path": { "path": "..." }` in `servicesources.local.json`).

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
