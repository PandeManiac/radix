using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Core.Compilation;

/// <summary>A flattened leaf component instance.</summary>
public sealed record CompiledComponent(
    int Index,
    string Path,
    string DefinitionId,
    string InstanceId,
    PrimitiveSpec Spec,
    int FirstPin,
    int PinCount);

/// <summary>A flattened pin. Primitive pins only; structural ports are resolved into nets.</summary>
public sealed record CompiledPin(
    int Index,
    int Component,
    int LocalPin,
    string PinId,
    PinDirection Direction,
    int Net);

/// <summary>A flattened net with precomputed driver pins and affected components.</summary>
public sealed record CompiledNet(
    int Index,
    SignalDomain Domain,
    int[] DriverPins,
    int[] SinkComponents,
    int InitialValue,
    string? Label,
    string RepresentativeNetId,
    string RepresentativePath,
    string RepresentativeDefinitionId);

/// <summary>A top-level interface pin of the compiled root definition.</summary>
public sealed record CompiledPort(
    string PinId,
    string Name,
    PinDirection Direction,
    SignalDomain Domain,
    int Net);

/// <summary>Location of an authored net at a specific hierarchy path.</summary>
public readonly record struct NetLocation(string Path, string NetId);

/// <summary>Location of an authored leaf pin at a specific hierarchy path.</summary>
public readonly record struct PinLocation(string Path, string InstanceId, string PinId);

/// <summary>One observed net transition.</summary>
public readonly record struct NetChange(long Time, int Net, int OldValue, int NewValue);

/// <summary>
/// Immutable, flattening result ready for simulation. Hierarchy is resolved here so
/// the engine sees only flat arrays and no definition lookups.
/// </summary>
public sealed class CompiledCircuit
{
    private readonly Dictionary<NetLocation, int> _netIndexByLocation;
    private readonly Dictionary<PinLocation, int> _pinIndexByLocation;

    internal CompiledCircuit(
        string rootDefinitionId,
        IReadOnlyList<CompiledComponent> components,
        IReadOnlyList<CompiledPin> pins,
        IReadOnlyList<CompiledNet> nets,
        IReadOnlyList<CompiledPort> ports,
        IReadOnlyDictionary<NetLocation, int> netIndexByLocation,
        IReadOnlyDictionary<PinLocation, int> pinIndexByLocation,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        RootDefinitionId = rootDefinitionId;
        Components = components;
        Pins = pins;
        Nets = nets;
        Ports = ports;
        Diagnostics = diagnostics;
        _netIndexByLocation = new Dictionary<NetLocation, int>(netIndexByLocation);
        _pinIndexByLocation = new Dictionary<PinLocation, int>(pinIndexByLocation);
    }

    public string RootDefinitionId { get; }

    public IReadOnlyList<CompiledComponent> Components { get; }

    public IReadOnlyList<CompiledPin> Pins { get; }

    public IReadOnlyList<CompiledNet> Nets { get; }

    public IReadOnlyList<CompiledPort> Ports { get; }

    /// <summary>Validation/compile diagnostics, including warnings.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public int TotalPinCount => Pins.Count;

    public int TotalNetCount => Nets.Count;

    public bool TryGetNet(NetLocation location, out int netIndex) =>
        _netIndexByLocation.TryGetValue(location, out netIndex);

    public bool TryGetPin(PinLocation location, out int pinIndex) =>
        _pinIndexByLocation.TryGetValue(location, out pinIndex);

    public IReadOnlyDictionary<NetLocation, int> NetIndexByLocation => _netIndexByLocation;

    public IReadOnlyDictionary<PinLocation, int> PinIndexByLocation => _pinIndexByLocation;

    public CompiledNet? FindNet(NetLocation location) =>
        _netIndexByLocation.TryGetValue(location, out int index) ? Nets[index] : null;

    public CompiledPin? FindPin(PinLocation location) =>
        _pinIndexByLocation.TryGetValue(location, out int index) ? Pins[index] : null;
}
