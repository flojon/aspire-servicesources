# Plan: several ports through one tunnel (#233)

Implements [the design](../specs/2026-09-05-backing-service-named-port-map-design.md). Each task
names its test first; each is independently verifiable with
`dotnet build -c Release -warnaserror && dotnet test -c Release --no-build`.

Ordered so that nothing depends on a later task. Tasks 1–3 are the config layer and are testable
with no AppHost at all; 4–7 are the source; 8–9 are the documentation.

---

## Task 1 — `KubernetesPorts` and its converter

**Test first** (`Config/KubernetesPortsTests.cs`, new): bind a `KubernetesBackingServiceDeveloperConfig`
through the real `ConfigurationBinder` and assert, for each row:

| Configured | Expected |
|---|---|
| `port` = `5432` | `SinglePort == 5432`, `Count == 0` |
| `port:amqp` = `5672`, `port:management` = `15672` | `SinglePort is null`, two entries |
| `port:amqp` = `5672`, looked up as `AMQP` | found — the comparer survives binding |
| `port` = `""` | the property is null (the unset gesture) |
| `port:amqp` = `"abc"` | the entry is **dropped**, `Count == 0` — pins the binder behaviour the validator exists to cover |
| `port` = `"abc"` | the binder throws — pins why the validator must run first |

**Then**: add `Config/KubernetesPorts.cs` exactly as the design gives it — `sealed`, `internal`,
`: Dictionary<string, int>`, `[TypeConverter]`, `OrdinalIgnoreCase` comparer, get-only `SinglePort`,
converter returning `null` for `""` and throwing otherwise.

Change `KubernetesBackingServiceDeveloperConfig.Port` from `int?` to `KubernetesPorts?`. This breaks
`KubernetesBackingServiceTests`'s `Config(...)` helper and every caller — leave them broken until
task 5; the tests in this task are the ones that matter here.

**Watch for:** `SinglePort`, not `Single` — the latter shadows `Enumerable.Single()` on an
`IEnumerable<>` and stops later readers compiling.

---

## Task 2 — `DeveloperConfigField.IsValueOrMap`, ahead of `IsList`

**Test first** (`Config/DeveloperConfigFieldTests.cs`, extend or create): `IsValueOrMap` is true for
`KubernetesPorts` and false for `string`, `int?`, `string[]` and a block type; `BlockFieldsOf`
returns null for `KubernetesPorts`; `DeveloperConfigShape.BackingService.Blocks` does **not** list
it as a block, and `BlockFields["Kubernetes"]` still carries `Port`.

**Then**: add `IsValueOrMap` to `DeveloperConfigField` — a type carrying a `[TypeConverter]` that is
also a `Dictionary<string,>`-shaped map. Wire it into `DeveloperConfigValidator.CollectBlock`
**before** the `IsList` branch, routing to `CollectValueOrMap` (task 3).

---

## Task 3 — `CollectValueOrMap` and its nine messages

**Test first** (`Config/DeveloperConfigValidatorTests.cs`, extend): one case per row of the design's
diagnostics table, asserting the **full** message text, plus:

- a `port` map carrying two bad entries → both reported in one exception (the collecting);
- a bad `port` alongside an unrelated bad key in the same entry → both reported;
- the *services* section's `port` still gets the old "takes a whole number" message — the service
  side is untouched, and the existing test that asserts it must still pass.

Cases: non-number scalar; whitespace scalar (existing `Blank`); `{}`; `null` (same message as `{}`);
an array; a non-number entry; a null entry; an empty/whitespace entry; a block entry; a blank name.
And `[]`, which is the **unset** gesture and must produce *no* problem.

**Then**: write `CollectValueOrMap`, each message exactly as the design gives it, each with `SetAt`
on the entry's own section where the fault is an entry and on the field's where it is the field.

**Watch for:** `{}` and `null` are the same to `IConfiguration` — one message serves both, and the
test proves it by asserting the identical string for both inputs. `[]` arrives as `Value == ""`, not
as children, so it must not reach this walk at all.

---

## Task 4 — `IPortAllocator.AllocatePorts`

**Test first** (`PortAllocation/SocketPortAllocatorTests.cs`, extend or create): `AllocatePorts(8)`
returns 8 distinct ports; `AllocatePorts(1)` agrees with `AllocatePort()` in shape; a non-positive
count is rejected.

