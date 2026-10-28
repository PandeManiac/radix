# Manual testing

This document is for checks that require a human at the desktop. The agent has **not** launched the visible editor, injected input, or captured the active desktop. Automated verification uses Avalonia Headless with offscreen Skia and is safe to run while you work.

## Before you start

```bash
dotnet build Radix.slnx -c Release
dotnet test tests/Radix.Core.Tests/Radix.Core.Tests.csproj
dotnet test tests/Radix.Editor.Tests/Radix.Editor.Tests.csproj
```

The editor tests write offscreen captures to `artifacts/editor/` (ignored by Git). They do not show a window:

- `workbench-counter.png` (light);
- `workbench-counter-dark.png`;
- `workbench-waveform.png` (light);
- `workbench-waveform-dark.png`;
- `workbench-accumulator-lanes.png` (scrolled numeric-lane inspector with a drive control).

Launch the editor only when you are ready to use it:

```bash
dotnet run --project src/Radix.Editor
```

Expected first screen: the light workbench (or your saved theme), the **4-bit binary counter** example, t = 0, 0 errors, 0 warnings, and the Library visible on the left. The default window is 1480×920; minimum is 1040×660.

## 1. Baseline simulation

1. Click **Begin**. Expected: simulation compiles, t remains 0, and no errors appear.
2. Set the advance box to `100` and click **Advance**.
3. Check **Inspect → I/O PINS**:
   - `Q0 = 1`
   - `Q1 = 0`
   - `Q2 = 1`
   - `Q3 = 0`
   - Header shows `t = 100`, zero errors/warnings.
4. Click **Waveform**. Expected: rows for `Q0`…`Q3`, `clk`, constants and adder sums over a 0–100 tick axis; the clock has 10-tick phases.
5. Click **Trace**. Expected: recent net changes newest first, formatted `t=<time>  <net> → <value>`.

If counter values differ, record the values and diagnostics; this is a correctness failure.

## 2. Themes

1. Use the toolbar **Auto / Light / Dark** selector.
2. Expected in Dark: window, toolbars, sidebars, inspectors, tabs, text fields, buttons, menus, canvas grid, component bodies, pin labels, wires, signal badges, waveform, and diagnostic rows all switch together. No light panels or dark-on-dark text should remain.
3. Expected in Light: the original high-contrast light surfaces return.
4. Select **Auto** and change the operating system appearance. Expected: the editor follows the system variant while Auto is selected (or after the next window activation, depending on the desktop).
5. Restart the editor. Expected: the last manual choice is restored from the user application-data directory. The project itself does not store the theme.
6. Hover a pin and a wire in both themes. Tooltips, selection halos, bendpoint handles, and rubber-band selection should remain legible.
7. Check that wires are distinguishable without relying on color alone: junction dots appear only at real branches, crossings have visible gaps, and wire labels/selection halos supplement signal color.

## 3. Wire routing, bendpoints, and readability

1. Open the counter and zoom to ~100%. Expected: orthogonal wires avoid component bodies; crossings are gapped; junction dots appear where three or more endpoints share a net.
2. Double-click a wire segment. Expected: a bendpoint handle appears near that point and the wire reroutes through it.
3. Drag the handle. Expected: the wire follows orthogonally; the rest of the net stays connected. Undo restores the previous route.
4. Alt+click a handle. Expected: that bendpoint is removed.
5. Select a routed net and click **Reset wire route** in the net inspector. Expected: automatic routing resumes.
6. Save, close, reopen. Expected: bendpoints, custom wire colors, numeric lanes, custom numeric formats, and the active definition/viewport survive (project format v5).
7. In the net inspector, set **Wire color** to `#38bdf8`, then `auto`. Expected: valid hex applies; invalid text is rejected with a status message; `auto` returns to domain/signal coloring.
8. Select a component. Expected: connected nets and pins are emphasized while unrelated wires dim. Hovering a pin or wire does the same.

## 4. Editing, selection, and clipboard

