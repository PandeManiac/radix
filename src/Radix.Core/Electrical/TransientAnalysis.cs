namespace Radix.Core.Electrical;

/// <summary>Time-domain integration method for the transient electrical solver.</summary>
public enum TransientMethod
{
    /// <summary>
    /// Implicit backward Euler. First order, L-stable, and numerically dissipative:
    /// it cannot ring on its own, so it is the default for stiff circuits.
    /// </summary>
    BackwardEuler,

    /// <summary>
    /// Trapezoidal rule. Second order and energy preserving for ideal LC networks,
    /// but not L-stable: it can ring on a stiff step. The first step always uses
    /// backward Euler because the trapezoidal history current is not yet known.
    /// </summary>
    Trapezoidal,
}

/// <summary>
/// A two-terminal ideal capacitor, <c>i = C dv/dt</c>, with current flowing from
/// <see cref="NodeA"/> to <see cref="NodeB"/> through the element.
/// </summary>
public sealed record TransientCapacitor(string Id, string NodeA, string NodeB, double Farads);

/// <summary>
/// A two-terminal ideal inductor, <c>v = L di/dt</c>, with current flowing from
/// <see cref="NodeA"/> to <see cref="NodeB"/> through the element.
/// </summary>
public sealed record TransientInductor(string Id, string NodeA, string NodeB, double Henries);

/// <summary>One breakpoint of a piecewise-linear source waveform.</summary>
public sealed record TransientWaveformPoint(double Time, double Value);

/// <summary>
/// A piecewise-linear drive for an independent source. Between breakpoints the
/// value is linear; before the first and after the last breakpoint it holds the
/// endpoint value. A step is a pair of breakpoints at (nearly) the same time, and a
/// pulse, ramp, triangle, or sampled signal is a longer breakpoint list.
/// </summary>
public sealed class TransientWaveform
{
    private readonly TransientWaveformPoint[] _points;

    private TransientWaveform(TransientWaveformPoint[] points) => _points = points;

    /// <summary>The breakpoints, in ascending time order.</summary>
    public IReadOnlyList<TransientWaveformPoint> Points => _points;

    /// <summary>
    /// Builds a waveform from breakpoints. Times must be finite and distinct;
    /// unsorted input is ordered so the result does not depend on input order.
    /// </summary>
    public static TransientWaveform Create(IEnumerable<TransientWaveformPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        TransientWaveformPoint[] ordered = points.OrderBy(point => point.Time).ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("A waveform needs at least one breakpoint.", nameof(points));
        }

        for (int index = 0; index < ordered.Length; index++)
        {
            if (!double.IsFinite(ordered[index].Time) || !double.IsFinite(ordered[index].Value))
            {
                throw new ArgumentException(
                    "Waveform breakpoints must have finite times and values.", nameof(points));
            }

            if (index > 0 && ordered[index].Time <= ordered[index - 1].Time)
            {
                throw new ArgumentException(
                    $"Waveform breakpoints must have distinct times; '{ordered[index - 1].Time}' is repeated.",
                    nameof(points));
            }
        }

        return new TransientWaveform(ordered);
    }

    /// <summary>The linearly interpolated value at <paramref name="time"/>.</summary>
    public double ValueAt(double time)
    {
        if (!double.IsFinite(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "Waveform time must be finite.");
        }

        if (time <= _points[0].Time)
        {
            return _points[0].Value;
        }

        if (time >= _points[^1].Time)
        {
            return _points[^1].Value;
        }

        int low = 0;
        int high = _points.Length - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (_points[middle].Time <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        TransientWaveformPoint left = _points[low];
        TransientWaveformPoint right = _points[high];
        double interval = right.Time - left.Time;
        double elapsed = time - left.Time;
        double fraction;
        if (double.IsFinite(interval) && double.IsFinite(elapsed))
        {
            fraction = elapsed / interval;
        }
        else
        {
            // Opposite-sign finite timestamps can have an infinite difference even
            // though their normalized position is representable.
            double timeScale = Math.Max(Math.Abs(left.Time), Math.Abs(right.Time));
            fraction = ((time / timeScale) - (left.Time / timeScale)) /
                ((right.Time / timeScale) - (left.Time / timeScale));
        }

        fraction = Math.Clamp(fraction, 0, 1);
        double valueDelta = right.Value - left.Value;
        if (double.IsFinite(valueDelta))
        {
            return left.Value + (valueDelta * fraction);
        }

        // The endpoints have opposite signs, so a convex weighted sum stays finite
        // even when subtracting them would overflow.
        return (left.Value * (1 - fraction)) + (right.Value * fraction);
    }
}

/// <summary>
/// Fixed-step transient analysis options. The interval
/// <see cref="StartTime"/>..<see cref="StopTime"/> is divided into
/// <see cref="Steps"/> uniform steps, so the run is deterministic and every
/// operating point lands on <c>start + n * step</c>.
/// </summary>
public sealed record TransientOptions
{
    /// <summary>Upper bound on steps in one run, to keep one analysis bounded.</summary>
    public const int MaximumSteps = 1_000_000;

    /// <summary>Simulated time of the initial operating point, in seconds.</summary>
    public double StartTime { get; init; }

    /// <summary>End of the analysed interval, in seconds.</summary>
    public double StopTime { get; init; } = 1;

    /// <summary>Number of uniform integration steps between start and stop.</summary>
    public int Steps { get; init; } = 100;

    /// <summary>
    /// Fraction of a node's own final value or swing used as the settling band in
    /// <see cref="TransientResult.Metrics"/>. Must be strictly between 0 and 1; the
    /// default is 2%.
    /// </summary>
    public double SettlingBand { get; init; } = 0.02;

    /// <summary>Integration method. The first step always uses backward Euler.</summary>
    public TransientMethod Method { get; init; } = TransientMethod.BackwardEuler;

    /// <summary>
    /// Relative local-error tolerance. `null` (the default) keeps the fixed-step
    /// uniform integration; a positive value enables adaptive step control by step
    /// doubling. Each state variable uses an absolute floor of
    /// `tolerance * 1e-6` so values near zero are still controlled.
    /// </summary>
    public double? AdaptiveTolerance { get; init; }

    /// <summary>
    /// Absolute local-error tolerance. Zero (the default) derives it as
    /// `AdapativeTolerance * scale`, where the scale is the largest magnitude of any
    /// independent source over the interval or any initial reactive state, with a floor
    /// of 1. That keeps a state that starts at zero from being controlled against zero.
    /// </summary>
    public double AdaptiveAbsoluteTolerance { get; init; }

    /// <summary>First adaptive step in seconds; zero derives it from <see cref="TimeStep"/>.</summary>
    public double InitialTimeStep { get; init; }

    /// <summary>Smallest adaptive step in seconds; zero derives it from the interval.</summary>
    public double MinimumTimeStep { get; init; }

    /// <summary>Largest adaptive step in seconds; zero derives it from the interval.</summary>
    public double MaximumTimeStep { get; init; }

    /// <summary>Bound on trial steps in an adaptive run.</summary>
    public int MaximumStepCount { get; init; } = 100_000;

    /// <summary>Whether the run controls its own step size.</summary>
    public bool IsAdaptive => AdaptiveTolerance is not null;

    /// <summary>The uniform integration step in seconds.</summary>
    public double TimeStep => (StopTime - StartTime) / Steps;