**Then**: add `AllocatePorts(int count)` to the interface with **no default implementation** — a
default that looped `AllocatePort()` would reintroduce the duplicate this exists to prevent.
Implement in `SocketPortAllocator` by binding every socket, reading every port, and releasing them
in a `finally`. Re-implement `AllocatePort()` as `AllocatePorts(1)[0]`.

Update the six existing fakes: the two backing-service ones return a sequence; the four service-side
ones (`ServiceEndpointTests`, `ServiceConfigurationExtensionsTests`, `KubernetesSourceTests` ×2)
throw, since a call from that side would be a bug.

---

## Task 5 — `KubectlPortForward` takes pairs

**Test first** (`Sources/KubectlPortForwardTests.cs`, extend or create): the pairs overload emits one
`local:remote` argument per pair in the order given, with one `--context` and one `--namespace`; the
single-pair overload produces exactly what it produces today (assert the existing array verbatim).

**Then**: add the pairs overload; make the existing single-pair signature delegate to it, so the two
cannot drift — which is the reason this type exists.

---

## Task 6 — the one invariant, and multi-port resolution

**Test first** (`BackingServices/KubernetesBackingServiceTests.cs`), and write this one before the
implementation, because it is the test the design exists for:

- **Three-way consistency**, on `{ "zulu": 5672, "alpha": 15672 }` — name order and remote-port
  order deliberately disagree. The local port substituted for `${port:zulu}` equals the local port
  paired with `5672` on the command line, and equals the port the `orders-db-tunnel-tcp-zulu` health
  check probes. A positional bug fails this; nothing else catches it.
- One `AddExecutable`, carrying every pair, in ordinal name order.
- One health check per forwarded port, all attached to the connection string.
- `${port:amqp}` twice → one allocation, the same number twice.
- The single-port form: unchanged connection string, unchanged command line, health-check key still
  `orders-db-tunnel-tcp`.

**Then**: in `Resolve`, build the single name→local-port dictionary by zipping
`AllocatePorts(count)` onto the entries in **ordinal name order**, and have the expression walk, the
`kubectl` pairs and the health-check registrations all read that one dictionary. No second ordering
anywhere.

**Watch for:** this is the silent-failure task. Do not let any reader re-derive an order.

---

## Task 7 — the refusals, and the messages that assumed one port

**Test first** (same file): each row of the design's resolution table —

- `${port:amqp}` against a single `port` → refused, saying it forwards one unnamed port;
- `${port}` against a map → refused, naming every forwarded port;
- `${port:mgmt}` against `{ amqp, management }` → refused, naming the near miss **and** the
  forwarded ports;
- a template naming two unknown ports → **both** reported in one exception (collecting);
- a port out of range, named → refused naming the port;
- 33 named ports → refused by the cap, naming the backing service and the key;
- a port name containing `\n` → escaped in the message;
- a map configured with no `${port…}` at all → `NothingAddressesTheTunnel`'s **map** wording,
  naming `${port:<name>}` and the forwarded names, not "write `${port}`".

**Then**: replace the named-port refusal branch; make the pass collect; branch
`NothingAddressesTheTunnel` on the configured shape; extend `RequirePortInRange` to every port and
add the cap; update `RequiredFields`' `port` and `connectionString` descriptions; leave the "a whole
entry reads" example on the single form.

Move `Escaped` to an internal helper both the validator and this file call, and put every echoed
port name through it — including `LocalPortHealthCheck`'s description, which gains the port name
under a map and stays as it is under the single form.

---

## Task 8 — README

Edit, do not append: the `${port:<name>}` bullet under *Connection-string placeholders*, the
"Forwarding several ports through one tunnel … is #233" paragraph under *Reaching a backing service
in a cluster*, and the `port` comment in that section's example. Add the note that two casings of
one port name in a single file take the whole file down, while across layers they merge.

## Task 9 — CHANGELOG

Edit the `## [Unreleased]` entry that currently says several ports are "not in this pass".

---

## Verification before the PR

`dotnet restore`, `dotnet build -c Release --no-restore -warnaserror`,
`dotnet test -c Release --no-build`, and `dotnet pack … --no-build`. The smoke tests, the TypeScript
typecheck and the invariants job need Docker, npm, the Aspire CLI or PyYAML and are left to CI —
named in the PR body, not claimed as passing.