1. Click **New**. Expected: a Save/Discard/Cancel prompt appears because the previous project may have edits. Cancel and verify nothing changed.
2. Add input pins `A` and `B`, output `Y`, and place an **AND gate**.
3. Wire A and B to the gate inputs, and the output to Y. Begin, drive A=1, B=1, advance 2 ticks. Expected Y=1.
4. Shift+click two components. Expected: both are selected; the inspector says “2 parts”. Drag one. Expected: the whole group moves in one undo step.
5. Rubber-band drag across empty canvas. Expected: intersecting parts are selected; Shift adds to the existing selection.
6. Press `Ctrl+C`, then `Ctrl+V`. Expected: copies appear offset from the originals and remain selected. Press `Ctrl+D`; expected: another duplicate without changing the clipboard.
7. Delete the duplicates, then undo once. Expected: the whole paste/duplicate returns in one step.
8. Copy two connected parts (with an internal wire), paste, and verify the pasted pair has its own internal net rather than sharing the original net. Wires to outside pins are intentionally not copied.
9. Right-click empty canvas, a component, and a wire. Expected: relevant actions (Paste/Select all/Fit, Copy/Duplicate/Open definition/Delete, Add bendpoint/Reset route/Delete wire) and no focus theft.
10. Type `ternary` in **search parts**. Expected: the Library filters and matching Definitions also filter. Clear the search to restore both lists.
11. Use arrow keys to nudge a selected part; Shift+arrow moves in 10-unit steps. Press `F` to fit.

## 5. Extract a reusable component

1. New project. Add input `A`, output `Y`, and two **NOT gate** parts.
2. Wire `A → NOT1.in0`, `NOT1.out → NOT2.in0`, `NOT2.out → Y`.
3. Select both NOT gates. In **Reusable part**, enter `Double inverter`, click **Extract selection**.
4. Expected:
   - the two gates disappear from the root and are replaced by one `Double inverter` instance;
   - the A and Y port wires now terminate on that instance;
   - Definitions shows a new `custom.double-inverter` part with 2 pins;
   - the new definition contains both gates and one internal net;
   - undo restores the two gates in one step.
5. Begin, drive A=1, advance 20. Expected Y=1 (double inversion).
6. Double-click the new instance. Expected: its internal schematic opens with `A`/`Y` port pills; edit a gate position and return to the parent. The instance and simulation wiring remain valid.
7. Try extracting one of two gates whose outputs are wired together. Expected: extraction is refused with a message about drivers on both sides; the project is unchanged.

## 6. Promote nets and interface organization

1. In a definition, place two NOT gates and wire the first output to the second input.
2. Select that connecting wire. In **Expose as interface pin**, leave the name blank or enter `MID`, then click **Output**.
3. Expected: `MID` appears in Interface/I/O PINS, a port pill becomes draggable on the canvas, and the net is bound to it. The promote buttons then disable because the net is already bound.
4. Undo. Expected: pin, binding, and generated net label are removed together.
5. In the Interface list, edit a pin's name and press Enter. Expected: the name updates, its bound net label follows when the net was using the old name, and one undo restores both.
6. Click `⇄` on a pin. Expected: its direction flips in the Interface list, I/O PINS, and any instance of this definition. Change its domain with the picker. Expected: the pin's port net adopts the same domain and an incompatible existing connection is reported in Diagnostics rather than silently rewritten.
7. Remove a pin, then undo. Expected: the pin, binding, generated net, and its wire all return in one step. If the definition has instances elsewhere, their nets lose references to that pin (and empty unbound nets disappear); the status bar reports how many dangling connections were pruned, and undo restores everything.

## 7. Numeric lanes

