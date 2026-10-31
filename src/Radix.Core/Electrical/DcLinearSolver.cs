using System.Globalization;

namespace Radix.Core.Electrical;

/// <summary>The kind of a two-terminal element in a DC netlist.</summary>
public enum DcElementKind
{
    Resistor,
    VoltageSource,
    CurrentSource,
    Diode,
}

/// <summary>
/// A two-terminal DC element. <see cref="NodeA"/> is the positive/reference terminal
/// for source voltages and element currents. The current reported for every element
/// flows through the element from <see cref="NodeA"/> to <see cref="NodeB"/>.
/// </summary>
public abstract record DcElement(string Id, string NodeA, string NodeB)
{
    public abstract DcElementKind Kind { get; }

    /// <summary>The element's resistance, voltage, or current in its natural unit.</summary>
    public abstract double Value { get; }

    public abstract string Unit { get; }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Id} ({Kind}, {NodeA}->{NodeB}, {Value} {Unit})");
}

/// <summary>A linear resistor with a finite, positive resistance.</summary>
public sealed record DcResistor(string Id, string NodeA, string NodeB, double Ohms)
    : DcElement(Id, NodeA, NodeB)
{
    public override DcElementKind Kind => DcElementKind.Resistor;

    public override double Value => Ohms;

    public override string Unit => "ohm";
}

/// <summary>An ideal independent voltage source enforcing <c>V(NodeA) - V(NodeB) = Volts</c>.</summary>
public sealed record DcVoltageSource(string Id, string NodeA, string NodeB, double Volts)
    : DcElement(Id, NodeA, NodeB)
{
    public override DcElementKind Kind => DcElementKind.VoltageSource;

    public override double Value => Volts;

    public override string Unit => "V";
}

/// <summary>
/// An ideal independent current source forcing <see cref="Amperes"/> through the
/// source from <c>NodeA</c> to <c>NodeB</c>. It therefore draws current from NodeA
/// and injects it into NodeB.
/// </summary>
public sealed record DcCurrentSource(string Id, string NodeA, string NodeB, double Amperes)
    : DcElement(Id, NodeA, NodeB)
{
    public override DcElementKind Kind => DcElementKind.CurrentSource;

    public override double Value => Amperes;

    public override string Unit => "A";
}

/// <summary>
/// A two-terminal Shockley diode: <c>I = Is * (exp(V / (N * Vt)) - 1)</c>, with
/// current flowing from <see cref="DcElement.NodeA"/> (anode) to
/// <see cref="DcElement.NodeB"/> (cathode). Reverse breakdown, junction
/// capacitance, series resistance, and self-heating are not modelled.
/// </summary>
/// <remarks>
/// <see cref="DefaultThermalVoltage"/> is <c>kT/q</c> at 300.15 K (25.852 mV).
/// The exponential argument is capped at 80 so arithmetic stays finite; that cap
/// is reached around 2 V per junction for <c>N = 1</c>, far beyond a realistic
/// forward drop without series resistance, and the model must not be used there.
/// </remarks>
public sealed record DcDiode(
    string Id,
    string NodeA,
    string NodeB,
    double SaturationCurrent,
    double EmissionCoefficient,
    double ThermalVoltage)
    : DcElement(Id, NodeA, NodeB)
{
    public const double DefaultThermalVoltage = 0.025852;

    private const double MaximumExponentialArgument = 80;

    public override DcElementKind Kind => DcElementKind.Diode;

    /// <summary>The saturation current in amperes.</summary>
    public override double Value => SaturationCurrent;

    public override string Unit => "A";

    /// <summary>The junction voltage scale <c>N * Vt</c> in volts.</summary>
    public double JunctionVoltage => EmissionCoefficient * ThermalVoltage;

    public double CurrentAt(double volts) =>
        SaturationCurrent * (Math.Exp(ExponentialArgument(volts)) - 1);

    public double ConductanceAt(double volts) =>
        (SaturationCurrent / JunctionVoltage) * Math.Exp(ExponentialArgument(volts));

    private double ExponentialArgument(double volts) =>
        Math.Min(volts / JunctionVoltage, MaximumExponentialArgument);
}

public enum DcSolveFailure
{
    NoGround,
    SingularMatrix,
    NonFiniteResult,

    /// <summary>Some nodes are not connected to the ground reference by any element.</summary>
    FloatingNetwork,

    /// <summary>Ideal voltage sources impose mutually contradictory voltage differences.</summary>
    InconsistentSourceConstraints,

    /// <summary>The linear system solved, but its residual is too large to trust the result.</summary>
    IllConditioned,

    /// <summary>Newton iteration did not reach a consistent nonlinear operating point.</summary>
    NonConvergent,
}

