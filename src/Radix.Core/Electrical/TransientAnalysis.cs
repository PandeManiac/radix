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
        return left.Value +
            ((right.Value - left.Value) * ((time - left.Time) / (right.Time - left.Time)));
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

    /// <summary>Integration method. The first step always uses backward Euler.</summary>
    public TransientMethod Method { get; init; } = TransientMethod.BackwardEuler;

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

        if (!double.IsFinite(TimeStep) || TimeStep <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Steps),
                Steps,
                "The requested time range and step count do not produce a finite positive time step.");
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
        double seriesResistance = 0)
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

        return AddStatic(new DcDiode(
            id,
            nodeA,
            nodeB,
            saturationCurrent,
            emissionCoefficient,
            thermalVoltage,
            seriesResistance));
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

        double timeStep = options.TimeStep;
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

        var points = new List<TransientPoint>(options.Steps + 1)
        {
            MakePoint(options.StartTime, initialState, capacitorCurrents, inductorCurrents),
        };

        for (int step = 1; step <= options.Steps; step++)
        {
            double time = step == options.Steps ? options.StopTime : options.StartTime + (step * timeStep);
            bool trapezoidal = options.Method == TransientMethod.Trapezoidal && step > 1;
            if (!TryBuildStepNetlist(
                    time,
                    timeStep,
                    trapezoidal,
                    capacitorVoltages,
                    capacitorCurrents,
                    inductorVoltages,
                    inductorCurrents,
                    out DcNetlist? stepNetlist,
                    out string? companionProblem))
            {
                return new TransientResult(
                    options, points, DcSolveFailure.NonFiniteResult, companionProblem, step);
            }

            DcSolution solution;
            try
            {
                solution = stepNetlist!.Solve();
            }
            catch (DcSolveException exception)
            {
                return new TransientResult(options, points, exception.Failure, exception.Message, step);
            }

            for (int index = 0; index < _capacitors.Count; index++)
            {
                TransientCapacitor capacitor = _capacitors[index];
                double volts = VoltageAcross(
                    solution.NodeVoltages, capacitor.NodeA, capacitor.NodeB);
                double current = trapezoidal
                    ? ((2 * capacitor.Farads / timeStep) * (volts - capacitorVoltages[index])) -
                      capacitorCurrents[index]
                    : (capacitor.Farads / timeStep) * (volts - capacitorVoltages[index]);
                if (!double.IsFinite(volts) || !double.IsFinite(current))
                {
                    return NonFiniteAt(step, capacitor.Id);
                }

                capacitorVoltages[index] = volts;
                capacitorCurrents[index] = current;
            }

            for (int index = 0; index < _inductors.Count; index++)
            {
                TransientInductor inductor = _inductors[index];
                double volts = VoltageAcross(
                    solution.NodeVoltages, inductor.NodeA, inductor.NodeB);
                double current = trapezoidal
                    ? inductorCurrents[index] +
                      ((timeStep / (2 * inductor.Henries)) * (volts + inductorVoltages[index]))
                    : inductorCurrents[index] + ((timeStep / inductor.Henries) * volts);
                if (!double.IsFinite(volts) || !double.IsFinite(current))
                {
                    return NonFiniteAt(step, inductor.Id);
                }

                inductorVoltages[index] = volts;
                inductorCurrents[index] = current;
            }

            points.Add(MakePoint(time, solution, capacitorCurrents, inductorCurrents));
        }

        return new TransientResult(options, points, null, null, null);

        TransientResult NonFiniteAt(int failedStep, string elementId) =>
            new(
                options,
                points,
                DcSolveFailure.NonFiniteResult,
                $"Element '{elementId}' produced a non-finite voltage or current at transient step {failedStep}; check the element value and the time step.",
                failedStep);
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
        double[] capacitorVoltages,
        double[] capacitorCurrents,
        double[] inductorVoltages,
        double[] inductorCurrents,
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
            double history = (conductance * capacitorVoltages[index]) +
                (trapezoidal ? capacitorCurrents[index] : 0);
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
            double history = inductorCurrents[index] +
                (trapezoidal ? conductance * inductorVoltages[index] : 0);
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

    private TransientPoint MakePoint(
        double time,
        DcSolution solution,
        double[] capacitorCurrents,
        double[] inductorCurrents) =>
        MakePoint(
            time,
            solution.NodeVoltages,
            solution.ElementCurrents,
            capacitorCurrents,
            inductorCurrents,
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
                    diode.SeriesResistance);
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
