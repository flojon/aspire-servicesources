# An orphaned `"repository"` entry still costs a clone: what is left after the deferred default (#217)

**Date:** 2026-09-28
**Status:** Approved
**Resolves:** #217 (a `services:` entry that the catalog declares, that no `AddService()` call adds,
and whose checkout is cold, is cloned speculatively and reported only once the clone is paid for).
**Relates to:** #216/#404 (deferred checkout is now the default), #76 (the first narrowing of the
prefetch set), #291 (grouped services share a checkout). Not touched: #215 (the warning for entries
the catalog does not declare, already landed), #66 (two services in one repository).

The ticket was written as a *decision to make*: is a config entry warrant enough to clone, or should
the prefetch wait for a signal of demand? Its own first comment predicts the problem "mostly
dissolves" with #216 and says to re-measure before designing anything. This document does that
first (section 1), then settles the decision (sections 2 and 3). The outcome is a **documented
decision plus a small correction to advice that is wrong for the modes where the cost survives**;
it changes no cloning behaviour.

Naming: the ticket says `"local"`; that source has since been renamed `"repository"` (#418). This
document uses the current name.

---

## 1. What remains, measured against `main` at `fb1c81e`

Every row was read from the code and, where a test exists, that test is named. "Cloned" means the
prefetch starts a `git clone` for an entry the AppHost never adds.

| Situation | Cloned? | Evidence |
|---|---|---|
| Run mode, default timing, cold entry, `dotnet`/`java`/`javascript` kind | **No** | `LocalCheckoutPrefetch.Run` drops any candidate for which `WouldBeDeferredIfAdded` is true; `DeferredCheckout.ShouldDefer` is true by default in run mode (`_enabled = true`). Test `DeferredTiming_ColdServiceTheAppHostNeverAdds_IsNotCloned`. |
| Warm checkout, `repository.path` override, `defaultSource`-derived entry, entry absent from the catalog, catalog service with no repository url, key unusable as a directory name | **No**, on every mode and timing | The candidate filters in `Run` (`IsColdManagedCheckout`, `DefaultedServiceNames`, `Catalog.Services`, `HasRepositoryToClone`, `IsContainedCheckoutDirectoryName`). Not #217's concern. |
| Run mode, `SetCheckoutTiming(CheckoutTiming.Eager)`, cold entry | **Yes** | `ShouldDefer` returns false. Test `ServiceMarkedRepositoryButNeverAdded_IsReportedRatherThanClonedSilently`. |
| **Publish mode** (`aspire publish`, manifest generation), any timing, cold entry | **Yes** | `ShouldDefer` returns false when `!builder.ExecutionContext.IsRunMode`, by design (the manifest would otherwise describe a project with no endpoints or profile environment). Test `PublishMode_ColdServiceTheAppHostNeverAdds_IsStillCloned`. |
| Run mode, default timing, cold entry whose custom kind answers `SupportsDeferredCheckout` false | **Yes** | `WouldBeDeferredIfAdded` falls through to the handler. Same cost as Eager, for a kind that opted out of deferral; the run-mode notice names it. |

So the ticket's prediction holds exactly: the flip removed the run-mode default case and left
**publish mode** and **explicit Eager** (plus custom kinds that decline deferral).

### The ref-reconciliation hazard is already gone

The ticket lists the hazard from the class remarks (a run that never mentions a service moving its
branch back onto the configured ref "on the strength of a config entry alone") as part of the cost.
That hazard does not exist in the code the remarks describe; the remark records the reason it was
designed out:

- `LocalCheckoutPrefetch.Run` only admits `IsColdManagedCheckout` entries, i.e. directories that do
  not exist. It never enters a working tree it did not create.
- `LocalGitCheckout.PrepareRepoRoot`, the prefetch's only git operation, puts a *fresh clone it just
  made* on the configured ref (holding nothing anyone could lose) and returns
  `NeedsReconciliation: true` for anything that pre-existed, including a clone that lost a race to a
  concurrent AppHost.
- The mutating half, `ReconcileRepoRoot`, runs only from `GetRepoRoot`, i.e. for services the AppHost
  really added. Test `ExistingCheckoutForAServiceNeverAdded_IsLeftOnTheRefItWasFoundOn` pins it.

It is independent of the deferred flip and of the mode: publish mode and Eager behave the same way.
Nothing remains of it to weigh. What survives is only the *clone itself* (network and disk).

---

## 2. Decision and the options weighed

**Decision: a config entry alone is still warrant to clone in the modes where the AppHost has to
block on the clone (publish mode, `Eager`, a kind that declines deferral). The prefetch is not
narrowed further. The cost is documented and reported accurately.**

| Option | Verdict | Why |
|---|---|---|
| **O1. Keep speculating over the entry (status quo) in the remaining modes; fix what is misleading around it** | **Chosen** | See below. |
| O2. Drop the prefetch in publish/Eager and clone each entry when `AddService()` asks | Rejected | `AddService()` blocks on its clone in these modes, so this serialises cold clones: wall-clock goes from `max(checkout)` to `sum(checkout)`. That is the tax #2 removed, and publish is often run cold. It trades a bounded, reported waste for a certain, unreported slowdown on every legitimate entry. |
| O3. Wait for a demand signal before cloning | Rejected | No demand signal exists at the decision point. `AddService()` is called one at a time and must return the real resource before the next call, so the information that would narrow the set arrives after the decision that needs it (the ticket's own point; class remarks). Each candidate signal is either prediction or a new burden: a timer/grace period (a heuristic, and it adds latency to the first call for everyone), an AppHost-declared set of names (new public API surface that duplicates the config the developer already wrote, and needs maintaining next to every `AddService()` call), or "clone on second call" (still speculative, and serial for the first). |
| O4. Defer in publish mode too | Rejected | Already decided against, for a sound reason: a manifest written from a repository that is not on disk describes a project without its endpoints or profile environment, and the start task would strand waiting on a `NotStarted` only DCP publishes (`DeferredCheckout.ShouldDefer` remarks). |
| O5. Make speculation opt-in (a switch that turns the prefetch off) | Rejected | It would be new public API for a cost the developer can already remove by deleting the entry the notice names, and turning it off would produce the O2 slowdown for whoever set it and forgot. Revisit only if a concrete complaint arrives. |

Why O1 is acceptable, not merely least bad:

- **The residual population is small and self-inflicted.** It needs a developer-local
  `servicesources.local.json` (or other configuration source) naming a *cold* service the AppHost does
  not add, *and* one of publish mode, an explicit Eager opt-in, or a deferral-declining custom kind.
  The default interactive path, where the surprise was worst, is fixed.
- **The waste is bounded and one-off.** A cold clone happens once per checkout; afterwards the entry is
  warm and is not speculated over at all.
- **It cannot make anything fail.** Speculation captures its exceptions and reports them only for
  services never requested (`FailedUnusedCheckoutMessages`); a requested service re-throws normally.
- **It is reported, with a remedy that works.** Once it is known (at `BeforeStartEvent`) the unused set
  is named per service with the configuration key to clear. The report cannot be earlier; that is the
  ticket's "tense" complaint and it is inherent (this section, O3). What it *can* be is correct, which is
  the part that is currently wrong (section 3).

---

## 3. Changes

Small, and all of them about accuracy rather than behaviour.

### 3.1 The notice gives advice that cannot work in publish mode

`UnusedCheckoutsRaw` ends with: *"Deferred checkout also stops it: a service whose first checkout is
deferred past startup is cloned only when it is added, which is the default unless this AppHost calls
builder.SetCheckoutTiming(CheckoutTiming.Eager)."* In publish mode, the notice is emitted (`BeforeStartEvent` is published in every mode but `inspect`, per the
`DeferredCheckout.ShouldDefer` comment, and the notice needs only an `ILoggerFactory`) and this sentence is false: deferral never applies there, and
nothing the developer sets in the AppHost changes it.

Change: `LocalCheckoutPrefetch` records `builder.ExecutionContext.IsRunMode` when it starts (under
the existing `_gate`, alongside `_started`). In run mode the notice keeps its deferral remedy and gains the cases where it does not help (Eager, a declining kind). In publish mode it
replaces the deferred-checkout sentence with one that says deferral does not apply to `aspire publish`,
which composes the manifest from checkouts that are on disk, so removing the entry is the only remedy.
No new state beyond one `bool`; the strings are literals composed through the existing `Raw` seam, so
no user-controlled text enters the notice that does not already.

The run-mode tail is written so it stays true for a kind that declines deferral: it says deferral
is the default, names `Eager` as one way it is lost, and says a declining kind (some javascript app
types, or a custom kind) is cloned up front, so clearing the entry is the only remedy for it.

### 3.2 The user docs invert the truth about Eager

`docs/sources/repository.md` says: *"Keep the file to the services you actually add — unless you opt
out with `builder.SetCheckoutTiming(CheckoutTiming.Eager)`, which removes the reason to."* This is
backwards. Under the default timing an unused cold entry costs nothing in run mode; **`Eager` is what
brings the reason back.** The bullet also says nothing about publish mode or a kind that declines deferral, where the default
does not help.

Change: rewrite the bullet: by default, in run mode, an entry you never add is not cloned; the cost
applies under `SetCheckoutTiming(CheckoutTiming.Eager)` and in `aspire publish`; the AppHost logs which
entries those were and warns if one failed; drop the entries you do not add. State that a checkout
which already exists is never touched for a service you do not add (the ref-reconciliation point, now
answered), which the following paragraph partly says already; keep it to a sentence, not a rewrite.

### 3.3 The class remarks

`LocalCheckoutPrefetch` remarks already say the speculative set survives "for as long as an AppHost
opts into `CheckoutTiming.Eager` or runs in publish mode" (the final remarks also name a kind that declines deferral). Add the *decision*, so the next reader does
not re-open it: why the set is not narrowed further in those modes (O2/O3 above, in two sentences). Do
not add ticket references or history (repo comment rule); state the constraint.

### 3.4 Not changed

- No change to what is cloned, when, or by whom. `WouldBeDeferredIfAdded`, the candidate filters,
  `ReportSpeculativeWork` timing and the failure notice are untouched.
- No CHANGELOG entry: nothing a consumer can act on differently changes (a log sentence for a mode in
  which it was wrong, and documentation). See Open Questions.
- `ServiceConfigAudit` and its remark already state that the orphan-with-catalog-entry half stays
  with the prefetch's report; still true.

### 3.5 Tests (each written before the change it covers)

1. Publish mode: the unused-checkouts notice for a cold, never-added entry names the entry, does
   **not** mention `SetCheckoutTiming` or "Deferred checkout", and does say deferral does not apply to
   publish. (New; fails today.)
2. Run mode, Eager: the notice still mentions `SetCheckoutTiming(CheckoutTiming.Eager)` as the way
   deferral is lost. (Guards the unchanged branch; the existing
   `ServiceMarkedRepositoryButNeverAdded_IsReportedRatherThanClonedSilently` keeps asserting the name
   and the count.)
3. Publish mode with every configured service added: no notice. Run mode, default timing, with a
   kind that declines deferral: the notice names the entry and says clearing it is the remedy.
4. Existing `PublishMode_ColdServiceTheAppHostNeverAdds_IsStillCloned` and
   `ExistingCheckoutForAServiceNeverAdded_IsLeftOnTheRefItWasFoundOn` stay as they are: they are the
   regression pins for the two surviving claims in section 1.

Verify legs are the repo's: `dotnet build -c Release -warnaserror`, `dotnet test -f net10.0` per round,
the full matrix once before landing.

---

## 4. Acceptance mapping

| Ticket criterion | Where |
|---|---|
| Re-measure against post-#404 main before designing | Section 1 |
| Config entry alone as warrant vs waiting for demand; record decision | Section 2 |
| Weigh what the prefetch buys against the "cannot be narrowed" remark | Section 2 (O2, O3) |
| Publish mode and ref-reconciliation | Section 1 (publish: survives, kept; ref hazard: already designed out, pinned by test) |
| Do not design a narrowing the flip makes redundant | Section 2: nothing designed for run mode default |
| Stay clear of #215, #66, #76 | Header, section 3.4 |
| Update remarks, docs, changelog if behaviour changes | 3.1 to 3.3; changelog: Open Question 2 |
| Legitimate outcome: documented decision or small change | This document |

## 5. Attack surface

None added. No new input is read, executed or trusted; nothing crosses a boundary. The only new
output is a fixed literal sentence in a log message, composed through the existing escaping seam
alongside service names that are already escaped through `Name`. The change does not alter which
repositories are cloned, so it cannot widen what an untrusted configuration source can make the
package download (that surface, and its `SECURITY.md` treatment, is unchanged).

## 6. Open Questions (resolved)

1. **Outcome:** resolved. Documented decision, no further narrowing, no prefetch opt-out (O5), plus the
   small corrections in section 3 (publish-mode notice text, the inverted Eager bullet in
   `docs/sources/repository.md`, the `LocalCheckoutPrefetch` class remarks).
2. **CHANGELOG:** resolved. No entry.
3. **Ticket closure:** resolved. The PR says `Closes #217`.