/// <summary>Why a DC netlist cannot have a unique, physically meaningful solution.</summary>
public enum DcTopologyIssueKind
{
    /// <summary>One or more nodes have no conductive path to the ground reference.</summary>
    FloatingNodes,

    /// <summary>Ideal voltage sources impose mutually contradictory voltage differences.</summary>
    ContradictoryVoltageSources,

    /// <summary>Ideal voltage sources form a consistent loop whose branch currents are not unique.</summary>
    RedundantVoltageSourceLoop,
}

/// <summary>
/// A topology finding independent of any numerical solve. Nodes and source IDs are
/// stable identifiers from the netlist that produced the issue.
/// </summary>
public sealed record DcTopologyIssue(
    DcTopologyIssueKind Kind,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> SourceIds,
    string Message);

/// <summary>Thrown when a DC netlist has no well-defined linear operating point.</summary>
public sealed class DcSolveException : Exception
{
    public DcSolveException(DcSolveFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public DcSolveFailure Failure { get; }
}

/// <summary>
/// A mutable netlist for the independent DC linear solver. Nodes are created by
/// element endpoints; <see cref="GroundNodeId"/> is the zero-volt reference and is
/// not part of the solved node-voltage unknowns.
/// </summary>
public sealed class DcNetlist
{
    private readonly List<DcElement> _elements = [];
    private readonly HashSet<string> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public DcNetlist(string groundNodeId = "0")
    {
        if (string.IsNullOrWhiteSpace(groundNodeId))
        {
            throw new ArgumentException("Ground node ID must not be empty.", nameof(groundNodeId));
        }

        GroundNodeId = groundNodeId;
        _nodes.Add(groundNodeId);
    }

    public string GroundNodeId { get; }

    public IReadOnlyList<DcElement> Elements => _elements;

    /// <summary>All referenced nodes, including ground, in ordinal order.</summary>
    public IReadOnlyList<string> Nodes =>
        _nodes.OrderBy(node => node, StringComparer.Ordinal).ToArray();

    public DcNetlist AddResistor(string id, string nodeA, string nodeB, double ohms)
    {
        ValidateElement(id, nodeA, nodeB);
        if (!double.IsFinite(ohms) || ohms <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ohms), ohms, "Resistance must be finite and greater than zero.");
        }

