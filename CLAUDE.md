# Running tests

Test projects target `net10.0`, so `dotnet test` runs each test once.

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
dotnet test --filter "Timing=true"                  # the flake-risk subset
dotnet test --filter "Concurrency=true|Timing=true" # repeat these when chasing a flake
dotnet test --filter "IO!=true&Timing!=true"        # fast, hermetic subset
```

When a test flakes, check its class's traits first. A flake in a class without `Timing` or `Concurrency` usually means a missing label or shared state between tests.
