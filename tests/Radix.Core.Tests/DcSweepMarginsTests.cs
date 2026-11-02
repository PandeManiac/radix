using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// The windows a level can occupy come from the measured curve: a circuit holds a level where its
/// gain is below one and amplifies between unity-gain crossings. These check the three cases that
/// matter — a circuit that never amplifies, a binary inverter with one forbidden band, and the
/// ternary inverter with three windows.
/// </summary>
public sealed class DcSweepMarginsTests
{
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private static DcNetlist Divider() =>
        new DcNetlist("gnd")
            .AddVoltageSource("V1", "in", "gnd", 0)
            .AddResistor("R1", "in", "out", 1_000)
            .AddResistor("R2", "out", "gnd", 1_000);

    private static DcNetlist CmosInverter() =>
        new DcNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VIN", "in", "gnd", 0)
            .AddMosfet("MP", "out", "in", "vdd", DcMosfetChannel.PChannel, 1, 1e-3, 0.02)
            .AddMosfet("MN", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-3, 0.02);

    [Fact]
    public void ADividerNeverAmplifiesSoTheWholeRangeIsOneWindow()
    {
        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(Divider(), "V1", DcSweep.Linspace(0, 10, 5));
        IReadOnlyList<DcSweepGainPoint> gains = DcSweepGain.Along(rows, "out");

        Assert.Empty(DcSweepMargins.UnityGainCrossings(gains));

        DcInputWindow window = Assert.Single(DcSweepMargins.Windows(gains));
        Assert.Equal(0, window.From, 12);
        Assert.Equal(10, window.To, 12);
        Assert.Equal(10, window.Width, 12);
    }

    [Fact]
    public void TheInverterHasOneForbiddenBandAndALegalBandAtEachRail()
    {
        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(CmosInverter(), "VIN", DcSweep.Linspace(0, 5, 64));
        IReadOnlyList<DcSweepGainPoint> gains = DcSweepGain.Along(rows, "out");

        IReadOnlyList<double> crossings = DcSweepMargins.UnityGainCrossings(gains);
        Assert.Equal(2, crossings.Count);
        Assert.InRange(crossings[0], 2.0, 2.5);
        Assert.InRange(crossings[1], 2.5, 3.0);
        Assert.True(crossings[0] < crossings[1], "crossings must be ordered");

        IReadOnlyList<DcInputWindow> windows = DcSweepMargins.Windows(gains);
        Assert.Equal(2, windows.Count);
        Assert.Equal(0, windows[0].From, 12);
        Assert.Equal(crossings[0], windows[0].To, 12);
        Assert.Equal(crossings[1], windows[1].From, 12);
        Assert.Equal(5, windows[1].To, 12);
    }

    [Fact]
    public void TheTernaryInverterHasThreeWindowsAndAMiddleOneTwoVoltsWide()
    {
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(RepositoryRoot(), "examples", "dc-ternary-inverter.radix"));
        ElectricalCircuit circuit = Assert.Single(project.ElectricalCircuits);
        DcNetlist netlist = circuit.ToDcNetlist();

        IReadOnlyList<DcSweepRow> rows = DcSweep.Run(netlist, "VIN", DcSweep.Linspace(0, 5, 64));
        IReadOnlyList<DcSweepGainPoint> gains = DcSweepGain.Along(rows, "out");

        // Two transitions, each with a crossing either side of its steep region.
        IReadOnlyList<double> crossings = DcSweepMargins.UnityGainCrossings(gains);
        Assert.Equal(4, crossings.Count);
        Assert.All(
            new[] { 0, 1, 2 },
            index => Assert.True(crossings[index] < crossings[index + 1], "crossings must be ordered"));

        IReadOnlyList<DcInputWindow> windows = DcSweepMargins.Windows(gains);
        Assert.Equal(3, windows.Count);
        Assert.Equal(0, windows[0].From, 12);
        Assert.Equal(5, windows[^1].To, 12);

        // The middle window is the ternary level's usable band, and it is measured, not assumed.
        Assert.True(windows[1].Width > 2.0, $"middle window {windows[1].From}..{windows[1].To}");
    }
}
