using Radix.Core.Electrical;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// A bias sweep reports regions and slopes across the input range. The quantitative check is the
/// square law it is built on: in saturation gm = K·(Vgs − Vth), so a sweep that reports anything
/// else is describing a different device from the one the solver stamped.
/// </summary>
public sealed class MosfetBiasSweepTests
{
    private static TransientNetlist CommonSource() =>
        new TransientNetlist("gnd")
            .AddVoltageSource("VDD", "vdd", "gnd", 5)
            .AddVoltageSource("VIN", "in", "gnd", 2)
            .AddResistor("RD", "vdd", "out", 1_000)
            .AddMosfet("M1", "out", "in", "gnd", DcMosfetChannel.NChannel, 1, 1e-3);

    [Fact]
    public void TheTransconductanceFollowsTheSquareLawAcrossTheSweep()
    {
        // Vth = 1 V and K = 1 mA/V^2, so the overdrives 0.5, 1 and 1.5 V give exactly 0.5, 1 and
        // 1.5 mA/V. Every point stays saturated, which is what makes the closed form exact rather
        // than approximate.
        IReadOnlyList<MosfetBiasPoint> points = MosfetBiasSweep.Run(
            CommonSource(), "VIN", [1.5, 2, 2.5]);

        Assert.Equal(3, points.Count);
        double[] expected = [5e-4, 1e-3, 1.5e-3];
        for (int index = 0; index < points.Count; index++)
        {
            AcDeviceReport device = Assert.Single(points[index].Devices);
            Assert.Equal(1.5 + (index * 0.5), points[index].BiasVolts, 12);
            Assert.Equal(expected[index], device.Transconductance, 12);
            Assert.Equal("Saturation", device.Region);
        }
    }

    [Fact]
    public void TheTernaryGateSweepFindsTheOffDeviceAndTheDrivingOne()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        CircuitProject project = ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"),
            validate: false);
        TransientNetlist netlist = Assert.Single(project.ElectricalCircuits).ToTransientNetlist();

        IReadOnlyList<MosfetBiasPoint> points = MosfetBiasSweep.Run(
            netlist, "VIN", MosfetBiasSweep.Linear(0, 5, 6));

        Assert.Equal(6, points.Count);
        Assert.All(points, point => Assert.Equal(4, point.Devices.Count));

        // At 0 V every device is off; by 2 V the output-driving n-channel device is on and carries
        // a real transconductance. Its region at that point is triode, not saturation, because its
        // drain is the out_low node pinned near the rail - so the assertion is that the sweep moved
        // the device out of cutoff and gave it a slope, not that it guessed a region in advance.
        AcDeviceReport atRest = points[0].Devices.Single(device => device.Id == "MNL");
        Assert.Equal("Cutoff", atRest.Region);
        Assert.InRange(atRest.Transconductance, -1e-15, 1e-15);

        AcDeviceReport onTransition = points[2].Devices.Single(device => device.Id == "MNL");
        Assert.NotEqual("Cutoff", onTransition.Region);
        Assert.True(onTransition.Transconductance > 1e-6, $"gm {onTransition.Transconductance}");
        Assert.True(
            Math.Abs(onTransition.Transconductance) > 1e3 * Math.Abs(atRest.Transconductance),
            $"on {onTransition.Transconductance} against rest {atRest.Transconductance}");

        // The drive really is being stepped: the bias column is the swept value, once each.
        Assert.Equal(
            [0d, 1d, 2d, 3d, 4d, 5d],
            points.Select(point => point.BiasVolts).ToArray());
    }

    [Fact]
    public void AnUnknownDriveIsRejected()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => MosfetBiasSweep.Run(CommonSource(), "NOPE", [0, 1]));

        Assert.Contains("NOPE", exception.Message, StringComparison.Ordinal);
        Assert.Contains("voltage source", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOrUnboundedSweepIsRejected()
    {
        Assert.Throws<ArgumentException>(() => MosfetBiasSweep.Run(CommonSource(), "VIN", []));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MosfetBiasSweep.Run(CommonSource(), "VIN", MosfetBiasSweep.Linear(0, 5, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MosfetBiasSweep.Run(CommonSource(), "VIN", [double.NaN]));
    }
}
