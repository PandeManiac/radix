using Radix.Core.Electrical;
using Radix.Core.Serialization;
using Radix.Editor.ViewModels;
using Xunit;

namespace Radix.Editor.Tests;

/// <summary>
/// The authored circuit can ask for the bias-dependent gate model, and the inspector is where that
/// is asked for. The test measures the difference on the ternary inverter rather than trusting the
/// flag: a model that changes nothing would pass a flag round-trip and fail here.
/// </summary>
public sealed class BiasDependentCapacitanceAuthoredTests
{
    private static EditorViewModel TernaryInverter()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        EditorViewModel viewModel = new(ProjectSerializer.Load(
            Path.Combine(directory!.FullName, "examples", "transient-ternary-inverter.radix"),
            validate: false));

        Assert.True(viewModel.HasSelectedElectricalCircuit);
        return viewModel;
    }

    private static TransientResult Run(ElectricalCircuit circuit) => circuit.ToTransientNetlist()
        .Run(new TransientOptions { StartTime = 0, StopTime = 120e-9, Steps = 240 });

    [Fact]
    public void TheInspectorTurnsTheModelOnForADevice()
    {
        EditorViewModel viewModel = TernaryInverter();
        viewModel.SelectedElectricalComponent =
            viewModel.ElectricalComponents.Single(item => item.Id == "MNL");

        EditableField field = viewModel.ElectricalComponentFields
            .Single(entry => entry.Label == "Bias-dependent C");
        Assert.Equal("no", field.Value);

        field.Value = "yes";
        Assert.True(viewModel.ApplyField(field));
        Assert.True(viewModel.SelectedElectricalCircuitModel!.FindComponent("MNL")!.BiasDependentCapacitance);

        // Anything but a clear yes or no leaves the device alone rather than guessing.
        field.Value = "maybe";
        viewModel.ApplyField(field);
        Assert.True(viewModel.SelectedElectricalCircuitModel!.FindComponent("MNL")!.BiasDependentCapacitance);
    }

    [Fact]
    public void TheAuthoredCircuitReallyChangesTheRun()
    {
        EditorViewModel viewModel = TernaryInverter();
        ElectricalCircuit circuit = viewModel.SelectedElectricalCircuitModel!;

        TransientResult constant = Run(circuit);

        foreach (ElectricalComponent component in circuit.Components
                     .Where(component => component.Kind == ElectricalComponentKind.Mosfet))
        {
            component.BiasDependentCapacitance = true;
        }

        TransientResult biased = Run(circuit);

        Assert.Equal(constant.Points.Count, biased.Points.Count);

        // The gate bus is charged through the devices' own gate capacitance, so re-evaluating that
        // capacitance during the switching event must move it. A difference of at least a millivolt
        // is a real change, not rounding: the waveform samples are the same times.
        double largestDifference = 0;
        for (int index = 0; index < constant.Points.Count; index++)
        {
            largestDifference = Math.Max(
                largestDifference,
                Math.Abs(constant.Points[index].NodeVoltages["g"] - biased.Points[index].NodeVoltages["g"]));
        }

        Assert.True(largestDifference > 1e-3, $"gate moved at most {largestDifference} V");

        // And it moves in the direction the model predicts: less gate charge, so the gate gets
        // further in the same time.
        int midpoint = constant.Points.Count / 4;
        Assert.True(
            biased.Points[midpoint].NodeVoltages["g"] > constant.Points[midpoint].NodeVoltages["g"],
            $"biased {biased.Points[midpoint].NodeVoltages["g"]} V against constant {constant.Points[midpoint].NodeVoltages["g"]} V");
    }
}
