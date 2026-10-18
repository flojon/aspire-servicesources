# Running tests

Test projects multi-target `net8.0;net9.0;net10.0`, so a plain `dotnet test` runs each test three times.

**Cheap leg, every round:** during implementation and code-review rounds, scope to .NET 10 only. TFM-specific failures are rare, so running net8/net9 on every round wastes time for no signal:

```bash
dotnet test -f net10.0
```

**Expensive leg, once before landing:** run the full matrix (no `-f`) once, after the final pre-land rebase and before marking the PR ready for review — not before opening the draft PR, which only needs the cheap leg green. Make sure all three target frameworks pass:

```bash
dotnet test
```

## Test traits

Test classes carry class-level xUnit traits that mark why a class could be flaky. A class with no trait is a pure in-memory unit test.

| Trait | Put it on a class that... |
|---|---|
| `[Trait("IO", "true")]` | touches the real filesystem (`TempDirectories`, `File`/`Directory`), spawns real processes (`git`, `dotnet`, `sleep`), or builds a real host that does |
| `[Trait("Concurrency", "true")]` | coordinates real threads or tasks (`Task.Run`, barriers, semaphores, concurrent callers racing on shared state) |
| `[Trait("Timing", "true")]` | relies on wall-clock waits or timeouts for synchronization (`WaitAsync(timeout)`, `CancelAfter`, polling) |

Traits combine. `Concurrency` plus `Timing` is the highest flake risk: real concurrency with wall-clock timeouts. Fake time (`ManualTimeProvider`) and a `Task.Delay` that only simulates work do not count as `Timing`.

Add the traits when you add a test class. Filter on them with `--filter` (xUnit trait syntax):

```bash
dotnet test -f net10.0 --filter "Timing=true"                  # the flake-risk subset
dotnet test -f net10.0 --filter "Concurrency=true|Timing=true" # repeat these when chasing a flake
dotnet test -f net10.0 --filter "IO!=true&Timing!=true"        # fast, hermetic subset
```

When a test flakes, check its class's traits first. A flake in a class without `Timing` or `Concurrency` usually means a missing label or shared state between tests.
