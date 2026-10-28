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
- `workbench-accumulator-lanes.png` (scrolled numeric-lane inspector with a drive control);
- `workbench-lane-editor.png` (numeric lane loaded into the edit form);
- `workbench-domain-policy.png` (signal-domain inspector with a priority table);
- `workbench-diagnostics-locate.png` (Diagnostics tab with a Locate action);
- `workbench-net-navigator.png` (filtered net list with live values);
- `workbench-interface-reorder.png` (interface rows with move controls).

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
4. Click **Waveform**. Expected: rows for `Q0`…`Q3`, `clk`, constants and adder sums over a 0–100 tick axis; the clock has 10-tick phases; the header describes up to 16 nets in declaration order.
5. In **Inspect**, select a late net in **NETS** (for example `sum3` or `carry`) and click **Show in waveform**. Expected: the button becomes **Hide from waveform**, the net row's summary gains `· waveform`, the Waveform tab lists that net first with the header reporting one pinned net, and simulation values are unchanged. Click again to unpin; the rows return to declaration order. Pins belong to the current definition and are not saved with the project.
6. Click **Trace**. Expected: recent net changes newest first, formatted `t=<time>  <net> → <value>`.
7. Return to **Waveform**. Wheel over the plot to zoom around the pointer. Expected: the tick labels narrow to the chosen range (for example `t=60`…`t=120`), the clock edges stay aligned with the narrower labels, and the header hint names the controls. Drag left or right to pan; the range must stop at tick 0 and at the simulation end. Click **Advance** while zoomed: the window must stay where it is. Double-click the plot: the full 0…current trace returns and later advances follow the end again. Click **Begin** (simulation reset) while zoomed: the whole new trace must be shown.

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
9. Scroll to **NETS** in the Inspect tab. Expected: every net in the current definition appears with its domain, pin count, and a live value once the simulation has advanced. Type `q` in the filter. Expected: only matching nets remain and the summary reports the match count. Click `q0_net`. Expected: its wire is selected and the net inspector opens, without changing connectivity or simulation state.
10. Open `examples/tiny-cpu.radix`, zoom to roughly 70%, and drag a large component such as the RAM or ROM. Expected: unchanged wires keep their paths and the drag stays responsive; on release, affected routes settle into canonical obstacle-free paths. Undo restores the position and the previous routes. Very large definitions may pause briefly on release while the final layout is rebuilt.

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
12. Begin and advance to a nonzero time. Drag or nudge a part, change a wire color, and add/remove a bendpoint. Expected: the circuit redraws but simulated time, values, and drives remain; no Begin is needed. Add or delete a part: expected the simulation is invalidated and must be begun again.
13. Select three parts at different positions. Expected: **ARRANGE SELECTION** appears under the Definitions list with four buttons; with exactly two parts only the two Align buttons are enabled. Click **Align left**: the parts share the leftmost X, the selection stays intact, and the status line reports the count. `Ctrl+Z` restores the previous positions in one step while keeping the running simulation.
14. With the same three parts at uneven X positions, click **Space ↔**. Expected: the outermost parts do not move and the middle part sits exactly halfway between their left edges; **Space ↕** behaves the same for Y. Re-running the command does not move anything.

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
7. Move a pin with `↑`/`↓`. Expected: it changes position in the Interface list and on every instance; its bound net and lane pin lists are unchanged, `↑` is disabled on the first pin and `↓` on the last, and one undo restores the previous order. Add a numeric lane afterward and verify the default pin order follows the new declaration order.
8. Remove a pin, then undo. Expected: the pin, binding, generated net, and its wire all return in one step. If the definition has instances elsewhere, their nets lose references to that pin (and empty unbound nets disappear); the status bar reports how many dangling connections were pruned, and undo restores everything.

## 7. Numeric lanes

1. Open `examples/binary-counter.radix`. In **NUMERIC LANES**, expected: `Count · Unsigned binary` with pins `Q0 · Q1 · Q2 · Q3`.
2. Begin, then advance 100 ticks. Expected: the lane value tracks the four Q outputs as an unsigned binary number (`5` at t=100), and the value changes when you advance again. A fixed **NUMERIC LANES** legend at the lower-left of the canvas mirrors the value; it stays readable while zooming and panning.
3. Expected: the output-only `Count` lane has no drive box. Attempting to drive it through code reports that every pin must be an input.
4. Delete the lane, then undo. Expected: it returns, one undo step.
5. Add a lane: select `Unsigned binary`, tick `Q0`..`Q3` in a different order check sequence, enter a name, click **Add numeric lane**. Expected: the lane uses declaration order (Q0 first, least significant) regardless of tick order, and the card explains the grouping. Move a pin with `↑`/`↓` before adding another lane to verify the new default order; existing lanes keep the order saved with them.
6. Edit a lane: click **Edit** on a lane card. Expected: the form shows that lane's name, format, and ticked pins, the button changes to **Update numeric lane**, and **Cancel lane edit** appears. Change the name, choose another format, untick one pin, and click **Update numeric lane**. Expected: the same card updates in place, keeps its lane, and all three changes land as one undo step. Undo restores the name, format, and pin set together. Repeat, change fields, then click **Cancel lane edit**. Expected: the lane and project are unchanged, the form returns to add mode, and the button reads **Add numeric lane**. Before saving an edit, clear every pin and click **Update numeric lane**. Expected: refused with a message, and the form stays in edit mode so the draft is not lost. Remove the lane while editing it. Expected: the lane disappears and the form exits edit mode.
7. Add a lane with mixed domains (one binary and one ternary pin). Expected: refused with a message that a lane groups one domain; no lane is created.
8. Open `examples/accumulator-cpu.radix`. Begin, type `9` in the **Data** lane box, and press Enter. Expected: `D0`/`D3` become 1 and `D1`/`D2` become 0 (9 = 1001 binary, first pin least significant), and the lane reads `9`. Click **Drive** instead of pressing Enter and verify the same result.
9. Type `2` in **Opcode** and drive it. Expected: `OP1=1`, `OP0=0`. Advance the clock; expected: the accumulator result lane updates according to the documented opcode.
10. Type `16` for the 4-bit Data lane and drive it. Expected: refused as outside the 0..15 range; no input pin changes; the previous value stays visible. Type a non-integer and verify a clear status message.
11. Change a lane pin's domain in the Interface picker, then inspect Diagnostics. Expected: a warning explains that the format's nominal domain differs; the lane still decodes signal indices if the values are valid.
12. Remove a pin used by a lane. Expected: the pin disappears from the lane. If it was the last pin, the lane is removed. Undo restores both.
13. Save, reopen, and export/import the definition as a `.radixlib`. Expected: lane name, format, pin order, and decoded behavior survive.
14. Expected non-behavior: lanes never merge nets, add wires, affect drivers, or prevent simulation. A lane referencing a missing or unknown format shows `invalid` with a tooltip and a Diagnostics error instead of throwing. Typing a lane value is not undoable; like other external drives, it belongs to the simulation session, and **Clear inputs** resets it.

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

