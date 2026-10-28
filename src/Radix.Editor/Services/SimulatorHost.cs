using Radix.Core;
using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Radix.Editor.ViewModels;

namespace Radix.Editor.Services;

/// <summary>
/// Owns the editor-side simulation: compiles the current project, replays external
/// drives, and exposes values for drawing. The GUI never touches engine internals.
/// </summary>
public sealed class SimulatorHost
{
    private readonly Dictionary<int, string> _rootNetLabels = [];
    private readonly List<DriveRecord> _drives = [];
    private readonly List<Diagnostic> _compileDiagnostics = [];
    private int _time;

    public SimulatorHost(CircuitProject project, CircuitDefinition definition)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public CircuitProject Project { get; private set; }

    public CircuitDefinition Definition { get; private set; }

    public CompiledCircuit? Circuit { get; private set; }

    public SimulationEngine? Engine { get; private set; }

    public IReadOnlyList<Diagnostic> CompileDiagnostics => _compileDiagnostics;

    public bool IsCompiled => Circuit is not null && Engine is not null;

    public long Now => Engine?.Now ?? _time;

    public bool IsHalted => Engine?.IsHalted ?? false;

    public string? HaltReason => Engine?.HaltReason;

    public long EventsProcessed => Engine?.EventsProcessed ?? 0;

    public long ComponentEvaluations => Engine?.ComponentEvaluations ?? 0;

    public IReadOnlyList<ExternalDrive> Drives => _drives.Select(d => new ExternalDrive(d.NetId, d.Value, d.Time)).ToArray();

    public void Retarget(CircuitProject project, CircuitDefinition definition)
    {
        Project = project;
        Definition = definition;
        Invalidate();
    }

    /// <summary>Drops the simulated state; the next command recompiles lazily.</summary>
    public void Invalidate()
    {
        Engine = null;
        Circuit = null;
        _time = 0;
        _drives.Clear();
        _rootNetLabels.Clear();
        _compileDiagnostics.Clear();
    }

    /// <summary>Compiles the project and creates a fresh engine at time zero.</summary>
    public bool Compile()
    {
        Invalidate();
        // The edited definition is the simulation root. This keeps port values and
        // waveforms meaningful while navigating into a reusable component, and makes
        // every definition independently testable in place.
        bool compiled = CircuitCompiler.TryCompile(
            Project,
            Definition.Id,
            out CompiledCircuit? circuit,
            out DiagnosticBag diagnostics);
        _compileDiagnostics.AddRange(diagnostics);
        if (!compiled || circuit is null)
        {
            return false;
        }

        Circuit = circuit;
        _compileDiagnostics.AddRange(circuit.Diagnostics);
        BuildRootNetLabels(circuit);
        Engine = new SimulationEngine(circuit, SimulationOptions.FromSettings(Project.Settings));
        Engine.AdvanceTo(0);
        _time = 0;
        return true;
    }

    public bool EnsureCompiled() => IsCompiled || Compile();

    public void Reset() => ResetTo(0);

    private void ResetTo(int time)
    {
        if (!EnsureCompiled())
        {
            return;
        }

        Engine!.Reset();
        _time = time;
        foreach (DriveRecord drive in _drives.OrderBy(d => d.Time))
        {
            if (TryNetIndex(drive.NetId, out int index, out _))
            {
                Engine.AdvanceTo(drive.Time);
                Engine.SetExternalInput(index, drive.Value);
            }
        }

        Engine.AdvanceTo(_time);
    }

    public void StepEvent()
    {
        if (!EnsureCompiled())
        {
            return;
        }

        Engine!.Step();
        _time = (int)Engine.Now;
    }

    public void Advance(int ticks)
    {
        if (!EnsureCompiled())
        {
            return;
        }

        if (Engine!.IsHalted)
        {
            return;
        }

        checked { _time += Math.Max(0, ticks); }

        if (Engine.Now < _time)
        {
            Engine.AdvanceTo(_time);
        }
    }

    /// <summary>Processes any remaining events at the current timestamp only.</summary>
    public void SettleCurrentTime()
    {
        if (!EnsureCompiled())
        {
            return;
        }

        Engine!.AdvanceTo(Engine.Now);
        _time = (int)Engine.Now;
    }

