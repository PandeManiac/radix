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

        string[] nodeIds = _nodes
            .Where(node => !string.Equals(node, GroundNodeId, StringComparison.Ordinal))
            .OrderBy(node => node, StringComparer.Ordinal)
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

        double[] solution = SolveLinearSystem(matrix, rhs);
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

        return new DcSolution(voltages, currents, elementVoltages, powers);
    }

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

    private static double[] SolveLinearSystem(double[,] matrix, double[] rhs)
    {
        int size = rhs.Length;
        if (size == 0)
        {
            return [];
        }

        double scale = 0;
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                scale = Math.Max(scale, Math.Abs(matrix[row, column]));
            }
        }

        double tolerance = Math.Max(scale * 1e-12, 1e-15);
        for (int column = 0; column < size; column++)
        {
            int pivotRow = column;
            double pivotMagnitude = Math.Abs(matrix[column, column]);
            for (int row = column + 1; row < size; row++)
            {
                double candidate = Math.Abs(matrix[row, column]);
                if (candidate > pivotMagnitude)
                {
                    pivotMagnitude = candidate;
                    pivotRow = row;
                }
            }

            if (pivotMagnitude <= tolerance)
            {
                throw new DcSolveException(
                    DcSolveFailure.SingularMatrix,
                    "The DC linear system is singular: check for a floating node, an open current-source path, or a loop of ideal voltage sources.");
            }

            if (pivotRow != column)
            {
                for (int index = column; index < size; index++)
                {
                    (matrix[column, index], matrix[pivotRow, index]) =
                        (matrix[pivotRow, index], matrix[column, index]);
                }

                (rhs[column], rhs[pivotRow]) = (rhs[pivotRow], rhs[column]);
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

    private DcSolution EmptySolution()
    {
        return new DcSolution(
            new Dictionary<string, double>(StringComparer.Ordinal) { [GroundNodeId] = 0 },
            new Dictionary<string, double>(StringComparer.Ordinal),
            new Dictionary<string, double>(StringComparer.Ordinal),
            new Dictionary<string, double>(StringComparer.Ordinal));
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
    IReadOnlyDictionary<string, double> ElementPowers);