    /// <summary>Rejects a range or step count that cannot produce a valid time step.</summary>
    public void Validate()
    {
        if (!double.IsFinite(StartTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(StartTime), StartTime, "Transient start time must be finite.");
        }

        if (!double.IsFinite(StopTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(StopTime), StopTime, "Transient stop time must be finite.");
        }

        if (!(StopTime > StartTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(StopTime), StopTime, "Transient stop time must be greater than the start time.");
        }

        if (Steps < 1 || Steps > MaximumSteps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Steps), Steps, $"A transient run needs between 1 and {MaximumSteps} steps.");
        }

        if (!double.IsFinite(SettlingBand) || SettlingBand <= 0 || SettlingBand >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SettlingBand),
                SettlingBand,
                "The settling band must be a fraction between 0 and 1, exclusive.");
        }

        if (!double.IsFinite(TimeStep) || TimeStep <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Steps),
                Steps,
                "The requested time range and step count do not produce a finite positive time step.");
        }

        double span = StopTime - StartTime;
        double firstFixedStep = Math.Min(TimeStep, span);
        if (StartTime + firstFixedStep <= StartTime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Steps),
                Steps,
                "The requested time step is too small to advance the start time at double precision.");
        }

        if (AdaptiveTolerance is not double tolerance)
        {
            return;
        }

        if (!double.IsFinite(tolerance) || tolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AdaptiveTolerance),
                tolerance,
                "Adaptive tolerance must be finite and greater than zero.");
        }

        if (!double.IsFinite(AdaptiveAbsoluteTolerance) || AdaptiveAbsoluteTolerance < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AdaptiveAbsoluteTolerance),
                AdaptiveAbsoluteTolerance,
                "The absolute adaptive tolerance must be finite and non-negative; zero derives it from the circuit scale.");
        }

        if (MaximumStepCount < 1 || MaximumStepCount > MaximumSteps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumStepCount),
                MaximumStepCount,
                $"An adaptive run needs between 1 and {MaximumSteps} trial steps.");
        }

        if (!double.IsFinite(InitialTimeStep) || InitialTimeStep < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialTimeStep),
                InitialTimeStep,
                "The initial adaptive step must be finite and non-negative; zero derives it from the step count.");
        }

        double initial = InitialTimeStep > 0 ? InitialTimeStep : TimeStep;
        if (!double.IsFinite(initial) || initial <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialTimeStep),
                InitialTimeStep,
                "The initial adaptive step must be finite and greater than zero.");
        }

        if (StartTime + Math.Min(initial, span) <= StartTime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialTimeStep),
                initial,
                "The initial adaptive time step is too small to advance the start time at double precision.");
        }

        if (!double.IsFinite(MinimumTimeStep) || MinimumTimeStep < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumTimeStep),
                MinimumTimeStep,
                "The minimum adaptive step must be finite and non-negative.");
        }

        if (!double.IsFinite(MaximumTimeStep) || MaximumTimeStep < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumTimeStep),
                MaximumTimeStep,
                "The maximum adaptive step must be finite and non-negative.");
        }

        if (MaximumTimeStep > 0 && MaximumTimeStep < initial)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumTimeStep),
                MaximumTimeStep,
                "The maximum adaptive step must not be smaller than the initial step.");
        }
    }
}

/// <summary>
/// One solved operating point of a transient run. Currents flow from each
/// element's <c>NodeA</c> to its <c>NodeB</c>; power is absorbed power
/// (<c>V * I</c>), so a negative source power means the source delivers energy.
/// </summary>
public sealed record TransientPoint(
    double Time,
    IReadOnlyDictionary<string, double> NodeVoltages,
    IReadOnlyDictionary<string, double> ElementCurrents,
    IReadOnlyDictionary<string, double> ElementVoltages,
    IReadOnlyDictionary<string, double> ElementPowers,
    double RelativeResidual);

/// <summary>
/// Energy ledger of a run, in joules. Delivered power is integrated over the
/// reported points with the trapezoidal rule, dissipated power is the same
/// integral over resistors and diodes, and the stored term is the change in
/// <c>v²C/2 + i²L/2</c> between the first and last point. The residual is
/// <c>delivered - dissipated - stored</c>, so it measures how well the reported
/// element powers and the reactive state agree; with the trapezoidal rule it is
/// near round-off, while backward Euler's artificial damping appears as a
/// difference between the power integral and the state energy.
/// </summary>
public sealed record TransientEnergyBalance(
    double DeliveredJoules,
    double DissipatedJoules,
    double StoredChangeJoules,
    double FinalStoredJoules,
    double ResidualJoules)
{
    /// <summary>Residual relative to the largest magnitude in the ledger.</summary>
    public double RelativeResidual
    {
        get
        {
            double scale = Math.Max(
                Math.Max(Math.Abs(DeliveredJoules), Math.Abs(DissipatedJoules)),
                Math.Max(Math.Abs(StoredChangeJoules), Math.Abs(FinalStoredJoules)));
            return scale > 0 ? Math.Abs(ResidualJoules) / scale : 0;
        }
    }
}

/// <summary>Peak, swing, and settling behaviour of one node over a run.</summary>
public sealed record TransientNodeMetrics(
    string NodeId,
    double MinimumVolts,
    double MaximumVolts,
    double FinalVolts,
    double PeakTimeSeconds,
    double? SettlingTimeSeconds);

/// <summary>
/// Summary metrics for a run: for every node, its minimum, maximum, final value, the
/// time of the maximum, and the time after which it stayed inside
/// <see cref="SettlingBand"/> of its final value. The band is a fraction of the node's
/// own swing (floored by its final magnitude), so it means the same thing for a 5 V
/// step and for a millivolt ripple. A null settling time means the node was still
/// outside the band at the last solved point, which is what an oscillation or a
/// diverging run looks like.
/// </summary>
public sealed record TransientMetrics(
    double SettlingBand,
    IReadOnlyList<TransientNodeMetrics> Nodes);

/// <summary>
/// Result of a transient run. A run that fails part way keeps every operating
/// point that was solved, names the step that failed, and reports the same
/// failure classification the DC solver uses.
/// </summary>
public sealed record TransientResult(
    TransientOptions Options,
    IReadOnlyList<TransientPoint> Points,
    DcSolveFailure? Failure,
    string? Message,
    int? FailedStep)
{
    /// <summary>Whether every requested step solved.</summary>
    public bool Completed => Failure is null;

    /// <summary>The uniform integration step in seconds.</summary>
    public double TimeStep => Options.TimeStep;

    /// <summary>The initial operating point at <see cref="TransientOptions.StartTime"/>.</summary>
    public TransientPoint? InitialPoint => Points.Count == 0 ? null : Points[0];

    /// <summary>The last solved operating point, which is the final one on success.</summary>
    public TransientPoint? FinalPoint => Points.Count == 0 ? null : Points[^1];

    /// <summary>
    /// The energy ledger over the solved points, or null when fewer than two points
    /// exist and no integral can be formed.
    /// </summary>
    public TransientEnergyBalance? Energy { get; init; }

    /// <summary>Per-node peak, swing, and settling behaviour, or null with fewer than two points.</summary>
    public TransientMetrics? Metrics { get; init; }

    /// <summary>
    /// Informational notes about the run, such as an initial condition that starts the circuit
    /// away from its own DC operating point. A note never changes a result or its failure
    /// classification; it explains a jump the numbers alone would not.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Deterministic fingerprint of the netlist this run solved, or null when it was not
    /// recorded. It identifies the circuit and its values, so an export can prove which revision
    /// produced a result even when two circuits share an id.
    /// </summary>
    public string? NetlistFingerprint { get; init; }
}

/// <summary>
/// A netlist for fixed-step transient analysis. It extends the DC element set
/// with ideal capacitors and inductors and solves each step as an independent
/// modified-nodal system through the DC solver's companion models:
/// <list type="bullet">
/// <item>Backward Euler replaces <c>C</c> with <c>C/h</c> in parallel with a
/// current source carrying <c>(C/h) v(t-h)</c>, and <c>L</c> with <c>h/L</c> in
/// parallel with a current source carrying <c>i(t-h)</c>.</item>
/// <item>The trapezoidal rule doubles those conductances and adds the previous
/// branch current (capacitor) or previous branch voltage (inductor) to the
/// history term.</item>
/// </list>
/// This is a linear companion model, so diodes in the same netlist are still
/// solved by the DC solver's Newton iteration at every step. Independent sources
/// may carry a piecewise-linear waveform; without one they stay constant.
/// </summary>
public sealed class TransientNetlist
{
    private const string CompanionPrefix = "~";

    private readonly List<DcElement> _elements = [];
    private readonly List<TransientCapacitor> _capacitors = [];
    private readonly List<TransientInductor> _inductors = [];
    private readonly Dictionary<string, TransientWaveform> _waveforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _initialCapacitorVolts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _initialInductorAmperes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nodes = new(StringComparer.Ordinal);

