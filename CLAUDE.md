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
