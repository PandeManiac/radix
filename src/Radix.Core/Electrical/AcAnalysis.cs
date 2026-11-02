using System.Globalization;
using System.Numerics;
using System.Text;

namespace Radix.Core.Electrical;

/// <summary>A frequency sweep for <see cref="AcAnalysis"/>.</summary>
public static class AcFrequencySweep
{
    /// <summary>Upper bound on points in one sweep, to keep a run bounded.</summary>
    public const int MaximumCount = 256;

    /// <summary>
    /// Evenly spaced points in log frequency, which is how a transfer function is read: a
    /// decade is the same width everywhere, so a pole shows up as a corner rather than as a
    /// feature squeezed against the left edge.
    /// </summary>
    public static IReadOnlyList<double> Logarithmic(double fromHertz, double toHertz, int count)
    {
        if (!double.IsFinite(fromHertz) || fromHertz <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fromHertz), fromHertz, "The start frequency must be finite and greater than zero.");
        }

        if (!double.IsFinite(toHertz) || toHertz < fromHertz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toHertz), toHertz, "The stop frequency must be finite and at least the start frequency.");
        }

        if (count < 2 || count > MaximumCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, $"A sweep needs between 2 and {MaximumCount} points.");
        }

        var frequencies = new double[count];
        double first = Math.Log(fromHertz);
        double span = Math.Log(toHertz) - first;
        for (int index = 0; index < count; index++)
        {
            frequencies[index] = Math.Exp(first + (span * index / (count - 1)));
        }

        return frequencies;
    }
}

/// <summary>Every node's phasor at one frequency, relative to a 1 V drive unless stated otherwise.</summary>
public sealed record AcPoint(double FrequencyHertz, IReadOnlyDictionary<string, Complex> NodeVoltages);

/// <summary>
/// One transistor at the operating point, as the small-signal analysis sees it. The gain in the
/// node table is assembled from exactly these numbers, so naming them lets a response be traced
/// back to a device instead of to an opaque matrix.
/// </summary>
public sealed record AcDeviceReport(
    string Id,
    string Region,
    double GateSourceVolts,
    double DrainSourceVolts,
    double CurrentAmperes,
    double Transconductance,
    double DrainConductance)
{
    /// <summary>|gm/gds|: the largest voltage gain this device could give with an ideal load.</summary>
    public double IntrinsicGain =>
        DrainConductance > 0
            ? Math.Abs(Transconductance / DrainConductance)
            : double.PositiveInfinity;
}