    /// <summary>Advances one rising edge of the chosen clock net, or one event if none.</summary>
    public bool StepClock(string clockNetId)
    {
        if (!EnsureCompiled())
        {
            return false;
        }

        if (!TryNetIndex(clockNetId, out int index, out SignalDomain? domain))
        {
            StepEvent();
            return false;
        }

        if (Circuit!.Nets[index].DriverPins.Length > 0)
        {
            // Clock is generated inside the circuit. Advance in one-tick steps until
            // the net changes so the editor follows the authored clock source.
            int before = Engine!.GetNetValue(index);
            for (int i = 0; i < 10_000 && !Engine.IsHalted; i++)
            {
                Engine.AdvanceBy(1);
                if (Engine.GetNetValue(index) != before)
                {
                    break;
                }
            }

            _time = (int)Engine.Now;
            return true;
        }

        int one = domain is { ValueCount: > 1 } ? domain.ValueCount - 1 : 0;
        const int zero = 0;

        // Finish the current timestamp, then drive the clock low and high with a
        // deliberate two-tick enable window before stepping the resulting events.
        Engine!.AdvanceTo(Engine.Now);
        Engine.SetExternalInput(index, zero);
        Engine.AdvanceTo(Engine.Now + 1);
        Engine.SetExternalInput(index, one);
        Engine.AdvanceTo(Engine.Now + 1);
        _time = (int)Engine.Now;
        return true;
    }

    public bool TryRead(EditorPin pin, out int value, out SignalDomain? domain)
    {
        value = SignalValue.UndrivenIndex;
        domain = DomainFor(pin);
        if (Engine is null || !TryNetIndex(pin, out int net, out SignalDomain? netDomain))
        {
            return false;
        }

        domain ??= netDomain;
        value = Engine.GetNetValue(net);
        return true;
    }

    public string Format(EditorPin pin)
    {
        return TryRead(pin, out int value, out SignalDomain? domain) ? Format(value, domain) : "–";
    }