    public TransientNetlist(string groundNodeId = "0")
    {
        if (string.IsNullOrWhiteSpace(groundNodeId))
        {
            throw new ArgumentException("Ground node ID must not be empty.", nameof(groundNodeId));
        }

        GroundNodeId = groundNodeId;
        _nodes.Add(groundNodeId);
    }

    /// <summary>The zero-volt reference node, excluded from the voltage unknowns.</summary>
    public string GroundNodeId { get; }

    /// <summary>The non-reactive elements, in insertion order.</summary>
    public IReadOnlyList<DcElement> Elements => _elements;

    public IReadOnlyList<TransientCapacitor> Capacitors => _capacitors;

    public IReadOnlyList<TransientInductor> Inductors => _inductors;

    /// <summary>All referenced nodes, including ground, in ordinal order.</summary>
    public IReadOnlyList<string> Nodes => _nodes.OrderBy(node => node, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// A copy that can be changed without touching this netlist. Elements, reactive components,
    /// presets and waveforms are all carried over, so a sweep varies exactly one thing.
    /// </summary>
    public TransientNetlist Clone()
    {
        var copy = new TransientNetlist(GroundNodeId);
        foreach (DcElement element in _elements)
        {
            switch (element)
            {
                case DcResistor resistor:
                    copy.AddResistor(resistor.Id, resistor.NodeA, resistor.NodeB, resistor.Ohms);
                    break;
                case DcVoltageSource source:
                    copy.AddVoltageSource(source.Id, source.NodeA, source.NodeB, source.Volts);
                    break;
                case DcCurrentSource source:
                    copy.AddCurrentSource(source.Id, source.NodeA, source.NodeB, source.Amperes);
                    break;
                case DcDiode diode:
                    copy.AddDiode(
                        diode.Id,
                        diode.NodeA,
                        diode.NodeB,
                        diode.SaturationCurrent,
                        diode.EmissionCoefficient,
                        diode.ThermalVoltage,
                        diode.SeriesResistance,
                        diode.BreakdownVoltage,
                        diode.BreakdownCurrent);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Element '{element.Id}' has an unknown type and cannot be copied.");
            }
        }

        foreach (TransientCapacitor capacitor in _capacitors)
        {
            copy.AddCapacitor(capacitor.Id, capacitor.NodeA, capacitor.NodeB, capacitor.Farads);
        }

        foreach (TransientInductor inductor in _inductors)
        {
            copy.AddInductor(inductor.Id, inductor.NodeA, inductor.NodeB, inductor.Henries);
        }

        foreach (KeyValuePair<string, double> preset in _initialCapacitorVolts)
        {
            copy.SetCapacitorInitialVoltage(preset.Key, preset.Value);
        }

        foreach (KeyValuePair<string, double> preset in _initialInductorAmperes)
        {
            copy.SetInductorInitialCurrent(preset.Key, preset.Value);
        }

        foreach (KeyValuePair<string, TransientWaveform> waveform in _waveforms)
        {
            copy.SetWaveform(waveform.Key, waveform.Value.Points.ToArray());
        }

        return copy;
    }

    /// <summary>
    /// A copy with one element's value replaced, for a parameter sweep. A diode's behaviour comes
    /// from four parameters, so it cannot be expressed as one swept value and is rejected instead
    /// of being changed only partly.
    /// </summary>
    public TransientNetlist WithElementValue(string id, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A swept value must be finite.");
        }

        TransientNetlist copy = Clone();
        int staticIndex = copy._elements.FindIndex(
            element => string.Equals(element.Id, id, StringComparison.Ordinal));
        if (staticIndex >= 0)
        {
            copy._elements[staticIndex] = copy._elements[staticIndex] switch
            {
                DcResistor resistor => resistor with { Ohms = RequireSweptPositive(value, id) },
                DcVoltageSource source => source with { Volts = value },
                DcCurrentSource source => source with { Amperes = value },
                DcDiode => throw new ArgumentException(
                    $"Element '{id}' is a diode; its diode parameters cannot be swept as one value, so sweep a resistor, source, capacitor or inductor instead.",
                    nameof(id)),
                _ => throw new ArgumentException($"Element '{id}' cannot be swept.", nameof(id)),
            };
            return copy;
        }

        int capacitorIndex = copy._capacitors.FindIndex(
            capacitor => string.Equals(capacitor.Id, id, StringComparison.Ordinal));
        if (capacitorIndex >= 0)
        {
            copy._capacitors[capacitorIndex] = copy._capacitors[capacitorIndex] with
            {
                Farads = RequireSweptPositive(value, id),
            };
            return copy;
        }

        int inductorIndex = copy._inductors.FindIndex(
            inductor => string.Equals(inductor.Id, id, StringComparison.Ordinal));
        if (inductorIndex >= 0)
        {
            copy._inductors[inductorIndex] = copy._inductors[inductorIndex] with
            {
                Henries = RequireSweptPositive(value, id),
            };
            return copy;
        }

        throw new ArgumentException($"Netlist has no element '{id}' to sweep.", nameof(id));
    }

    private static double RequireSweptPositive(double value, string id)
    {
        if (!(value > 0) || !double.IsFinite(1.0 / value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"A swept value for '{id}' must be positive and invertible.");
        }

        return value;
    }

