using System.Globalization;

namespace Radix.Core.Electrical;

/// <summary>The kind of a two-terminal element in a DC linear netlist.</summary>
public enum DcElementKind
{
    Resistor,
    VoltageSource,
    CurrentSource,
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
}

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
    /// Solves the DC operating point with modified nodal analysis. The matrix is
    /// assembled from node-voltage unknowns plus one branch-current unknown per
    /// independent voltage source.
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

        string[] sortedNodes = _nodes.OrderBy(node => node, StringComparer.Ordinal).ToArray();
        EnsureNoFloatingIslands(sortedNodes);
        EnsureVoltageSourceConsistency();

        string[] nodeIds = sortedNodes
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

        (double[] solution, double relativeResidual) = SolveLinearSystem(matrix, rhs);
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
    /// Rejects a netlist whose graph is not a single ground-referenced component.
    /// A floating island makes the node-voltage system underdetermined; naming the
    /// island is more useful than a generic singular-matrix message.
    /// </summary>
    private void EnsureNoFloatingIslands(IReadOnlyList<string> sortedNodes)
    {
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
        if (floating.Length == 0)
        {
            return;
        }

        const int shown = 5;
        string names = string.Join(", ", floating.Take(shown).Select(node => $"'{node}'"));
        if (floating.Length > shown)
        {
            names += $", and {floating.Length - shown} more";
        }

        throw new DcSolveException(
            DcSolveFailure.FloatingNetwork,
            $"Nodes {names} are not connected to the ground node '{GroundNodeId}' by any element; a floating network has no unique DC operating point.");
    }

    /// <summary>
    /// Checks that every ideal voltage source in a connected source group agrees with
    /// the voltage differences implied by the other sources in that group. A
    /// contradiction has no solution; a consistent but redundant loop still leaves the
    /// branch currents underdetermined and is reported later as a singular system.
    /// </summary>
    private void EnsureVoltageSourceConsistency()
    {
        var adjacency = new Dictionary<string, List<SourceEdge>>(StringComparer.Ordinal);
        foreach (DcVoltageSource source in _elements.OfType<DcVoltageSource>())
        {
            ConnectSource(adjacency, source.NodeA, source.NodeB, -source.Volts, source.Id);
            ConnectSource(adjacency, source.NodeB, source.NodeA, source.Volts, source.Id);
        }

        var potentials = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (DcVoltageSource source in _elements.OfType<DcVoltageSource>())
        {
            if (potentials.ContainsKey(source.NodeA))
            {
                continue;
            }

            potentials[source.NodeA] = 0;
            var pending = new Queue<string>();
            pending.Enqueue(source.NodeA);
            while (pending.Count > 0)
            {
                string node = pending.Dequeue();
                if (!adjacency.TryGetValue(node, out List<SourceEdge>? edges))
                {
                    continue;
                }

                foreach (SourceEdge edge in edges)
                {
                    double candidate = potentials[node] + edge.Delta;
                    if (potentials.TryGetValue(edge.Node, out double existing))
                    {
                        double tolerance = Math.Max(
                            1e-9 * Math.Max(Math.Abs(existing), Math.Abs(candidate)),
                            1e-15);
                        if (Math.Abs(existing - candidate) > tolerance)
                        {
                            throw new DcSolveException(
                                DcSolveFailure.InconsistentSourceConstraints,
                                $"Ideal voltage source '{edge.SourceId}' conflicts with the voltage differences imposed by the other sources at node '{edge.Node}' ({FormatVolts(existing)} V versus {FormatVolts(candidate)} V); the ideal-source constraints have no solution.");
                        }
                    }
                    else
                    {
                        potentials[edge.Node] = candidate;
                        pending.Enqueue(edge.Node);
                    }
                }
            }
        }
    }

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
    /// Backward error of the solved linear system: <c>max|Ax-b|</c> divided by the
    /// combined magnitude of the right-hand side and the matrix-vector product.
    /// Values near double precision indicate that the reported operating point
    /// satisfies the assembled circuit equations; they are not a forward-error bound.
    /// </summary>
    public double RelativeResidual { get; init; }
}