    public static string Format(int? value, SignalDomain? domain)
    {
        if (value is not int index)
        {
            return "–";
        }

        if (index == SignalValue.UndrivenIndex)
        {
            return "z";
        }

        if (domain is not null)
        {
            return domain.IsValidValue(index) ? domain.ValueName(index) : "!";
        }

        return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public SignalDomain? DomainFor(EditorPin pin)
    {
        if (pin.Kind == EditorPinKind.Port)
        {
            return Project.FindDomain(Definition.FindPin(pin.PinId)?.DomainId);
        }

        ComponentInstance? instance = Definition.FindInstance(pin.OwnerId);
        if (instance is null)
        {
            return null;
        }

        PinDescriptor? descriptor = Project.FindInstancePin(instance, pin.PinId);
        if (descriptor?.DomainId is string declared)
        {
            return Project.FindDomain(declared);
        }

        if (TryNetIndex(pin, out int net, out SignalDomain? netDomain))
        {
            return netDomain;
        }

        return null;
    }

    public Net? NetFor(EditorPin pin)
    {
        if (pin.Kind == EditorPinKind.Port)
        {
            return Definition.FindPortNet(pin.PinId);
        }

        ComponentInstance? instance = Definition.FindInstance(pin.OwnerId);
        if (instance is null)
        {
            return null;
        }

        return Definition.Nets.FirstOrDefault(n => n.Pins.Any(p =>
            string.Equals(p.InstanceId, pin.OwnerId, StringComparison.Ordinal) &&
            string.Equals(p.PinId, pin.PinId, StringComparison.Ordinal)));
    }

    public bool IsInput(EditorPin pin)
    {
        if (pin.Kind == EditorPinKind.Port)
        {
            return Definition.FindPin(pin.PinId)?.Direction == PinDirection.Input;
        }

        ComponentInstance? instance = Definition.FindInstance(pin.OwnerId);
        return instance is not null &&
               Project.FindInstancePin(instance, pin.PinId)?.Direction == PinDirection.Input;
    }

    /// <summary>Adds an external value at the current simulated time and replays history.</summary>
    public bool Drive(EditorPin pin, int value)
    {
        if (!IsInput(pin) || NetFor(pin) is not Net net)
        {
            return false;
        }

        if (!EnsureCompiled() || !TryNetIndex(net.Id, out _, out SignalDomain? domain))
        {
            return false;
        }

        if (domain is not null && !domain.IsValidValue(value))
        {
            return false;
        }

        _drives.RemoveAll(d => string.Equals(d.NetId, net.Id, StringComparison.Ordinal) && d.Time == _time);
        _drives.Add(new DriveRecord(net.Id, value, _time));
        ResetTo(_time);
        return true;
    }

    public bool ClearDrives()
    {
        _drives.Clear();
        ResetTo(_time);
        return true;
    }

    public bool DrivePort(string pinId, int value) => Drive(EditorPin.ForPort(pinId), value);

    public bool TryNetIndex(EditorPin pin, out int netIndex, out SignalDomain? domain)
    {
        netIndex = -1;
        domain = null;
        if (NetFor(pin) is not Net net)
        {
            return false;
        }

        return TryNetIndex(net.Id, out netIndex, out domain);
    }

    public bool TryNetIndex(string netId, out int netIndex, out SignalDomain? domain)
    {
        netIndex = -1;
        domain = null;
        if (Circuit is null || !Circuit.TryGetNet(new NetLocation(string.Empty, netId), out netIndex))
        {
            return false;
        }

        domain = Circuit.Nets[netIndex].Domain;
        return true;
    }

    public string NetLabel(int netIndex)
    {
        if (_rootNetLabels.TryGetValue(netIndex, out string? label))
        {
            return label;
        }

        return Circuit is not null ? Circuit.Nets[netIndex].RepresentativeNetId : $"net{netIndex}";
    }

    public IReadOnlyList<Diagnostic> AllDiagnostics()
    {
        var all = new List<Diagnostic>(_compileDiagnostics);
        if (Engine is not null)
        {
            all.AddRange(Engine.RuntimeDiagnostics);
        }

        return all;
    }

    public IReadOnlyList<string> InputEventDescriptions()
    {
        var descriptions = new List<string> { "begin simulation", "advance", "settle" };
        foreach (PinDeclaration pin in Definition.Pins.Where(p => p.Direction == PinDirection.Input))
        {
            SignalDomain? domain = Project.FindDomain(pin.DomainId);
            if (domain is null)
            {
                continue;
            }

            foreach (string name in domain.ValueNames)
            {
                descriptions.Add($"append {pin.Name}={name} @ t={Now}");
            }
        }

        return descriptions;
    }

    public bool RunEventDescription(string description)
    {
        if (description == "begin simulation")
        {
            Reset();
            return true;
        }

        if (description == "advance")
        {
            Advance((int)Project.Settings.DefaultAdvanceTicks);
            return true;
        }

        if (description == "settle")
        {
            SettleCurrentTime();
            return true;
        }

        if (!description.StartsWith("append ", StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = description["append ".Length..].Split('=', '@');
        if (parts.Length < 2)
        {
            return false;
        }

        string pinName = parts[0].Trim();
        string valueName = parts[1].Trim();
        PinDeclaration? pin = Definition.Pins.FirstOrDefault(p =>
            string.Equals(p.Name, pinName, StringComparison.Ordinal) && p.Direction == PinDirection.Input);
        SignalDomain? domain = pin is null ? null : Project.FindDomain(pin.DomainId);
        if (pin is null || domain is null)
        {
            return false;
        }

        int value = -1;
        for (int i = 0; i < domain.ValueCount; i++)
        {
            if (string.Equals(domain.ValueName(i), valueName, StringComparison.Ordinal))
            {
                value = i;
                break;
            }
        }

        return value >= 0 && DrivePort(pin.Id, value);
    }

    private void BuildRootNetLabels(CompiledCircuit circuit)
    {
        _rootNetLabels.Clear();
        foreach (Net net in Definition.Nets)
        {
            if (circuit.TryGetNet(new NetLocation(string.Empty, net.Id), out int index))
            {
                _rootNetLabels[index] = net.Label ?? net.Id;
            }
        }

        foreach (PinDeclaration pin in Definition.Pins)
        {
            if (Definition.FindPortNet(pin.Id) is Net net &&
                circuit.TryGetNet(new NetLocation(string.Empty, net.Id), out int index))
            {
                _rootNetLabels[index] = pin.Name;
            }
        }
    }

    private readonly record struct DriveRecord(string NetId, int Value, int Time);
}

public readonly record struct ExternalDrive(string NetId, int Value, long Time);