1. In Inspect, under **SIGNAL DOMAINS**, enter `five state` and values `lo mid hi X Z`, leave **Single driver** selected, and click **Add domain**.
2. Expected: the domain appears in the list with its values and `single driver` policy, in the pin domain picker, and in the net domain selector.
3. Create a priority domain: name `wired`, values `0 1 X Z`, policy **Priority table**, priorities `1 1 3 0`, conflict index `2`; click **Add domain**. Expected: the list summary reads `0 1 X Z · priority [0:1, 1:1, 2:3, 3:0] (ties resolve to 2)`, and the new domain appears in both pickers. With two constants on one net of this domain, compile/run Diagnostics should show no single-driver warning; if equal-priority drivers disagree, the net settles to `X` and a conflict warning names the net.
4. Validation: set policy **Priority table** and try priorities `1 1 3` (wrong count), `1 1 3 nope`, and conflict index `4`. Expected: each is refused with a specific message, no domain is added, and the form keeps the entered draft.
5. Add an input pin and choose `five state` as its domain. Expected: the port and its net use `five-state`.
6. Save As in a scratch path. Close and relaunch, then Open. Expected: domain values and driver policy, pin, current root, instance positions, wire colors, numeric lanes, bendpoints, active definition, viewport zoom, and pan survive.
7. Open a child definition, zoom to a distinctive level, and pan away from center. Save, close, and reopen. Expected: the same definition is active and the canvas returns to the same scale and offset instead of fitting to content.
8. Pan and zoom without changing the circuit. Expected: the title does not gain an unsaved-changes marker.
9. Change an existing domain's policy: click **Policy** on the row for `wired`. Expected: the form loads the domain's name, values, policy, priorities, and conflict index; name and values fields are disabled; the button reads **Update domain policy**; **Cancel policy edit** appears; a hint explains that labels and count stay fixed.
10. Set priorities `1 2 3 0` and click **Update domain policy**. Expected: the edit ends and the form returns to add mode, the list summary shows `priority [0:1, 1:2, 2:3, 3:0]`, no new domain ID appears, pin/net domain pickers still show the same entry, and undo restores the previous table.
11. Click **Policy** again and enter a wrong priority count (`1 1 3`) with **Update domain policy**. Expected: the update is refused with a specific message, the edit stays active, and the stored policy is unchanged. **Cancel policy edit** returns to the add-domain form without changing the domain.
12. Expected non-persistence: selection, external drive history, and waveform history.

## 11. Hierarchy and malformed projects

1. Open `examples/binary-counter.radix`. Expected: root counter loads without a modal error.
2. Double-click the ripple-carry adder, then a full adder, then a half adder. Expected: nested internals render and the definition header identifies the current simulation root.
3. Click `←` in the canvas top-right control cluster twice. Expected: the full adder and then the ripple-carry adder reopen in reverse order, `→` becomes enabled, and `←` disables at the root. Click `→` once and verify it returns to the full adder. Open a different definition after backing up: `→` disables, so the forward trail was dropped. The simulation root and its reset behavior follow each switch exactly as a double-click navigation does.
4. Return to the root and verify simulation resets for the new root.
5. Open a deliberately malformed file (change `"version": 5` to `99`, or remove `root`). Expected: Open fails with a status-bar error and the previous project remains. The loader must not partially replace editor state.
6. Trigger a located diagnostic (for example, drive one single-driver net from two outputs, or reopen a project whose Diagnostics lists a located error), switch to **Diagnostics**, and click **Locate** on the message. Expected: the status bar names the code, the owning definition opens when needed, the named instance or net is selected, and the canvas centers on it without changing the circuit. A project-wide message has no **Locate** button.
7. Save a project and inspect the JSON. Expected: `"version": 5`, with optional net `color`/`route`, definition `numericLanes`, project `customNumericFormats`, and a top-level `view` object, and no executable content.
8. Load a version 1, 2, 3, or 4 project. Expected: it opens normally, missing metadata is empty, and the next save writes version 5.
9. Duplicate a definition: select a part and click `⧉` in the Definitions list. Expected: a new entry appears directly below with an ID ending `-copy` and a name ending ` copy`; it opens automatically with the same pins, instances, nets, lane, and layout. Edit the copy's wiring or move a part; the original and every existing instance of it must stay unchanged.
10. Duplicate the copy. Expected: the ID counts up (`part-copy2`, not `part-copy-copy`) and the name counts up (`… copy 2`). Undo once to remove only the second copy, and again to remove the first; the original remains throughout.

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
7. Resize to about 1200×800, select the **Waveform** tab, and close the app. Relaunch. Expected: the window reopens at the same size with **Waveform** active. Maximize, close, and relaunch; expected: maximized again. Restore the window; expected: it returns to the last normal size.
8. Preferences live outside projects: `%APPDATA%\Radix\workspace.json` on Windows or `~/.config/Radix/workspace.json` on Linux; the theme remains in `theme.txt` beside it. Delete `workspace.json` and relaunch. Expected: the default 1480×920 window on the **Inspect** tab.
9. Resize the window or switch tabs, then save a project. Expected: the project JSON and its `view` metadata are unchanged by these preferences, and reopening the project does not alter the window layout.
10. Open two different projects with **Open…**, then check **Recent ▾**. Expected: both appear newest first; choosing one opens it, and an unsaved project prompts Save/Discard/Cancel first.
11. Close and relaunch the editor. Expected: **Recent ▾** still lists both projects. Delete one of the files outside the editor, relaunch again, and reopen the menu: the deleted entry is gone and the other remains.
12. Launch with a path, for example `dotnet run --project src/Radix.Editor -- examples/tiny-cpu.radix`. Expected: the tiny CPU opens instead of the counter demo, its saved viewport is restored, and it appears in **Recent ▾**. Launching with a missing path opens the counter demo.

