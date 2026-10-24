# Choosing a source in the dashboard

A service nobody has chosen a source for normally stops the AppHost with `'orders' has no source
configured`. Call `UseInteractiveServiceConfiguration()` and that service instead comes up as a
`Not configured` placeholder, and the Aspire dashboard asks you which source to use.

```csharp
var builder = DistributedApplication.CreateBuilder(args);
builder.UseInteractiveServiceConfiguration();

var orders = builder.AddService("orders");
```

The call is opt-in: without it nothing changes. A service that is not in the catalog at all still
throws, since that is a mistake in the AppHost rather than something to answer in a dialog.

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
second dialog: `kubernetes` needs a context (and a port and an optional namespace and scheme, unless
the catalog already gives them), `path` needs a directory and `url` needs the URL, each unless the
catalog already supplies it. Answers are validated like the same values written by hand; an answer
that would not resolve is reported and nothing is saved for that service.

Closing the dialog without saving changes nothing; the services stay unconfigured and the AppHost
keeps running. Each unconfigured service also has a **Configure** command in the dashboard that
raises the same dialog for that one service, if you dismissed it or want to answer later.

## Where the answer goes

Answers are merged into `servicesources.local.json` next to the AppHost, leaving every other
entry as it was. That file is the lowest-precedence configuration layer, so it can still be
overridden from `appsettings`, user secrets, environment variables or the command line.

- **It takes effect on the next run.** The placeholder cannot turn into a real resource while the
  AppHost is running; restart it. The dashboard says so when the answer is saved.
- **A file with comments is refused.** Saving would drop them, so the dialog reports the problem
  and leaves the file alone; edit it by hand instead.
- **A higher layer can keep it from applying.** If a provider above the file sets the service's
  `source` to blank, the saved choice is shadowed. The dashboard names the provider, and the choice
  takes effect once that provider stops setting it.
- **A linked git worktree writes its own copy** of the file; it does not change the main
  worktree's.

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
