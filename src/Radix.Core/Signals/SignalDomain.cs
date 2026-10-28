namespace Radix.Core.Signals;

/// <summary>
/// A discrete signal value, represented as an index into a <see cref="SignalDomain"/>.
/// The index carries no numeric meaning; numeric interpretation belongs to
/// <c>Radix.Core.Interpretation</c> and never affects simulation.
/// </summary>
/// <remarks>
/// <see cref="UndrivenIndex"/> is a kernel-level "no driver" marker. It is distinct
/// from an explicit high-impedance value that a domain may define.
/// </remarks>
public readonly record struct SignalValue(int Index) : IComparable<SignalValue>
{
    /// <summary>Index used when no terminal is driving a net.</summary>
    public const int UndrivenIndex = -1;

    /// <summary>The undriven value.</summary>
    public static SignalValue Undriven => new(UndrivenIndex);

    /// <summary>True when a driver contributes a value.</summary>
    public bool IsDriven => Index >= 0;

    public int CompareTo(SignalValue other) => Index.CompareTo(other.Index);

    public override string ToString() => IsDriven ? Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "undriven";
}

/// <summary>Direction of a terminal as seen from the component that owns it.</summary>
public enum PinDirection
{
    Input,
    Output,
}

/// <summary>Outcome of resolving several simultaneously active drivers on a net.</summary>
/// <param name="Value">The resulting domain value index, or -1 when undriven.</param>
/// <param name="Conflict">True when active drivers disagreed under the selected policy.</param>
public readonly record struct DriverResolution(int Value, bool Conflict);

/// <summary>
/// Policy for combining values from multiple active drivers on one net.
/// Implementations must be deterministic and depend only on the values and their
/// order, never on ambient state.
/// </summary>
public interface IDriverResolution
{
    /// <summary>Stable identifier used by the project format.</summary>
    public string Kind { get; }

    /// <summary>Human-readable description for documentation and diagnostics.</summary>
    public string Describe();

    /// <summary>
    /// Resolves driver values. <paramref name="driverValues"/> contains only active
    /// drivers (never <see cref="SignalValue.UndrivenIndex"/>), ordered by compiled pin order.
    /// An empty span means the net is undriven, which must resolve to -1.
    /// </summary>
    public DriverResolution Resolve(SignalDomain domain, ReadOnlySpan<int> driverValues);
}

/// <summary>
/// An immutable, named set of discrete signal values plus a multi-driver policy.
/// Domains are deliberately free of numeric interpretation: a symbol such as "1"
/// is just an index here.
/// </summary>
public sealed class SignalDomain
{
    private readonly string[] _valueNames;

    public SignalDomain(
        string id,
        string name,
        IEnumerable<string> valueNames,
        IDriverResolution drivers,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(valueNames);
        ArgumentNullException.ThrowIfNull(drivers);

        _valueNames = valueNames.ToArray();
        if (_valueNames.Length == 0)
        {
            throw new ArgumentException("A signal domain needs at least one value.", nameof(valueNames));
        }

        if (_valueNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Signal value names must be non-empty.", nameof(valueNames));
        }

        DriverResolutionTest(drivers, _valueNames.Length);

        Id = id;
        Name = name;
        Drivers = drivers;
        Description = description;
    }

    /// <summary>Stable identifier, unique within a project.</summary>
    public string Id { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Optional documentation shown by tools.</summary>
    public string? Description { get; }

    /// <summary>Number of discrete values.</summary>
    public int ValueCount => _valueNames.Length;

    /// <summary>Value names indexed by signal value index.</summary>
    public IReadOnlyList<string> ValueNames => _valueNames;

    /// <summary>Multi-driver resolution policy.</summary>
    public IDriverResolution Drivers { get; }

    public bool IsValidValue(int index) => index >= 0 && index < _valueNames.Length;

    public string ValueName(int index) =>
        index >= 0 && index < _valueNames.Length
            ? _valueNames[index]
            : index == SignalValue.UndrivenIndex ? "undriven" : $"<invalid:{index}>";

    public bool IsMultipleDriverPolicy => Drivers is not SingleDriverResolution;

    internal static void DriverResolutionTest(IDriverResolution drivers, int valueCount)
    {
        switch (drivers)
        {
            case PriorityDriverResolution priority:
                if (priority.Priorities.Count != valueCount)
                {
                    throw new ArgumentException(
                        $"Priority table length {priority.Priorities.Count} does not match value count {valueCount}.");
                }

                if (!priority.IsValidConflictValue(valueCount))
                {
                    throw new ArgumentException("Priority conflict value is outside the domain.");
                }

                break;
            case SingleDriverResolution:
                break;
            default:
                throw new ArgumentException($"Unsupported driver resolution type {drivers.GetType().Name}.");
        }
    }

    public override string ToString() => $"{Name} ({Id}, {ValueCount} values)";
}