## 13. Scripted CLI tests

These steps use a terminal only; they never open a window. Run from the repository root.

1. Run `dotnet run --project src/Radix.Cli -- test examples/dff-demo.radix examples/tests/dff-captures.radixtest`. Expected: `PASS: 4 step(s) through t=50`, exit code 0. The capture is asserted with `"within": { "ticks": 10, ... }`, so the script waits for the transport delay instead of pinning one tick; the held low phase is asserted with `"stable": { "ticks": 10, ... }`, which checks every tick from 40 through 50.
2. Run `dotnet run --project src/Radix.Cli -- test examples/accumulator-cpu.radix examples/tests/accumulator.radixtest`. Expected: PASS; the signed `Result` lane is `-1` after LOAD 15 and `0` after adding 1, with `CARRY=1`.
3. Run `dotnet run --project src/Radix.Cli -- test examples/balanced-ternary-adder-2.radix examples/tests/balanced-ternary-adder-2.radixtest`. Expected: `PASS: 10 step(s) through t=140`, exit code 0. The step expectations cover sums 8, −8, 0, 2, and 1 through the three-trit `A + B` lane.
4. Run `dotnet run --project src/Radix.Cli -- simulate examples/balanced-ternary-adder-2.radix --set a0=2 --set a1=2 --set b0=2 --set b1=2 --ticks 30 --bus s0,s1,cout --format balanced-ternary`. Expected: the decoded bus prints `8`.
5. Run `dotnet run --project src/Radix.Cli -- inspect examples/balanced-ternary-adder-2.radix`. Expected: `std.bt-full-adder` and `std.bt-adder-2` appear as definitions, and the project root is `demo.bt-adder-2`.
6. Run `dotnet run --project src/Radix.Cli -- test examples/tiny-cpu.radix examples/tests/tiny-cpu.radixtest`. Expected: `PASS: 37 step(s) through t=1180`, exit code 0.
7. Copy the DFF script to a scratch file and change its window to `"ticks": 0` while keeping `"q": 1`. Expected: `FAIL`, one line naming `step 3 (t=20)` with `no tick up to t=20 satisfied the window` and the last observed `q`; exit code 1. This shows a window is a settle-time bound, not an immediate guarantee.
8. Change a script field name (for example `expect` to `expected`). Expected: `test script error`, exit code 2, and no partial run output beyond the error.
9. Confirm the project files are untouched by any passing or failing script. Expected: no writes; scripts only drive external inputs in memory.
10. Run `RADIX_LAYOUT_BENCH=1 dotnet test tests/Radix.Editor.Tests/Radix.Editor.Tests.csproj -c Release --filter FullyQualifiedName~LayoutBenchmarkTests --logger "console;verbosity=detailed"`. Expected: one passing test and one line per size (`200`, `500`, `1,000` gates) in the form `1000 gates: construct … ms, canonical frames … ms, reused frames … ms, final rebuild … ms`. Reused frames must be far below canonical frames; exact milliseconds depend on the machine. With the variable unset, the test passes without printing.
11. Run `dotnet run --project src/Radix.Cli -- test examples/ternary-counter.radix examples/tests/ternary-counter.radixtest`. Expected: `PASS: 28 step(s) through t=700`, exit code 0; the script walks all nine counts, checks the wrap to 0, and requires exactly one CLK, T0, and T1 change on the wrap edge.
12. Run `dotnet run --project src/Radix.Cli -- simulate examples/ternary-counter.radix --ticks 100 --schedule 0:clk=0 --schedule 20:clk=2 --schedule 60:clk=0 --bus t0,t1 --format unsigned-ternary`. Expected: the decoded bus prints `1`; the two flip-flops sample the increment only on the 0→2 clock edge.
13. Run `dotnet run --project src/Radix.Cli -- test examples/ternary-adder-2.radix examples/tests/ternary-adder-2.radixtest`. Expected: `PASS: 10 step(s) through t=140`, exit code 0, covering sums 16, 9, 8, and 0.
14. Run `dotnet run --project src/Radix.Cli -- test examples/ternary-accumulator.radix examples/tests/ternary-accumulator.radixtest`. Expected: `PASS: 17 step(s) through t=410`, exit code 0; the `Result` lane goes 5 → 8 → 3 (wrap) → 3 (hold) → 8.
15. Run `dotnet run --project src/Radix.Cli -- test examples/ternary-machine.radix examples/tests/ternary-machine.radixtest`. Expected: `PASS: 32 step(s) through t=2260`, exit code 0; PC walks 0..8, ACC follows 1, 3, 5, 5, 2, 4, 5, 5, 5, then restarts with 1.
16. Run `dotnet run --project src/Radix.Cli -- test examples/ternary-branch-machine.radix examples/tests/ternary-branch-machine.radixtest`. Expected: `PASS: 28 step(s) through t=2040`, exit code 0; MEM1 becomes 4, a BZ falls through, a later BZ jumps to PC 8, LOADM reads 4 back into ACC, and PC wraps to 0.
17. Run `dotnet run --project src/Radix.Cli -- test examples/balanced-ternary-accumulator.radix examples/tests/balanced-ternary-accumulator.radixtest`. Expected: `PASS: 21 step(s) through t=1220`, exit code 0; the `Result` lane goes 0 → 4 → 4 (hold) → −1 (wrap) → 1 → 1.
18. Run `dotnet run --project src/Radix.Cli -- test examples/ternary-call-machine.radix examples/tests/ternary-call-machine.radixtest`. Expected: `PASS: 28 step(s) through t=2040`, exit code 0; SP walks 0 → 1 → 2 → 1 → 0 and both RETs return to the instruction after their CALL.
19. Run `dotnet run --project src/Radix.Cli -- test examples/ternary-memory.radix examples/tests/ternary-memory.radixtest`. Expected: `PASS: 10 step(s) through t=680`, exit code 0; word 5 holds 8, word 0 holds 3, and WE=1 leaves word 5 intact while D changes.
20. Run `dotnet run --project src/Radix.Cli -- test examples/ternary-stack-machine.radix examples/tests/ternary-stack-machine.radixtest`. Expected: `PASS: 30 step(s) through t=3480`, exit code 0; TOS goes 0 → 2 → 3 → 2 → 5 → 5 → 5 → 5 → 0, SP returns to 0, and OUT becomes 5.
21. Run `dotnet run --project src/Radix.Cli -- test examples/four-state-bus.radix examples/tests/four-state-bus.radixtest`. Expected: `PASS: 14 step(s) through t=122`, exit code 0, plus one `WARNING RAD101` for the bus net on the first run; the warning does not fail the script.
22. Copy the DFF script to a scratch file and change its final `stable` expectation to `"q": 0`. Expected: `FAIL`, one line naming `step 4 (t=40)` with `stable assertion failed at t=40` and `pin 'q': expected 0 (0), got 1 (1)`; exit code 1. This shows a stability window checks its starting tick and every tick through its deadline, not only the end.
23. Copy the ternary counter script to a scratch file and change `"t0": 1` in its `transitions` block to `"t0": 0`. Expected: `FAIL`, one line naming `step 27 (t=700)` with `pin 't0': expected 0 transition(s) between t=670 and t=700, observed 1`; exit code 1. This shows transition counts cover the whole interval, including the drive at its start.

