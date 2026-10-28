using System.Diagnostics;
using System.Globalization;
using Radix.Core;
using Radix.Core.Analysis;
using Radix.Core.Compilation;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Radix.Core.Testing;
using Radix.StdLib;

namespace Radix.Cli;

internal static class Program
{
    private const string Version = "0.1.0";

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "validate" => ValidateCommand(args[1..]),
                "simulate" or "sim" => SimulateCommand(args[1..]),
                "test" => TestCommand(args[1..]),
                "sweep" => SweepCommand(args[1..]),
                "inspect" => InspectCommand(args[1..]),
                "examples" => ExamplesCommand(args[1..]),
                "bench" or "benchmark" => BenchCommand(args[1..]),
                "version" or "--version" => PrintVersion(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (ProjectFormatException exception)
        {
            Console.Error.WriteLine($"project format error: {exception.Message}");
            return 2;
        }
        catch (SimulationScriptException exception)
        {
            Console.Error.WriteLine($"test script error: {exception.Message}");
            return 2;
        }
        catch (CircuitValidationException exception)
        {
            PrintDiagnostics(exception.Diagnostics, Console.Error);
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"I/O error: {exception.Message}");
            return 2;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"access denied: {exception.Message}");
            return 2;
        }
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"radix {Version}");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            RADIX — arbitrary-radix circuit and architecture simulator

            Usage:
              radix <command> [arguments] [options]

            Commands:
              validate <project.radix>       Validate structure and compile the project
              simulate <project.radix>       Compile and run a definition headlessly
                  [--definition <id>]        Definition to simulate (default: project root)
                  [--ticks <n>]              Simulated ticks to advance (default 100)
                  [--set <pin>=<value>]      Drive an input pin at time zero
                  [--schedule <tick>:<pin>=<value>]
                                             Drive an input at a simulated tick (repeatable)
                  [--bus <pin,pin,...>]      Pin order for numeric display (LSB first)
                  [--format <id>]            Decode the bus using a numeric format
                  [--trace]                  Print net transitions (bounded)
                  [--max-changes <n>]        Trace print limit (default 200)
              test <project.radix> <script.radixtest>
                                             Run scripted drives and assertions
                  [--definition <id>]        Definition to simulate (default: script or root)
              sweep <project.radix>          Exhaustively drive input combinations
                  [--definition <id>]        Definition to sweep (default: project root)
                  [--ticks <n>]              Ticks to settle after each row (default 10)
                  [--max-rows <n>]           Combination limit (default 4096)
              inspect <project.radix>        Print the project structure
              examples [directory]           Write the bundled example projects
              bench [--gates <n>] [--ticks <n>]
                                             Measure event throughput on a synthetic chain

            Numeric formats:
              Built-ins: unsigned-binary, twos-complement, ones-complement,
              sign-magnitude, unsigned-ternary, ternary-top-negative, balanced-ternary.
              Projects may also define custom formats, listed by `radix inspect`.

            Test scripts are JSON files (.radixtest) with timed drives and assertions;
            see docs/test-scripts.md for the format and semantics.

            Exit codes: 0 success, 1 validation/simulation failure, 2 usage or I/O error.

            The CLI never opens a window; the graphical editor is a separate executable.
            """);
    }

    private static int ValidateCommand(string[] args)
    {
        (var positional, _) = Parse(args, []);
        if (positional.Count != 1)
        {
            Console.Error.WriteLine("usage: radix validate <project.radix>");
            return 2;
        }

        CircuitProject project = ProjectSerializer.Load(positional[0], validate: false);
        Console.WriteLine(
            $"project '{project.Name}' — {project.Definitions.Count.ToString(CultureInfo.InvariantCulture)} definitions, " +
            $"{project.Domains.Count.ToString(CultureInfo.InvariantCulture)} domains, root '{project.RootDefinitionId}'");

        bool compiled = CircuitCompiler.TryCompile(
            project,
            project.RootDefinitionId,
            out CompiledCircuit? circuit,
            out DiagnosticBag report);
        PrintDiagnostics(report, Console.Out);

        if (!compiled || report.HasErrors)
        {
            Console.Error.WriteLine(
                $"INVALID: {report.ErrorCount.ToString(CultureInfo.InvariantCulture)} error(s), " +
                $"{report.WarningCount.ToString(CultureInfo.InvariantCulture)} warning(s).");
            return 1;
        }

        Console.WriteLine(
            $"OK: {circuit!.Components.Count.ToString(CultureInfo.InvariantCulture)} primitive components, " +
            $"{circuit.Nets.Count.ToString(CultureInfo.InvariantCulture)} nets, " +
            $"{circuit.Pins.Count.ToString(CultureInfo.InvariantCulture)} pins.");
        return 0;
    }

    private static int SimulateCommand(string[] args)
    {
        (var positional, var options) = Parse(args, ["definition", "ticks", "set", "schedule", "bus", "format", "trace", "max-changes"]);
        if (positional.Count != 1)
        {
            Console.Error.WriteLine("usage: radix simulate <project.radix> [options]");
            return 2;
        }

        CircuitProject project = ProjectSerializer.Load(positional[0]);
        string definitionId = GetOption(options, "definition") ?? project.RootDefinitionId;
        if (project.FindDefinition(definitionId) is null)
        {
            Console.Error.WriteLine($"unknown definition '{definitionId}'.");
            return 2;
        }

        long ticks = GetLongOption(options, "ticks", 100, 0, long.MaxValue / 2);
        SimulationOptions simulationOptions = SimulationOptions.FromSettings(project.Settings);
        CompiledCircuit circuit = CircuitCompiler.Compile(project, definitionId);
        var engine = new SimulationEngine(circuit, simulationOptions);

        Console.WriteLine($"project:    {project.Name}");
        Console.WriteLine($"definition: {definitionId}");
        Console.WriteLine($"domain(s):  {string.Join(", ", project.Domains.Select(d => d.Id))}");

        if (options.TryGetValue("set", out List<string>? sets))
        {
            foreach (string assignment in sets.SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                if (!TryApplyAssignment(engine, assignment))
                {
                    return 2;
                }
            }
        }

        var scheduled = new List<(long Time, int Port, int Value)>();
        if (options.TryGetValue("schedule", out List<string>? schedules))
        {
            foreach (string entry in schedules.SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                int colon = entry.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0 ||
                    !long.TryParse(entry[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out long time) ||
                    time < 0 || time > ticks ||
                    !TryResolveAssignment(engine, entry[(colon + 1)..], out int port, out int value))
                {
                    Console.Error.WriteLine($"invalid --schedule entry '{entry}' (expected <tick>:<pin>=<value> within 0..{ticks}).");
                    return 2;
                }

                scheduled.Add((time, port, value));
            }
        }

        foreach ((long time, int port, int value) in scheduled.OrderBy(s => s.Time))
        {
            engine.AdvanceTo(time);
            engine.SetPortValue(port, value);
        }

        engine.AdvanceTo(ticks);
        PrintDiagnostics(circuit.Diagnostics.Concat(engine.RuntimeDiagnostics), Console.Out);

        if (engine.IsHalted)
        {
            Console.Error.WriteLine($"SIMULATION HALTED: {engine.HaltReason}");
        }

        Console.WriteLine(
            $"simulated 0..{engine.Now.ToString(CultureInfo.InvariantCulture)} ticks; " +
            $"{engine.EventsProcessed.ToString(CultureInfo.InvariantCulture)} events, " +
            $"{engine.ComponentEvaluations.ToString(CultureInfo.InvariantCulture)} evaluations, " +
            $"{engine.NetTransitions.ToString(CultureInfo.InvariantCulture)} net transitions.");

        PrintPorts(engine);

        if (options.ContainsKey("format"))
        {
            string formatId = GetOption(options, "format")!;
            INumericFormat? format = NumericFormatCatalog.All(project).FirstOrDefault(f =>
                string.Equals(f.Id, formatId, StringComparison.OrdinalIgnoreCase));
            if (format is null)
            {
                Console.Error.WriteLine(
                    $"unknown numeric format '{formatId}'. Known: {string.Join(", ", NumericFormatCatalog.All(project).Select(f => f.Id))}");
                return 2;
            }

            string? busOption = GetOption(options, "bus");
            List<(CompiledPort Port, int Index)> bus = SelectBus(engine, busOption);
            if (bus.Count == 0)
            {
                Console.Error.WriteLine("no output pins available for numeric display.");
                return 1;
            }

            int[] values = bus.Select(entry => engine.GetPortValue(entry.Index)).ToArray();
            if (!format.LeastSignificantFirst)
            {
                Array.Reverse(values);
            }

            NumericDecodeResult decoded = format.Decode(values);
            string label = string.Join(",", bus.Select(entry => entry.Port.PinId));
            Console.WriteLine($"bus [{label}]: {decoded.Display()}");
            Console.WriteLine($"format: {format.Describe(values.Length)}");
            if (bus.Any(entry => !string.Equals(entry.Port.Domain.Id, format.DomainId, StringComparison.Ordinal)))
            {
                Console.WriteLine($"note: bus domains differ from the format's nominal domain '{format.DomainId}'.");
            }
        }

        if (options.ContainsKey("trace"))
        {
            int limit = GetIntOption(options, "max-changes", 200, 0, 1_000_000);
            PrintTrace(engine, limit);
        }

        return engine.IsHalted ? 1 : 0;
    }

    private static int TestCommand(string[] args)
    {
        (var positional, var options) = Parse(args, ["definition"]);
        if (positional.Count != 2)
        {
            Console.Error.WriteLine("usage: radix test <project.radix> <script.radixtest> [--definition <id>]");
            return 2;
        }

        CircuitProject project = ProjectSerializer.Load(positional[0]);
        SimulationScript script = SimulationScript.Load(positional[1]);
        string? definitionOverride = GetOption(options, "definition");
        if (definitionOverride is not null && project.FindDefinition(definitionOverride) is null)
        {
            Console.Error.WriteLine($"unknown definition '{definitionOverride}'.");
            return 2;
        }

        SimulationScriptResult result = SimulationScriptRunner.Run(project, script, definitionOverride);
        Console.WriteLine($"test:       {result.ScriptName}");
        Console.WriteLine($"project:    {project.Name}");
        Console.WriteLine($"definition: {result.DefinitionId}");
        PrintDiagnostics(result.Diagnostics, Console.Out);

        if (result.Passed)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"PASS: {result.StepsExecuted} step(s) through t={result.FinalTime}; " +
                $"{result.EventsProcessed} events."));
            return 0;
        }

        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"FAIL: {result.Failures.Count} failure(s) after {result.StepsExecuted} step(s)."));
        foreach (ScriptFailure failure in result.Failures)
        {
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  step {failure.Step} (t={failure.At}): {failure.Message}"));
        }

        return 1;
    }

    private static void PrintPorts(SimulationEngine engine)
    {
        Console.WriteLine("ports:");
        if (engine.Circuit.Ports.Count == 0)
        {
            Console.WriteLine("  (none)");
            return;
        }

        foreach (CompiledPort port in engine.Circuit.Ports)
        {
            int value = engine.GetNetValue(port.Net);
            string direction = port.Direction == PinDirection.Input ? "in " : "out";
            string valueText = value < 0 ? "undriven" : $"{value.ToString(CultureInfo.InvariantCulture)} ({port.Domain.ValueName(value)})";
            Console.WriteLine($"  {direction}  {port.PinId,-12} {port.Domain.Id,-18} {valueText}");
        }
    }

    private static List<(CompiledPort Port, int Index)> SelectBus(SimulationEngine engine, string? busOption)
    {
        var result = new List<(CompiledPort Port, int Index)>();
        if (string.IsNullOrWhiteSpace(busOption))
        {
            for (int i = 0; i < engine.Circuit.Ports.Count; i++)
            {
                CompiledPort port = engine.Circuit.Ports[i];
                if (port.Direction == PinDirection.Output)
                {
                    result.Add((port, i));
                }
            }

            return result;
        }

        foreach (string pin in busOption!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int index = engine.FindPortIndex(pin);
            if (index < 0)
            {
                throw new ProjectFormatException($"bus pin '{pin}' is not a top-level interface pin.");
            }

            result.Add((engine.Circuit.Ports[index], index));
        }

        return result;
    }

    private static void PrintTrace(SimulationEngine engine, int limit)
    {
        Console.WriteLine("trace:");
        int printed = 0;
        foreach (var change in engine.Trace)
        {
            if (printed >= limit)
            {
                Console.WriteLine($"  ... trace truncated at {limit} entries ({engine.Trace.TotalRecorded} recorded).");
                break;
            }

            CompiledNet net = engine.Circuit.Nets[change.Net];
            string label = net.Label ?? $"{net.RepresentativePath}:{net.RepresentativeNetId}";
            string oldValue = change.OldValue < 0 ? "undriven" : $"{change.OldValue} ({net.Domain.ValueName(change.OldValue)})";
            string newValue = change.NewValue < 0 ? "undriven" : $"{change.NewValue} ({net.Domain.ValueName(change.NewValue)})";
            Console.WriteLine($"  t={change.Time,-8} {label,-30} {oldValue} -> {newValue}");
            printed++;
        }

        if (engine.Trace.TotalRecorded == 0)
        {
            Console.WriteLine("  (no recorded transitions)");
        }
    }

    private static bool TryApplyAssignment(SimulationEngine engine, string assignment)
    {
        if (!TryResolveAssignment(engine, assignment, out int portIndex, out int value))
        {
            return false;
        }

        engine.SetPortValue(portIndex, value);
        return true;
    }

    private static bool TryResolveAssignment(
        SimulationEngine engine,
        string assignment,
        out int portIndex,
        out int value)
    {
        portIndex = -1;
        value = -1;
        int equals = assignment.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0 || equals == assignment.Length - 1)
        {
            Console.Error.WriteLine($"invalid assignment '{assignment}' (expected pin=value).");
            return false;
        }

        string pin = assignment[..equals].Trim();
        string valueText = assignment[(equals + 1)..].Trim();
        portIndex = engine.FindPortIndex(pin);
        if (portIndex < 0)
        {
            Console.Error.WriteLine($"unknown interface pin '{pin}'.");
            return false;
        }

        CompiledPort port = engine.Circuit.Ports[portIndex];
        if (port.Direction != PinDirection.Input)
        {
            Console.Error.WriteLine($"pin '{pin}' is an output and cannot be driven.");
            return false;
        }

        if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            int byLabel = -1;
            for (int i = 0; i < port.Domain.ValueCount; i++)
            {
                if (string.Equals(port.Domain.ValueName(i), valueText, StringComparison.OrdinalIgnoreCase))
                {
                    byLabel = i;
                    break;
                }
            }

            if (byLabel < 0)
            {
                Console.Error.WriteLine($"'{valueText}' is not a valid value for pin '{pin}'.");
                return false;
            }

            value = byLabel;
        }

        if (!port.Domain.IsValidValue(value))
        {
            Console.Error.WriteLine($"value {value} is outside domain '{port.Domain.Id}' (0..{port.Domain.ValueCount - 1}).");
            return false;
        }

        return true;
    }

    private static int SweepCommand(string[] args)
    {
        (var positional, var options) = Parse(args, ["definition", "ticks", "max-rows"]);
        if (positional.Count != 1)
        {
            Console.Error.WriteLine(
                "usage: radix sweep <project.radix> [--definition <id>] [--ticks <n>] [--max-rows <n>]");
            return 2;
        }

        CircuitProject project = ProjectSerializer.Load(positional[0]);
        string? definitionId = GetOption(options, "definition");
        if (definitionId is not null && project.FindDefinition(definitionId) is null)
        {
            Console.Error.WriteLine($"unknown definition '{definitionId}'.");
            return 2;
        }

        long settleTicks = GetLongOption(options, "ticks", 10, 0, 100_000);
        int maxRows = GetIntOption(options, "max-rows", InputSweep.DefaultMaxRows, 1, 1_000_000);
        InputSweepResult result;
        try
        {
            result = InputSweep.Run(project, definitionId, settleTicks, maxRows);
        }
        catch (InputSweepException exception)
        {
            Console.Error.WriteLine($"sweep error: {exception.Message}");
            return 1;
        }

        Console.WriteLine($"project:    {project.Name}");
        Console.WriteLine($"definition: {result.DefinitionId}");
        Console.WriteLine($"inputs:     {FormatSweepPorts(result.Inputs)}");
        Console.WriteLine($"outputs:    {FormatSweepPorts(result.Outputs)}");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"rows:       {result.Rows.Count} (settle {result.SettleTicks} tick(s))"));
        PrintSweepTable(result);
        return 0;
    }

    private static string FormatSweepPorts(IReadOnlyList<SweepPort> ports) =>
        ports.Count == 0
            ? "(none)"
            : string.Join("  ", ports.Select(port => $"{port.PinId}:{port.Domain.Id}"));

    private static string FormatSweepValue(SweepPort port, int value) =>
        value == SignalValue.UndrivenIndex ? "-" : port.Domain.ValueName(value);

    private static void PrintSweepTable(InputSweepResult result)
    {
        var widths = new int[result.Inputs.Count + result.Outputs.Count];
        for (int i = 0; i < result.Inputs.Count; i++)
        {
            SweepPort port = result.Inputs[i];
            widths[i] = Math.Max(port.PinId.Length, result.Rows
                .Select(row => FormatSweepValue(port, row.Inputs[i]).Length)
                .DefaultIfEmpty(1)
                .Max());
        }

        for (int i = 0; i < result.Outputs.Count; i++)
        {
            SweepPort port = result.Outputs[i];
            int column = result.Inputs.Count + i;
            widths[column] = Math.Max(port.PinId.Length, result.Rows
                .Select(row => FormatSweepValue(port, row.Outputs[i]).Length)
                .DefaultIfEmpty(1)
                .Max());
        }

        Console.WriteLine();
        if (widths.Length == 0)
        {
            Console.WriteLine("(no interface ports)");
            return;
        }

        var header = new List<string>(widths.Length);
        for (int i = 0; i < result.Inputs.Count; i++)
        {
            header.Add(result.Inputs[i].PinId.PadRight(widths[i]));
        }

        for (int i = 0; i < result.Outputs.Count; i++)
        {
            header.Add(result.Outputs[i].PinId.PadRight(widths[result.Inputs.Count + i]));
        }

        Console.WriteLine(string.Join(" | ", header));
        Console.WriteLine(string.Join("-+-", widths.Select(width => new string('-', width))));

        foreach (SweepRow row in result.Rows)
        {
            var cells = new List<string>(widths.Length);
            for (int i = 0; i < result.Inputs.Count; i++)
            {
                cells.Add(FormatSweepValue(result.Inputs[i], row.Inputs[i]).PadRight(widths[i]));
            }

            for (int i = 0; i < result.Outputs.Count; i++)
            {
                cells.Add(FormatSweepValue(result.Outputs[i], row.Outputs[i]).PadRight(widths[result.Inputs.Count + i]));
            }

            Console.WriteLine(string.Join(" | ", cells));
        }
    }

    private static int InspectCommand(string[] args)
    {
        (var positional, _) = Parse(args, []);
        if (positional.Count != 1)
        {
            Console.Error.WriteLine("usage: radix inspect <project.radix>");
            return 2;
        }

        CircuitProject project = ProjectSerializer.Load(positional[0], validate: false);

        Console.WriteLine($"Project: {project.Name}");
        Console.WriteLine($"Root:    {project.RootDefinitionId}");
        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            Console.WriteLine($"About:   {project.Description}");
        }

        Console.WriteLine("Domains:");
        foreach (SignalDomain domain in project.Domains)
        {
            Console.WriteLine($"  {domain.Id} — {domain.Name}: [{string.Join(", ", domain.ValueNames)}], drivers={domain.Drivers.Kind}");
        }

        if (project.CustomNumericFormats.Count > 0)
        {
            Console.WriteLine("Custom numeric formats:");
            foreach (NumericFormatDefinition format in project.CustomNumericFormats)
            {
                Console.WriteLine(
                    $"  {format.Id} — {format.Name}: domain={format.DomainId}, radix={format.Radix}, " +
                    $"digits=[{string.Join(", ", format.DigitValues)}], weighting={format.Weighting}, " +
                    $"sign={(format.NegativeSignValue is int sign ? sign.ToString(CultureInfo.InvariantCulture) : "none")}");
            }
        }

        Console.WriteLine("Definitions:");
        foreach (CircuitDefinition definition in project.Definitions)
        {
            Console.WriteLine($"  {definition.Id} — {definition.Name}");
            Console.WriteLine($"    pins:      {string.Join(", ", definition.Pins.Select(p => $"{p.Id}:{p.Direction.ToString().ToLowerInvariant()}:{p.DomainId}"))}");
            Console.WriteLine($"    instances: {definition.Instances.Count.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine($"    nets:      {definition.Nets.Count.ToString(CultureInfo.InvariantCulture)}");
            foreach (NumericLane lane in definition.NumericLanes)
            {
                Console.WriteLine($"    lane:      {lane.Id} ({lane.Name}) format={lane.FormatId} pins=[{string.Join(", ", lane.PinIds)}]");
            }
        }

        return 0;
    }

    private static int ExamplesCommand(string[] args)
    {
        (var positional, _) = Parse(args, []);
        if (positional.Count > 1)
        {
            Console.Error.WriteLine("usage: radix examples [directory]");
            return 2;
        }

        string directory = positional.Count == 1 ? positional[0] : "examples";
        Directory.CreateDirectory(directory);
        foreach (ExampleProject example in ExampleProjects.All())
        {
            string path = Path.Combine(directory, example.FileName);
            ProjectSerializer.Save(path, example.Project);
            Console.WriteLine($"{example.DisplayName,-36} {path}");
        }

        return 0;
    }

    private static int BenchCommand(string[] args)
    {
        (var positional, var options) = Parse(args, ["gates", "ticks"]);
        if (positional.Count != 0)
        {
            Console.Error.WriteLine("usage: radix bench [--gates <n>] [--ticks <n>]");
            return 2;
        }

        int gates = GetIntOption(options, "gates", 2000, 1, 200_000);
        int ticks = GetIntOption(options, "ticks", 50, 1, 100_000);
        CircuitProject project = BuildBench(gates);

        var validateWatch = Stopwatch.StartNew();
        CircuitValidator.Validate(project, "bench");
        validateWatch.Stop();

        var compileWatch = Stopwatch.StartNew();
        CompiledCircuit circuit = CircuitCompiler.Compile(project, "bench");
        compileWatch.Stop();

        var optionsForEngine = new SimulationOptions
        {
            RecordTrace = false,
            MaxRecordedChanges = 0,
            MaxEvaluationsPerComponentPerTimestamp = 64,
        };
        var engine = new SimulationEngine(circuit, optionsForEngine);
        engine.AdvanceTo(0);
        long warmupEvents = engine.EventsProcessed;

        var runWatch = Stopwatch.StartNew();
        engine.AdvanceTo(ticks);
        runWatch.Stop();

        long events = engine.EventsProcessed - warmupEvents;
        double seconds = Math.Max(runWatch.Elapsed.TotalSeconds, 1e-9);
        Console.WriteLine("RADIX benchmark (synthetic zero-delay inverter chain, one clock tick = one full ripple)");
        Console.WriteLine($"  gates:                {gates.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"  ticks simulated:      {ticks.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"  validate:             {validateWatch.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine($"  compile (incl. validate): {compileWatch.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine($"  flatten (compile - validate): {(compileWatch.Elapsed - validateWatch.Elapsed).TotalMilliseconds:F1} ms");
        Console.WriteLine($"  simulate:             {runWatch.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine($"  events:               {events.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"  component evals:      {engine.ComponentEvaluations.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"  net transitions:      {engine.NetTransitions.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"  events/sec:           {events / seconds:N0}");
        Console.WriteLine($"  evaluations/sec:      {engine.ComponentEvaluations / seconds:N0}");
        Console.WriteLine($"  is halted:            {engine.IsHalted}");
        if (engine.IsHalted)
        {
            Console.Error.WriteLine(engine.HaltReason);
            return 1;
        }

        Console.WriteLine("Note: this measures the managed event engine on this machine; it is not an analog/physical timing claim.");
        return 0;
    }

    private static CircuitProject BuildBench(int gates)
    {
        var builder = new ProjectBuilder("benchmark");
        var definition = builder.Define("bench", "Benchmark chain");
        Net clock = definition.AddNet("clk", StandardDomains.BinaryId);
        definition.AddPrimitive("clock", new WaveformSpec([1, 1], [0, 1]), 40, 40);
        definition.Connect(clock, definition.Pin("clock", "out"));

        Net current = clock;
        for (int i = 0; i < gates; i++)
        {
            Net output = definition.AddNet($"w{i}", StandardDomains.BinaryId);
            string instance = $"g{i}";
            definition.AddPrimitive(instance, new LookupSpec(1, [1, 0], delayTicks: 0), 100 + (i * 10), 100);
            definition.Connect(current, definition.Pin(instance, "in0"));
            definition.Connect(output, definition.Pin(instance, "out"));
            current = output;
        }

        definition.AddPrimitive("probe", new ProbeSpec("chain"), 100, 300);
        definition.Connect(current, definition.Pin("probe", "in0"));
        return builder.Build();
    }

    private static (List<string> Positional, Dictionary<string, List<string>> Options) Parse(string[] args, string[] knownOptions)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
            {
                positional.Add(argument);
                continue;
            }

            string key = argument[2..];
            if (!knownOptions.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                throw new ProjectFormatException($"unknown option '--{key}'.");
            }

            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (!options.TryGetValue(key, out List<string>? values))
            {
                values = [];
                options[key] = values;
            }

            if (value is not null)
            {
                values.Add(value);
            }
        }

        return (positional, options);
    }

    private static string? GetOption(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out List<string>? values) && values.Count > 0 ? values[^1] : null;

    private static long GetLongOption(Dictionary<string, List<string>> options, string key, long fallback, long min, long max)
    {
        string? text = GetOption(options, key);
        if (text is null)
        {
            return fallback;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < min || value > max)
        {
            throw new ProjectFormatException($"option '--{key}' must be an integer in {min}..{max}.");
        }

        return value;
    }

    private static int GetIntOption(Dictionary<string, List<string>> options, string key, int fallback, int min, int max)
    {
        long value = GetLongOption(options, key, fallback, min, max);
        return checked((int)value);
    }

    private static void PrintDiagnostics(IEnumerable<Diagnostic> diagnostics, TextWriter writer)
    {
        foreach (Diagnostic diagnostic in diagnostics)
        {
            writer.WriteLine(diagnostic.ToString());
        }
    }
}
