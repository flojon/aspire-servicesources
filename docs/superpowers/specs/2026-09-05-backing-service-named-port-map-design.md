# Backing services: several ports through one tunnel

**Status: Reviewed.** Design for [#233], split out of stage 2 of [#144] ([#234]) — `port` as a named
map, and `${port:<name>}` resolved against it.

The decision itself is not open. The database-source design already settled it, under *Multi-port
backends: one port-forward, many ports*: `port` accepts either a single port or a named map, one
`kubectl port-forward` carries every pair, and one health check watches each. What that section
does not settle is *how* — which is the whole reason #233 exists as an issue rather than as a
binding detail of #234, and it is what this document is for.

## Why this is not a two-line change

Four things stand between the decision and the code, and each is measured below rather than assumed.

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

**A named port introduces three independent orderings where there was one number.** Pairing any two
of them by position instead of by name is a silent wrong-listener bug that reports healthy — see
*The one invariant* below, which is the single most important paragraph in this document.

**Allocating several local ports one at a time can hand back the same port twice**, which with one
port per backing service was invisible and with several ports in one `kubectl` invocation is a
tunnel that fails to bind.

## Measurements

Everything in this section was run against this repo's pinned binder and JSON provider on
`net10.0`. Every one of them is kept as a test (see *Testing*), which is also what extends them to
`net8.0` and `net9.0`: the package multi-targets all three, the binder arrives transitively through
`Aspire.Hosting`, and these are behaviours of code this package does not own and cannot see change.

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

### How each spelling of `port` actually arrives

Measured through `JsonConfigurationProvider`, because several spellings that read as different
things in a file are the same thing by the time the validator sees them. The last column is what
decides whether a message is even possible: the validator walks `block.GetChildren()`.

| Written | The `port` section | Seen by the walk? |
|---|---|---|
| `"port": 5432` | `Value='5432'` | yes |
| `"port": { "amqp": 5672 }` | `Value=null`, child `[amqp]='5672'` | yes |
| `"port": [5672, 15672]` | `Value=null`, children `[0]='5672' [1]='15672'` | yes — **an array is indistinguishable from a map named `"0"`, `"1"`** |
| `"port": {}` | `Value=null`, no children, `Exists()==false` | **yes**, as `[port]=<null>` |
| `"port": null` | identical to `{}` | yes — the two cannot be told apart |
| `"port": []` | **`Value=''`** | yes — arrives as the *unset* gesture, not as an empty map |
| `"port": { "amqp": null }` | child `[amqp]=<null>` | yes — binder drops it, map binds one short |
| `"port": { "amqp": "" }` | child `[amqp]=''` | yes — binder drops it too |
| `"port": { "amqp": " " }` | child `[amqp]=' '` | yes |
| `"port": { "": 5672 }` | child `[]='5672'` | yes — a blank name is reachable |
| `"port": { "amqp": { "x": 1 } }` | child `[amqp]` with children | yes |
| `"port": { "a:b": 5672 }` | flattens to `port:a:b` | binds as entry `a` → **port 0** |

Two consequences worth pulling out, because both were nearly designed wrongly:

- **An empty map is reportable.** `{}` reaches the walk as a child with a null value, so acceptance
  criterion 5 is achievable — but only from the walk, since by the time the binder has run, `{}`,
  `null` and an absent `port` are the same absent field.
- **A colon in a port name binds to port 0, not to nothing.** The binder manufactures
  `default(int)` for a section that has children and no value. So the per-entry range check is a
  second line of defence against a port number the developer never wrote reaching the command line,
  not a redundant restatement of "is this a number". It must not later be relaxed as redundant.

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

### What is *not* a risk here

Recorded because each was checked and each would otherwise be re-litigated:

- **Nothing developer-named reaches a shell or a command line.** `AddExecutable`'s arguments land in
  Aspire's `ExecutableSpec.Args`, a `List<string>` serialised to DCP as a JSON array — an argument
  vector, not a joined string. And `KubectlPortForward.Args` builds `$"{local}:{remote}"` from two
  `int`s, so a port *name* never reaches the command line at all.
- **Allocating N loopback ports widens no boundary.** The allocator binds `IPAddress.Loopback`,
  `kubectl port-forward` binds 127.0.0.1 by default, and `LocalPortHealthCheck` connects to
  `IPAddress.Loopback`. This is the same trust boundary N times.
- **No new *Aspire resource* name is derived.** There is still one `AddExecutable`, still named
  `<name>-tunnel`, still guarded by the existing `ArgumentException` catch. What is new is a
  health-check *registration key*, which is a different thing with different rules — see below.

## Decisions

### The one invariant: a name is bound to a local port once, and everything reads that binding

**This is the finding that changed the design, and the failure it prevents is silent.** With one
port there was one number and nothing could be mispaired. With `{ "amqp": 5672, "management": 15672 }`
there are three sequences in play — the map's iteration order, the ordinal-by-name order the command
line is written in, and the order `AllocatePorts` returned. Pairing any two of them *by position*
gives `${port:amqp}` the local port `kubectl` forwarded to **15672**. Both health checks pass,
because both local ports are listening; every resource reports healthy; and the application speaks
AMQP to the management port.

That is exactly the outcome `NothingAddressesTheTunnel` already exists to prevent — its own remarks
say a literal port means "the AppHost connects to the wrong database while reporting every resource
healthy" — reintroduced one level down.

So: `Resolve` builds **one** `IReadOnlyDictionary<string, int>` from port name to allocated local
port, by zipping `AllocatePorts(count)` onto the entries **in ordinal name order**, and that
dictionary is the single source for all three readers — the expression walk, the `kubectl` pairs,
and the health-check registrations. No reader re-derives an order of its own, and no code path pairs
by index. A test asserts the three agree, on a two-port map whose name order and remote-port order
deliberately disagree (`{ "zulu": 5672, "alpha": 15672 }`), which is the arrangement that makes a
positional bug fail the test rather than pass it by luck.

Because the binding is per *forwarded port* and not per *placeholder*, a template naming
`${port:amqp}` twice substitutes the same number twice and allocates once.

### `port` binds to a `KubernetesPorts`, which is a dictionary that may instead hold one unnamed port

```csharp
[TypeConverter(typeof(KubernetesPortsConverter))]
internal sealed class KubernetesPorts : Dictionary<string, int>
{
    public KubernetesPorts() : base(StringComparer.OrdinalIgnoreCase) { }
    internal KubernetesPorts(int single) : this() => SinglePort = single;

    /// <summary>The one port, when `port` was written as a number rather than as a map.</summary>
    public int? SinglePort { get; }
}
```

Exactly one of `SinglePort` and the entries is ever populated, and which one it is *is* the
distinction `${port}` turns on. That is why the single form is not stored as a one-entry map under a
sentinel name: the acceptance criteria refuse `${port}` against a map of one named port, so "written
as a number" has to survive binding as a fact of its own rather than be inferred from a count.

Named `SinglePort` rather than `Single`: a property named `Single` on a type that is an
`IEnumerable<>` shadows `Enumerable.Single()`, so `ports.Single()` would stop compiling for every
later reader.

`SinglePort` is get-only, so it is not a configurable key — `DeveloperConfigField.IsConfigurable`
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

The walk it routes to, `CollectValueOrMap`, reports the following. Every message is given in full,
because the tests assert on this text and three unfinished sentences in the draft were three
decisions nobody had made:

| What is written | The message |
|---|---|
| a value that is not a whole number | `'port' in the 'kubernetes' block takes a port number or a block of named ports, but is set to 'abc'.` |
| whitespace *as the field's value* | the existing `Blank` message, unchanged |
| an empty map — `{}`, and equally `null` | `'port' in the 'kubernetes' block is an empty block of named ports. Write a port number, as "port": 5432, or name at least one port, as "port": { "amqp": 5672 }.` |
| entries keyed by position — an array | `'port' in the 'kubernetes' block is a list of ports, but a port block names each one: "port": { "amqp": 5672 }. A port written by position has no name for a connection string to reach it by.` |
| an entry whose value is not a whole number | `'port' in the 'kubernetes' block names a port 'amqp', but its value 'abc' is not a whole number. A named port whose value is not a number is dropped rather than read, so the tunnel would forward one port fewer than the block names.` |
| an entry with no value at all — `null` | the same sentence, with `is not a whole number` replaced by `has no value`. A null entry is dropped by the binder exactly as a null list element is, which is what `ListElementMissing` exists for on the list side. |
| an entry whose value is empty or whitespace | the same sentence. **The unset gesture is a field-level gesture, not an entry-level one**: blanking `port` unsets the field, and there is no spelling that removes one entry from a map a lower layer wrote. Saying so is what stops a reader trying. |
| an entry that is itself a block | `'port' in the 'kubernetes' block names a port 'amqp', but its entry is a block of settings rather than a number. Every named port is a number: "port": { "amqp": 5672 }.` |
| an entry with a blank name | `'port' in the 'kubernetes' block names a port with no name. Every port in the block needs a name a connection string can reach it by, as "port": { "amqp": 5672 }.` |

Each carries `SetAt` naming **the entry's own section** where the problem is an entry
(`…__Kubernetes__Port__amqp`) and the field's where the problem is the field, matching the existing
file's deliberate `SetAt(element)`-versus-`SetAtList(field)` split. Each goes through the same
`Failure`/`CombinedFailure` collecting the entry already uses, so several problems with one `port`
read as a list rather than as one startup per mistake — and, per *the placeholder pass collects*
below, that habit is now followed on the source side too.

Two of these rows are decisions rather than mechanics, and are called out as such:

- **An array at `port` is refused, not accepted as positional names.** `[5672, 15672]` binds
  perfectly well as a map named `"0"` and `"1"`, reachable as `${port:0}` — which is why it needs a
  message rather than silence. It is refused because a name that is a position is not a name a
  developer would write on purpose, and accepting it would make `${port:0}` a documented spelling
  nobody meant to document.
- **`"port": null` gets the empty-map message**, because `IConfiguration` cannot tell it from `{}`.
  `"port": []`, by contrast, arrives as an empty *value* and is therefore the unset gesture, exactly
  as `"port": ""` is; it is not routed here at all.

Range (`1..65535`) stays where it is, in the source rather than the validator, and is applied to
every named port rather than only to the single one. It is a different question from "is this a
number", the source is where the existing message lives, that message can name the port — and, per
the colon measurement above, it is the check that catches a port `0` the binder manufactured.

### Developer-invented names are escaped where they are echoed

Port names are the first free-form, developer-invented strings this package puts into messages as a
*documented feature* rather than as a typo path. A JSON key can hold anything, including newlines,
and these messages are relayed into `~/.aspire/logs` and routinely pasted into issues — so a name
carrying `\n` can forge a line in a startup failure.

`DeveloperConfigValidator.Escaped` already renders whitespace as escapes for exactly this reason,
but it is `private static` and unreachable from the two other files that now echo a name. It moves
to an internal helper both the validator and `KubernetesBackingServiceSource` call, and **every
message that echoes a port name — validator, source refusals, and the health check's description —
puts it through that helper.** The validator's existing call sites are unchanged in behaviour.

### `${port}` and `${port:<name>}` resolve against what was written

| Configured | `${port}` | `${port:amqp}` |
|---|---|---|
| `"port": 5432` | the allocated local port | refused: this backing service forwards one unnamed port, so write `${port}` |
| `"port": { "amqp": … }` | refused, naming every forwarded port | that entry's local port |

Refusing `${port:<name>}` against a single `port` is a **decision, not an open question** — it was
listed as one in the draft and is promoted here, because the acceptance criteria already test it. It
reads as a developer who edited half of a change, and the message says which half.

For a name the map does not carry, the message follows the shape `NotValidHere` already uses for a
misspelled key: **the near miss when there is one, and otherwise every forwarded port.**
`NearMiss.Nearest` returns empty when nothing is close, so a near-miss-only message would say
nothing at all for `${port:mgmt}` against `{ amqp, management }` — while the acceptance criterion
requires naming the ports that *are* forwarded. Both halves are needed; the draft asserted each of
them separately and contradicted itself.

A template still has to carry at least one port placeholder — the rule stage 2 shipped, for the
reason it shipped it. But **`NothingAddressesTheTunnel` now branches on the configured shape.** Its
current text tells the reader to "replace the port in it with `${port}`", which under a named map
earns them a *second* startup failure from the table above, contradicting the first. Under a map it
names `${port:<name>}` and the forwarded names instead.

**The placeholder pass collects rather than throwing at the first refusal**, matching
`RequireEveryField` in the same file, which documents at length why: reporting one problem per run
costs a failed startup per mistake. A template naming one forwarded port and one that is not should
say so once.

### One invocation, one process, one health check per port

`KubectlPortForward.Args` gains a pairs-taking overload, and **the existing single-pair signature
stays and delegates to it**. Keeping two independent bodies would work against that type's own
stated reason for existing — "two arrays asserted separately drift" — while keeping the call site
simple for `KubernetesSource`, which forwards exactly one port and has no reason to learn about maps.

Every forwarded port gets a `LocalPortHealthCheck`, and the connection string carries all of them:
`WaitFor` waits for every check, so a consumer waits for the whole tunnel rather than for whichever
port happened to be registered. The single-port form keeps its existing key, `<name>-tunnel-tcp`; a
named port's key is `<name>-tunnel-tcp-<port name>`, and the check's description names the port so
the dashboard says which half of the tunnel is missing. Under the single form the description is
unchanged, since there is no half to name.

**Keeping the single form's key is a preference, not an obligation** — see *Nothing here is a
compatibility constraint* below — chosen because it keeps a passing test passing and keeps the
common case's dashboard identical. It is worth knowing it was a choice.

That derived key has a guard gap worth recording rather than fixing: Aspire's `WithHealthCheck`
rejects a duplicate key **ordinally**, while the health-check service's own validation dedupes
**case-insensitively** and throws from its constructor — at the first probe, not at build, naming a
key nobody wrote. Reaching it needs a contrived pair of backing services whose derived keys collide
(`orders` with a port named `replica-tunnel-tcp`, alongside `orders-tunnel-tcp-replica`). Left
alone: the fix is Aspire's, the likelihood is negligible, and inventing a guard here would duplicate
a rule this package does not own.

### `AllocatePorts(int count)` on `IPortAllocator`

Binds every socket, reads every port, then releases them all — distinct by construction, and keeping
the existing TOCTOU trade for the release, which is the accepted one. `AllocatePort()` stays as the
one-port spelling every other caller uses, implemented as `AllocatePorts(1)[0]`.

Two things the one-line draft left unwritten, both of which matter on a startup path:

- **Sockets are released in a `finally`.** A plain loop leaks every socket bound before a throwing
  one, permanently, on a path a developer will retry.
- **The interface gets no default implementation.** A default of "call `AllocatePort()` n times"
  would silently reintroduce the exact duplicate the measurement above exists to prevent. The six
  existing test fakes — four of them on the *service* side, which never calls this — implement it
  explicitly; the service-side fakes can throw, since a call would be a bug.

`count` comes from a developer-config map with no cardinality check anywhere. A map with thousands
of entries would bind that many sockets at once inside `Resolve` and exhaust the file-descriptor
limit, surfacing as a bare `SocketException` naming no backing service and no key — the one shape
every message in this package is written to avoid. **A cap of 32 forwarded ports per backing
service** is refused with an ordinary configuration message naming the backing service and the key.
The number is arbitrary and generous: the motivating case is a broker with two.

## Design

Resolution in `KubernetesBackingServiceSource`, in order, so that nothing is added to the model or
allocated on the way to a refusal — the arrangement stage 2 already has:

1. `RequireEveryField` — its `port` predicate becomes "written as a number, or as a map with at
   least one entry", so a `port` that bound to nothing is *missing* rather than present and empty.
   Two of its message strings change with it: `port`'s description gains the map form, and
   `connectionString`'s stops saying `${port}` unconditionally. The "a whole entry reads: …" example
   keeps the single-port form — it is the common case and the example earns its keep by being short.
2. `RequirePortsInRange` — every port, named or not, plus the cardinality cap.
3. `ConnectionStringTemplate.Parse` — unchanged; it already produces `Port { Name }` and already
   validates the named form's syntax.
4. `RequireEveryPlaceholderIsResolvable` — the branch that today refuses a named `${port:…}` by name
   becomes the resolution table above, collecting rather than throwing first. The secret branch is
   untouched, and stays for stage 3.
5. `AllocatePorts` — one per forwarded port — and the name→port dictionary of *the one invariant*.
6. The expression walk — `Port { Name: null }` takes the single port, `Port { Name: … }` takes the
   dictionary's entry.
7. One `AddExecutable` with every pair, in ordinal name order; one health check per port, from the
   same dictionary; `WithHealthCheck` for each.

The *service*-side `"kubernetes"` source is untouched. `KubernetesDeveloperConfig.Port` stays
`int?`, keeps its "takes a whole number" message and its existing test — the two sources share a
block name and a key name but not a type, and the validator routes by type. An implementer should
not make them symmetric.

## Testing

- `KubernetesPorts` binding: the scalar form, the map form, case-insensitive lookup, the empty
  value, and the two bad-value routes. These run on all three target frameworks, which is what
  extends the net10-only measurements above.
- Each row of the arrival table that the validator has to answer for, including the array, the
  `null`/`{}` pair, and `[]` as the unset gesture.
- `DeveloperConfigValidator`: each row of the diagnostics table by its full text, plus a `port` map
  alongside another mistake in the same entry, to hold the collecting.
- **The three-way consistency test** of *the one invariant*: on `{ "zulu": 5672, "alpha": 15672 }`,
  the local port substituted for `${port:zulu}` is the local port paired with `5672` on the command
  line and the one the `-zulu` health check probes. This is the test the design exists for.
- `KubernetesBackingServiceSource`: the four rows of the resolution table; a template naming two bad
  ports reporting both; one executable carrying every pair; the single-port form unchanged.
- A port name carrying a newline is escaped in every message that echoes it.
- `IPortAllocator`: `AllocatePorts` returns distinct ports, and the cap is refused by name.

## Documentation

Both of these already promise this feature as *not yet landed*, and both must be edited rather than
appended to. They are part of the definition of done, not a follow-up:

- **`CHANGELOG.md`**, inside the same `## [Unreleased]` block the kubernetes source lives in, which
  currently carries "Not in this pass: forwarding several ports through one tunnel … [#233]".
- **`README.md`**: the `${port:<name>}` bullet under *Connection-string placeholders* ("refused
  until [#233]"), the paragraph under *Reaching a backing service in a cluster* that says the same,
  and the `port` field comment in that section's example.

Worth a sentence in the README while it is open: two casings of one port name **in a single JSON
file** make `AddJsonFile(...).Build()` throw and take the whole file with it, whereas across two
configuration layers they merge with the last writer's casing winning. That asymmetry is
pre-existing and not introduced here — entry names have always been developer-invented — but this
change adds a second, nested layer of invented keys per entry, so it is newly easy to meet.

## Nothing here is a compatibility constraint

`AddBackingService`, the `"kubernetes"` source and the placeholder syntax are all still under
`## [Unreleased]`; the last tag is `v0.4.0`. Every type involved — `IPortAllocator`,
`KubectlPortForward`, `KubernetesBackingServiceSource`, `LocalPortHealthCheck`,
`KubernetesBackingServiceDeveloperConfig` — is `internal`.

So the health-check key, `KubectlPortForward.Args`'s signature, `IPortAllocator`'s shape and every
error string are free to change. Acceptance criterion 1's "a single `port` keeps working unchanged"
is about **behaviour and config shape, not about bytes**: the messages named above change on
purpose. Saying this once is what stops each of those edits reading as a breach of the criterion.

The one decision *not* freed by being unreleased is `SinglePort` as a concept distinct from a
one-entry map, which follows from the acceptance criteria themselves.

## Delivery

One PR on `main`. **[#242] (stage 3, secrets) is open on the same file and removes the neighbouring
branch of `RequireEveryPlaceholderIsResolvable`; whichever lands second resolves that conflict.**
Neither should wait for the other — a single `port` stays valid either way, and the two features
share no decision.

## Open Questions

1. **Is a forwarded port that no placeholder mentions worth a warning?** #206 built a warnings
   channel for config nothing reads, and an unreferenced named port fits its shape. Not proposed
   here: the issue's own example forwards a management port on purpose, precisely so a developer can
   open it in a browser. Left for the human to settle.
2. **Is a cap of 32 the right number, and is a cap wanted at all?** It exists to turn
   file-descriptor exhaustion into a sentence. Nothing about the feature needs a limit otherwise.

[#144]: https://github.com/flojon/aspire-servicesources/issues/144
[#233]: https://github.com/flojon/aspire-servicesources/issues/233
[#234]: https://github.com/flojon/aspire-servicesources/pull/234
[#242]: https://github.com/flojon/aspire-servicesources/pull/242