### Editor script runner

1. Start the workbench with `dotnet run --project src/Radix.Editor` and open `examples/dff-demo.radix`. Select the **Diagnostics** tab. Expected: a **Run script…** button sits at the upper right of the tab.
2. Click **Run script…** and choose `examples/tests/dff-captures.radixtest`. Expected: a result panel appears with a `PASS` summary naming the script and `4 step(s) · t=50`; it has no failure rows, and the live simulation time and canvas are unchanged.
3. Copy the script to a scratch file, change the final `stable` expectation to `"q": 0`, and run that file. Expected: the panel shows `FAIL`, one row reading `step 4 (t=40): pin 'q': expected 0 (0), got 1 (1).`, and the status bar reports the failure.
4. Click **Clear**. Expected: the result panel disappears while the ordinary diagnostics remain.
5. Run the passing script again, then open another project. Expected: the result panel clears because it described the previous project.

## 14. Ternary multi-trit addition

This check uses the GUI and the examples from the previous section.

1. Open `examples/balanced-ternary-adder-2.radix`. Expected: the canvas shows one `adder` instance wired to A0/A1, B0/B1, S0/S1, and COUT; the inspector lists lanes **A**, **B**, and **A + B**.
2. Click **Begin**. Set `A0 = +`, `A1 = +` (`A = 4`), `B0 = +`, `B1 = +` (`B = 4`), then **Settle**. Expected: `S0 = −`, `S1 = 0`, `COUT = +`, and the `A + B` lane reads `8`.
3. Set `A0 = −`, `A1 = −`, `B0 = −`, `B1 = −` (`A = B = −4`) and settle. Expected: `S0 = +`, `S1 = 0`, `COUT = −`, lane `−8`.
4. Set `A0 = −`, `A1 = +` (`A = 2`) and `B0 = +`, `B1 = −` (`B = −2`) and settle. Expected: every sum trit is `0` and the lane reads `0`.
5. Double-click the `adder` instance. Expected: two `std.bt-full-adder` instances and a constant zero are visible, with the low adder's carry wired into the high adder. Double-click one full adder. Expected: separate sum and carry lookup primitives with a shared `sum`/`carry` table layout.
6. Return to the root and use **Locate** diagnostics if any appear. Expected: no errors or warnings for the shipped example.
7. Open `examples/ternary-adder-2.radix`. Expected: lanes **A**, **B**, and **A + B** over `unsigned-ternary`. Set `A0 = A1 = B0 = B1 = 2` (A = B = 8) and settle. Expected: `S0 = 1`, `S1 = 2`, `COUT = 1`, and `A + B` reads `16`.
8. Set `A0 = A1 = 1` (A = 4), `B0 = 2`, `B1 = 1` (B = 5), and settle. Expected: `S0 = S1 = 0`, `COUT = 1`, and the lane reads `9`.

## 15. Ternary counter (sequential ternary logic)

This uses the GUI.