1. Open `examples/binary-counter.radix`. In **NUMERIC LANES**, expected: `Count · Unsigned binary` with pins `Q0 · Q1 · Q2 · Q3`.
2. Begin, then advance 100 ticks. Expected: the lane value tracks the four Q outputs as an unsigned binary number (`5` at t=100), and the value changes when you advance again. A fixed **NUMERIC LANES** legend at the lower-left of the canvas mirrors the value; it stays readable while zooming and panning.
3. Expected: the output-only `Count` lane has no drive box. Attempting to drive it through code reports that every pin must be an input.
4. Delete the lane, then undo. Expected: it returns, one undo step.
5. Add a lane: select `Unsigned binary`, tick `Q0`..`Q3` in a different order check sequence, enter a name, click **Add numeric lane**. Expected: the lane uses declaration order (Q0 first, least significant) regardless of tick order, and the card explains the grouping.
6. Add a lane with mixed domains (one binary and one ternary pin). Expected: refused with a message that a lane groups one domain; no lane is created.
7. Open `examples/accumulator-cpu.radix`. Begin, type `9` in the **Data** lane box, and press Enter. Expected: `D0`/`D3` become 1 and `D1`/`D2` become 0 (9 = 1001 binary, first pin least significant), and the lane reads `9`. Click **Drive** instead of pressing Enter and verify the same result.
8. Type `2` in **Opcode** and drive it. Expected: `OP1=1`, `OP0=0`. Advance the clock; expected: the accumulator result lane updates according to the documented opcode.
9. Type `16` for the 4-bit Data lane and drive it. Expected: refused as outside the 0..15 range; no input pin changes; the previous value stays visible. Type a non-integer and verify a clear status message.
10. Change a lane pin's domain in the Interface picker, then inspect Diagnostics. Expected: a warning explains that the format's nominal domain differs; the lane still decodes signal indices if the values are valid.
11. Remove a pin used by a lane. Expected: the pin disappears from the lane. If it was the last pin, the lane is removed. Undo restores both.
12. Save, reopen, and export/import the definition as a `.radixlib`. Expected: lane name, format, pin order, and decoded behavior survive.
13. Expected non-behavior: lanes never merge nets, add wires, affect drivers, or prevent simulation. A lane referencing a missing or unknown format shows `invalid` with a tooltip and a Diagnostics error instead of throwing. Typing a lane value is not undoable; like other external drives, it belongs to the simulation session, and **Clear inputs** resets it.

## 8. Custom numeric formats

