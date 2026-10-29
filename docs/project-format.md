# RADIX project format, version 1

RADIX projects are UTF-8 JSON files with the extension `.radix`. The format is intentionally explicit: it records domains, definitions, instance placement, connectivity, parameters, and simulation settings. It does not record executable code.

The loader is strict. Unknown object properties fail rather than being ignored, unknown primitive kinds fail, and every ID/reference is validated. Save writes a temporary file beside the target and atomically replaces it.

## Top-level object

```json
{
  "format": "radix-project",
  "version": 1,
  "name": "Example",
  "description": "optional",
  "root": "main",
  "settings": { "...": "..." },
  "domains": [ "...": "..." ],
  "definitions": [ "...": "..." ]
}
```

| Field | Required | Meaning |
| --- | --- | --- |
| `format` | yes | Must be exactly `"radix-project"` |
| `version` | yes | Currently `1`; newer versions are rejected, not guessed |
| `name` | no | Display name; loader defaults to `Untitled` |
| `description` | no | Optional project note |
| `root` | yes | Definition opened by default and simulated by the CLI unless overridden |
| `settings` | no | Simulation defaults; omitted fields use defaults |
| `domains` | yes | At least one domain required for a valid project |
| `definitions` | yes | One or more definitions; root must be present |

Property names are camelCase. JSON comments and trailing commas are accepted when loading.

## Settings

```json
"settings": {
  "maxEvaluationsPerComponentPerTimestamp": 64,
  "maxRecordedNetChanges": 500000,
  "defaultAdvanceTicks": 10,
  "traceAllNets": true
}
```

- `maxEvaluationsPerComponentPerTimestamp`: non-convergence guard for zero-delay feedback. Invalid low values are warned about and clamped by the engine.
- `maxRecordedNetChanges`: capacity of the trace ring buffer; `0` disables trace storage.
- `defaultAdvanceTicks`: editor default for an Advance action.
- `traceAllNets`: whether net transitions are recorded at all.

## Domains

```json
{
  "id": "five-state",
  "name": "Five state",
  "values": ["lo", "mid", "hi", "X", "Z"],
  "drivers": { "kind": "single" },
  "description": "optional"
}
```

- `id`: stable identifier, unique in the project. The editor derives IDs from names and removes unsafe characters.
- `values`: ordered value labels. Their positions are the signal indices used everywhere in the model. The kernel attaches no numeric meaning to them.
- `drivers`: how simultaneous active drivers resolve.

### Driver policies

`single`:

```json
"drivers": { "kind": "single" }
```

Multiple active drivers resolve deterministically to the first active driver in compiled pin order, with a runtime conflict diagnostic. A net with no active drivers is `undriven` (`-1`), not necessarily a domain value.

`priority`:

```json
"drivers": {
  "kind": "priority",
  "priorities": [1, 1, 3, 0],
  "conflictValue": 2
}
```

`priorities` must have exactly one entry per domain value. Higher priority wins. Disagreeing drivers at equal priority resolve to `conflictValue`, which must be a valid domain index.

## Definitions

```json
{
  "id": "main",
  "name": "Main",
  "description": "optional",
  "pins": [],
  "instances": [],
  "nets": [],
  "ports": []
}
```

A definition is a reusable circuit: its interface (`pins`), its body (`instances`, `nets`), and the bindings from interface pins to internal nets (`ports`). Definitions may instantiate other definitions. Cycles are rejected; maximum instantiation depth is 64.

### Pins

```json
{ "id": "a", "name": "A", "direction": "input", "domain": "binary" }
```

- `id` is stable within the definition.
- `direction` is `"input"` or `"output"`.
- `domain` must name a project domain.
- The display `name` may differ from `id`.
- Every pin must have exactly one port binding.

### Instances

An instance refers to **either** a definition **or** an inline primitive, never both:

```json
{
  "id": "ff0",
  "definition": "std.dff",
  "label": "bit 0",
  "x": 560,
  "y": 60
}
```

```json
{
  "id": "gate",
  "primitive": { "kind": "lookup", "inputCount": 2, "table": [1,1,1,0], "delay": 1 },
  "x": 220,
  "y": 120
}
```

- `id` is unique within the definition.
- `definition` must exist.
- `primitive.kind` is one of the closed built-in primitive set.
- `x`, `y` are canvas coordinates in abstract world units. They persist editor placement; they never affect simulation.
- `label` is display-only.

Instance pins are derived from the referenced definition's interface or from the primitive's input count. A primitive has inputs `in0`, `in1`, ... and at most one output `out`; these IDs are used in net pin references.

### Primitive payloads