1. Open `examples/ternary-counter.radix`. Expected: one `inc`, one `carry_out`, one `add_high`, and two `ff` instances; the inspector lists a single **Count** lane over T0/T1.
2. Click **Begin**. With `CLK = 0`, settle, then set `CLK = 2` and settle. Expected: `T0 = 1`, `T1 = 0`, **Count** = `1`; setting `CLK` back to `0` does not change the count.
3. Repeat the 0→2 edge. Expected: `T0 = 2`, count `2`; next edge: `T0 = 0`, `T1 = 1`, count `3` (the low trit wraps and carries).
4. Continue to count `8` (`T0 = 2`, `T1 = 2`), then pulse once more. Expected: `T0 = 0`, `T1 = 0`, count `0`.
5. Double-click `ff0`, then its `slave` latch. Expected: two `std.t-latch` instances, a Kleene NOT for the clock, and inside the latch two cross-coupled NOT/min pairs. The definitions use `std.t-lognot`, not the mod-3 `std.t-not`.
6. Check **Trace**: the clock, `next T0`, `T0 carry`, `next T1`, and both stored trits should transition only around edges; no warnings or errors appear.

## 16. Ternary accumulator

This uses the GUI. The accumulator is two trit flip-flops plus a ternary adder and four trit muxes; `LOAD` and `ADD` use the trit value 2 as active, 0 as inactive.

1. Open `examples/ternary-accumulator.radix`, click **Begin**, and drive `CLK = 0`, `LOAD = 0`, `ADD = 0`. Expected: the inspector lists **Data** and **Result** lanes, `Q0 = Q1 = 0`, `Result = 0`.
2. Set `D0 = 2`, `D1 = 1` (`Data = 5`), set `LOAD = 2`, click **Settle**, then set `CLK = 2` and settle. Expected: `Q0 = 2`, `Q1 = 1`, `Result = 5`. Lower `CLK` to 0.
3. Set `LOAD = 0`, `ADD = 2`, `D0 = 0`, `D1 = 1` (`Data = 3`), settle, then raise `CLK`. Expected: `Result = 8`.
4. Change `Data` to 4 and pulse `CLK` again. Expected: `Result = 3`, because 8 + 4 wraps modulo 9.
5. Set `ADD = 0`, leave `LOAD = 0`, set `Data = 8`, and pulse `CLK`. Expected: `Result` stays 3; the data lane shows 8 but the register holds.
6. Set `LOAD = 2` and pulse `CLK`. Expected: `Result = 8`.
7. Double-click `register`, then `mux0`, then `ff0` and its `slave` latch. Expected: the register contains two mux/flip-flop trit slices, the mux is a single lookup primitive, and the latch/flip-flop structure matches section 15. Double-click `mux_add0` at the root. Expected: one lookup primitive selecting data or the adder result. No errors or warnings appear.

## 17. Crash recovery

Recovery uses a 20-second timer and one snapshot slot. Verify the destructive step with a scratch copy so no real work is at risk.

1. Open `examples/full-adder.radix`, place one extra part on the canvas, and wait at least 25 seconds. Then terminate the editor process without closing the window (for example `kill <pid>` on Linux or Task Manager on Windows).
2. Relaunch. Expected: the project opens immediately, the title is marked dirty (`*`), the status line says `Recovered unsaved changes from full-adder.radix`, and the extra part is present.
3. Press `Ctrl+S`. Expected: it writes to the original `examples/full-adder.radix` path (no Save As dialog), the dirty marker clears, and `recovery.radix`/`recovery.json` disappear from `%APPDATA%\Radix` or `~/.config/Radix`.
4. Relaunch. Expected: the normal full-adder example opens clean; **Recent ▾** lists it first.
5. Repeat step 1 with another scratch edit, then close the window and choose **Discard**. Relaunch. Expected: no recovery opens and the snapshot files are gone.
6. Repeat step 1 but kill the process within a few seconds of the edit. Expected, per the limitation below: the previous or no snapshot is restored, showing that at most 20 seconds of edits are protected.

## 18. Ternary program machine

This uses the GUI. The machine fetches a two-trit instruction from lookup ROMs and executes it on each rising clock edge; the program is data in the two ROM tables.

1. Open `examples/ternary-machine.radix` and click **Begin**. Drive `CLK = 0` and settle. Expected: lanes **PC** = 0, **ACC** = 0, **Opcode** = 1, **Operand** = 1. The status is clean.
2. Set `CLK = 2`, settle, then set `CLK = 0` and settle. Expected: **PC** = 1, **ACC** = 1, opcode 2, operand 2 (ADD 2 is next).
3. Repeat the 0 → 2 → 0 pulse seven more times. Expected pairs (PC, ACC): (2,3), (3,5), (4,5), (5,2), (6,4), (7,5), (8,5). Opcode/operand follow the program: ADD 2, NOP 0, LOAD 2, ADD 2, ADD 1, NOP 0, NOP 0.
4. Pulse once more. Expected: **PC** returns to 0, ACC stays 5, opcode/operand read LOAD 1. Pulse again: ACC becomes 1, showing the program restarted.
5. Select the `rom_op` instance and edit its table in the inspector. Change entry 0 (PC 0) from 1 to 2 and commit. Expected: one undo step, a diagnostic-free project, and PC 0 now executes ADD instead of LOAD (ACC becomes 1+1=2 on the next pass). Undo restores LOAD.
6. Double-click `pc_reg` and `acc_reg`. Expected: each contains two mux/flip-flop slices; the PC register's `LOAD` input is tied to a constant 2, while the accumulator's comes from the decoder and a ternary max gate. No errors or warnings appear.

## 19. Ternary branch machine

This uses the GUI. The machine has a 2-trit opcode, a 2-trit argument, three words of writable trit memory, and branch control.