    public TransientNetlist AddResistor(string id, string nodeA, string nodeB, double ohms)
    {
        ValidateElement(id, nodeA, nodeB);
        if (!double.IsFinite(ohms) || ohms <= 0 || !double.IsFinite(1.0 / ohms))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ohms), ohms, "Resistance must be finite and produce a finite conductance.");
        }

        return AddStatic(new DcResistor(id, nodeA, nodeB, ohms));
    }

    public TransientNetlist AddVoltageSource(string id, string nodeA, string nodeB, double volts)
    {
        ValidateElement(id, nodeA, nodeB);
        if (!double.IsFinite(volts))
        {
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "Voltage must be finite.");
        }

        return AddStatic(new DcVoltageSource(id, nodeA, nodeB, volts));
    }

    public TransientNetlist AddCurrentSource(string id, string nodeA, string nodeB, double amperes)
    {
        ValidateElement(id, nodeA, nodeB);
        if (!double.IsFinite(amperes))
        {
            throw new ArgumentOutOfRangeException(nameof(amperes), amperes, "Current must be finite.");
        }

        return AddStatic(new DcCurrentSource(id, nodeA, nodeB, amperes));
    }

    /// <summary>
    /// Adds a Shockley diode with the same parameters as <see cref="DcNetlist.AddDiode"/>.
    /// A diode is time-invariant here: junction capacitance is not modelled.
    /// </summary>
    public TransientNetlist AddDiode(
        string id,
        string nodeA,
        string nodeB,
        double saturationCurrent,
        double emissionCoefficient = 1.0,
        double thermalVoltage = DcDiode.DefaultThermalVoltage,
        double seriesResistance = 0,
        double? breakdownVoltage = null,
        double? breakdownCurrent = null)
    {
        ValidateElement(id, nodeA, nodeB);
        if (!double.IsFinite(saturationCurrent) || saturationCurrent <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(saturationCurrent),
                saturationCurrent,
                "Saturation current must be finite and greater than zero.");
        }

        if (!double.IsFinite(emissionCoefficient) || emissionCoefficient <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(emissionCoefficient),
                emissionCoefficient,
                "Emission coefficient must be finite and greater than zero.");
        }

        if (!double.IsFinite(thermalVoltage) || thermalVoltage <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thermalVoltage),
                thermalVoltage,
                "Thermal voltage must be finite and greater than zero.");
        }

        if (!double.IsFinite(seriesResistance) || seriesResistance < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seriesResistance),
                seriesResistance,
                "Series resistance must be finite and non-negative.");
        }

        DcDiode.ValidateBreakdownParameters(breakdownVoltage, breakdownCurrent);

        return AddStatic(new DcDiode(
            id,
            nodeA,
            nodeB,
            saturationCurrent,
            emissionCoefficient,
            thermalVoltage,
            seriesResistance,
            breakdownVoltage,
            breakdownCurrent));
    }

    public TransientNetlist AddCapacitor(string id, string nodeA, string nodeB, double farads)
    {
        ValidateElement(id, nodeA, nodeB);
        if (!double.IsFinite(farads) || farads <= 0 || !double.IsFinite(1.0 / farads))
        {
            throw new ArgumentOutOfRangeException(
                nameof(farads), farads, "Capacitance must be finite, positive, and invertible.");
        }

        _ids.Add(id);
        _nodes.Add(nodeA);
        _nodes.Add(nodeB);
        _capacitors.Add(new TransientCapacitor(id, nodeA, nodeB, farads));
        return this;
    }

    public TransientNetlist AddInductor(string id, string nodeA, string nodeB, double henries)
    {
        ValidateElement(id, nodeA, nodeB);
        if (!double.IsFinite(henries) || henries <= 0 || !double.IsFinite(1.0 / henries))
        {
            throw new ArgumentOutOfRangeException(
                nameof(henries), henries, "Inductance must be finite, positive, and invertible.");
        }

        _ids.Add(id);
        _nodes.Add(nodeA);
        _nodes.Add(nodeB);
        _inductors.Add(new TransientInductor(id, nodeA, nodeB, henries));
        return this;
    }

    /// <summary>
    /// Attaches a piecewise-linear waveform to an independent voltage or current
    /// source. The source's own value is used wherever no waveform is attached.
    /// </summary>
    public TransientNetlist SetWaveform(string sourceId, IEnumerable<TransientWaveformPoint> points)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(points);
        bool exists = _elements.Any(element =>
            string.Equals(element.Id, sourceId, StringComparison.Ordinal) &&
            element is DcVoltageSource or DcCurrentSource);
        if (!exists)
        {
            throw new ArgumentException(
                $"Netlist has no independent voltage or current source '{sourceId}'.", nameof(sourceId));
        }

        _waveforms[sourceId] = TransientWaveform.Create(points);
        return this;
    }

    /// <summary>The waveform attached to a source, or null when it is constant.</summary>
    public TransientWaveform? FindWaveform(string sourceId) =>
        _waveforms.TryGetValue(sourceId, out TransientWaveform? waveform) ? waveform : null;

    /// <summary>
    /// Presets a capacitor's voltage at the start of the run, replacing the value
    /// the DC operating point would give it. The initial capacitor current stays
    /// zero, which is the DC steady-state value.
    /// </summary>
    public TransientNetlist SetCapacitorInitialVoltage(string id, double volts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!double.IsFinite(volts))
        {
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "Initial voltage must be finite.");
        }

        if (!_capacitors.Any(capacitor => string.Equals(capacitor.Id, id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Netlist has no capacitor '{id}'.", nameof(id));
        }

        _initialCapacitorVolts[id] = volts;
        return this;
    }

    /// <summary>
    /// Presets an inductor's current at the start of the run, replacing the value
    /// the DC operating point would give it.
    /// </summary>
    public TransientNetlist SetInductorInitialCurrent(string id, double amperes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!double.IsFinite(amperes))
        {
            throw new ArgumentOutOfRangeException(nameof(amperes), amperes, "Initial current must be finite.");
        }

        if (!_inductors.Any(inductor => string.Equals(inductor.Id, id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Netlist has no inductor '{id}'.", nameof(id));
        }

        _initialInductorAmperes[id] = amperes;
        return this;
    }

    /// <summary>
    /// Runs a fixed-step transient analysis from the DC operating point. The
    /// operating point treats capacitors as open circuits and inductors as ideal
    /// zero-volt shorts, uses every source's waveform value at
    /// <see cref="TransientOptions.StartTime"/>, and is the standard consistent
    /// initial condition for these companion models. A preset initial value
    /// overrides it per reactive element.
    /// </summary>
    public TransientResult Run(TransientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var capacitorVoltages = new double[_capacitors.Count];
        var capacitorCurrents = new double[_capacitors.Count];
        var inductorVoltages = new double[_inductors.Count];
        var inductorCurrents = new double[_inductors.Count];

        DcSolution operatingPoint;
        try
        {
            operatingPoint = SolveOperatingPoint(options.StartTime);
        }
        catch (DcSolveException exception)
        {
            return new TransientResult(options, [], exception.Failure, exception.Message, null);
        }

        for (int index = 0; index < _capacitors.Count; index++)
        {
            TransientCapacitor capacitor = _capacitors[index];
            capacitorVoltages[index] = _initialCapacitorVolts.TryGetValue(capacitor.Id, out double preset)
                ? preset
                : VoltageAcross(operatingPoint.NodeVoltages, capacitor.NodeA, capacitor.NodeB);
        }

        for (int index = 0; index < _inductors.Count; index++)
        {
            TransientInductor inductor = _inductors[index];
            inductorCurrents[index] = _initialInductorAmperes.TryGetValue(inductor.Id, out double preset)
                ? preset
                : operatingPoint.ElementCurrents[inductor.Id];
        }

        List<string> notes = InitialConditionNotes(operatingPoint);

        DcSolution initialState;
        try
        {
            initialState = SolveInitialState(
                options.StartTime, capacitorVoltages, inductorCurrents);
        }
        catch (DcSolveException exception)
        {
            return new TransientResult(options, [], exception.Failure, exception.Message, null);
        }

        for (int index = 0; index < _capacitors.Count; index++)
        {
            capacitorCurrents[index] = initialState.ElementCurrents[_capacitors[index].Id];
        }

        for (int index = 0; index < _inductors.Count; index++)
        {
            TransientInductor inductor = _inductors[index];
            inductorVoltages[index] = VoltageAcross(
                initialState.NodeVoltages, inductor.NodeA, inductor.NodeB);
        }

        var state = new ReactiveState(
            capacitorVoltages, capacitorCurrents, inductorVoltages, inductorCurrents);
        var points = new List<TransientPoint>(options.Steps + 1)
        {
            MakePoint(options.StartTime, initialState, state),
        };

        TransientResult result = options.IsAdaptive
            ? RunAdaptive(options, state, points)
            : RunFixed(options, state, points);
        return result with
        {
            Energy = MeasureEnergy(result.Points),
            Metrics = MeasureMetrics(options, result.Points),
            Notes = notes,
            NetlistFingerprint = Fingerprint(),
        };
    }

    /// <summary>The reactive state at one integration point. Arrays are replaced, never mutated.</summary>
    private sealed class ReactiveState
    {
        public ReactiveState(
            double[] capacitorVolts,
            double[] capacitorAmps,
            double[] inductorVolts,
            double[] inductorAmps)
        {
            CapacitorVolts = capacitorVolts;
            CapacitorAmps = capacitorAmps;
            InductorVolts = inductorVolts;
            InductorAmps = inductorAmps;
        }

        public double[] CapacitorVolts { get; }

        public double[] CapacitorAmps { get; }

        public double[] InductorVolts { get; }

        public double[] InductorAmps { get; }
    }

    /// <summary>
    /// Breakpoint times strictly inside the analysed interval, in ascending order. A
    /// step must not straddle one: the drive changes slope there, so a single step
    /// across it would apply a source value that never held, and a pulse narrower than
    /// the step would be sampled only where it is zero and disappear entirely.
    /// </summary>
    private double[] InteriorBreakpoints(TransientOptions options)
    {
        var times = new List<double>();
        foreach (TransientWaveform waveform in _waveforms.Values)
        {
            foreach (TransientWaveformPoint point in waveform.Points)
            {
                if (point.Time > options.StartTime && point.Time < options.StopTime)
                {
                    times.Add(point.Time);
                }
            }
        }

        times.Sort();
        return times.Distinct().ToArray();
    }

    /// <summary>
    /// Runs the fixed-step sequence. The requested step size is nominal: a step that
    /// would cross a waveform breakpoint is shortened to land on it, then the run
    /// continues at the requested size, so a run with breakpoints reports more points
    /// than it has steps and never integrates across a drive discontinuity.
    /// </summary>
    private TransientResult RunFixed(
        TransientOptions options,
        ReactiveState state,
        List<TransientPoint> points)
    {
        double timeStep = options.TimeStep;
        double[] breakpoints = InteriorBreakpoints(options);
        double time = options.StartTime;
        int breakpoint = 0;
        int steps = 0;
        int maximumSteps = options.Steps + breakpoints.Length + 1;
        while (time < options.StopTime)
        {
            if (steps++ >= maximumSteps)
            {
                return new TransientResult(
                    options,
                    points,
                    DcSolveFailure.NonConvergent,
                    $"Fixed-step integration did not reach the stop time within {maximumSteps} steps.",
                    points.Count - 1);
            }

            double remaining = options.StopTime - time;
            double step = Math.Min(timeStep, remaining);
            while (breakpoint < breakpoints.Length && breakpoints[breakpoint] <= time)
            {
                breakpoint++;
            }

            if (breakpoint < breakpoints.Length)
            {
                step = Math.Min(step, breakpoints[breakpoint] - time);
            }

            if (!(step > 0) || !double.IsFinite(step))
            {
                step = Math.Min(timeStep, remaining);
            }

            // Accumulated addition leaves a rounding remainder below the stop time; take it
            // as the last step instead of emitting a spurious near-zero step afterwards. Each
            // addition can lose a fraction of a bit of the current time, so the tolerance grows
            // with the number of points; 1e-14 * points stays well below a real step even at the
            // 10^6-step limit, where it is 1% of one.
            double rounding = Math.Abs(options.StopTime) * 1e-14 * Math.Max(1, points.Count);
            if (remaining - step <= rounding)
            {
                step = remaining;
            }

            double next = step >= remaining ? options.StopTime : time + step;
            if (next <= time)
            {
                return new TransientResult(
                    options,
                    points,
                    DcSolveFailure.NonConvergent,
                    $"Fixed-step integration cannot advance time from {time:G17} s by {step:G6} s at double precision.",
                    points.Count - 1);
            }

            bool trapezoidal = options.Method == TransientMethod.Trapezoidal && points.Count > 1;
            if (!TryAdvance(
                    next,
                    next - time,
                    trapezoidal,
                    state,
                    out ReactiveState? advanced,
                    out DcSolution? solution,
                    out DcSolveFailure? failure,
                    out string? message))
            {
                return new TransientResult(options, points, failure, message, points.Count - 1);
            }

            state = advanced!;
            time = next;
            points.Add(MakePoint(time, solution!, state));
        }

        return new TransientResult(options, points, null, null, null);
    }

    /// <summary>
    /// Runs adaptive step control by step doubling: one full step and two half steps
    /// from the same state estimate the local error, an accepted step keeps the two
    /// half steps (the more accurate pair), and a rejected step shrinks the step size.
    /// A trial step that cannot be solved also shrinks the step, so a Newton failure
    /// caused by a too-large step is recovered instead of ending the run. The step
    /// size exponent is `1 / (order + 1)`, which is 1/3 for the trapezoidal rule and
    /// 1/2 for backward Euler.
    /// </summary>
    private TransientResult RunAdaptive(
        TransientOptions options,
        ReactiveState state,
        List<TransientPoint> points)
    {
        double tolerance = options.AdaptiveTolerance!.Value;
        double absoluteTolerance = options.AdaptiveAbsoluteTolerance > 0
            ? options.AdaptiveAbsoluteTolerance
            : tolerance * ReferenceScale(options, state);
        double span = options.StopTime - options.StartTime;
        double[] breakpoints = InteriorBreakpoints(options);
        int breakpoint = 0;
        double minimumStep = options.MinimumTimeStep > 0 ? options.MinimumTimeStep : span * 1e-12;
        double maximumStep = options.MaximumTimeStep > 0 ? options.MaximumTimeStep : span;
        double timeStep = Math.Min(
            options.InitialTimeStep > 0 ? options.InitialTimeStep : options.TimeStep, maximumStep);
        double time = options.StartTime;
        bool firstStep = true;
        int trials = 0;
        DcSolveFailure? lastFailure = null;
        string? lastMessage = null;

        while (time < options.StopTime)
        {
            double remaining = options.StopTime - time;
            double trial = Math.Min(timeStep, remaining);
            while (breakpoint < breakpoints.Length && breakpoints[breakpoint] <= time)
            {
                breakpoint++;
            }

            if (breakpoint < breakpoints.Length)
            {
                trial = Math.Min(trial, breakpoints[breakpoint] - time);
            }

            // Adaptive runs accumulate time too, so the remainder tolerance grows with the
            // number of reported points for the same reason as the fixed-step loop.
            double rounding = Math.Abs(options.StopTime) * 1e-14 * Math.Max(1, points.Count);
            if (remaining - trial <= rounding)
            {
                trial = remaining;
            }

            double nextTime = trial >= remaining ? options.StopTime : time + trial;
            if (nextTime <= time)
            {
                return new TransientResult(
                    options,
                    points,
                    DcSolveFailure.NonConvergent,
                    $"Adaptive integration cannot advance time from {time:G17} s by {trial:G6} s at double precision; allow a larger minimum step or use a smaller absolute time origin.",
                    points.Count - 1);
            }

            bool trapezoidal = options.Method == TransientMethod.Trapezoidal && !firstStep;
            if (++trials > options.MaximumStepCount)
            {
                return new TransientResult(
                    options,
                    points,
                    DcSolveFailure.NonConvergent,
                    $"Adaptive integration used its budget of {options.MaximumStepCount} trial steps before reaching the stop time (last step {trial:G6} s); relax the tolerance, raise --max-steps, or allow smaller steps.",
                    points.Count - 1);
            }

            if (!TryAdvance(
                    time,
                    trial,
                    trapezoidal,
                    state,
                    out ReactiveState? coarse,
                    out _,
                    out lastFailure,
                    out lastMessage))
            {
                if (Shrink(ref timeStep, trial, minimumStep))
                {
                    continue;
                }

                return StepTooSmall();
            }

            double half = trial / 2;
            if (!TryAdvance(
                    time,
                    half,
                    trapezoidal,
                    state,
                    out ReactiveState? firstHalf,
                    out _,
                    out lastFailure,
                    out lastMessage) ||
                !TryAdvance(
                    time + half,
                    half,
                    trapezoidal,
                    firstHalf!,
                    out ReactiveState? fine,
                    out DcSolution? fineSolution,
                    out lastFailure,
                    out lastMessage))
            {
                if (Shrink(ref timeStep, trial, minimumStep))
                {
                    continue;
                }

                return StepTooSmall();
            }

            int order = trapezoidal ? 2 : 1;
            double error = StateError(fine!, coarse!, tolerance, absoluteTolerance, order);
            if (error <= 1)
            {
                time = trial >= remaining ? options.StopTime : time + trial;
                state = fine!;
                firstStep = false;
                points.Add(MakePoint(time, fineSolution!, state));
            }

            double factor = error <= 0
                ? 4
                : Math.Clamp(0.9 * Math.Pow(error, -1.0 / (order + 1)), 0.2, 4);
            timeStep = Math.Min(maximumStep, trial * factor);
            if (timeStep < minimumStep)
            {
                if (error > 1)
                {
                    return new TransientResult(
                        options,
                        points,
                        DcSolveFailure.NonConvergent,
                        $"Adaptive integration could not meet the tolerance of {tolerance:G6} before reaching the minimum step of {minimumStep:G6} s near t={time:G6} s; relax the tolerance or allow smaller steps.",
                        points.Count - 1);
                }

                timeStep = minimumStep;
            }
        }

        return new TransientResult(options, points, null, null, null);

        bool Shrink(ref double currentStep, double trialStep, double floor)
        {
            currentStep = trialStep / 2;
            return currentStep >= floor && trials <= options.MaximumStepCount;
        }

        TransientResult StepTooSmall() =>
            new(
                options,
                points,
                lastFailure ?? DcSolveFailure.NonConvergent,
                $"Adaptive integration could not take a solvable step of at least {minimumStep:G6} s near t={time:G6} s: {lastMessage ?? "the step would be smaller than the minimum step."}",
                points.Count - 1);
    }

    /// <summary>
    /// Advances the reactive state by one solve of the companion network. A failure
    /// returns false with its classification and message instead of throwing, so the
    /// fixed loop can end the run and the adaptive loop can retry with a smaller step.
    /// </summary>
    private bool TryAdvance(
        double time,
        double timeStep,
        bool trapezoidal,
        ReactiveState state,
        out ReactiveState? next,
        out DcSolution? solution,
        out DcSolveFailure? failure,
        out string? message)
    {
        next = null;
        solution = null;
        failure = null;
        message = null;

        if (!TryBuildStepNetlist(time, timeStep, trapezoidal, state, out DcNetlist? netlist, out string? problem))
        {
            failure = DcSolveFailure.NonFiniteResult;
            message = problem;
            return false;
        }

        try
        {
            solution = netlist!.Solve();
        }
        catch (DcSolveException exception)
        {
            failure = exception.Failure;
            message = exception.Message;
            return false;
        }

        var capacitorVolts = new double[_capacitors.Count];
        var capacitorAmps = new double[_capacitors.Count];
        for (int index = 0; index < _capacitors.Count; index++)
        {
            TransientCapacitor capacitor = _capacitors[index];
            double volts = VoltageAcross(solution.NodeVoltages, capacitor.NodeA, capacitor.NodeB);
            double current = trapezoidal
                ? ((2 * capacitor.Farads / timeStep) * (volts - state.CapacitorVolts[index])) -
                  state.CapacitorAmps[index]
                : (capacitor.Farads / timeStep) * (volts - state.CapacitorVolts[index]);
            if (!double.IsFinite(volts) || !double.IsFinite(current))
            {
                failure = DcSolveFailure.NonFiniteResult;
                message =
                    $"Capacitor '{capacitor.Id}' produced a non-finite voltage or current at a {timeStep:G6} s step; check the element value and the step size.";
                return false;
            }

            capacitorVolts[index] = volts;
            capacitorAmps[index] = current;
        }

        var inductorVolts = new double[_inductors.Count];
        var inductorAmps = new double[_inductors.Count];
        for (int index = 0; index < _inductors.Count; index++)
        {
            TransientInductor inductor = _inductors[index];
            double volts = VoltageAcross(solution.NodeVoltages, inductor.NodeA, inductor.NodeB);
            double current = trapezoidal
                ? state.InductorAmps[index] +
                  ((timeStep / (2 * inductor.Henries)) * (volts + state.InductorVolts[index]))
                : state.InductorAmps[index] + ((timeStep / inductor.Henries) * volts);
            if (!double.IsFinite(volts) || !double.IsFinite(current))
            {
                failure = DcSolveFailure.NonFiniteResult;
                message =
                    $"Inductor '{inductor.Id}' produced a non-finite voltage or current at a {timeStep:G6} s step; check the element value and the step size.";
                return false;
            }

            inductorVolts[index] = volts;
            inductorAmps[index] = current;
        }

        next = new ReactiveState(capacitorVolts, capacitorAmps, inductorVolts, inductorAmps);
        return true;
    }

    /// <summary>
    /// Normalised local-error estimate of the two-half-step solution against one full
    /// step, divided by <c>2^order - 1</c> as step doubling requires. Only the true
    /// ordinary-differential-equation states are compared (capacitor voltage and
    /// inductor current); the derived capacitor current and inductor voltage follow
    /// from them and must not drive step control, because at a drive kink their
    /// magnitude is independent of the step size. A value at or below 1 accepts the
    /// step.
    /// </summary>
    private static double StateError(
        ReactiveState fine,
        ReactiveState coarse,
        double relativeTolerance,
        double absoluteTolerance,
        int order)
    {
        double divisor = Math.Pow(2, order) - 1;
        double worst = 0;
        Accumulate(fine.CapacitorVolts, coarse.CapacitorVolts);
        Accumulate(fine.InductorAmps, coarse.InductorAmps);
        return worst;

        void Accumulate(double[] fineValues, double[] coarseValues)
        {
            for (int index = 0; index < fineValues.Length; index++)
            {
                double scale = absoluteTolerance +
                    (relativeTolerance *
                     Math.Max(Math.Abs(fineValues[index]), Math.Abs(coarseValues[index])));
                worst = Math.Max(
                    worst, Math.Abs(fineValues[index] - coarseValues[index]) / (divisor * scale));
            }
        }
    }

    /// <summary>
    /// The circuit's own magnitude, used to derive the absolute error scale: the largest
    /// independent source value over the interval, the largest initial reactive state,
    /// and a floor of 1. It is a numerical scale, not a physical quantity, so a voltage
    /// and a current share it.
    /// </summary>
    private double ReferenceScale(TransientOptions options, ReactiveState state)
    {
        double scale = 1;
        foreach (DcElement element in _elements)
        {
            if (element is not (DcVoltageSource or DcCurrentSource))
            {
                continue;
            }

            if (!_waveforms.TryGetValue(element.Id, out TransientWaveform? waveform))
            {
                scale = Math.Max(scale, Math.Abs(element.Value));
                continue;
            }

            scale = Math.Max(scale, Math.Abs(waveform.ValueAt(options.StartTime)));
            scale = Math.Max(scale, Math.Abs(waveform.ValueAt(options.StopTime)));
            foreach (TransientWaveformPoint point in waveform.Points)
            {
                if (point.Time >= options.StartTime && point.Time <= options.StopTime)
                {
                    scale = Math.Max(scale, Math.Abs(point.Value));
                }
            }
        }

        foreach (double volts in state.CapacitorVolts)
        {
            scale = Math.Max(scale, Math.Abs(volts));
        }

        foreach (double amperes in state.InductorAmps)
        {
            scale = Math.Max(scale, Math.Abs(amperes));
        }

        return scale;
    }

    /// <summary>
    /// Solves the DC operating point that supplies default reactive states:
    /// capacitors are open (omitted) and every inductor is replaced by the ideal
    /// zero-volt source it equals at DC. The inductor's own ID is reused so its DC
    /// branch current is still available under that ID. A capacitor whose nodes have
    /// no other DC path has no operating-point voltage unless one is preset, so that
    /// case is reported as a floating network instead of being invented.
    /// </summary>
    private DcSolution SolveOperatingPoint(double time)
    {
        var dc = new DcNetlist(GroundNodeId);
        foreach (DcElement element in _elements)
        {
            CopyElement(dc, element, time);
        }

        foreach (TransientInductor inductor in _inductors)
        {
            dc.AddVoltageSource(inductor.Id, inductor.NodeA, inductor.NodeB, 0);
        }

        DcSolution solution = dc.Solve();
        string[] missing = _capacitors
            .Where(capacitor => !_initialCapacitorVolts.ContainsKey(capacitor.Id))
            .SelectMany(capacitor => new[] { capacitor.NodeA, capacitor.NodeB })
            .Where(node => !solution.NodeVoltages.ContainsKey(node))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new DcSolveException(
                DcSolveFailure.FloatingNetwork,
                $"Nodes {string.Join(", ", missing.Select(node => $"'{node}'"))} are connected only through capacitors, so the netlist has no DC operating point to start a transient from; add a resistive DC path or set an explicit initial voltage.");
        }

        return solution;
    }

    /// <summary>
    /// Solves the state at the start time by imposing each reactive element's state:
    /// a capacitor becomes an ideal voltage source at its initial voltage, and an
    /// inductor becomes an ideal current source at its initial current. The reported
    /// initial node voltages then agree with the states the first integration step
    /// continues from, including preset initial conditions.
    /// </summary>
    private DcSolution SolveInitialState(
        double time,
        double[] capacitorVoltages,
        double[] inductorCurrents)
    {
        if (_capacitors.Count == 0 && _inductors.Count == 0)
        {
            return SolveOperatingPoint(time);
        }

        var state = new DcNetlist(GroundNodeId);
        foreach (DcElement element in _elements)
        {
            CopyElement(state, element, time);
        }

        for (int index = 0; index < _capacitors.Count; index++)
        {
            TransientCapacitor capacitor = _capacitors[index];
            state.AddVoltageSource(
                capacitor.Id, capacitor.NodeA, capacitor.NodeB, capacitorVoltages[index]);
        }

        for (int index = 0; index < _inductors.Count; index++)
        {
            TransientInductor inductor = _inductors[index];
            state.AddCurrentSource(
                inductor.Id, inductor.NodeA, inductor.NodeB, inductorCurrents[index]);
        }

        return state.Solve();
    }

    /// <summary>
    /// Assembles one step as a purely resistive network of companion models. The
    /// companion conductances and history currents must all be finite; an extreme
    /// time step is reported as a numeric failure rather than handed to the solver
    /// as a non-physical coefficient.
    /// </summary>
    private bool TryBuildStepNetlist(
        double time,
        double timeStep,
        bool trapezoidal,
        ReactiveState state,
        out DcNetlist? netlist,
        out string? problem)
    {
        netlist = new DcNetlist(GroundNodeId);
        foreach (DcElement element in _elements)
        {
            CopyElement(netlist, element, time);
        }

        for (int index = 0; index < _capacitors.Count; index++)
        {
            TransientCapacitor capacitor = _capacitors[index];
            double conductance =
                ((trapezoidal ? 2 : 1) * capacitor.Farads) / timeStep;
            double history = (conductance * state.CapacitorVolts[index]) +
                (trapezoidal ? state.CapacitorAmps[index] : 0);
            if (!IsUsable(conductance) || !double.IsFinite(history))
            {
                problem =
                    $"Capacitor '{capacitor.Id}' has no finite companion model at a {timeStep} s step; increase the step or check the capacitance.";
                return false;
            }

            netlist.AddResistor(
                CompanionId(capacitor.Id, "g"), capacitor.NodeA, capacitor.NodeB, 1 / conductance);
            netlist.AddCurrentSource(
                CompanionId(capacitor.Id, "i"), capacitor.NodeB, capacitor.NodeA, history);
        }

        for (int index = 0; index < _inductors.Count; index++)
        {
            TransientInductor inductor = _inductors[index];
            double conductance =
                timeStep / ((trapezoidal ? 2 : 1) * inductor.Henries);
            double history = state.InductorAmps[index] +
                (trapezoidal ? conductance * state.InductorVolts[index] : 0);
            if (!IsUsable(conductance) || !double.IsFinite(history))
            {
                problem =
                    $"Inductor '{inductor.Id}' has no finite companion model at a {timeStep} s step; increase the step or check the inductance.";
                return false;
            }

            netlist.AddResistor(
                CompanionId(inductor.Id, "g"), inductor.NodeA, inductor.NodeB, 1 / conductance);
            netlist.AddCurrentSource(
                CompanionId(inductor.Id, "i"), inductor.NodeA, inductor.NodeB, history);
        }

        problem = null;
        return true;
    }

    /// <summary>The source value in effect at <paramref name="time"/>.</summary>
    private double ValueAt(DcElement element, double time) =>
        _waveforms.TryGetValue(element.Id, out TransientWaveform? waveform)
            ? waveform.ValueAt(time)
            : element.Value;

    /// <summary>
    /// Builds the energy ledger by integrating the reported element powers with the
    /// trapezoidal rule and comparing the result with the reactive state energy. The
    /// stored energy uses the same element voltages and currents the report shows, so
    /// a mismatch can only come from the integration, never from a different state.
    /// </summary>
    private TransientEnergyBalance? MeasureEnergy(IReadOnlyList<TransientPoint> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        double delivered = 0;
        double dissipated = 0;
        double firstStored = StoredEnergy(points[0]);
        for (int index = 1; index < points.Count; index++)
        {
            double span = points[index].Time - points[index - 1].Time;
            if (!(span > 0) || !double.IsFinite(span))
            {
                continue;
            }

            (double previousDelivered, double previousDissipated) = PowerFlow(points[index - 1]);
            (double currentDelivered, double currentDissipated) = PowerFlow(points[index]);
            delivered += span * Average(previousDelivered, currentDelivered);
            dissipated += span * Average(previousDissipated, currentDissipated);
        }

        double lastStored = StoredEnergy(points[^1]);
        double stored = lastStored - firstStored;
        return new TransientEnergyBalance(
            delivered,
            dissipated,
            stored,
            lastStored,
            delivered - dissipated - stored);
    }

    private static double Average(double left, double right)
    {
        double sum = left + right;
        return double.IsFinite(sum)
            ? sum / 2
            : (left / 2) + (right / 2);
    }

    /// <summary>
    /// Builds the per-node summary. Minima and maxima come from the same reported points
    /// the caller sees, and settling is measured against the node's own final value, so
    /// the numbers describe the run rather than a second, independent calculation.
    /// </summary>
    private static TransientMetrics? MeasureMetrics(
        TransientOptions options,
        IReadOnlyList<TransientPoint> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        double settlingBand = options.SettlingBand;
        string[] nodes = points
            .SelectMany(point => point.NodeVoltages.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var metrics = new List<TransientNodeMetrics>(nodes.Length);
        foreach (string node in nodes)
        {
            double minimum = double.PositiveInfinity;
            double maximum = double.NegativeInfinity;
            double peakTime = points[0].Time;
            foreach (TransientPoint point in points)
            {
                double volts = NodeVoltage(point.NodeVoltages, node);
                minimum = Math.Min(minimum, volts);
                if (volts > maximum)
                {
                    maximum = volts;
                    peakTime = point.Time;
                }
            }

            double final = NodeVoltage(points[^1].NodeVoltages, node);
            double tolerance = settlingBand * Math.Max(Math.Abs(final), maximum - minimum);
            double? settling = 0;
            if (tolerance > 0)
            {
                double lastOutside = 0;
                bool outside = false;
                foreach (TransientPoint point in points)
                {
                    if (Math.Abs(NodeVoltage(point.NodeVoltages, node) - final) > tolerance)
                    {
                        lastOutside = point.Time;
                        outside = true;
                    }
                }

                if (outside)
                {
                    settling = Math.Abs(NodeVoltage(points[^1].NodeVoltages, node) - final) > tolerance
                        ? null
                        : lastOutside;
                }
            }

            metrics.Add(new TransientNodeMetrics(
                node, minimum, maximum, final, peakTime, settling));
        }

        return new TransientMetrics(settlingBand, metrics);
    }

    /// <summary>Delivered source power and dissipated resistor/diode power at one point.</summary>
    private (double Delivered, double Dissipated) PowerFlow(TransientPoint point)
    {
        double delivered = 0;
        double dissipated = 0;
        foreach (DcElement element in _elements)
        {
            double power = point.ElementPowers[element.Id];
            if (element is DcResistor or DcDiode)
            {
                dissipated += power;
            }
            else
            {
                delivered -= power;
            }
        }

        return (delivered, dissipated);
    }

    /// <summary>Stored reactive energy <c>v²C/2 + i²L/2</c> at one point.</summary>
    private double StoredEnergy(TransientPoint point)
    {
        double stored = 0;
        foreach (TransientCapacitor capacitor in _capacitors)
        {
            double volts = point.ElementVoltages[capacitor.Id];
            stored += 0.5 * capacitor.Farads * volts * volts;
        }

        foreach (TransientInductor inductor in _inductors)
        {
            double amperes = point.ElementCurrents[inductor.Id];
            stored += 0.5 * inductor.Henries * amperes * amperes;
        }

        return stored;
    }

    /// <summary>
    /// Deterministic fingerprint of what this netlist will solve: the ground node, every element
    /// with its type, nodes and values, the reactive elements, their presets and any source
    /// waveform, each ordered by id so the fingerprint does not depend on construction order.
    /// It is a SHA-256 over that canonical text, shortened to 16 hex characters; changing any
    /// value changes it, and two netlists that solve the same equations share it.
    /// </summary>
    public string Fingerprint()
    {
        var text = new System.Text.StringBuilder();
        text.Append("ground:").Append(GroundNodeId).Append('\n');
        foreach (DcElement element in _elements.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            text.Append("element:").Append(element.Id).Append('|').Append(element.Kind)
                .Append('|').Append(element.NodeA).Append('|').Append(element.NodeB);
            switch (element)
            {
                case DcResistor resistor:
                    text.Append("|r|").Append(Number(resistor.Ohms));
                    break;
                case DcVoltageSource source:
                    text.Append("|v|").Append(Number(source.Value));
                    break;
                case DcCurrentSource source:
                    text.Append("|i|").Append(Number(source.Value));
                    break;
                case DcDiode diode:
                    text.Append("|d|").Append(Number(diode.SaturationCurrent))
                        .Append('|').Append(Number(diode.EmissionCoefficient))
                        .Append('|').Append(Number(diode.ThermalVoltage))
                        .Append('|').Append(Number(diode.SeriesResistance))
                        .Append('|').Append(diode.BreakdownVoltage is double bv ? Number(bv) : "-")
                        .Append('|').Append(diode.BreakdownCurrent is double bi ? Number(bi) : "-");
                    break;
                default:
                    text.Append("|?|").Append(Number(element.Value));
                    break;
            }

            text.Append('\n');
        }

        foreach (TransientCapacitor capacitor in _capacitors.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            text.Append("capacitor:").Append(capacitor.Id).Append('|').Append(capacitor.NodeA)
                .Append('|').Append(capacitor.NodeB).Append('|').Append(Number(capacitor.Farads))
                .Append('\n');
        }

        foreach (TransientInductor inductor in _inductors.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            text.Append("inductor:").Append(inductor.Id).Append('|').Append(inductor.NodeA)
                .Append('|').Append(inductor.NodeB).Append('|').Append(Number(inductor.Henries))
                .Append('\n');
        }

        foreach (KeyValuePair<string, double> preset in _initialCapacitorVolts.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.Append("preset-capacitor:").Append(preset.Key).Append('|')
                .Append(Number(preset.Value)).Append('\n');
        }

        foreach (KeyValuePair<string, double> preset in _initialInductorAmperes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.Append("preset-inductor:").Append(preset.Key).Append('|')
                .Append(Number(preset.Value)).Append('\n');
        }

        foreach (KeyValuePair<string, TransientWaveform> waveform in _waveforms.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.Append("waveform:").Append(waveform.Key);
            foreach (TransientWaveformPoint point in waveform.Value.Points)
            {
                text.Append('|').Append(Number(point.Time)).Append(':').Append(Number(point.Value));
            }

            text.Append('\n');
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string Number(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Reports a preset initial condition that disagrees with the circuit's own DC operating
    /// point, because the run then begins with a discontinuity instead of a steady start.
    /// Starting from a pre-charged capacitor is legitimate, so this is a note rather than a
    /// diagnostic: it tells the reader why the first samples move faster than the sources imply.
    /// </summary>
    private List<string> InitialConditionNotes(DcSolution operatingPoint)
    {
        var notes = new List<string>();
        foreach (TransientCapacitor capacitor in _capacitors)
        {
            if (!_initialCapacitorVolts.TryGetValue(capacitor.Id, out double preset))
            {
                continue;
            }

            double dc = VoltageAcross(operatingPoint.NodeVoltages, capacitor.NodeA, capacitor.NodeB);
            if (!StartsAwayFromDc(preset, dc))
            {
                continue;
            }

            notes.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"'{capacitor.Id}' starts at {preset:G6} V but its DC operating point is {dc:G6} V; the run begins with a {Math.Abs(preset - dc):G3} V discontinuity."));
        }

        foreach (TransientInductor inductor in _inductors)
        {
            if (!_initialInductorAmperes.TryGetValue(inductor.Id, out double preset))
            {
                continue;
            }

            double dc = operatingPoint.ElementCurrents.TryGetValue(inductor.Id, out double amperes)
                ? amperes
                : 0;
            if (!StartsAwayFromDc(preset, dc))
            {
                continue;
            }

            notes.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"'{inductor.Id}' starts at {preset:G6} A but its DC operating point is {dc:G6} A; the run begins with a {Math.Abs(preset - dc):G3} A discontinuity."));
        }

        return notes;
    }

    /// <summary>
    /// Whether a preset state differs from the DC value by more than rounding. The threshold is
    /// relative to the larger magnitude, so a 5 V circuit and a millivolt one are judged the
    /// same way, and an exact match is never reported.
    /// </summary>
    private static bool StartsAwayFromDc(double preset, double operatingPoint)
    {
        double scale = Math.Max(Math.Abs(preset), Math.Abs(operatingPoint));
        return scale > 0 &&
            Math.Abs(preset - operatingPoint) > Math.Max(scale * 1e-9, 1e-15);
    }

    private TransientPoint MakePoint(
        double time,
        DcSolution solution,
        ReactiveState state) =>
        MakePoint(
            time,
            solution.NodeVoltages,
            solution.ElementCurrents,
            state.CapacitorAmps,
            state.InductorAmps,
            solution.RelativeResidual);

    private TransientPoint MakePoint(
        double time,
        IReadOnlyDictionary<string, double> nodeVoltages,
        IReadOnlyDictionary<string, double> staticCurrents,
        double[] capacitorCurrents,
        double[] inductorCurrents,
        double relativeResidual)
    {
        int count = _elements.Count + _capacitors.Count + _inductors.Count;
        var voltages = new Dictionary<string, double>(count, StringComparer.Ordinal);
        var currents = new Dictionary<string, double>(count, StringComparer.Ordinal);
        var powers = new Dictionary<string, double>(count, StringComparer.Ordinal);

        foreach (DcElement element in _elements)
        {
            Record(
                element.Id,
                VoltageAcross(nodeVoltages, element.NodeA, element.NodeB),
                staticCurrents[element.Id]);
        }

        for (int index = 0; index < _capacitors.Count; index++)
        {
            TransientCapacitor capacitor = _capacitors[index];
            Record(
                capacitor.Id,
                VoltageAcross(nodeVoltages, capacitor.NodeA, capacitor.NodeB),
                capacitorCurrents[index]);
        }

        for (int index = 0; index < _inductors.Count; index++)
        {
            TransientInductor inductor = _inductors[index];
            Record(
                inductor.Id,
                VoltageAcross(nodeVoltages, inductor.NodeA, inductor.NodeB),
                inductorCurrents[index]);
        }

        return new TransientPoint(
            time, nodeVoltages, currents, voltages, powers, relativeResidual);

        void Record(string id, double volts, double amperes)
        {
            voltages[id] = volts;
            currents[id] = amperes;
            powers[id] = volts * amperes;
        }
    }

    private static bool IsUsable(double conductance) =>
        double.IsFinite(conductance) && conductance > 0 && double.IsFinite(1 / conductance);

    private static string CompanionId(string id, string suffix) =>
        $"{CompanionPrefix}{id}/{suffix}";

    private static double VoltageAcross(
        IReadOnlyDictionary<string, double> nodeVoltages,
        string nodeA,
        string nodeB) =>
        NodeVoltage(nodeVoltages, nodeA) - NodeVoltage(nodeVoltages, nodeB);

    private static double NodeVoltage(IReadOnlyDictionary<string, double> nodeVoltages, string node) =>
        nodeVoltages.TryGetValue(node, out double volts) ? volts : 0;

    private void CopyElement(DcNetlist target, DcElement element, double time)
    {
        switch (element)
        {
            case DcResistor resistor:
                target.AddResistor(resistor.Id, resistor.NodeA, resistor.NodeB, resistor.Ohms);
                break;
            case DcVoltageSource source:
                target.AddVoltageSource(source.Id, source.NodeA, source.NodeB, ValueAt(source, time));
                break;
            case DcCurrentSource source:
                target.AddCurrentSource(source.Id, source.NodeA, source.NodeB, ValueAt(source, time));
                break;
            case DcDiode diode:
                target.AddDiode(
                    diode.Id,
                    diode.NodeA,
                    diode.NodeB,
                    diode.SaturationCurrent,
                    diode.EmissionCoefficient,
                    diode.ThermalVoltage,
                    diode.SeriesResistance,
                    diode.BreakdownVoltage,
                    diode.BreakdownCurrent);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported transient element '{element.Id}' ({element.Kind}).");
        }
    }

    private TransientNetlist AddStatic(DcElement element)
    {
        _ids.Add(element.Id);
        _nodes.Add(element.NodeA);
        _nodes.Add(element.NodeB);
        _elements.Add(element);
        return this;
    }

    private void ValidateElement(string id, string nodeA, string nodeB)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Element ID must not be empty.", nameof(id));
        }

        if (id.StartsWith(CompanionPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Element ID '{id}' must not start with '{CompanionPrefix}'; that prefix is reserved for internal companion elements.",
                nameof(id));
        }

        if (_ids.Contains(id))
        {
            throw new ArgumentException($"Element ID '{id}' is already in this netlist.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(nodeA) || string.IsNullOrWhiteSpace(nodeB))
        {
            throw new ArgumentException("Element node IDs must not be empty.");
        }

        if (string.Equals(nodeA, nodeB, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Element '{id}' connects node '{nodeA}' to itself; two-terminal elements need two distinct nodes.");
        }
    }
}
