# Orphaned Repository Entry Clone Cost (#217) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Superseded in detail by the merged code.** Review rounds reworded both notice tails, added the declining-kind case (four new tests, field `_isRunMode`) and widened the docs to "some javascript app types, or a custom kind". Where this plan quotes notice wording or a test list, the code and tests are authoritative.

**Goal:** Record the decision that a config entry alone stays warrant to clone where `AddService()` must block on the clone, and correct the three places that give wrong or missing advice about it.

**Architecture:** No cloning behaviour changes. `LocalCheckoutPrefetch` remembers whether the builder is in run mode and picks the tail sentence of the unused-checkouts notice accordingly. The user doc bullet and the class remarks are rewritten to match the measured behaviour.

**Tech Stack:** C# (multi-target net8.0/net9.0/net10.0), xUnit, Aspire.Hosting; docs in Markdown.

**Spec:** `docs/superpowers/specs/2026-09-28-217-orphaned-repository-entry-clone-cost-design.md`

## Global Constraints

- No change to which services are cloned, when, or by whom (spec 3.4): `WouldBeDeferredIfAdded`, the candidate filters in `Run`, `ReportSpeculativeWork` timing and the failure notice are untouched.
- No CHANGELOG entry (resolved Open Question 2).
- No new public API and no prefetch opt-out (resolved Open Question 1). `LocalCheckoutPrefetch` is `internal`.
- Notice strings go through the `Raw` seam (`Raw.Compose` / `Raw.Literal`); no user-controlled text beyond the already-escaped `Name` holes.
- Code comments: only the non-obvious WHY, short; no ticket/PR/issue/commit references (user's global rule). Class remarks state constraints, not history.
- Build gate is `dotnet build -c Release --no-restore -warnaserror`; per-task tests run `dotnet test -f net10.0`; full matrix (`dotnet test`, no `-f`) once before landing.
- PR body says `Closes #217`.

## Review Focus

- Publish mode with **no** unused entry: `UnusedCheckoutsMessage` stays `null` (the new branch must not create a notice).
- Publish mode with an unused entry: message names the entry and gives removal as the only remedy, without the words "Deferred checkout" or `SetCheckoutTiming`.
- Run mode, default (Deferred) timing, cold unused entry: nothing cloned, message `null` (regression pin for the fixed default path).
- Run mode, Eager: the existing wording, including `SetCheckoutTiming(CheckoutTiming.Eager)`, is preserved verbatim.
- Grouped services sharing one checkout in publish mode: both names still listed, count still per service.

Each line has its test in Task 1.

---

### Task 1: Truthful unused-checkouts notice in publish mode

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs` (field near line 134, `EnsureStarted` near line 173, `UnusedCheckoutsRaw` tail near lines 236-238)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalCheckoutPrefetchTests.cs` (add after `PublishMode_ColdServiceTheAppHostNeverAdds_IsStillCloned`, ~line 899)

**Interfaces:**
- Consumes: `TestHelpers.CreateBuilder(dir)`, `TestHelpers.CreatePublishingBuilder(dir)`, `CreateAppHostDirectory(params string[] names)`, `Definition(name)`, `DevConfig()`, `FakeGitClient { StartBarrier }`, `LocalProjectSource(git).Resolve(...)`, `LocalCheckoutPrefetch.For(builder, git).UnusedCheckoutsMessage` (all already used by neighbouring tests).
- Produces: private `bool _runMode` on `LocalCheckoutPrefetch`; no signature changes.

- [ ] **Step 1: Write the failing publish-mode test, plus the guard tests**

Add to `LocalCheckoutPrefetchTests`:

```csharp
    [Fact]
    public void PublishMode_UnusedCheckoutNotice_DoesNotOfferDeferralAsARemedy()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreatePublishingBuilder(dir);
        var git = new FakeGitClient { StartBarrier = new Barrier(2) };

        new LocalProjectSource(git).Resolve(builder, "orders", Definition("orders"), DevConfig());

        var message = LocalCheckoutPrefetch.For(builder, git).UnusedCheckoutsMessage;

        Assert.NotNull(message);
        Assert.Contains("billing", message);
        Assert.Contains("1 service", message);
        Assert.DoesNotContain("SetCheckoutTiming", message);
        Assert.DoesNotContain("Deferred checkout", message);
        Assert.Contains("publish", message);
    }

    [Fact]
    public void PublishMode_EveryConfiguredServiceAdded_HasNoUnusedCheckoutNotice()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreatePublishingBuilder(dir);
        var git = new FakeGitClient();

        new LocalProjectSource(git).Resolve(builder, "orders", Definition("orders"), DevConfig());

        Assert.Null(LocalCheckoutPrefetch.For(builder, git).UnusedCheckoutsMessage);
    }

    [Fact]
    public void RunModeEager_UnusedCheckoutNotice_StillNamesEagerAsHowDeferralIsLost()
    {
        var dir = CreateAppHostDirectory("orders", "billing");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.SetCheckoutTiming(CheckoutTiming.Eager);
        var git = new FakeGitClient { StartBarrier = new Barrier(2) };

        new LocalProjectSource(git).Resolve(builder, "orders", Definition("orders"), DevConfig());

        var message = LocalCheckoutPrefetch.For(builder, git).UnusedCheckoutsMessage;

        Assert.NotNull(message);
        Assert.Contains("Deferred checkout", message);
        Assert.Contains("SetCheckoutTiming(CheckoutTiming.Eager)", message);
    }
```

If the "no unused entry" or default-timing tests need a different `FakeGitClient` setup (a deferred registration may need the clone to be awaited), copy the shape of `DeferredTiming_ColdServiceTheAppHostNeverAdds_IsNotCloned` in the same file.

- [ ] **Step 2: Run the tests to verify the publish-mode one fails and the others pass** (the Eager test may instead be folded as two extra asserts into `ServiceMarkedRepositoryButNeverAdded_IsReportedRatherThanClonedSilently`)

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~LocalCheckoutPrefetchTests&(FullyQualifiedName~PublishMode_UnusedCheckoutNotice|FullyQualifiedName~PublishMode_EveryConfiguredServiceAdded|FullyQualifiedName~RunModeEager_UnusedCheckoutNotice)"`
Expected: `PublishMode_UnusedCheckoutNotice_DoesNotOfferDeferralAsARemedy` FAILS (message contains "Deferred checkout"); the other two PASS (they pin unchanged behaviour).

- [ ] **Step 3: Implement**

In `LocalCheckoutPrefetch.cs`, next to `_started`:

```csharp
    private bool _started;

    // Deferral never applies to publish, so the notice's remedy differs between the two modes.
    private bool _isRunMode;
```

In `EnsureStarted`, right after `_started = true;`:

```csharp
            _isRunMode = builder.ExecutionContext.IsRunMode;
```

In `UnusedCheckoutsRaw`, replace everything from "Deferred checkout also stops it" (mid-line 236, so split that fragment after "to stop paying for them. ") through line 238 with a tail chosen by mode:

```csharp
            var remedyTail = _isRunMode
                ? Raw.Literal(
                    "Deferred checkout also stops it: a service whose first checkout is deferred "
                    + "past startup is cloned only when it is added, which is the default unless this AppHost calls "
                    + "builder.SetCheckoutTiming(CheckoutTiming.Eager).")
                : Raw.Literal(
                    "Deferral does not apply to 'aspire publish', which composes the manifest from "
                    + "checkouts that are already on disk, so clearing the entry is the only remedy.");
```

and end the `Raw.Compose` with `+ $"{remedyTail}");` after `... to stop paying for them. `. Keep the preceding sentence text unchanged. If `Raw.Literal` rejects the string (check its signature), use `Raw.Compose($"...")` with the same text instead.

- [ ] **Step 4: Run the four tests, then the whole prefetch test class**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~LocalCheckoutPrefetchTests"`
Expected: all PASS, including `ServiceMarkedRepositoryButNeverAdded_IsReportedRatherThanClonedSilently` and `PublishMode_ColdServiceTheAppHostNeverAdds_IsStillCloned`.

- [ ] **Step 5: Build with CI flags**

Run: `dotnet build -c Release --no-restore -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalCheckoutPrefetchTests.cs
git commit -m "Tell the truth about deferral in the publish-mode unused-checkouts notice"
```

(End with the attribution line required by the session reminder.)

### Task 2: Correct the inverted Eager bullet in the user docs

**Files:**
- Modify: `docs/sources/repository.md:89-97` (the "Keep the file to the services you actually add" bullet; the "Nothing else is speculated over" paragraph after it stays)
- Test: no test project covers prose; the check is a grep (below) plus a read-through that the bullet agrees with the table in spec section 1.

**Interfaces:** Consumes the notice wording from Task 1 (publish mode has no deferral remedy). Produces nothing.

- [ ] **Step 1: Write the failing check**

Run: `grep -n "removes the reason to" docs/sources/repository.md`
Expected today: one hit (line 90). After the change: no hits.

- [ ] **Step 2: Rewrite the bullet**

Replace lines 89-97 with text making these points, in the file's existing voice and without ticket numbers: entries you never add are, by default and in run mode, not cloned; the cost applies under `builder.SetCheckoutTiming(CheckoutTiming.Eager)` and in `aspire publish` (where the manifest needs the checkout on disk and `AddService()` blocks on it), and for a custom kind that declines deferred checkout; in those cases an entry whose first checkout an `AddService()` call would block on is cloned on the first call, in parallel, before the AppHost says which it wants; the AppHost logs which entries those were and warns if one failed; so drop entries you never add. Do not add a new sentence about existing checkouts: the paragraph at lines 106-108 ("Either way ... never touched") already says it; keep it and make sure "Either way" still has a referent.

- [ ] **Step 3: Verify**

Run: `grep -n "removes the reason to" docs/sources/repository.md` (expect no output) and `grep -n "aspire publish" docs/sources/repository.md` (expect the new bullet among the hits). Read lines 85-110 to confirm the following "Nothing else is speculated over" paragraph still reads without repeating the bullet.

- [ ] **Step 4: Commit**

```bash
git add docs/sources/repository.md
git commit -m "Docs: say which modes still clone an entry the AppHost never adds"
```

### Task 3: Record the decision in the class remarks

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs:65-69` (the paragraph ending "…or runs in publish mode.")
- Test: none behavioural (comment-only); the gate is the warnaserror build and the prefetch tests staying green.

**Interfaces:** none.

- [ ] **Step 1: Confirm the baseline is green**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~LocalCheckoutPrefetchTests"`
Expected: PASS.

- [ ] **Step 2: Extend the remarks paragraph**

After the sentence ending "…or runs in publish mode.", add two sentences stating the constraint, no history or references: in those modes `AddService()` blocks on its own clone, so waiting for demand before cloning would serialise cold clones (wall-clock `sum` rather than `max`), and no signal of demand exists before the calls that would produce it; hence the set is deliberately not narrowed further, and the cost is reported instead. Keep XML-doc valid (`<c>`, `<see cref>`), lines within the file's existing width.

- [ ] **Step 3: Build and re-run**

Run: `dotnet build -c Release --no-restore -warnaserror` then the Step 1 test command.
Expected: 0 warnings/errors; PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs
git commit -m "Record why the speculative prefetch set is not narrowed in publish and Eager"
```

### Task 4: Full verification and landing prep

**Files:** none modified except notes.

- [ ] **Step 1:** Run `dotnet build -c Release --no-restore -warnaserror` and `dotnet test -f net10.0`. Expected: green.
- [ ] **Step 2:** Confirm `git diff origin/main --stat` touches only: the spec, this plan, `LocalCheckoutPrefetch.cs`, `LocalCheckoutPrefetchTests.cs`, `docs/sources/repository.md`. No CHANGELOG change.
- [ ] **Step 3:** Leave the full net8/net9/net10 matrix (`dotnet test`) for the Land phase per CLAUDE.md; note CI-only legs (smoketests, polyglot checks) as not run locally.
- [ ] **Step 4:** PR body: what changed and why, acceptance checklist, `Closes #217`.

## Self-Review

- Spec 3.1 -> Task 1; 3.2 -> Task 2; 3.3 -> Task 3; 3.4 (no behaviour change, no CHANGELOG) -> Global Constraints and Task 4 Step 2; 3.5 tests 1-2 -> Task 1 Step 1, test 3 (existing pins) -> Task 1 Step 4; section 1 evidence rows are covered by existing tests plus the default-timing guard.
- Open Questions 1-3 are resolved in the spec in the same commit as this plan.