1. Open `examples/accumulator-cpu.radix`. Expected: the **CUSTOM NUMERIC FORMATS** list contains `Signed 4-bit` (binary, radix 2, digits 0/1, radix complement), the **Result** lane uses it instead of an unsigned format, and the canvas legend labels that lane `Signed 4-bit`.
2. Begin, drive **Data** with `15` and **Opcode** with `1` (load), then advance the clock. Expected: Result reads `-1` (two's-complement interpretation of `1111`), while Q0..Q3 are all 1. Then drive **Data** with `1`, **Opcode** with `2` (add), and advance. Expected: Result reads `0` and CARRY is 1 (4-bit wrap), with Q0..Q3 all 0.
3. In a new project, create four binary input pins and a `Custom unsigned` format: domain `Binary`, digits `0 1`, radix `2`, mode **Standard positional**, LSB first. Expected: it appears in the custom-format list and in the lane format picker.
4. Add a lane over the four input pins using `Custom unsigned`, begin, and drive `9`. Expected: pins encode 1001 and the lane reads `9`.
5. Attempt to remove `Custom unsigned` while the lane uses it. Expected: refused with the number of dependent lanes; the format remains. Remove the lane, remove the format, then undo. Expected: the format returns in one step; undo again restores the lane.
6. Enter invalid format data: duplicate digits (`0 0 1`), radix `1`, or a sign index outside the digit list. Expected: a clear status message, no format added, project unchanged.
7. Save, reopen, and export/import the part as a `.radixlib`. Expected: used custom formats travel with the library. If the target has a different custom format with the same ID, the imported format is renamed and the lane still resolves; the local format is not overwritten.
8. Expected non-behavior: custom formats are not signal domains. Removing an unused format never changes net values, compilation, or simulation results.
9. Click **Edit** on `Custom unsigned`, change its name and digits, and click **Update numeric format**. Expected: the form enters edit mode, the preview line decodes the lane's live value, the format keeps its ID, the lane still resolves, and one undo restores the previous format. Repeat, change fields, then click **Cancel edit**. Expected: the project is unchanged and the form returns to add mode.

## 9. Library export and import

1. Create the `Double inverter` part from section 5. Click **Export part…**.
2. Save `double-inverter.radixlib` in a scratch directory.
3. New project, then **Import library…** and choose that file.
4. Expected:
   - the imported definition appears under Definitions and opens automatically;
   - standard definitions referenced by the library are reused, not duplicated;
   - the project becomes dirty and one undo restores the pre-import state.
5. Import the same library again. Expected: no additional definition appears.
6. Create a different local definition with the same ID but different internals, then import. Expected: the existing definition is not overwritten; the imported one gets a renamed ID such as `custom.name2`, and the status message reports the import.
7. Attempt to import a library whose domain ID exists locally with different values or driver policy. Expected: import is refused with a domain message and the project is completely unchanged.
8. Export a definition that instantiates other definitions. Expected: the `.radixlib` contains the dependency closure and imports successfully into a project that does not have those dependencies.

## 10. Domains and project round trip

1. In Inspect, under **SIGNAL DOMAINS**, enter `five state` and values `lo mid hi X Z`, click **Add domain**.
2. Expected: the domain appears in the list, pin domain picker, and net domain selector.
3. Add an input pin using the new domain. Expected: the port and its net use `five-state`.
4. Save As in a scratch path. Close and relaunch, then Open. Expected: domain, pin, current root, instance positions, wire colors, numeric lanes, bendpoints, active definition, viewport zoom, and pan survive.
5. Open a child definition, zoom to a distinctive level, and pan away from center. Save, close, and reopen. Expected: the same definition is active and the canvas returns to the same scale and offset instead of fitting to content.
6. Pan and zoom without changing the circuit. Expected: the title does not gain an unsaved-changes marker.
7. Expected non-persistence: selection, external drive history, and waveform history.

## 11. Hierarchy and malformed projects

1. Open `examples/binary-counter.radix`. Expected: root counter loads without a modal error.
2. Double-click the ripple-carry adder, then a full adder, then a half adder. Expected: nested internals render and the definition header identifies the current simulation root.
3. Return to the root and verify simulation resets for the new root.
4. Open a deliberately malformed file (change `"version": 5` to `99`, or remove `root`). Expected: Open fails with a status-bar error and the previous project remains. The loader must not partially replace editor state.
5. Save a project and inspect the JSON. Expected: `"version": 5`, with optional net `color`/`route`, definition `numericLanes`, project `customNumericFormats`, and a top-level `view` object, and no executable content.
6. Load a version 1, 2, 3, or 4 project. Expected: it opens normally, missing metadata is empty, and the next save writes version 5.

## 12. Unsaved changes and window behavior

1. Modify a project (move a part). Click **New**, **Open…**, and close the window.
2. Expected each time: a themed Save/Discard/Cancel dialog.
   - Cancel leaves the project and window untouched.
   - Discard proceeds without writing.
   - Save writes to the current path, or opens Save As if untitled; cancelling Save As aborts the New/Open/close action.
3. Confirm `Ctrl+N`, `Ctrl+O`, and the window close button use the same prompt.
4. Resize to minimum size: left rail and inspector scroll, canvas remains usable.
5. Zoom/pan, clear selection on empty click, press Escape during a pending wire, and hover pins for tooltips.
6. `Ctrl+Z`, `Ctrl+Shift+Z`/`Ctrl+Y`, `Ctrl+C/V/D/A`, `Delete`, arrow nudging, and `Ctrl+S/Shift+S` should work without moving the mouse. Shortcuts must not hijack typing in a TextBox.

## 13. Scripted CLI tests

These steps use a terminal only; they never open a window. Run from the repository root.

1. Run `dotnet run --project src/Radix.Cli -- test examples/dff-demo.radix examples/tests/dff-captures.radixtest`. Expected: `PASS: 6 step(s) through t=50`, exit code 0.
2. Run `dotnet run --project src/Radix.Cli -- test examples/accumulator-cpu.radix examples/tests/accumulator.radixtest`. Expected: PASS; the signed `Result` lane is `-1` after LOAD 15 and `0` after adding 1, with `CARRY=1`.
3. Run `dotnet run --project src/Radix.Cli -- test examples/tiny-cpu.radix examples/tests/tiny-cpu.radixtest`. Expected: `PASS: 37 step(s) through t=1180`, exit code 0.
4. Copy the DFF script to a scratch file and change the `t=30` expectation for `q` from `1` to `0`. Expected: `FAIL`, one line naming `step 4 (t=30)`, expected and actual values; exit code 1.
5. Change a script field name (for example `expect` to `expected`). Expected: `test script error`, exit code 2, and no partial run output beyond the error.
6. Confirm the project files are untouched by any passing or failing script. Expected: no writes; scripts only drive external inputs in memory.

## 14. Tiny program-driven CPU

This uses the GUI, so run it yourself. The machine is built entirely from library parts and lookup tables; there is no CPU code in the kernel.

1. Open `examples/tiny-cpu.radix`. Expected: the canvas shows the ROM, decoder, program counter, accumulator, RAM, stack pointer, and a fixed lane legend; **OPCODE**, **OPERAND**, **INSTRUCTION**, **PC**, **ACC**, **SP**, and **MEM** appear in the inspector.
2. Click **Begin**, then drive `CLK = 0` and `RESET = 0`. Expected: `PC = 0`, `ACC = 0`, `SP = 0`, `MEM = 0`, `ZERO = 1`, `IR = 21` (0x15), opcode 1, operand 5.
3. For each rising edge, set `CLK = 1`, click **Settle**, check the row below, then set `CLK = 0`. Expected sequence:

| Edge | PC | IR | Op | Operand | ACC | SP | MEM | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 56 (0x38) | 3 | 8 | 5 | 0 | 0 | LDA 5 done; STA 8 fetched |
| 2 | 2 | 25 (0x19) | 1 | 9 | 5 | 0 | 0 | STA 8 stored 5 |
| 3 | 3 | 160 (0xA0) | 10 | 0 | 9 | 0 | 0 | LDA 9 done; PUSH fetched |
| 4 | 4 | 16 (0x10) | 1 | 0 | 9 | 1 | 9 | PUSH wrote 9; SP advanced |
| 5 | 5 | 176 (0xB0) | 11 | 0 | 0 | 1 | 9 | LDA 0 done; stack top visible |
| 6 | 6 | 57 (0x39) | 3 | 9 | 9 | 0 | 0 | POP restored 9; SP rewound |
| 7 | 7 | 204 (0xCC) | 12 | 12 | 9 | 0 | 9 | STA 9 stored 9; CALL fetched |
| 8 | 12 | 136 (0x88) | 8 | 8 | 9 | 1 | 5 | CALL pushed return address 8 |
| 9 | 13 | 153 (0x99) | 9 | 9 | 5 | 1 | 9 | LDA@ 8 loaded 5 |
| 10 | 14 | 208 (0xD0) | 13 | 0 | 14 | 1 | 8 | ADD@ 9 made 14; stack top is 8 |
| 11 | 8 | 104 (0x68) | 6 | 8 | 14 | 0 | 5 | RET returned to PC 8 |
| 12 | 8 | 104 (0x68) | 6 | 8 | 14 | 0 | 5 | JMP 8 halt loop holds state |

4. Drive `RESET = 1`, raise `CLK`, then lower `CLK`. Expected: `PC = 0`, `ACC = 0`, `SP = 0`, `CARRY = 0`; RAM contents are retained, and the machine restarts at instruction 0. Drive `RESET = 0` before continuing.
5. Check the **Trace** and **Waveform** tabs. Expected: transitions are recorded for the clock, PC bits, accumulator bits, SP bits, RAM write enables, IR bits, carry, and zero; no errors or warnings appear.
9. Check the **Trace** and **Waveform** tabs. Expected: transitions are recorded for the clock, PC bits, accumulator bits, IR bits, carry, and zero; no errors or warnings appear.

## 15. Known limitations to verify as *limitations*

Do not file these as new failures until they change in the README:

- auto-routing is heuristic; dense circuits may need bendpoints, and components are not collision-moved automatically;
- extraction refuses nets with drivers on both sides because there is no inout pin direction;
- library equivalence ignores labels/layout and numeric-lane presentation; conflicting IDs are renamed rather than offering a merge dialog;
- selection, external drive history, and waveform history are not saved; viewport and active definition are saved;
- no analog solver, JIT, parallel engine, or first-class bus/vector type;
- editor-created domains use single-driver resolution only;
- waveform tail is bounded and may omit old transitions;
- the format preview decodes only the first lane that uses the edited format; the canvas lane legend is fixed at the lower-left, caps at seven rows, and the remaining lanes stay in the inspector;
- `.radixtest` scripts are linear; there are no loops, variables, expressions, random drives, or waveform-window assertions;
- undo snapshots whole projects and is capped at 80 entries;
- the editor does not auto-save or recover crash state.

## Isolation statement

Automated GUI verification in this repository is headless only:

- Avalonia Headless platform with offscreen Skia;
- no `DISPLAY` override, no visible windows, no input injection;
- `artifacts/editor/*.png` are generated from `RenderTargetBitmap` inside the test process;
- no test starts the desktop editor executable.

Do not "help" a future automated run by starting the GUI on the active display. When an interactive check is needed, it must remain a human-driven step documented here.
