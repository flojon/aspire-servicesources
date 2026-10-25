# Choosing a source in the dashboard

A service nobody has chosen a source for normally stops the AppHost with `'orders' has no source
configured`. Call `UseInteractiveServiceConfiguration()` and that service instead comes up as a
`Not configured` placeholder, and the Aspire dashboard asks you which source to use.

```csharp
var builder = DistributedApplication.CreateBuilder(args);
builder.UseInteractiveServiceConfiguration();

var orders = builder.AddService("orders");
```

The call is opt-in: without it nothing changes. Make it before the first `AddService`; a service
added earlier has already thrown, because whether to build a placeholder is decided when the
service is added. A service that is not in the catalog at all still
throws, since that is a mistake in the AppHost rather than something to answer in a dialog. So does
a service whose configured source is invalid: only a missing source becomes a placeholder.

## What you are asked

Once the dashboard is up, one dialog lists every unconfigured service with a choice of source. Each
service is offered only the sources its catalog entry can actually use:

| Source | Offered when the service has |
| --- | --- |
| `repository` | a repository to clone |
| `container` | an image and a port |
| `kubernetes` | a Kubernetes service name |
| `url`, `path`, `disabled` | always |

Leave a service blank to decide later. A source that needs more than its name asks a follow-up in a
second dialog:

- `kubernetes` always asks for a context, and for an optional namespace and endpoint scheme. It
  asks for the service port only when the catalog does not give one.
- `path` asks for a directory, and `url` for the URL, unless the catalog already supplies it.

Answers are checked the way the next run would check them: the same validation as values written by
hand, plus what resolving the source needs (a `url` must be an absolute `http` or `https` URL, a
Kubernetes port must be between 1 and 65535 and its scheme `http` or `https`, and a `path` directory
must exist, relative to the AppHost directory). An answer that would not resolve is reported with the
same message the next run would give, and nothing is saved for that service.

Closing the dialog without saving changes nothing; the services stay unconfigured and the AppHost
keeps running. Each unconfigured service also has a **Configure** command in the dashboard that
raises the same dialog for that one service, if you dismissed it or want to answer later.

## Where the answer goes

Answers are merged into `servicesources.local.json` next to the AppHost, leaving every other
entry as it was. That file is the lowest-precedence configuration layer, so it can still be
overridden from `appsettings`, user secrets, environment variables or the command line. Choosing
`kubernetes` for `orders` with context `dev` and port `8080` writes:

```json
{
  "services": {
    "orders": {
      "source": "kubernetes",
      "kubernetes": { "context": "dev", "port": "8080" }
    }
  }
}
```

- **It takes effect on the next run.** The placeholder cannot turn into a real resource while the
  AppHost is running; restart it. The dashboard says so when the answer is saved.
- **Some files are refused, and left byte-for-byte as they were.** The dashboard says why. The
  file is refused when it:
  - contains comments, which saving would drop;
  - is not valid JSON, including a key that appears twice in one object (keys are
    case-insensitive);
  - does not have a JSON object at its root, or has a non-object (other than `null`) where
    `services` or the service's entry goes;
  - is a directory or a symbolic link, since replacing it would sever the link;
  - is larger than 256 KiB;
  - already holds fields for that service that would fail the next run (say an empty `url.url`
    hiding the catalog's URL, or an unsupported `kubernetes.scheme`). The whole entry is checked as
    it would be written, not only your new answers. Edit the file by hand to fix it.

  A byte order mark and trailing commas are accepted, and a `null` where an object goes counts as
  missing, as it does when the file is read.
- **A higher layer can keep it from applying.** If a provider above the file sets the service's
  `source` to blank, the saved choice is shadowed. The dashboard names the file or kind of layer, and
  the choice takes effect once you remove that setting.
- **A linked git worktree writes its own copy** of the file; it does not change the main
  worktree's.

### Known limits

- **Configure reports success when nothing was saved.** The command succeeds once the dialog has
  run, including when you close it or the save is refused. The outcome is in the notification and
  the log.
- **The write lock is in-process only.** Two dialogs in one AppHost cannot interleave their writes,
  but another process, or an editor, saving the file at the same moment can lose a change.
- **A `path` directory that comes from the catalog is not checked when you save.** A broken catalog
  `path:` only fails on the next run. A directory you type in the dialog is checked.

## When it is off

The prompt follows the same rule as the source-pick prompt, so one setting governs both:

| `SERVICESOURCES_INTERACTIVE` | Behaviour |
| --- | --- |
| unset | Interactive unless `CI`, `TF_BUILD` or `JENKINS_URL` is set |
| `1` / `true` / `yes` | Interactive regardless of CI |
| `0` / `false` / `no` | Strict regardless of CI |

In strict mode an unconfigured service throws exactly as it does without the opt-in, naming the
service and the file. Publish mode always fails fast, because a manifest cannot describe the
placeholder. The override has no effect without the `UseInteractiveServiceConfiguration()` call.

## What the placeholder does meanwhile

The placeholder starts nothing and has no endpoints. Configuration calls chained off the service
(`WithEnvironment`, `WithReference`) are skipped with a warning, and a consumer's `WaitFor` does not
hang on it. `GetServiceEndpoint()` and the type-returning `Unwrap<T>()` still throw, with a message
saying no source has been chosen.