| Kind | Fields | Notes |
| --- | --- | --- |
| `constant` | `value` | Non-negative domain index |
| `lookup` | `inputCount`, `table`, `delay` | Table length is checked against connected domains at compile time; `delay` defaults to 0; maximum 16 inputs and 1,048,576 entries |
| `delay` | `delay`, `initial` | `delay` must be at least 1; `initial` is an output domain index, default 0 |
| `waveform` | `durations`, `values` | Equal non-empty lengths; each duration at least 1; values are domain indices |
| `probe` | `label` | Zero semantic effect |

`probe` has one input and no output. Its label is informational only.

### Nets

```json
{
  "id": "q0_net",
  "domain": "binary",
  "label": "Q0",
  "initial": 0,
  "pins": [
    { "instance": "ff0", "pin": "q" },
    { "instance": "u1", "pin": "in0" }
  ]
}
```

- All pins on a net share the net's domain; declared interface-pin domains are checked.
- `initial` is the net's value before any driver commits. Omit it for undriven.
- A pin may appear on at most one net.
- `pins` may be empty, but validation warns.
- Multiple output pins are permitted and resolved by the domain's driver policy.

### Port bindings

```json
"ports": [
  { "pin": "a", "net": "a_net" },
  { "pin": "y", "net": "y_net" }
]
```

Every interface pin needs exactly one binding. The pin's declared domain must match the bound net's domain. Only root ports are exposed to the CLI/testbench; nested definition ports are resolved by the compiler into flattened nets.

## Complete minimal example

This project has one definition, one input port, one lookup inverter, and one output port:

```json
{
  "format": "radix-project",
  "version": 1,
  "name": "Inverter",
  "root": "main",
  "domains": [
    {
      "id": "binary",
      "name": "Binary",
      "values": ["0", "1"],
      "drivers": { "kind": "single" }
    }
  ],
  "definitions": [
    {
      "id": "main",
      "name": "Inverter",
      "pins": [
        { "id": "a", "name": "A", "direction": "input", "domain": "binary" },
        { "id": "y", "name": "Y", "direction": "output", "domain": "binary" }
      ],
      "instances": [
        {
          "id": "inv",
          "primitive": { "kind": "lookup", "inputCount": 1, "table": [1, 0], "delay": 1 },
          "x": 200,
          "y": 100
        }
      ],
      "nets": [
        {
          "id": "a_net",
          "domain": "binary",
          "pins": [{ "instance": "inv", "pin": "in0" }]
        },
        {
          "id": "y_net",
          "domain": "binary",
          "label": "Y",
          "pins": [{ "instance": "inv", "pin": "out" }]
        }
      ],
      "ports": [
        { "pin": "a", "net": "a_net" },
        { "pin": "y", "net": "y_net" }
      ]
    }
  ]
}
```

## Loading and errors

- Malformed JSON raises `ProjectFormatException` with context from the parser.
- The loader maps the DTO into the domain model, rejecting missing IDs and invalid primitive parameters.
- Unless `validate:false` is requested by an internal caller, the project is then validated. Structural errors raise `CircuitValidationException` containing a diagnostic bag.
- The compiler performs additional connectivity-dependent checks such as lookup table sizing and port reachability.
- The CLI reports diagnostics with `RAD###` codes and exits nonzero for errors.
- The editor can load structurally invalid projects only through internal snapshots; the public open path surfaces an error and leaves the current project unchanged. (Undo snapshots are loaded with `validate:false` because they already passed through the same editor.)

## Versioning and migration

Version 1 is the only implemented format. Rules for future revisions:

1. Increment `version` for any breaking or semantically significant change.
2. Reject files newer than the build supports; never load them partially.
3. Add a single, tested migration path in the serializer layer, not in definitions everywhere.
4. Keep optional display metadata additive within a version only if old builds can still reject unknown properties without data loss; because unknown properties are currently rejected, additive fields still require a version decision.
5. Preserve domain and definition IDs across migrations where possible so editor selections and external tooling remain meaningful.
6. Add round-trip and old-file fixtures before changing the current version.

## Security model

- No executable code, plugin assembly references, type names, or scripts are accepted.
- Primitive behavior is a closed built-in set; adding a primitive requires code and tests.
- String IDs and all references are validated before simulation.
- The loader accepts comments, trailing commas, and indented JSON; it does not resolve external references or URLs.
- There is currently no maximum file size or maximum table count beyond the per-table 1,048,576-entry limit. Treat untrusted projects as a potential resource-exhaustion vector until a configurable size limit is added.
- `ProjectSerializer.Save` writes via a temporary file and atomic move, but callers are responsible for choosing a safe destination. The editor uses the platform file picker.

## What is not persisted yet

- viewport pan/zoom, active tab, and selection;
- chosen numeric display formats;
- external input event history;
- simulation runtime state (projects save topology and initial configuration only).

Instance `x`/`y` coordinates are persisted. Editor viewport state is intentionally a future addition; adding it should not change circuit semantics.