/// <summary>
/// The small-signal response of a circuit over frequency. Each node's phasor is its voltage for the
/// driven source's unit amplitude, so the ratio to 1 V is the transfer function from that source to
/// that node, phase included.
/// </summary>
public sealed record AcResponse(
    string DriveSourceId,
    double Amplitude,
    double BiasResidual,
    IReadOnlyList<AcPoint> Points)
{
    /// <summary>Each transistor's region and small-signal slopes at the operating point.</summary>
    public IReadOnlyList<AcDeviceReport> Devices { get; init; } = [];

    /// <summary>The non-ground node IDs, in ordinal order.</summary>
    public IReadOnlyList<string> Nodes =>
        Points.Count == 0
            ? []
            : Points[0].NodeVoltages.Keys.OrderBy(node => node, StringComparer.Ordinal).ToArray();

    /// <summary>A readable table of magnitude in decibels and phase in degrees.</summary>
    public string ToText()
    {
        IReadOnlyList<string> nodes = Nodes;
        var text = new StringBuilder();
        text.Append("AC analysis: drive '").Append(DriveSourceId).Append("' at ")
            .Append(Number(Amplitude)).Append(" V, ")
            .Append(Points.Count.ToString(CultureInfo.InvariantCulture))
            .Append(Points.Count == 1 ? " frequency" : " frequencies")
            .AppendLine();
        text.Append("  Hz");
        foreach (string node in nodes)
        {
            text.Append("  ").Append(node).Append(" dB").Append("  phase ").Append(node);
        }

        text.AppendLine();
        foreach (AcPoint point in Points)
        {
            text.Append("  ").Append(Number(point.FrequencyHertz));
            foreach (string node in nodes)
            {
                Complex value = point.NodeVoltages[node];
                text.Append("  ").Append(Number(value.Magnitude <= 0 ? -300 : 20 * Math.Log10(value.Magnitude)))
                    .Append("  ").Append(Number(PhaseDegrees(value)));
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Phase in degrees, measured from the positive real axis.</summary>
    public static double PhaseDegrees(Complex value) =>
        value.Magnitude == 0 ? 0 : Math.Atan2(value.Imaginary, value.Real) * (180 / Math.PI);

    private static string Number(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>
/// Small-signal frequency response around the circuit's DC operating point.
/// <para>
/// The operating point is the one the transient solver starts from — capacitors open, inductors
/// shorted — and it is linearized there: a diode becomes its junction conductance, a MOSFET its
/// drain conductance and gate transconductance, a capacitor <c>jωC</c>, an inductor
/// <c>1/(jωL)</c>. Independent sources are set to zero except the driven one, whose branch is
/// imposed at the requested amplitude, so each node's phasor is the transfer function from that
/// source.
/// </para>
/// <para>
/// This is a linearization, not a large-signal answer: it says how a small disturbance at the
/// operating point travels through the network, which is what a gain, a corner frequency or a
/// phase margin is about. Component capacitances participate, including the device capacitances a
/// MOSFET carries, so a transistor's own charge appears in its bandwidth.
/// </para>
/// </summary>
public static class AcAnalysis
{
    /// <summary>
    /// Solves the circuit at each frequency. <paramref name="driveSourceId"/> must name a voltage
    /// source; every other independent source is zeroed, as small-signal analysis requires.
    /// </summary>
    public static AcResponse Run(
        TransientNetlist netlist,
        string driveSourceId,
        IEnumerable<double> frequencies,
        double amplitude = 1)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(driveSourceId);
        ArgumentNullException.ThrowIfNull(frequencies);
        if (!double.IsFinite(amplitude) || amplitude == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amplitude), amplitude, "The drive amplitude must be finite and non-zero.");
        }

        DcVoltageSource drive = netlist.Elements
            .OfType<DcVoltageSource>()
            .FirstOrDefault(source => string.Equals(source.Id, driveSourceId, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"Netlist has no voltage source '{driveSourceId}' to drive.", nameof(driveSourceId));

        double[] sweep = frequencies.ToArray();
        if (sweep.Length == 0)
        {
            throw new ArgumentException("A sweep needs at least one frequency.", nameof(frequencies));
        }

        foreach (double frequency in sweep)
        {
            if (!double.IsFinite(frequency) || frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequencies), frequency, "Every frequency must be finite and greater than zero.");
            }
        }

        DcSolution bias = netlist.SolveDc();
        DcVoltageSource[] sources = netlist.Elements.OfType<DcVoltageSource>().ToArray();
        var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string node in netlist.Nodes)
        {
            if (!string.Equals(node, netlist.GroundNodeId, StringComparison.Ordinal))
            {
                nodeIndex[node] = nodeIndex.Count;
            }
        }

        // The bias fixes every device's region and slopes, so they are measured once here rather
        // than re-derived from the matrix later. A bias sweep reads the same numbers through the
        // same helper.
        IReadOnlyList<AcDeviceReport> devices = MosfetReports.Measure(netlist, bias);

        var sourceRow = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (DcVoltageSource source in sources)
        {
            sourceRow[source.Id] = nodeIndex.Count + sourceRow.Count;
        }

        int size = nodeIndex.Count + sources.Length;
        var points = new List<AcPoint>(sweep.Length);
        foreach (double frequency in sweep)
        {
            var matrix = new Complex[size, size];
            var rhs = new Complex[size];
            double omega = 2 * Math.PI * frequency;

            foreach (DcElement element in netlist.Elements)
            {
                switch (element)
                {
                    case DcResistor resistor:
                        StampAdmittance(1 / resistor.Ohms);
                        break;
                    case DcDiode diode:
                        StampAdmittance(diode.JunctionConductance(diode.JunctionVoltageFor(
                            NodeBias(bias, diode.NodeA) - NodeBias(bias, diode.NodeB))));
                        break;
                    case DcMosfet mosfet:
                        StampMosfet(mosfet);
                        break;
                }

                void StampAdmittance(double conductance)
                {
                    Stamp(element.NodeA, element.NodeA, conductance);
                    Stamp(element.NodeB, element.NodeB, conductance);
                    Stamp(element.NodeA, element.NodeB, -conductance);
                    Stamp(element.NodeB, element.NodeA, -conductance);
                }
            }

            foreach (TransientCapacitor capacitor in netlist.CapacitorsIncludingDevices)
            {
                double susceptance = omega * capacitor.Farads;
                Stamp(capacitor.NodeA, capacitor.NodeA, new Complex(0, susceptance));
                Stamp(capacitor.NodeB, capacitor.NodeB, new Complex(0, susceptance));
                Stamp(capacitor.NodeA, capacitor.NodeB, new Complex(0, -susceptance));
                Stamp(capacitor.NodeB, capacitor.NodeA, new Complex(0, -susceptance));
            }

            foreach (TransientInductor inductor in netlist.Inductors)
            {
                Complex admittance = Complex.One / new Complex(0, omega * inductor.Henries);
                Stamp(inductor.NodeA, inductor.NodeA, admittance);
                Stamp(inductor.NodeB, inductor.NodeB, admittance);
                Stamp(inductor.NodeA, inductor.NodeB, -admittance);
                Stamp(inductor.NodeB, inductor.NodeA, -admittance);
            }

            foreach (DcVoltageSource source in sources)
            {
                int row = sourceRow[source.Id];
                int a = Index(source.NodeA);
                int b = Index(source.NodeB);
                StampRow(a, row, Complex.One);
                StampRow(b, row, -Complex.One);
                StampRow(row, a, Complex.One);
                StampRow(row, b, -Complex.One);
                rhs[row] = string.Equals(source.Id, drive.Id, StringComparison.Ordinal)
                    ? amplitude
                    : Complex.Zero;
            }

            Complex[] solution = SolveComplex(matrix, rhs, frequency);
            var voltages = new Dictionary<string, Complex>(nodeIndex.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> entry in nodeIndex)
            {
                voltages[entry.Key] = solution[entry.Value];
            }

            points.Add(new AcPoint(frequency, voltages));

            void StampMosfet(DcMosfet mosfet)
            {
                double drainSource = NodeBias(bias, mosfet.Drain) - NodeBias(bias, mosfet.Source);
                double gateSource = NodeBias(bias, mosfet.Gate) - NodeBias(bias, mosfet.Source);
                DcMosfetOperatingPoint point = mosfet.Evaluate(drainSource, gateSource);
                double conductance = point.DrainConductance;
                double transconductance = point.GateTransconductance;

                Stamp(mosfet.Drain, mosfet.Drain, conductance);
                Stamp(mosfet.Source, mosfet.Source, conductance);
                Stamp(mosfet.Drain, mosfet.Source, -conductance);
                Stamp(mosfet.Source, mosfet.Drain, -conductance);
                Stamp(mosfet.Drain, mosfet.Gate, transconductance);
                Stamp(mosfet.Source, mosfet.Gate, -transconductance);
                Stamp(mosfet.Drain, mosfet.Source, -transconductance);
                Stamp(mosfet.Source, mosfet.Source, transconductance);
            }

            void Stamp(string nodeA, string nodeB, Complex value) =>
                StampRow(Index(nodeA), Index(nodeB), value);

            void StampRow(int row, int column, Complex value)
            {
                if (row >= 0 && column >= 0)
                {
                    matrix[row, column] += value;
                }
            }

            int Index(string node) =>
                string.Equals(node, netlist.GroundNodeId, StringComparison.Ordinal)
                    ? -1
                    : nodeIndex[node];
        }

        return new AcResponse(drive.Id, amplitude, bias.RelativeResidual, points) { Devices = devices };
    }

    private static double NodeBias(DcSolution bias, string node) => MosfetReports.NodeBias(bias, node);

    /// <summary>
    /// Solves the complex system by Gaussian elimination with partial pivoting. A pivot is judged
    /// against the largest magnitude in the matrix rather than against zero, because the entries
    /// span a decade per reactance and a truly singular system leaves a pivot many orders below
    /// everything else.
    /// </summary>
    private static Complex[] SolveComplex(Complex[,] matrix, Complex[] rhs, double frequency)
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
                scale = Math.Max(scale, matrix[row, column].Magnitude);
            }
        }

        const double relativePivotFloor = 1e-14;
        for (int column = 0; column < size; column++)
        {
            int pivot = -1;
            double best = 0;
            for (int row = column; row < size; row++)
            {
                double magnitude = matrix[row, column].Magnitude;
                if (magnitude > best)
                {
                    best = magnitude;
                    pivot = row;
                }
            }

            if (pivot < 0 || best <= scale * relativePivotFloor)
            {
                throw new DcSolveException(
                    DcSolveFailure.SingularMatrix,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The AC system at {frequency:G6} Hz is singular at column {column}: its largest pivot is {best:G6} beside a matrix scale of {scale:G6}. Check for a floating node or a redundant source loop."));
            }

            if (pivot != column)
            {
                for (int index = column; index < size; index++)
                {
                    (matrix[column, index], matrix[pivot, index]) = (matrix[pivot, index], matrix[column, index]);
                }

                (rhs[column], rhs[pivot]) = (rhs[pivot], rhs[column]);
            }

            Complex diagonal = matrix[column, column];
            for (int row = column + 1; row < size; row++)
            {
                Complex factor = matrix[row, column] / diagonal;
                if (factor == Complex.Zero)
                {
                    continue;
                }

                matrix[row, column] = Complex.Zero;
                for (int index = column + 1; index < size; index++)
                {
                    matrix[row, index] -= factor * matrix[column, index];
                }

                rhs[row] -= factor * rhs[column];
            }
        }

        var solution = new Complex[size];
        for (int row = size - 1; row >= 0; row--)
        {
            Complex sum = rhs[row];
            for (int column = row + 1; column < size; column++)
            {
                sum -= matrix[row, column] * solution[column];
            }

            solution[row] = sum / matrix[row, row];
        }

        return solution;
    }
}
