using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Radix.Core.Testing;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class SimulationScriptTests
{
    public static TheoryData<string, string> ShippedScripts => new()
    {
        { "dff-demo.radix", "dff-captures.radixtest" },
        { "balanced-ternary-adder-2.radix", "balanced-ternary-adder-2.radixtest" },
        { "ternary-counter.radix", "ternary-counter.radixtest" },
        { "ternary-adder-2.radix", "ternary-adder-2.radixtest" },
        { "ternary-accumulator.radix", "ternary-accumulator.radixtest" },
        { "ternary-machine.radix", "ternary-machine.radixtest" },
        { "ternary-branch-machine.radix", "ternary-branch-machine.radixtest" },
        { "ternary-call-machine.radix", "ternary-call-machine.radixtest" },
        { "ternary-memory.radix", "ternary-memory.radixtest" },
        { "ternary-stack-machine.radix", "ternary-stack-machine.radixtest" },
        { "four-state-bus.radix", "four-state-bus.radixtest" },
        { "balanced-ternary-accumulator.radix", "balanced-ternary-accumulator.radixtest" },
        { "binary-counter.radix", "counter-counts.radixtest" },
        { "accumulator-cpu.radix", "accumulator.radixtest" },
        { "tiny-cpu.radix", "tiny-cpu.radixtest" },
    };

    [Theory]
    [MemberData(nameof(ShippedScripts))]
    public void ShippedExampleScriptsPass(string projectFile, string scriptFile)
    {
        string root = RepositoryRoot();
        CircuitProject project = ProjectSerializer.Load(Path.Combine(root, "examples", projectFile));
        SimulationScript script = SimulationScript.Load(Path.Combine(root, "examples", "tests", scriptFile));

        SimulationScriptResult result = SimulationScriptRunner.Run(project, script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
    }

    [Fact]
    public void MissingAtInheritsThePreviousTick()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "name": "ticks",
              "steps": [
                { "set": { "clk": 0 } },
                { "at": 5 },
                { "expect": { "clk": 0 } }
              ]
            }
            """);

        Assert.Equal([0L, 5L, 5L], script.Steps.Select(step => step.At).ToArray());
        Assert.Equal("ticks", script.Name);
    }

    [Fact]
    public void RejectsUnsupportedVersion()
    {
        SimulationScriptException exception = Assert.Throws<SimulationScriptException>(() =>
            SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 2, "steps": [ { "at": 0 } ] }
                """));

        Assert.Contains("not supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonScalarValue()
    {
        SimulationScriptException exception = Assert.Throws<SimulationScriptException>(() =>
            SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [ { "at": 0, "set": { "clk": true } } ]
                }
                """));

        Assert.Contains("must be a number, a string, or null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsBackwardTime()
    {
        SimulationScriptException exception = Assert.Throws<SimulationScriptException>(() =>
            SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [ { "at": 10 }, { "at": 5 } ]
                }
                """));

        Assert.Contains("moves backward", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PassingScriptRunsTheDffEdgeSequence()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "name": "DFF captures on the rising edge",
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 0 } },
                { "at": 10, "expect": { "q": 0, "qn": 1 } },
                { "at": 20, "set": { "clk": 1 } },
                { "at": 30, "expect": { "q": 1, "qn": 0 } },
                { "at": 40, "set": { "clk": 0, "d": 0 } },
                { "at": 50, "expect": { "q": 1, "qn": 0 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal("demo.dff", result.DefinitionId);
        Assert.Equal(6, result.StepsExecuted);
        Assert.Equal(50, result.FinalTime);
        Assert.False(result.Halted);
    }

    [Fact]
    public void FailedExpectationReportsStepPinAndValues()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 0 } },
                { "at": 20, "expect": { "q": 1 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Equal(2, failure.Step);
        Assert.Equal(20, failure.At);
        Assert.Contains("pin 'q'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("expected 1 (1)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("got 0 (0)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DomainLabelsResolveForDrivesAndExpectations()
    {
        var builder = new ProjectBuilder("labels");
        builder.EnsureDomain(new SignalDomain(
            "level",
            "Level",
            ["low", "high"],
            new SingleDriverResolution()));
        DefinitionBuilder definition = builder.Define("labels", "Labels");
        Net input = definition.AddPin("a", "A", PinDirection.Input, "level");
        Net output = definition.AddPin("y", "Y", PinDirection.Output, "level");
        definition.AddPrimitive("buf", new LookupSpec(1, [0, 1]));
        definition.Connect(input, definition.Pin("buf", "in0"));
        definition.Connect(output, definition.Pin("buf", "out"));

        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "a": "high" } },
                { "at": 20, "expect": { "y": "high" } },
                { "at": 30, "set": { "a": "low" } },
                { "at": 50, "expect": { "y": "low" } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(builder.Build("labels"), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
    }

    [Fact]
    public void LaneExpectationUsesTheProjectsCustomFormat()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "name": "accumulator signed lane",
              "steps": [
                {
                  "at": 0,
                  "set": { "clk": 0, "d0": 1, "d1": 1, "d2": 1, "d3": 1, "op0": 1, "op1": 0 }
                },
                { "at": 40 },
                { "at": 50, "set": { "clk": 1 } },
                {
                  "at": 100,
                  "expect": { "q0": 1, "q1": 1, "q2": 1, "q3": 1 },
                  "expectLane": { "result": -1, "data": 15, "opcode": 1 }
                },
                {
                  "at": 110,
                  "set": { "clk": 0, "d0": 1, "d1": 0, "d2": 0, "d3": 0, "op0": 0, "op1": 1 }
                },
                { "at": 160 },
                { "at": 170, "set": { "clk": 1 } },
                { "at": 220, "expectLane": { "result": 0, "data": 1, "opcode": 2 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateAccumulatorCpuDemo(),
            script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
    }

    [Fact]
    public void WindowPassesWhenPinsAndLanesConvergeAtTheSameTick()
    {
        var builder = new ProjectBuilder("window lane");
        DefinitionBuilder definition = builder.Define("lane", "Window lane");
        Net input = definition.AddPin("a", "A", PinDirection.Input, "binary");
        Net output = definition.AddPin("y0", "Y0", PinDirection.Output, "binary");
        definition.AddPrimitive("buf", new LookupSpec(1, [0, 1], delayTicks: 3));
        definition.Connect(input, definition.Pin("buf", "in0"));
        definition.Connect(output, definition.Pin("buf", "out"));
        definition.AddNumericLane("value", "Value", "unsigned-binary", "y0");

        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "set": { "a": 1 },
                  "within": { "ticks": 10, "expect": { "a": 1, "y0": 1 }, "expectLane": { "value": 1 } }
                },
                { "at": 20, "expect": { "y0": 1 }, "expectLane": { "value": 1 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(builder.Build("lane"), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(20, result.FinalTime);
        Assert.Equal(2, result.StepsExecuted);
    }

    [Fact]
    public void WindowFailureReportsTheDeadlineAndLastValues()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "set": { "clk": 0, "d": 0 },
                  "within": { "ticks": 5, "expect": { "q": 1 } }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Equal(5, failure.At);
        Assert.Contains("no tick up to t=5 satisfied the window", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Last values:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pin 'q': expected 1 (1)", failure.Message, StringComparison.Ordinal);
        Assert.Equal(5, result.FinalTime);
    }

    [Fact]
    public void WindowFailureInsideAZeroTickWindowDoesNotAdvance()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 0 }, "within": { "ticks": 0, "expect": { "q": 1 } } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        Assert.Contains("t=0", result.Failures[0].Message, StringComparison.Ordinal);
        Assert.Equal(0, result.FinalTime);
    }

    [Fact]
    public void RejectsMalformedWindows()
    {
        Assert.Contains(
            "explicit 'ticks'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "within": { "expect": { "q": 1 } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "non-negative",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "within": { "ticks": -1, "expect": { "q": 1 } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "at least one",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "within": { "ticks": 5 } } ] }
                """)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedHaltIsAPassAndStopsTheRun()
    {
        CircuitProject project = InverterLoopProject();
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "expectHalted": true },
                { "at": 10, "expectHalted": true }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(project, script);

        Assert.True(result.Passed);
        Assert.True(result.Halted);
        Assert.Equal(SimulationEngine.NonConvergenceCode, result.Diagnostics.Last().Code);
        Assert.Equal(1, result.StepsExecuted);
        Assert.NotEmpty(result.HaltReason ?? string.Empty);
    }

    [Fact]
    public void UnexpectedHaltIsAFailure()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 10, "expect": { "y": 0 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(InverterLoopProject(), script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Contains("halted unexpectedly", failure.Message, StringComparison.Ordinal);
        Assert.True(result.Halted);
    }

    [Fact]
    public void UnknownPinIsAFailureNotAnException()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "expect": { "nope": 1 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        Assert.Contains("unknown interface pin 'nope'", result.Failures[0].Message, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "examples")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository's examples directory.");
    }

    private static CircuitProject InverterLoopProject()
    {
        var builder = new ProjectBuilder("inverter loop");
        DefinitionBuilder definition = builder.Define("loop", "Inverter loop");
        definition.AddPin("a", "A", PinDirection.Input, "binary");
        definition.AddPin("y", "Y", PinDirection.Output, "binary");
        definition.AddPrimitive("inv", new LookupSpec(1, [1, 0], delayTicks: 0));
        definition.Connect("y_net", definition.Pin("inv", "out"), definition.Pin("inv", "in0"));
        definition.Definition.FindNet("y_net")!.InitialValue = 0;
        return builder.Build("loop");
    }
}
