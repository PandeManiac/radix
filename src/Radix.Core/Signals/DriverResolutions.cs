namespace Radix.Core.Signals;

/// <summary>
/// Default policy for domains where a net may have at most one active driver.
/// Multiple drivers are a modelling error; the first driver (in compiled pin order)
/// is used and the conflict is reported so tools can surface it.
/// </summary>
public sealed class SingleDriverResolution : IDriverResolution
{
    public string Kind => "single";

    public string Describe() => "single driver (multiple active drivers are a conflict)";

    public DriverResolution Resolve(SignalDomain domain, ReadOnlySpan<int> driverValues)
    {
        ArgumentNullException.ThrowIfNull(domain);

        if (driverValues.IsEmpty)
        {
            return new DriverResolution(SignalValue.UndrivenIndex, Conflict: false);
        }

        return new DriverResolution(driverValues[0], Conflict: driverValues.Length > 1);
    }
}

/// <summary>
/// Priority-based resolution for multi-driver domains. The driver with the highest
/// priority wins; if two different values share that priority the domain's conflict
/// value is produced. This models wired logic, tri-state buses, and similar schemes
/// without assuming binary levels.
/// </summary>
public sealed class PriorityDriverResolution : IDriverResolution
{
    public PriorityDriverResolution(IEnumerable<int> priorities, int conflictValue)
    {
        ArgumentNullException.ThrowIfNull(priorities);
        Priorities = priorities.ToArray();
        ConflictValue = conflictValue;
        if (Priorities.Count == 0)
        {
            throw new ArgumentException("Priority table must not be empty.", nameof(priorities));
        }

        if (Priorities.Any(p => p < 0))
        {
            throw new ArgumentException("Priorities must be non-negative.", nameof(priorities));
        }

        if (conflictValue < 0)
        {
            throw new ArgumentException("Conflict value must be a valid value index.", nameof(conflictValue));
        }
    }

    public string Kind => "priority";

    public IReadOnlyList<int> Priorities { get; }

    public int ConflictValue { get; }

    internal bool IsValidConflictValue(int valueCount) => ConflictValue >= 0 && ConflictValue < valueCount;

    public string Describe() =>
        $"priority [{string.Join(", ", Priorities.Select((p, i) => $"{i}:{p}"))}] (ties resolve to {ConflictValue})";

    public DriverResolution Resolve(SignalDomain domain, ReadOnlySpan<int> driverValues)
    {
        ArgumentNullException.ThrowIfNull(domain);

        if (driverValues.IsEmpty)
        {
            return new DriverResolution(SignalValue.UndrivenIndex, Conflict: false);
        }

        if (driverValues.Length == 1)
        {
            return new DriverResolution(driverValues[0], Conflict: false);
        }

        int bestPriority = -1;
        int bestValue = SignalValue.UndrivenIndex;
        bool conflict = false;

        foreach (int value in driverValues)
        {
            if (!domain.IsValidValue(value))
            {
                return new DriverResolution(ConflictValue, Conflict: true);
            }

            int priority = Priorities[value];
            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestValue = value;
                conflict = false;
            }
            else if (priority == bestPriority && value != bestValue)
            {
                conflict = true;
            }
        }

        return conflict
            ? new DriverResolution(ConflictValue, Conflict: true)
            : new DriverResolution(bestValue, Conflict: false);
    }
}