1. Open `examples/ternary-branch-machine.radix` and click **Begin**. Drive `CLK = 0` and settle. Expected: **PC** = 0, **ACC** = 0, **Memory read** = 0, **Opcode** = 1, **Argument** = 2 (LOAD 2).
2. Set `CLK = 2`, settle, then set `CLK = 0`. Expected: **PC** = 1, **ACC** = 2, opcode 2, argument 2 (ADD 2 is next).
3. Pulse three more times. Expected after each pulse: (PC 2, ACC 4), (PC 3, ACC 4), (PC 4, ACC 4). The third pulse executed `STORE 1`, and the fourth fetched `BZ 8` and did **not** branch because ACC is 4. `Memory read` still shows 0 because the address pins carry the next instruction's argument, not the store address.
4. Pulse twice more. Expected: (PC 5, ACC 0) and (PC 6, ACC 0). Then pulse again: **PC** jumps straight to 8, skipping the `LOAD 1` at PC 7, because `BZ 8` sees ACC = 0. Opcode/argument read 4 and 1 (LOADM 1), and **Memory read** becomes 4 — word 1 now drives the read port.
5. Pulse twice more. Expected: **PC** = 0 and **ACC** = 4 — `LOADM 1` captured the stored word — then **PC** = 1 with ACC = 2 as the program restarts, and `Memory read` returns to 0.
6. Double-click `memory`. Expected: the `std.t-ram-3x2` body with three `word` registers, three write-decode lookups, and two 4-input read lookups. Double-click `word1` and then `mux0`. Expected: a `std.t-reg-2` body with two mux/flip-flop slices; `word1` holds 4 after the STORE. Select `decode_write1` and change its table entry for `(WE = 2, ADDR = 1)` from 2 to 0, then commit: one undo step and no diagnostics; the next `STORE 1` leaves word 1 unchanged. Undo restores the write.
7. With `memory` open as the current definition, click **Begin** to run the part standalone (this starts a fresh simulation, so it does not inherit the machine's word 1). Expected: lanes **Address** over `ADDR`, **Data** over `D0/D1`, and **Read** over `Q0/Q1`; **Read** = 0. Drive `WE = 0`, `ADDR = 1`; set `D0 = 1`, `D1 = 1` (Data = 4) and `WE = 2`; pulse `CLK` 0 → 2 → 0. Expected: **Read** = 4. Set `WE = 0` and step `ADDR` through 0, 1, and 2: expected **Read** = 0, 4, 0. No errors or warnings appear.

## 20. Ternary call machine

This uses the GUI. CALL/RET use the `std.t-ram-9x2` part as a nine-word return stack, and SP is a two-trit counter. CALL at SP=8 writes the last word and raises the latched fault; nesting up to eight levels is safe.

1. Open `examples/ternary-call-machine.radix` and click **Begin**. Drive `CLK = 0` and settle. Expected: **PC** = 0, **ACC** = 0, **SP** = 0, **Stack read** = 0, **Opcode** = 5, **Argument** = 4 (CALL 4).
2. Pulse `CLK` 0 → 2 → 0. Expected: **PC** = 4, **SP** = 1, ACC stays 0; opcode 5, argument 7 (the routine's own CALL).
3. Pulse again. Expected: **PC** = 7, **SP** = 2; opcode 1, argument 0 (LOAD 0). **Stack read** is 0 because ADDR now points past the saved words.
4. Pulse again. Expected: **PC** = 8, ACC = 0, SP = 2; **Stack read** = 5 and opcode 6 (RET). The inner return address is already visible below SP.
5. Pulse again. Expected: **PC** = 5, **SP** = 1; opcode 1, argument 2. The inner RET popped 5, so execution resumed after the inner CALL.
6. Pulse again. Expected: **PC** = 6, ACC = 2, SP = 1; **Stack read** = 1 and opcode 6 (RET).
7. Pulse again. Expected: **PC** = 1, ACC = 2, **SP** = 0; opcode 1, argument 1. The outer RET popped 1, returning to the instruction after CALL 4.
8. Pulse twice. Expected: (PC 2, ACC 1), then (PC 3, ACC 3, opcode 3, argument 3). Pulse again: PC stays 3 because `JMP 3` loops with the stack empty.
9. Double-click `stack`. Expected: the `std.t-ram-9x2` body with nine word registers, nine write-decode lookups, and a two-level read tree of `std.t-mux3` selectors per lane. Double-click `sp_reg` and `sp_mux_hold0`: the two-trit SP is a `std.t-reg-2` fed by hold muxes, so it changes only on CALL and RET. No errors or warnings appear.
10. Drive `STACK ERROR` behavior with the core test `TernaryCallMachineLatchesStackOverflowAndUnderflow`, which rewrites the instruction ROMs to make the machine recurse: the ninth CALL at SP=8 latches `STACK ERROR = 2` and wraps SP to 0, a RET with SP = 0 latches it and wraps SP to 8, and the latch survives later CALLs until the simulation restarts. In the editor the shipped program never faults, so `STACK ERROR` stays 0; it appears in the inspector's pin values and the waveform, not as a numeric lane.
11. Expected limitation: the pointer wraps on overflow and underflow, so a fault is reported but not prevented; the ninth push overwrites the oldest saved address, and recovery requires restarting the simulation. The flag also ignores a software stack colliding with data, which belongs to a future memory model.

## 21. Ternary 9 × 2 memory

This uses the GUI. The example is a wrapper around `std.t-ram-9x2`, which has a two-trit address selecting nine 2-trit words.

1. Open `examples/ternary-memory.radix` and click **Begin**. Expected: **Address** = 0, **Data** = 0, **Read** = 0; the project is clean and the canvas shows one `memory` instance with five inputs and two outputs.
2. Set `ADDR0 = 2`, `ADDR1 = 1` (Address 5), `D0 = 2`, `D1 = 2` (Data 8), `WE = 2`. Pulse `CLK` 0 → 2 → 0. Expected: **Read** = 8.
3. Set `WE = 0`, `ADDR0 = 0`, `ADDR1 = 0`, `D0 = 0`, `D1 = 1` (Data 3). Pulse `CLK`. Expected: **Address** = 0 and **Read** = 3; word 5 still holds 8.
4. Set `ADDR0 = 2`, `ADDR1 = 1` again and change `D0`/`D1` while `WE` stays 0 or 1. Expected: **Read** stays 8 in both cases; only `WE = 2` writes.
5. Double-click `memory`. Expected: the `std.t-ram-9x2` body with nine `std.t-reg-2` word instances, nine write-decode lookups, six low `std.t-mux3` selectors inside the read tree, and two high selectors. Double-click `read0_low1` and confirm its 81-entry table selects A/B/C for select 0/1/2. No errors or warnings appear.
6. Expected limitation: the part is fixed at nine 2-trit words; there is no runtime-sized memory, byte-lane view, or collision checking between a program and its data.

## 22. Ternary stack machine

This uses the GUI. A 2-trit TOS register sits over a 9-word stack RAM; SP counts the words below it.

1. Open `examples/ternary-stack-machine.radix` and click **Begin**. Expected: **PC** = 0, **TOS** = 0, **SP** = 0, **Output** = 0, **Opcode** = 1, **Argument** = 2; the project is clean.
2. Pulse `CLK` 0 → 2 → 0. Expected: **PC** = 1, **TOS** = 2, **SP** = 1. The pushed old TOS (0) is now at stack word 0.
3. Pulse again. Expected: **PC** = 2, **TOS** = 3, **SP** = 2, **Stack read** = 2.
4. Pulse again. Expected: **PC** = 3, **TOS** = 2, **SP** = 2, **Stack read** = 3; SWAP exchanged TOS with the word below it without moving SP.
5. Pulse again. Expected: **PC** = 4, **TOS** = 5, **SP** = 1; ADD consumed the second operand.
6. Pulse again. Expected: **PC** = 5, **TOS** = 5, **SP** = 2; DUP pushed a copy.
7. Pulse again. Expected: **PC** = 6, **TOS** = 5, **SP** = 1; POP discarded the copy.
8. Pulse again. Expected: **PC** = 7, **Output** = 5, TOS and SP unchanged.
9. Pulse twice. Expected: PC 8 with TOS = 0, SP = 0, then PC 0 with **Output** still 5; the program loops through JMP 0.
10. Double-click `stack`. Expected: `std.t-ram-9x2` with nine word registers and a selector read tree. Double-click `top_reg`, `sp_reg`, and `out_reg`: each is a `std.t-reg-2`. No errors or warnings appear.
11. Expected limitation: ADD drops the 2-trit adder carry, so results wrap modulo 9; a push at SP=8 or an operation on an empty stack latches `STACK ERROR` but the pointer still wraps, so recovery requires restarting the simulation.

## 23. Balanced ternary accumulator

This uses the GUI. Control, data, and clock are all balanced trits, so a pin value of `0` means `−`, `1` means `0`, and `2` means `+`.

1. Open `examples/balanced-ternary-accumulator.radix` and click **Begin**. Drive `CLK = 0`, `LOAD = 0`, `ADD = 0`, `D0 = 1`, `D1 = 1`. Expected: the **Data** and **Result** lanes both read `0`, and the project is clean.
2. Set `D0 = 2`, `D1 = 2` (Data lane reads `4`), then `LOAD = 2`. Pulse `CLK` to `2`, settle, then back to `0`. Expected: **Result** = `4`.
3. Set `LOAD = 0`, `D0 = 0`, `D1 = 0` (Data lane reads `−4`). Pulse `CLK`. Expected: **Result** stays `4`, showing the register holds while LOAD is inactive.
4. Set `ADD = 2` with `D0 = D1 = 2` (`+4`) and pulse `CLK`. Expected: **Result** = `−1`; the 2-trit sum wraps modulo 9 instead of saturating.
5. Set `D0 = 0`, `D1 = 2` (Data lane reads `2`) and pulse `CLK`. Expected: **Result** = `1`.
6. Set `ADD = 0`, `D0 = 1`, `D1 = 1`, and pulse `CLK`. Expected: **Result** stays `1`; ADD took precedence while it was active, and now both controls hold.
7. Double-click `register`. Expected: a `std.bt-reg-2` body with two `mux` slices and two `ff` slices, and **Data**/**Result** lanes that decode as balanced ternary. Double-click `ff0`, then `slave`: the balanced latch is the same Kleene cross-coupled structure as the 0/1/2 latch, wired from `std.bt-not` and `std.bt-min` rather than `std.t-lognot`/`std.t-min`. No errors or warnings appear.

## 24. Tiny program-driven CPU

This uses the GUI, so run it yourself. The machine is built entirely from library parts and lookup tables; there is no CPU code in the kernel.

1. Open `examples/tiny-cpu.radix`. Expected: the canvas shows the ROM, decoder, program counter, accumulator, RAM, stack pointer, and a fixed lane legend; **OPCODE**, **OPERAND**, **INSTRUCTION**, **PC**, **ACC**, **SP**, and **MEM** appear in the inspector.
2. Click **Begin**, then drive `CLK = 0` and `RESET = 0`. Expected: `PC = 0`, `ACC = 0`, `SP = 0`, `MEM = 0`, `STACK_ERROR = 0`, `ZERO = 1`, `IR = 21` (0x15), opcode 1, operand 5.
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
| 8 | 12 | 232 (0xE8) | 14 | 8 | 9 | 1 | 5 | CALL pushed return address 8 |
| 9 | 13 | 153 (0x99) | 9 | 9 | 5 | 1 | 9 | LDA 8 direct loaded 5 |
| 10 | 14 | 208 (0xD0) | 13 | 0 | 14 | 1 | 8 | ADD@ 9 made 14; stack top is 8 |
| 11 | 8 | 104 (0x68) | 6 | 8 | 14 | 0 | 5 | RET returned to PC 8 |
| 12 | 8 | 104 (0x68) | 6 | 8 | 14 | 0 | 5 | JMP 8 halt loop holds state |

4. Drive `RESET = 1`, raise `CLK`, then lower `CLK`. Expected: `PC = 0`, `ACC = 0`, `SP = 0`, `CARRY = 0`, `STACK_ERROR = 0`; RAM contents are retained, and the machine restarts at instruction 0. Drive `RESET = 0` before continuing.
5. Check the **Trace** and **Waveform** tabs. Expected: transitions are recorded for the clock, PC bits, accumulator bits, SP bits, RAM write enables, IR bits, carry, zero, and the stack-error latch; no errors or warnings appear.
6. `STACK_ERROR` stays `0` for the shipped program. The output latches if `PUSH`/`CALL` occurs at `SP = 15` or `POP`/`RET` at `SP = 0`, and reset clears it. It detects pointer wrap only, not a software stack overlapping data in RAM; the core tests cover both fault directions with generated 16-instruction programs.

## 25. Four-state bus resolution

This uses the GUI. Both data inputs and the bus are on the four-state domain; indices 0 and 1 are logic values, 2 is X, and 3 is Z. The enable pins are binary.

1. Open `examples/four-state-bus.radix` and click **Begin**. Set `EN A = 0`, `EN B = 0`. Expected: **BUS** = Z and **CONFLICT** = 0; no RAD101 diagnostic appears.
2. Set `EN A = 1`, `A = 0`. Expected: **BUS** = 0; the disabled B driver's Z yields.
3. Set `EN B = 1`, `B = 1`. Expected: **BUS** = X and **CONFLICT** = 1; the Diagnostics tab shows exactly one `RAD101` warning for the bus net, even if you keep changing disagreeing values.
4. Set `B = 0`. Expected: **BUS** = 0 and **CONFLICT** = 0; agreement is not a conflict, and the earlier warning remains in the diagnostic list.
5. Set `EN B = 0`, `B = 2` (X). Expected: **BUS** stays 0 because the disabled driver outputs Z, not X.
6. Set `EN A = 0`, `EN B = 1`, `B = 1`. Expected: **BUS** = 1 and **CONFLICT** = 0.
7. Set `EN A = 1`, `A = 2`. Expected: **BUS** = X and **CONFLICT** = 1; X has higher priority than 1 in this domain.
8. Expected limitation: the resolution policy is the domain's one priority table; there is no wired-AND/open-drain option, and the diagnostic reports a conflict per net once, not per transition.

## 26. Known limitations to verify as *limitations*

Do not file these as new failures until they change in the README:

- auto-routing is heuristic; dense circuits may need bendpoints, and components are not collision-moved automatically;
- extraction refuses nets with drivers on both sides because there is no inout pin direction;
- library equivalence ignores labels/layout and numeric-lane presentation; conflicting IDs are renamed rather than offering a merge dialog;
- selection, external drive history, and waveform history are not saved; viewport and active definition are saved;
- no analog solver, JIT, parallel engine, or first-class bus/vector type;
- the tiny CPU's stack-error output detects pointer wrap only, not a software stack colliding with data;
- the ternary machines have 9-word program memories and 2-trit data words: the branch machine has no halt instruction, and both it and the call machine drop the adder carry on ADD, so their 0..8 accumulators wrap rather than overflow; the call machine's stack holds nine returns with a two-trit pointer and latches a stack-error flag for call overflow or return underflow, but still overwrites the oldest return address on overflow and does not detect collisions with data;
- the ternary RAMs are fixed at 3×2 and 9×2 words; there is no runtime-sized memory, byte-lane view, or protection between program and data;
- the ternary stack machine has a 9-word stack and a 2-trit pointer: eight pushes are safe, a ninth wraps and latches the fault; ADD is modulo 9 because the 2-trit adder carry is dropped, and BZ does not pop its condition;
- the four-state bus uses only the domain's single priority table: there is no open-drain/wired-AND policy, and RAD101 is reported once per net per run rather than per transition;
- the balanced accumulator is also 2 trits wide and drops the adder carry, so it wraps in −4..4 instead of overflowing; its third trit is not stored;
- in-place domain-policy editing keeps a domain's ID, labels, and value count; changing the value set requires creating a new domain;
- layout is not incremental: drag frames reuse unchanged routes, but releasing a drag and structural edits rebuild all geometry, so very large definitions may pause briefly;
- waveform tail is bounded and may omit old transitions;
- the format preview decodes only the first lane that uses the edited format; the canvas lane legend is fixed at the lower-left, caps at seven rows, and the remaining lanes stay in the inspector;
- `.radixtest` scripts are linear; there are no loops, variables, expressions, or random drives. `within` asserts eventual convergence, `stable` asserts sampled held values, and `transitions` counts changes; they do not express edge direction, edge order, or minimum setup/hold constraints. The editor's result panel is transient and is not marked stale when the circuit is edited afterward; rerun for a current result;
- undo snapshots whole projects and is capped at 80 entries;
- crash recovery keeps one snapshot refreshed every 20 seconds, so a crash can lose recent edits; it restores snapshots without validation, so a recovered project may need repair;
- the definition navigation trail is in-memory only: opening a project, undo/redo, or creating/removing a definition resets it to the current definition, and it is not saved in the project.

## Isolation statement

Automated GUI verification in this repository is headless only:

- Avalonia Headless platform with offscreen Skia;
- no `DISPLAY` override, no visible windows, no input injection;
- `artifacts/editor/*.png` are generated from `RenderTargetBitmap` inside the test process;
- no test starts the desktop editor executable.

Do not "help" a future automated run by starting the GUI on the active display. When an interactive check is needed, it must remain a human-driven step documented here.
