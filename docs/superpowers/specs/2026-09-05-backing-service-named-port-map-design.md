# Backing services: several ports through one tunnel

**Status: Draft.** Design for [#233], split out of stage 2 of [#144] ([#234]) — `port` as a named
map, and `${port:<name>}` resolved against it.

The decision itself is not open. The database-source design already settled it, under *Multi-port
backends: one port-forward, many ports*: `port` accepts either a single port or a named map, one
`kubectl port-forward` carries every pair, and one health check watches each. What that section
does not settle is *how* — which is the whole reason #233 exists as an issue rather than as a
binding detail of #234, and it is what this document is for.

## Why this is not a two-line change

Three things stand between the decision and the code, and each is measured below rather than
assumed.

**The developer config has three field shapes, and this is a fourth.** `DeveloperConfigField`
answers "value, list, or block", and the order it is asked in is already load-bearing: a list
arrives from `IConfiguration` as indexed children and its type is a class, so a list asked about as
a block is answered with a message about a block. A map arrives looking exactly like a block too,
and `Dictionary<,>` is an `IEnumerable`, so today a `port` map is classified as a **list** and
answered with a sentence about list elements — about a field that is neither a list nor has
elements.

**The standard binder cannot carry a value-or-map field in the obvious shape.** The developer config
is bound in one `section.Get<Dictionary<string, BackingServiceDeveloperConfig>>()` call, so whatever
`port` becomes has to survive `ConfigurationBinder` unaided.

**Allocating several local ports one at a time can hand back the same port twice**, which with one
port per backing service was invisible and with several ports in one `kubectl` invocation is a
tunnel that fails to bind.

## Measurements

Everything in this section was run against this repo's pinned binder on `net10.0`, and the
spike is reproduced by the tests listed under *Testing*.

### The binder, on a field that is either a scalar or a map

| Shape | Scalar `"port": 5432` | Map `"port": { "amqp": 5672 }` |
|---|---|---|
| `int?` | binds | binder never sees it; `port` is left unset |
| `Dictionary<string,int>` | **binds to `null`, silently** | binds |
| `sealed class : Dictionary<string,int>` + `[TypeConverter]` | binds **through the converter** | binds as entries |

The binder is value-first: a section carrying a `Value` is offered to the type's `TypeConverter`
before its children are looked at, and a section with children and no value goes to the dictionary
walk. So a `Dictionary<string,int>` subclass carrying a converter is the one shape that catches
both — and it is the *only* one, since a non-dictionary class cannot bind children whose keys are
names the developer invented.

Three further behaviours, each of which the design has to answer for:

- **A subclass whose parameterless constructor passes `StringComparer.OrdinalIgnoreCase` keeps that
  comparer through binding**, and a get-only property added alongside does not disturb the walk.
  This is what makes `${port:AMQP}` find `amqp`, which it has to: configuration keys are
  case-insensitive, so the casing that survives a merge is whichever provider wrote last.
- **A converter returning `null` for `""` leaves the property null and does not throw**, so the
  repo's "an empty value unsets the field" gesture survives unchanged.
- **A bad value is reported in two different, and both unacceptable, ways.** A bad *scalar*
  (`"port": "abc"`) throws `InvalidOperationException` — *"Failed to convert configuration value
  'abc' at 'k:Port' to type '…KubernetesPorts'"* — naming a CLR type in a message no handler
  upstream treats as a configuration problem. A bad *map entry* (`"port": { "amqp": "abc" }`) is
  **silently dropped**: no throw, and the map binds one entry short. That second one is the same
  failure the list walk's null-element check exists for — nothing downstream can report what it
  never receives — and it is the reason the validation below is per-entry rather than per-field.

### The port allocator, asked for several ports

`SocketPortAllocator` binds an ephemeral socket, reads the port and releases the socket immediately.
Asked 2000 times in a row on this machine, it returned **721 duplicate ports overall and 0 immediate
repeats**: the OS walks the ephemeral range and comes back round to ports it has already handed out.
Holding 200 sockets open until every port has been read returned **0 duplicates**, by construction.

With one port per backing service, a duplicate against some *other* allocation is the TOCTOU race
the cluster-source design already accepted. Within one backing service it is a different thing: two
equal local ports in one `kubectl port-forward svc/x 5000:5672 5000:15672` is an invocation that
cannot bind its second pair, failing in kubectl's words about a tunnel the developer did not write
that way.

## Decisions

### `port` binds to a `KubernetesPorts`, which is a dictionary that may instead hold one unnamed port

```csharp
[TypeConverter(typeof(KubernetesPortsConverter))]
internal sealed class KubernetesPorts : Dictionary<string, int>
{
    public KubernetesPorts() : base(StringComparer.OrdinalIgnoreCase) { }
    internal KubernetesPorts(int single) : this() => Single = single;

    /// <summary>The one port, when `port` was written as a number rather than as a map.</summary>
    public int? Single { get; }
}
```

Exactly one of `Single` and the entries is ever populated, and which one it is *is* the distinction
`${port}` turns on. That is why the single form is not stored as a one-entry map under a sentinel
name: the acceptance criteria refuse `${port}` against a map of one named port, so "written as a
number" has to survive binding as a fact of its own rather than be inferred from a count.

`Single` is get-only, so it is not a configurable key — `DeveloperConfigField.IsConfigurable`
already excludes a property configuration cannot put a value at, and nothing enumerates this type's
properties in any case, since it classifies as neither a block nor a list under the rule below.

The converter answers `""` with `null` (the unset gesture) and anything else unparseable by
throwing — which the validator makes unreachable, and which is left throwing rather than returning
null so that a route into the binder nobody predicted fails loudly instead of unsetting the field.

### The validator gains a fourth classification, asked ahead of `IsList`

`DeveloperConfigField.IsValueOrMap(type)` — true for a type that carries a `[TypeConverter]` *and*
binds children as a map. `CollectBlock` asks it before `IsList`, for precisely the reason `IsList`
is already asked before the block question: whichever shape is asked about second is described in
the other one's words.

The walk it routes to, `CollectValueOrMap`, reports:

| What is written | What is said |
|---|---|
| a value that is not a whole number | `'port' in the 'kubernetes' block takes a port number or a block of named ports, but is set to 'abc'.` |
| whitespace | the existing `Blank` message, unchanged — it is the same mistake here as anywhere |
| an entry whose value is not a whole number | `'port' in the 'kubernetes' block names a port 'amqp', but its value 'abc' is not a whole number. A named port that is not a number is dropped rather than reported…` |
| an entry that is itself a block | `…its entry at 'amqp' is a block of settings. Every named port has to be a number.` |
| an entry with a blank name | `…names a port with no name.` |
| an empty map | `'port' in the 'kubernetes' block is an empty block of named ports…` |

Each keeps the `SetAt` remedy every other message in that file carries, and each names the backing
service through the same `Failure`/`CombinedFailure` collecting the entry already goes through — so
several problems with one `port` read as a list rather than as one startup per mistake.

Range (`1..65535`) stays where it is, in the source rather than the validator, and is applied to
every named port rather than only to the single one. It is a different question from "is this a
number", the source is where the existing message lives, and that message can name the port.

### `${port}` and `${port:<name>}` resolve against what was written

| Configured | `${port}` | `${port:amqp}` |
|---|---|---|
| `"port": 5432` | the allocated local port | refused, saying this backing service forwards one unnamed port |
| `"port": { "amqp": … }` | refused, naming the ports that *are* forwarded | that entry's local port |

Both refusals name the ports that exist, because a developer who wrote the wrong one is looking at
a file where the right one is written down. The refusal for a named port the map does not carry is
the near-miss shape the rest of the package uses where it has a candidate list to offer.

A template still has to carry at least one port placeholder — the rule stage 2 shipped, for the
reason it shipped it: a connection string with a literal port addresses the developer's own machine
and connects to the wrong database while reporting healthy. It stays "at least one", not "one per
forwarded port": the management port in the issue's own example is forwarded so a developer can open
it in a browser, and nothing puts it in a connection string.

### One invocation, one process, one health check per port

`KubectlPortForward.Args` gains a pairs-taking overload; the existing single-pair signature stays,
because `KubernetesSource` — a *service*, not a backing service — forwards exactly one port and has
no reason to learn about maps.

The pairs are ordered by port name, ordinal, so the command line, the dashboard and any message
listing them read the same on every run. `GetChildren()` is already sorted and already merges two
casings of one name into one child, so no ordering or duplicate-name question survives binding.

Every forwarded port gets a `LocalPortHealthCheck`, and the connection string carries all of them:
`WaitFor` waits for every check, so a consumer waits for the whole tunnel rather than for whichever
port happened to be registered. The single-port form keeps its existing key, `<name>-tunnel-tcp`,
unchanged; a named port's key is `<name>-tunnel-tcp-<port name>`, and the check's unhealthy message
names the port so the dashboard says which half of the tunnel is missing.

### `AllocatePorts(int count)` on `IPortAllocator`

Binds every socket, reads every port, then releases them all — distinct by construction, and keeping
the existing TOCTOU trade for the release, which is the accepted one. `AllocatePort()` stays as the
one-port spelling every other caller uses.

## Design

Resolution in `KubernetesBackingServiceSource`, in order, so that nothing is added to the model or
allocated on the way to a refusal — the arrangement stage 2 already has:

1. `RequireEveryField` — unchanged, except that its `port` predicate becomes "written as a number,
   or as a map with at least one entry", so a `port` that bound to nothing is missing rather than
   present and empty.
2. `RequirePortsInRange` — every port, named or not.
3. `ConnectionStringTemplate.Parse` — unchanged; it already produces `Port { Name }`.
4. `RequireEveryPlaceholderIsResolvable` — the branch that today refuses a named `${port:…}` by name
   becomes the resolution table above. The secret branch is untouched, and stays for stage 3.
5. `AllocatePorts` — one per forwarded port.
6. The expression walk — `Port { Name: null }` takes the single port, `Port { Name: … }` takes that
   entry's.
7. One `AddExecutable` with every pair; one health check per port; `WithHealthCheck` for each.

## Testing

- `KubernetesPorts` binding: the scalar form, the map form, the case-insensitive lookup, the empty
  value, and the two bad-value routes — the spike above, kept as tests, because every one of them is
  a behaviour of a binder this package does not own and cannot see change.
- `DeveloperConfigValidator`: each row of the diagnostics table, plus a `port` map alongside another
  mistake in the same entry, to hold the collecting.
- `KubernetesBackingServiceSource`: the four acceptance rows of the resolution table; one executable
  carrying every pair, in name order; one health check per port; the single-port form unchanged.
- `IPortAllocator`: `AllocatePorts` returns distinct ports.

## Delivery

One PR on `main`. **[#242] (stage 3, secrets) is open on the same file and removes the neighbouring
branch of `RequireEveryPlaceholderIsResolvable`; whichever lands second resolves that conflict.**
Neither should wait for the other — a single `port` stays valid either way, and the two features
share no decision.

## Open Questions

1. **Is a forwarded port that no placeholder mentions worth a warning?** #206 built a warnings
   channel for config nothing reads, and an unreferenced named port fits its shape. Not proposed
   here: the issue's own example forwards a management port on purpose. Left for the human to
   settle.
2. **Should `${port:<name>}` be accepted against a single `port`, treating the name as decoration?**
   Proposed: no, per the table above. It reads as a developer who edited half of a change.

[#144]: https://github.com/flojon/aspire-servicesources/issues/144
[#233]: https://github.com/flojon/aspire-servicesources/issues/233
[#234]: https://github.com/flojon/aspire-servicesources/pull/234
[#242]: https://github.com/flojon/aspire-servicesources/pull/242
