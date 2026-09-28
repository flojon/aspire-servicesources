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
