# Manual testing

This document is for checks that require a human at the desktop. The agent has **not** launched the visible editor, injected input, or captured the active desktop. Automated verification uses Avalonia Headless with offscreen Skia and is safe to run while you work.

## Before you start

```bash
dotnet build Radix.slnx -c Release
dotnet test tests/Radix.Core.Tests/Radix.Core.Tests.csproj
dotnet test tests/Radix.Editor.Tests/Radix.Editor.Tests.csproj
```

The editor tests write offscreen render captures to `artifacts/editor/` (ignored by Git). They do not show a window.

Launch the editor only when you are ready to use it:

```bash
dotnet run --project src/Radix.Editor
```

Expected first screen: a light workbench titled **RADIX — circuit workbench**, the **4-bit binary counter** example, t = 0, 0 errors, 0 warnings, and the Library visible on the left. The default window is 1480×920; it can be resized and has a minimum size of 1040×660.

## 1. Baseline: open the counter and advance

1. Click **Begin**. Expected: simulation compiles, status says reset, t remains 0, and no errors appear.
2. Set the advance box to `100` and click **Advance**.
3. Check the right **Inspect → I/O PINS** section:
   - `Q0 = 1`
   - `Q1 = 0`
   - `Q2 = 1`
   - `Q3 = 0`
   - The header shows `t = 100` and zero errors/warnings.
4. Click the **Waveform** tab. Expected: rows for the root nets (`Q0`…`Q3`, `clk`, constant inputs, adder sums) and a 0–100 tick axis. The clock shows ten 10-tick phases; output rows show their transitions.
5. Click **Trace**. Expected: recent net changes in reverse chronological order, formatted `t=<time>  <net> → <value>`.

If the counter values differ, record the values and diagnostics; this is a correctness failure, not a cosmetic issue.

## 2. Definition navigation and subcircuit simulation

1. Click the `D flip-flop` row in **Definitions** (or double-click a D flip-flop on the canvas).
2. Expected: the canvas changes to the D flip-flop internals, the header identifies a non-root definition, and the Library remains available. Simulation is now rooted at this definition.
3. Click **Begin**.
4. In I/O PINS, click `D` value `1`, then `CLK` value `0`.
5. Set advance to `20` and click **Advance**. Expected: `Q = 0`, `QN = 1` (no rising edge yet).
6. Click `CLK` value `1`, advance `20`. Expected: `Q = 1`, `QN = 0`.
7. Click `D` value `0`, advance `20` without changing CLK. Expected: Q remains `1` (level, not edge, does not alter the slave output).
8. Toggle CLK to `0`, advance `20`; then to `1`, advance `20`. Expected: Q becomes `0`.
9. Click **Clock** once with a valid D value. Expected: the host advances to the next clock transition over at most a few ticks. It may not settle all logic; follow with **Advance**.
10. Return to the root counter via Definitions and verify simulation resets for the new root.

Notes:
- The **Clock** command drives a low-to-high sequence when the clock net has no internal source. If the definition contains a waveform clock, it advances to the next transition of that source instead.
- Navigating definitions clears the running engine. This is intentional: compiled indices are definition-specific.

## 3. Wiring, editing, and undo

1. Click **New** and confirm the starter project appears (Library, empty Main definition, standard definitions). This discards unsaved work without a prompt; verify only after saving anything important.
2. In **Interface**, add input pins `A` and `B` and output pin `Y`, all on the `binary` domain.
3. From the Library, click **AND gate**. Expected: a gate appears near the current canvas center and is selected; Inspect shows its table, delay, label, and position.
4. Drag from the `A` port dot (right side of the left port pill) to the gate’s first input pin. Expected: a wire appears, and the status bar confirms the connection. Repeat for B to the second input, and the gate output to Y.
5. Click **Begin**. Drive A=0, B=1, then advance 2 ticks. Expected Y=0. Drive A=1, B=1, advance 2 ticks. Expected Y=1.
6. Select the gate and change its `Delay` to `0` via Inspect, press Enter/leave the field. Expected the wire value may settle without an extra tick; no project corruption.
7. Press `Delete`. Expected the gate disappears and its wires are removed. Undo restores it.
8. Drag the gate to a new position. Undo should restore its previous position in **one** undo step, not one per mouse movement.
9. Try connecting a binary port to a ternary interface pin. Expected: a status-bar rejection naming the domain mismatch; the circuit is unchanged. This is an editor policy, not a kernel error.

Automatic routing: wires use orthogonal segments and a shared trunk for multi-pin nets. There is no bendpoint editing yet; the routing may look busy on dense circuits. That is a known limitation, not a wiring failure.

## 4. Custom domains and project round trip

1. In Inspect, under **SIGNAL DOMAINS**, enter name `five state` and values `lo mid hi X Z`, then click **Add domain**.
2. Expected: the domain appears in the list, under the new pin domain picker, and in the net domain selector.
3. Add an input pin using the new domain. Expected: the port and its net use `five-state`.
4. Save As `manual-five-state.radix` outside the repository or in a scratch path.
5. Close and relaunch the editor, then Open the file. Expected: the custom domain, value labels, pin, current root, and instance positions survive.
6. Expected non-persistence: viewport pan/zoom, selected tab, selection, external drive history, and waveform history.

## 5. Hierarchy and malformed projects

1. Open `examples/binary-counter.radix` with **Open**. Expected: no modal error, root counter loaded, definitions visible.
2. Double-click the ripple-carry adder. Expected: internal full adders and wires render. Double-click a full adder; expected: half adders. Use the breadcrumb/definition list to return.
3. Open a deliberately malformed file (for example, change `"version": 1` to `99` or remove `root`). Expected: Open fails with a status-bar error and the previously open project remains. The loader must not partially replace the editor state.
4. Use **Validate** in the CLI on the same file for the full diagnostic text.

## 6. Window and interaction behavior

- Resize the window down to the minimum size: the left rail scrolls, the canvas remains usable, and the right tab content scrolls.
- Zoom with `Ctrl+wheel`; pan with middle-drag or Alt+drag. Expected cursor-focused zoom; wires and boxes stay aligned.
- Click empty canvas: selection clears. Press `F`: view fits the circuit.
- Hover a pin: a tooltip reports pin ID, declared/net domain, and value.
- Press `Escape` while dragging a wire: the pending wire is cancelled.
- Keyboard `Ctrl+Z`, `Ctrl+Shift+Z`/`Ctrl+Y`, `Delete`, and arrow-key nudging should operate without moving the mouse.
- Click New with unsaved changes: expected current behavior is immediate discard. This is listed as a limitation, not a pass criterion.

## 7. Known limitations to verify as *limitations*

Do not file these as new failures until they change in the README:

- no bendpoint editing, multi-select, copy/paste, or rubber-band selection;
- no prompt before discarding unsaved New/Open/close;
- viewport/selection not saved;
- no assertion or regression mode in the CLI; timed drives exist, but checking expected values is manual;
- no analog solver, JIT, parallel engine, or first-class buses;
- editor-created domains use single-driver resolution only;
- waveform tail is bounded and may omit old transitions;
- numeric display formats are CLI-only in this build;
- undo snapshots whole projects and is capped at 80 entries.

## Isolation statement

Automated GUI verification in this repository is headless only:

- Avalonia Headless platform with offscreen Skia;
- no `DISPLAY` override, no visible windows, no input injection;
- `artifacts/editor/*.png` are generated from `RenderTargetBitmap` inside the test process;
- no test starts the desktop editor executable.

Do not "help" a future automated run by starting the GUI on the active display. When an interactive check is needed, it must remain a human-driven step documented here.