        if (!double.IsFinite(1.0 / ohms))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ohms), ohms, "Resistance is too small to produce a finite conductance.");
        }

        return Add(new DcResistor(id, nodeA, nodeB, ohms));
    }

    public DcNetlist AddVoltageSource(string id, string nodeA, string nodeB, double volts)
    {
        ValidateElement(id, nodeA, nodeB);
        if (!double.IsFinite(volts))
        {
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "Voltage must be finite.");
        }

        return Add(new DcVoltageSource(id, nodeA, nodeB, volts));
    }

    public DcNetlist AddCurrentSource(string id, string nodeA, string nodeB, double amperes)
    {
        ValidateElement(id, nodeA, nodeB);
        if (!double.IsFinite(amperes))
        {
            throw new ArgumentOutOfRangeException(nameof(amperes), amperes, "Current must be finite.");
        }

        return Add(new DcCurrentSource(id, nodeA, nodeB, amperes));
    }

    /// <summary>
    /// Adds a Shockley diode. <paramref name="saturationCurrent"/> is in amperes,
    /// <paramref name="emissionCoefficient"/> is dimensionless, and
    /// <paramref name="thermalVoltage"/> is in volts.
    /// </summary>
    public DcNetlist AddDiode(
        string id,
        string nodeA,
        string nodeB,
        double saturationCurrent,
        double emissionCoefficient = 1.0,
        double thermalVoltage = DcDiode.DefaultThermalVoltage)
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

        return Add(new DcDiode(
            id, nodeA, nodeB, saturationCurrent, emissionCoefficient, thermalVoltage));
    }

    /// <summary>
    /// Solves the DC operating point with modified nodal analysis. Linear netlists
    /// use one factorization; netlists containing diodes use a damped Newton
    /// iteration with junction-voltage limiting.
    /// </summary>
    public DcSolution Solve()
    {
        if (_elements.Count == 0)
        {
            return EmptySolution();
        }

        if (!_elements.Any(element =>
                string.Equals(element.NodeA, GroundNodeId, StringComparison.Ordinal) ||
                string.Equals(element.NodeB, GroundNodeId, StringComparison.Ordinal)))
        {
            throw new DcSolveException(
                DcSolveFailure.NoGround,
                $"No element references the ground node '{GroundNodeId}'; the DC operating point has no voltage reference.");
        }

        foreach (DcTopologyIssue issue in AnalyzeTopology())
        {
            throw issue.Kind switch
            {
                DcTopologyIssueKind.FloatingNodes => new DcSolveException(
                    DcSolveFailure.FloatingNetwork,
                    issue.Message),
                DcTopologyIssueKind.ContradictoryVoltageSources => new DcSolveException(
                    DcSolveFailure.InconsistentSourceConstraints,
                    issue.Message),
                DcTopologyIssueKind.RedundantVoltageSourceLoop => new DcSolveException(
                    DcSolveFailure.SingularMatrix,
                    issue.Message),
                _ => new DcSolveException(DcSolveFailure.SingularMatrix, issue.Message),
            };
        }

        string[] nodeIds = Nodes
            .Where(node => !string.Equals(node, GroundNodeId, StringComparison.Ordinal))
            .ToArray();
        var nodeIndex = new Dictionary<string, int>(nodeIds.Length, StringComparer.Ordinal);
        for (int index = 0; index < nodeIds.Length; index++)
        {
            nodeIndex[nodeIds[index]] = index;
        }

        DcVoltageSource[] voltageSources = _elements.OfType<DcVoltageSource>().ToArray();
        int size = nodeIds.Length + voltageSources.Length;
        var matrix = new double[size, size];
        var rhs = new double[size];
        foreach (DcElement element in _elements)
        {
            switch (element)
            {
                case DcResistor resistor:
                    StampResistor(matrix, nodeIndex, resistor);
                    break;
                case DcCurrentSource currentSource:
                    StampCurrentSource(rhs, nodeIndex, currentSource);
                    break;
            }
        }

        var voltageSourceRows = new Dictionary<string, int>(voltageSources.Length, StringComparer.Ordinal);
        for (int index = 0; index < voltageSources.Length; index++)
        {
            DcVoltageSource source = voltageSources[index];
            int row = nodeIds.Length + index;
            voltageSourceRows[source.Id] = row;
            StampVoltageSource(matrix, rhs, nodeIndex, source, row);
        }

        DcDiode[] diodes = _elements.OfType<DcDiode>().ToArray();
        return diodes.Length == 0
            ? SolveLinear(nodeIds, nodeIndex, voltageSources, voltageSourceRows, matrix, rhs)
            : SolveNonlinear(
                nodeIds,
                nodeIndex,
                voltageSources,
                voltageSourceRows,
                matrix,
                rhs,
                diodes);
    }

    private DcSolution SolveLinear(
        string[] nodeIds,
        Dictionary<string, int> nodeIndex,
        DcVoltageSource[] voltageSources,
        Dictionary<string, int> voltageSourceRows,
        double[,] matrix,
        double[] rhs)
    {
        (double[] solution, double relativeResidual) = SolveLinearSystem(matrix, rhs);
        return BuildSolution(
            nodeIds,
            nodeIndex,
            voltageSources,
            voltageSourceRows,
            solution,
            relativeResidual);
    }

    /// <summary>
    /// Newton iteration over the diode junction voltages. Each iteration linearizes
    /// every diode around its current junction voltage, solves the resulting linear
    /// MNA system, limits a large forward jump (the standard pn-junction limiting
    /// used to keep the exponential from overshooting), and stops only when the
    /// solution is self-consistent and the nonlinear KCL/branch residual is small.
    /// </summary>
    private DcSolution SolveNonlinear(
        string[] nodeIds,
        Dictionary<string, int> nodeIndex,
        DcVoltageSource[] voltageSources,
        Dictionary<string, int> voltageSourceRows,
        double[,] baseMatrix,
        double[] baseRhs,
        DcDiode[] diodes)
    {
        const int maxIterations = 100;
        const double voltageTolerance = 1e-9;

        int size = baseRhs.Length;
        var diodeVoltages = new double[diodes.Length];
        var nextDiodeVoltages = new double[diodes.Length];
        var previous = new double[size];
        var solution = new double[size];
        double residual = double.PositiveInfinity;

        for (int iteration = 1; iteration <= maxIterations; iteration++)
        {
            var matrix = (double[,])baseMatrix.Clone();
            var rhs = (double[])baseRhs.Clone();
            for (int index = 0; index < diodes.Length; index++)
            {
                StampDiode(matrix, rhs, nodeIndex, diodes[index], diodeVoltages[index]);
            }

            (solution, _) = SolveLinearSystem(matrix, rhs);

            double maxChange = 0;
            double maxValue = 0;
            for (int index = 0; index < size; index++)
            {
                maxChange = Math.Max(maxChange, Math.Abs(solution[index] - previous[index]));
                maxValue = Math.Max(maxValue, Math.Abs(solution[index]));
            }

            double maxLimitError = 0;
            for (int index = 0; index < diodes.Length; index++)
            {
                double raw = VoltageAcross(solution, nodeIndex, diodes[index].NodeA, diodes[index].NodeB);
                double limited = LimitJunction(raw, diodeVoltages[index], diodes[index]);
                maxLimitError = Math.Max(maxLimitError, Math.Abs(raw - limited));
                maxChange = Math.Max(maxChange, Math.Abs(limited - diodeVoltages[index]));
                maxValue = Math.Max(maxValue, Math.Abs(limited));
                nextDiodeVoltages[index] = limited;
            }

            residual = NonlinearResidual(nodeIndex, voltageSources, voltageSourceRows, solution);
            double tolerance = voltageTolerance * Math.Max(1, maxValue);
            if (maxChange <= tolerance &&
                maxLimitError <= tolerance &&
                residual <= 1)
            {
                return BuildSolution(
                    nodeIds,
                    nodeIndex,
                    voltageSources,
                    voltageSourceRows,
                    solution,
                    residual);
            }

            Array.Copy(solution, previous, size);
            Array.Copy(nextDiodeVoltages, diodeVoltages, diodes.Length);
        }

        throw new DcSolveException(
            DcSolveFailure.NonConvergent,
            $"The nonlinear DC operating point did not converge after {maxIterations} Newton iterations (last normalized residual {residual.ToString("G6", CultureInfo.InvariantCulture)}); check diode parameters, series resistance, and whether the operating point is physically bounded.");
    }

    private static void StampDiode(
        double[,] matrix,
        double[] rhs,
        Dictionary<string, int> nodeIndex,
        DcDiode diode,
        double volts)
    {
        double conductance = diode.ConductanceAt(volts);
        double equivalentCurrent = diode.CurrentAt(volts) - (conductance * volts);
        int nodeA = IndexOf(nodeIndex, diode.NodeA);
        int nodeB = IndexOf(nodeIndex, diode.NodeB);
        Add(matrix, nodeA, nodeA, conductance);
        Add(matrix, nodeB, nodeB, conductance);
        Add(matrix, nodeA, nodeB, -conductance);
        Add(matrix, nodeB, nodeA, -conductance);
        if (nodeA >= 0)
        {
            rhs[nodeA] -= equivalentCurrent;
        }

        if (nodeB >= 0)
        {
            rhs[nodeB] += equivalentCurrent;
        }
    }

    /// <summary>
    /// Standard pn-junction limiting. A forward jump larger than two junction
    /// voltages is compressed logarithmically so Newton cannot overshoot into the
    /// exponential's overflow region; a solution only counts as converged once the
    /// limit no longer changes the junction voltage.
    /// </summary>
    private static double LimitJunction(double vnew, double vold, DcDiode diode)
    {
        double junctionVoltage = diode.JunctionVoltage;
        double critical = junctionVoltage / (Math.Sqrt(2) * diode.SaturationCurrent);
        if (critical <= 1)
        {
            return vnew;
        }

        double vcrit = junctionVoltage * Math.Log(critical);
        if (vnew <= vcrit || Math.Abs(vnew - vold) <= 2 * junctionVoltage)
        {
            return vnew;
        }

        if (vold > 0)
        {
            double argument = 1 + ((vnew - vold) / junctionVoltage);
            return argument > 0
                ? vold + (junctionVoltage * Math.Log(argument))
                : vcrit;
        }

        return junctionVoltage * Math.Log(vnew / junctionVoltage);
    }

    /// <summary>
    /// Residual violation ratio of the actual nonlinear equations: KCL at every
    /// non-ground node and every ideal-source branch equation. Each row is compared
    /// with a relative tolerance plus an absolute floor, because subtracting nearly
    /// equal node voltages can leave a KCL error larger than a purely relative
    /// picoamp threshold can express. A ratio at or below 1 is acceptable.
    /// </summary>
    private double NonlinearResidual(
        Dictionary<string, int> nodeIndex,
        DcVoltageSource[] voltageSources,
        Dictionary<string, int> voltageSourceRows,
        double[] solution)
    {
        var residual = new double[solution.Length];
        double currentScale = 1e-12;
        double voltageScale = 1;

        foreach (DcElement element in _elements)
        {
            double voltage = VoltageAcross(solution, nodeIndex, element.NodeA, element.NodeB);
            switch (element)
            {
                case DcResistor resistor:
                {
                    double current = voltage / resistor.Ohms;
                    AddNodeCurrent(residual, nodeIndex, element.NodeA, element.NodeB, current);
                    currentScale = Math.Max(currentScale, Math.Abs(current));
                    break;
                }

                case DcCurrentSource currentSource:
                    AddNodeCurrent(
                        residual,
                        nodeIndex,
                        element.NodeA,
                        element.NodeB,
                        currentSource.Amperes);
                    currentScale = Math.Max(currentScale, Math.Abs(currentSource.Amperes));
                    break;

                case DcDiode diode:
                {
                    double current = diode.CurrentAt(voltage);
                    AddNodeCurrent(residual, nodeIndex, element.NodeA, element.NodeB, current);
                    currentScale = Math.Max(currentScale, Math.Abs(current));
                    break;
                }

                case DcVoltageSource source:
                {
                    int row = voltageSourceRows[source.Id];
                    double branchCurrent = solution[row];
                    AddNodeCurrent(
                        residual,
                        nodeIndex,
                        element.NodeA,
                        element.NodeB,
                        branchCurrent);
                    residual[row] = voltage - source.Volts;
                    currentScale = Math.Max(currentScale, Math.Abs(branchCurrent));
                    voltageScale = Math.Max(voltageScale, Math.Max(Math.Abs(voltage), Math.Abs(source.Volts)));
                    break;
                }
            }
        }

        const double currentTolerance = 1e-9;
        const double voltageTolerance = 1e-9;
        const double absoluteCurrentFloor = 1e-15;
        const double absoluteVoltageFloor = 1e-12;

        double worst = 0;
        double allowedCurrent = (currentTolerance * currentScale) + absoluteCurrentFloor;
        for (int index = 0; index < nodeIndex.Count; index++)
        {
            worst = Math.Max(worst, Math.Abs(residual[index]) / allowedCurrent);
        }

        double allowedVoltage = (voltageTolerance * voltageScale) + absoluteVoltageFloor;
        foreach (DcVoltageSource source in voltageSources)
        {
            int row = voltageSourceRows[source.Id];
            worst = Math.Max(worst, Math.Abs(residual[row]) / allowedVoltage);
        }

        return worst;
    }

    private static void AddNodeCurrent(
        double[] target,
        Dictionary<string, int> nodeIndex,
        string nodeA,
        string nodeB,
        double current)
    {
        int indexA = IndexOf(nodeIndex, nodeA);
        int indexB = IndexOf(nodeIndex, nodeB);
        if (indexA >= 0)
        {
            target[indexA] += current;
        }

        if (indexB >= 0)
        {
            target[indexB] -= current;
        }
    }

    private static double VoltageAcross(
        double[] solution,
        Dictionary<string, int> nodeIndex,
        string nodeA,
        string nodeB) =>
        (nodeIndex.TryGetValue(nodeA, out int indexA) ? solution[indexA] : 0) -
        (nodeIndex.TryGetValue(nodeB, out int indexB) ? solution[indexB] : 0);

    private DcSolution BuildSolution(
        string[] nodeIds,
        Dictionary<string, int> nodeIndex,
        DcVoltageSource[] voltageSources,
        Dictionary<string, int> voltageSourceRows,
        double[] solution,
        double relativeResidual)
    {
        var voltages = new Dictionary<string, double>(nodeIndex.Count + 1, StringComparer.Ordinal)
        {
            [GroundNodeId] = 0,
        };
        for (int index = 0; index < nodeIds.Length; index++)
        {
            voltages[nodeIds[index]] = solution[index];
        }

        var currents = new Dictionary<string, double>(_elements.Count, StringComparer.Ordinal);
        var elementVoltages = new Dictionary<string, double>(_elements.Count, StringComparer.Ordinal);
        var powers = new Dictionary<string, double>(_elements.Count, StringComparer.Ordinal);
        foreach (DcElement element in _elements)
        {
            double voltage = voltages[element.NodeA] - voltages[element.NodeB];
            double current = element switch
            {
                DcResistor resistor => voltage / resistor.Ohms,
                DcCurrentSource currentSource => currentSource.Amperes,
                DcVoltageSource => solution[voltageSourceRows[element.Id]],
                DcDiode diode => diode.CurrentAt(voltage),
                _ => throw new InvalidOperationException($"Unsupported DC element '{element.Id}'."),
            };

            if (!double.IsFinite(voltage) || !double.IsFinite(current))
            {
                throw new DcSolveException(
                    DcSolveFailure.NonFiniteResult,
                    $"Solving element '{element.Id}' produced a non-finite voltage or current.");
            }

            elementVoltages[element.Id] = voltage;
            currents[element.Id] = current;
            powers[element.Id] = voltage * current;
        }

        return new DcSolution(voltages, currents, elementVoltages, powers)
        {
            RelativeResidual = relativeResidual,
        };
    }

    /// <summary>
    /// Finds topology problems that no choice of node voltages can satisfy. The
    /// solver consumes the blocking issues; electrical rule checking can report them
    /// before a solve. One analysis keeps the solver and the rule checker from
    /// disagreeing about connectivity.
    /// </summary>
    public IReadOnlyList<DcTopologyIssue> AnalyzeTopology()
    {
        var issues = new List<DcTopologyIssue>();
        string[] sortedNodes = _nodes.OrderBy(node => node, StringComparer.Ordinal).ToArray();

        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (DcElement element in _elements)
        {
            Connect(adjacency, element.NodeA, element.NodeB);
            Connect(adjacency, element.NodeB, element.NodeA);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { GroundNodeId };
        var pending = new Queue<string>();
        pending.Enqueue(GroundNodeId);
        while (pending.Count > 0)
        {
            string node = pending.Dequeue();
            if (!adjacency.TryGetValue(node, out List<string>? neighbors))
            {
                continue;
            }

            foreach (string neighbor in neighbors)
            {
                if (visited.Add(neighbor))
                {
                    pending.Enqueue(neighbor);
                }
            }
        }

        string[] floating = sortedNodes
            .Where(node => !visited.Contains(node))
            .ToArray();
        if (floating.Length > 0)
        {
            issues.Add(new DcTopologyIssue(
                DcTopologyIssueKind.FloatingNodes,
                floating,
                [],
                FloatingMessage(floating)));
        }

        var sourceAdjacency = new Dictionary<string, List<SourceEdge>>(StringComparer.Ordinal);
        foreach (DcVoltageSource source in _elements.OfType<DcVoltageSource>())
        {
            ConnectSource(sourceAdjacency, source.NodeA, source.NodeB, -source.Volts, source.Id);
            ConnectSource(sourceAdjacency, source.NodeB, source.NodeA, source.Volts, source.Id);
        }

        var potentials = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (DcVoltageSource source in _elements.OfType<DcVoltageSource>())
        {
            if (potentials.ContainsKey(source.NodeA))
            {
                continue;
            }

            potentials[source.NodeA] = 0;
            var groupNodes = new HashSet<string>(StringComparer.Ordinal) { source.NodeA };
            var groupSources = new HashSet<string>(StringComparer.Ordinal);
            var groupPending = new Queue<string>();
            bool conflict = false;
            groupPending.Enqueue(source.NodeA);
            while (!conflict && groupPending.Count > 0)
            {
                string node = groupPending.Dequeue();
                if (!sourceAdjacency.TryGetValue(node, out List<SourceEdge>? edges))
                {
                    continue;
                }

                foreach (SourceEdge edge in edges)
                {
                    groupSources.Add(edge.SourceId);
                    double candidate = potentials[node] + edge.Delta;
                    if (potentials.TryGetValue(edge.Node, out double existing))
                    {
                        if (Math.Abs(existing - candidate) > VoltageTolerance(existing, candidate))
                        {
                            issues.Add(new DcTopologyIssue(
                                DcTopologyIssueKind.ContradictoryVoltageSources,
                                [edge.Node],
                                [edge.SourceId],
                                $"Ideal voltage source '{edge.SourceId}' conflicts with the voltage differences imposed by the other sources at node '{edge.Node}' ({FormatVolts(existing)} V versus {FormatVolts(candidate)} V); the ideal-source constraints have no solution."));
                            conflict = true;
                            break;
                        }

                        continue;
                    }

                    potentials[edge.Node] = candidate;
                    groupNodes.Add(edge.Node);
                    groupPending.Enqueue(edge.Node);
                }
            }

            if (!conflict && groupSources.Count >= groupNodes.Count)
            {
                string[] sources = groupSources.Order(StringComparer.Ordinal).ToArray();
                issues.Add(new DcTopologyIssue(
                    DcTopologyIssueKind.RedundantVoltageSourceLoop,
                    groupNodes.Order(StringComparer.Ordinal).ToArray(),
                    sources,
                    $"Ideal voltage sources {FormatIds(sources)} form a consistent but redundant loop; the node voltages are determined, but the individual source currents are not unique."));
            }
        }

        return issues;
    }

    private static string FloatingMessage(IReadOnlyList<string> floating)
    {
        const int shown = 5;
        string names = string.Join(", ", floating.Take(shown).Select(node => $"'{node}'"));
        if (floating.Count > shown)
        {
            names += $", and {floating.Count - shown} more";
        }

        return $"Nodes {names} are not connected to a ground node by any element; a floating network has no unique DC operating point.";
    }

    private static string FormatIds(IReadOnlyList<string> ids) =>
        string.Join(", ", ids.Select(id => $"'{id}'"));

    private static double VoltageTolerance(double left, double right) =>
        Math.Max(1e-9 * Math.Max(Math.Abs(left), Math.Abs(right)), 1e-15);

    private static void Connect(Dictionary<string, List<string>> adjacency, string from, string to)
    {
        if (!adjacency.TryGetValue(from, out List<string>? neighbors))
        {
            neighbors = [];
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
    }

    private static void ConnectSource(
        Dictionary<string, List<SourceEdge>> adjacency,
        string from,
        string to,
        double delta,
        string sourceId)
    {
        if (!adjacency.TryGetValue(from, out List<SourceEdge>? edges))
        {
            edges = [];
            adjacency[from] = edges;
        }

        edges.Add(new SourceEdge(to, delta, sourceId));
    }

    private static string FormatVolts(double volts) =>
        volts.ToString("G12", CultureInfo.InvariantCulture);

    private readonly record struct SourceEdge(string Node, double Delta, string SourceId);

    private DcNetlist Add(DcElement element)
    {
        if (!_ids.Add(element.Id))
        {
            throw new ArgumentException($"Element ID '{element.Id}' is already in this netlist.", nameof(element));
        }

        _nodes.Add(element.NodeA);
        _nodes.Add(element.NodeB);
        _elements.Add(element);
        return this;
    }

    private static void ValidateElement(string id, string nodeA, string nodeB)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Element ID must not be empty.", nameof(id));
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

    private static void StampResistor(
        double[,] matrix,
        Dictionary<string, int> nodeIndex,
        DcResistor resistor)
    {
        int nodeA = IndexOf(nodeIndex, resistor.NodeA);
        int nodeB = IndexOf(nodeIndex, resistor.NodeB);
        double conductance = 1.0 / resistor.Ohms;
        Add(matrix, nodeA, nodeA, conductance);
        Add(matrix, nodeB, nodeB, conductance);
        Add(matrix, nodeA, nodeB, -conductance);
        Add(matrix, nodeB, nodeA, -conductance);
    }

    private static void StampCurrentSource(
        double[] rhs,
        Dictionary<string, int> nodeIndex,
        DcCurrentSource source)
    {
        int nodeA = IndexOf(nodeIndex, source.NodeA);
        int nodeB = IndexOf(nodeIndex, source.NodeB);
        if (nodeA >= 0)
        {
            rhs[nodeA] -= source.Amperes;
        }

        if (nodeB >= 0)
        {
            rhs[nodeB] += source.Amperes;
        }
    }

    private static void StampVoltageSource(
        double[,] matrix,
        double[] rhs,
        Dictionary<string, int> nodeIndex,
        DcVoltageSource source,
        int row)
    {
        int nodeA = IndexOf(nodeIndex, source.NodeA);
        int nodeB = IndexOf(nodeIndex, source.NodeB);
        if (nodeA >= 0)
        {
            matrix[nodeA, row] += 1;
            matrix[row, nodeA] += 1;
        }

        if (nodeB >= 0)
        {
            matrix[nodeB, row] -= 1;
            matrix[row, nodeB] -= 1;
        }

        rhs[row] = source.Volts;
    }

    private static int IndexOf(Dictionary<string, int> nodeIndex, string nodeId) =>
        nodeIndex.TryGetValue(nodeId, out int index) ? index : -1;

    private static void Add(double[,] matrix, int row, int column, double value)
    {
        if (row >= 0 && column >= 0)
        {
            matrix[row, column] += value;
        }
    }

    /// <summary>
    /// Solves the dense modified-nodal system with scaled partial pivoting. Pivot
    /// selection compares each candidate with the magnitude of its own row, so a weak
    /// but well-defined branch is not mistaken for a singular system just because
    /// another part of the circuit uses much larger conductances. The returned residual
    /// is the backward error of the computed solution, <c>max|Ax-b|</c> relative to the
    /// magnitudes involved.
    /// </summary>
    private static (double[] Solution, double RelativeResidual) SolveLinearSystem(
        double[,] matrix,
        double[] rhs)
    {
        int size = rhs.Length;
        if (size == 0)
        {
            return ([], 0);
        }

        // The original system is kept for the residual check after elimination.
        var original = (double[,])matrix.Clone();
        var originalRhs = (double[])rhs.Clone();
        var rowScale = new double[size];
        double rhsScale = 0;
        double matrixScale = 0;
        for (int row = 0; row < size; row++)
        {
            double scale = 0;
            for (int column = 0; column < size; column++)
            {
                double magnitude = Math.Abs(matrix[row, column]);
                if (!double.IsFinite(magnitude))
                {
                    throw new DcSolveException(
                        DcSolveFailure.NonFiniteResult,
                        "The DC linear system contains a non-finite coefficient.");
                }

                scale = Math.Max(scale, magnitude);
            }

            rowScale[row] = scale;
            matrixScale = Math.Max(matrixScale, scale);
            rhsScale = Math.Max(rhsScale, Math.Abs(rhs[row]));
        }

        // A pivot is only treated as zero when it is negligible compared with its own
        // row. The floor is far above double precision (2.2e-16) but far below any
        // physically meaningful conductance ratio.
        const double relativePivotFloor = 1e-12;
        for (int column = 0; column < size; column++)
        {
            int pivotRow = -1;
            double bestRatio = 0;
            for (int row = column; row < size; row++)
            {
                if (rowScale[row] <= 0)
                {
                    continue;
                }

                double ratio = Math.Abs(matrix[row, column]) / rowScale[row];
                if (ratio > bestRatio)
                {
                    bestRatio = ratio;
                    pivotRow = row;
                }
            }

            if (pivotRow < 0 ||
                Math.Abs(matrix[pivotRow, column]) <= rowScale[pivotRow] * relativePivotFloor)
            {
                throw new DcSolveException(
                    DcSolveFailure.SingularMatrix,
                    "The DC linear system is singular: check for an open current-source path, an undriven node, or a redundant loop of ideal voltage sources.");
            }

            if (pivotRow != column)
            {
                for (int index = column; index < size; index++)
                {
                    (matrix[column, index], matrix[pivotRow, index]) =
                        (matrix[pivotRow, index], matrix[column, index]);
                }

                (rhs[column], rhs[pivotRow]) = (rhs[pivotRow], rhs[column]);
                (rowScale[column], rowScale[pivotRow]) = (rowScale[pivotRow], rowScale[column]);
            }

            double pivot = matrix[column, column];
            for (int row = column + 1; row < size; row++)
            {
                double factor = matrix[row, column] / pivot;
                if (factor == 0)
                {
                    continue;
                }

                matrix[row, column] = 0;
                for (int index = column + 1; index < size; index++)
                {
                    matrix[row, index] -= factor * matrix[column, index];
                }

                rhs[row] -= factor * rhs[column];
            }
        }

        double[] solution = SolveUpperTriangular(matrix, rhs);
        double solutionScale = solution.Max(Math.Abs);
        double maxResidual = MaxResidual(original, originalRhs, solution);
        double denominator = Math.Max(rhsScale, matrixScale * solutionScale);
        double relativeResidual = denominator > 0 ? maxResidual / denominator : maxResidual;
        if (!double.IsFinite(relativeResidual) || relativeResidual > 1e-8)
        {
            throw new DcSolveException(
                DcSolveFailure.IllConditioned,
                $"The DC linear system has a relative residual of {relativeResidual.ToString("G6", CultureInfo.InvariantCulture)}, so the computed operating point cannot be trusted; check for nearly redundant sources or extreme parameter ratios.");
        }

        return (solution, relativeResidual);
    }

    private static double[] SolveUpperTriangular(double[,] matrix, double[] rhs)
    {
        int size = rhs.Length;
        var solution = new double[size];
        for (int row = size - 1; row >= 0; row--)
        {
            double sum = rhs[row];
            for (int column = row + 1; column < size; column++)
            {
                sum -= matrix[row, column] * solution[column];
            }

            solution[row] = sum / matrix[row, row];
            if (!double.IsFinite(solution[row]))
            {
                throw new DcSolveException(
                    DcSolveFailure.NonFiniteResult,
                    "The DC linear system produced a non-finite node voltage or branch current.");
            }
        }

        return solution;
    }

    private static double MaxResidual(double[,] matrix, double[] rhs, double[] solution)
    {
        double maxResidual = 0;
        for (int row = 0; row < rhs.Length; row++)
        {
            double sum = 0;
            for (int column = 0; column < rhs.Length; column++)
            {
                sum += matrix[row, column] * solution[column];
            }

            maxResidual = Math.Max(maxResidual, Math.Abs(sum - rhs[row]));
        }

        return maxResidual;
    }

    private DcSolution EmptySolution()
    {
        return new DcSolution(
            new Dictionary<string, double>(StringComparer.Ordinal) { [GroundNodeId] = 0 },
            new Dictionary<string, double>(StringComparer.Ordinal),
            new Dictionary<string, double>(StringComparer.Ordinal),
            new Dictionary<string, double>(StringComparer.Ordinal))
        {
            RelativeResidual = 0,
        };
    }
}

/// <summary>
/// Solved DC operating point. Element currents flow from NodeA to NodeB through the
/// element; element power is absorbed power (<c>V * I</c>), so a negative source
/// power means the source delivers energy to the network.
/// </summary>
public sealed record DcSolution(
    IReadOnlyDictionary<string, double> NodeVoltages,
    IReadOnlyDictionary<string, double> ElementCurrents,
    IReadOnlyDictionary<string, double> ElementVoltages,
    IReadOnlyDictionary<string, double> ElementPowers)
{
    /// <summary>
    /// For a linear netlist, the backward error of the solved system: <c>max|Ax-b|</c>
    /// divided by the combined magnitude of the right-hand side and the matrix-vector
    /// product. For a netlist containing diodes, the largest nonlinear KCL or
    /// ideal-source residual as a fraction of its acceptance bound; a value at or
    /// below 1 means the Newton iteration converged. Neither quantity is a
    /// forward-error bound.
    /// </summary>
    public double RelativeResidual { get; init; }
}
