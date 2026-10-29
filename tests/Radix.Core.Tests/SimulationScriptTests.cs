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
        { "quaternary-counter.radix", "quaternary-counter.radixtest" },
        { "quaternary-accumulator.radix", "quaternary-accumulator.radixtest" },
        { "quaternary-memory.radix", "quaternary-memory.radixtest" },
        { "quaternary-machine.radix", "quaternary-machine.radixtest" },
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
        { "byte-machine.radix", "byte-machine.radixtest" },
        { "8-bit-alu.radix", "eight-bit-alu.radixtest" },
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
    public void LaneDriveEncodesOneValueAcrossInputPins()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "name": "drive quaternary data as one number",
              "steps": [
                { "at": 0, "set": { "clk": 0, "load": 0, "add": 0 }, "setLane": { "data": 9 } },
                { "at": 20, "expect": { "d0": 1, "d1": 2 }, "expectLane": { "data": 9 } },
                { "at": 40, "set": { "load": 3 } },
                { "at": 80, "set": { "clk": 3 } },
                { "at": 120, "expectLane": { "result": 9 } },
                { "at": 140, "set": { "clk": 0, "load": 0, "add": 3 }, "setLane": { "data": 15 } },
                { "at": 180, "set": { "clk": 3 } },
                { "at": 220, "expect": { "carry": 1 }, "expectLane": { "result": 8 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateQuaternaryAccumulatorDemo(),
            script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
    }

    [Fact]
    public void LaneDriveRejectsUnknownAndOutputLanes()
    {
        SimulationScript unknown = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [ { "at": 0, "setLane": { "nope": 1 } } ]
            }
            """);
        SimulationScriptResult unknownResult = SimulationScriptRunner.Run(
            ExampleProjects.CreateQuaternaryAccumulatorDemo(),
            unknown);
        Assert.Contains(
            "has no numeric lane 'nope'",
            Assert.Single(unknownResult.Failures).Message,
            StringComparison.Ordinal);

        SimulationScript output = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [ { "at": 0, "setLane": { "result": 1 } } ]
            }
            """);
        SimulationScriptResult outputResult = SimulationScriptRunner.Run(
            ExampleProjects.CreateQuaternaryAccumulatorDemo(),
            output);
        Assert.Contains(
            "every pin is an input",
            Assert.Single(outputResult.Failures).Message,
            StringComparison.Ordinal);

        SimulationScript overflow = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [ { "at": 0, "setLane": { "data": 16 } } ]
            }
            """);
        SimulationScriptResult overflowResult = SimulationScriptRunner.Run(
            ExampleProjects.CreateQuaternaryAccumulatorDemo(),
            overflow);
        Assert.Contains(
            "cannot drive lane 'data' with 16",
            Assert.Single(overflowResult.Failures).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedLaneDrives()
    {
        Assert.Contains(
            "must be an integer value",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "setLane": { "data": true } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "empty 'setLane' object",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "setLane": {} } ] }
                """)).Message,
            StringComparison.Ordinal);
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
    public void StableWindowPassesForEveryTickAndLaneInTheInterval()
    {
        var builder = new ProjectBuilder("stable lane");
        DefinitionBuilder definition = builder.Define("lane", "Stable lane");
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
                { "at": 0, "set": { "a": 1 } },
                {
                  "at": 3,
                  "stable": { "ticks": 10, "expect": { "a": 1, "y0": 1 }, "expectLane": { "value": 1 } }
                },
                { "at": 20 }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(builder.Build("lane"), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(20, result.FinalTime);
        Assert.Equal(3, result.StepsExecuted);
    }

    [Fact]
    public void StableWindowFailsAtTheFirstDivergingTick()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "stable": { "ticks": 10, "expect": { "y": 0 } } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(WaveformProject(), script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Equal(10, failure.At);
        Assert.Contains("stable assertion failed at t=10", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pin 'y': expected 0 (0), got 1 (1)", failure.Message, StringComparison.Ordinal);
        Assert.Equal(10, result.FinalTime);
    }

    [Fact]
    public void StableWindowFailureAtItsStartDoesNotAdvance()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 0 }, "stable": { "ticks": 5, "expect": { "q": 1 } } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        Assert.Contains("stable assertion failed at t=0", result.Failures[0].Message, StringComparison.Ordinal);
        Assert.Equal(0, result.FinalTime);
    }

    [Fact]
    public void SetupWindowChecksEveryTickBeforeTheDrives()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 1 } },
                { "at": 10, "setup": { "ticks": 5, "expect": { "clk": 0, "d": 1 } }, "set": { "clk": 1 } },
                { "at": 20, "setup": { "ticks": 5, "expect": { "clk": 1, "d": 1 } }, "expect": { "q": 1 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(3, result.StepsExecuted);
        Assert.Equal(20, result.FinalTime);
    }

    [Fact]
    public void SetupWindowFailsAtTheFirstTickThatDiverges()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 1 } },
                { "at": 10, "setup": { "ticks": 5, "expect": { "d": 0 } }, "set": { "clk": 1 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Equal(5, failure.At);
        Assert.Contains("setup assertion failed at t=5", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pin 'd': expected 0 (0), got 1 (1)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupWindowCannotReachIntoAlreadySimulatedTime()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 40, "set": { "clk": 0, "d": 1 } },
                { "at": 45, "setup": { "ticks": 10, "expect": { "d": 1 } }, "set": { "clk": 1 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        Assert.Contains("setup window starts at t=35, before the current time t=40", result.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupWindowRejectsATickRangeBeforeZero()
    {
        SimulationScriptException exception = Assert.Throws<SimulationScriptException>(() =>
            SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    { "at": 5, "setup": { "ticks": 10, "expect": { "d": 1 } } }
                  ]
                }
                """));

        Assert.Contains("cannot start before t=0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransitionWindowCountsTheStartingDriveAndTheEdgeItCauses()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 1 } },
                {
                  "at": 20,
                  "set": { "clk": 1 },
                  "transitions": { "ticks": 20, "expect": { "clk": 1, "q": 1, "qn": 1 } }
                },
                { "at": 40, "expect": { "q": 1, "qn": 0 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(40, result.FinalTime);
        Assert.Equal(3, result.StepsExecuted);
    }

    [Fact]
    public void TransitionWindowCountsZeroAcrossAStableInterval()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 1 } },
                { "at": 20, "set": { "clk": 1 }, "within": { "ticks": 10, "expect": { "q": 1 } } },
                { "at": 40, "transitions": { "ticks": 20, "expect": { "clk": 0, "q": 0, "qn": 0 } } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(60, result.FinalTime);
    }

    [Fact]
    public void TransitionWindowFailureReportsExpectedAndObservedCounts()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 1 } },
                {
                  "at": 20,
                  "set": { "clk": 1 },
                  "transitions": { "ticks": 10, "expect": { "q": 2 } }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Equal(30, failure.At);
        Assert.Contains(
            "pin 'q': expected 2 transition(s) between t=20 and t=30, observed 1",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransitionWindowRejectsUnknownPinsAsFailures()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "transitions": { "ticks": 5, "expect": { "nope": 0 } } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        Assert.Contains("unknown interface pin 'nope'", result.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EdgeWindowCountsOnlyMatchingTransitions()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "edges": {
                    "ticks": 30,
                    "expect": {
                      "y": [
                        { "from": 0, "to": 1, "count": 2 },
                        { "from": 1, "to": 0, "count": 1 },
                        { "to": 1, "count": 2 },
                        { "from": 0, "count": 2 }
                      ]
                    }
                  }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(WaveformProject(), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(30, result.FinalTime);
        Assert.Equal(1, result.StepsExecuted);
    }

    [Fact]
    public void EdgeWindowResolvesDomainLabels()
    {
        var builder = new ProjectBuilder("edge labels");
        builder.EnsureDomain(new SignalDomain(
            "level",
            "Level",
            ["low", "high"],
            new SingleDriverResolution()));
        DefinitionBuilder definition = builder.Define("labels", "Labels");
        Net input = definition.AddPin("a", "A", PinDirection.Input, "level");
        Net output = definition.AddPin("y", "Y", PinDirection.Output, "level");
        definition.AddPrimitive("buf", new LookupSpec(1, [0, 1], delayTicks: 1));
        definition.Connect(input, definition.Pin("buf", "in0"));
        definition.Connect(output, definition.Pin("buf", "out"));

        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "a": "low" } },
                {
                  "at": 10,
                  "set": { "a": "high" },
                  "edges": { "ticks": 5, "expect": { "y": [ { "from": "low", "to": "high", "count": 1 } ] } }
                },
                {
                  "at": 20,
                  "set": { "a": "low" },
                  "edges": { "ticks": 5, "expect": { "y": [ { "from": "high", "count": 1 }, { "to": "high", "count": 0 } ] } }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(builder.Build("labels"), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(25, result.FinalTime);
    }

    [Fact]
    public void EdgeWindowFailureReportsTheDirectionAndCounts()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "edges": { "ticks": 20, "expect": { "y": [ { "from": 0, "to": 1, "count": 3 } ] } }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(WaveformProject(), script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Equal(20, failure.At);
        Assert.Contains(
            "pin 'y': expected 3 edge(s) 0 (0) -> 1 (1) between t=0 and t=20, observed 1",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EdgeWindowRejectsUnknownPinsAndValuesAsFailures()
    {
        SimulationScript unknownPin = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "edges": { "ticks": 5, "expect": { "nope": [ { "to": 1, "count": 0 } ] } } }
              ]
            }
            """);
        SimulationScriptResult pinResult = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            unknownPin);
        Assert.False(pinResult.Passed);
        Assert.Contains("unknown interface pin 'nope'", pinResult.Failures[0].Message, StringComparison.Ordinal);

        SimulationScript unknownValue = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "edges": { "ticks": 5, "expect": { "q": [ { "to": 5, "count": 0 } ] } } }
              ]
            }
            """);
        SimulationScriptResult valueResult = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            unknownValue);
        Assert.False(valueResult.Passed);
        Assert.Contains("invalid 'to' for pin 'q'", valueResult.Failures[0].Message, StringComparison.Ordinal);
        Assert.Contains("outside domain 'binary'", valueResult.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedEdgeWindows()
    {
        Assert.Contains(
            "'edges' needs an explicit 'ticks' value",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "edges": { "expect": { "q": [ { "to": 1, "count": 1 } ] } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "'edges.ticks' must be non-negative",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "edges": { "ticks": -1, "expect": { "q": [ { "to": 1, "count": 1 } ] } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "'edges' needs at least one",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "edges": { "ticks": 5, "expect": {} } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs at least one edge filter",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "edges": { "ticks": 5, "expect": { "q": [] } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "filter needs 'from', 'to', or both",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "edges": { "ticks": 5, "expect": { "q": [ { "count": 1 } ] } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs a non-negative 'count'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "edges": { "ticks": 5, "expect": { "q": [ { "to": 1 } ] } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs a non-negative 'count'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "edges": { "ticks": 5, "expect": { "q": [ { "to": 1, "count": -1 } ] } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "must be a number, a string, or null",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "edges": { "ticks": 5, "expect": { "q": [ { "to": true, "count": 1 } ] } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "cannot combine 'edges' with 'within', 'stable', 'transitions', or 'sequence'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    {
                      "edges": { "ticks": 5, "expect": { "q": [ { "to": 1, "count": 1 } ] } },
                      "transitions": { "ticks": 5, "expect": { "q": 1 } }
                    }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "cannot combine a timing window with 'expectHalted: true'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    { "edges": { "ticks": 5, "expect": { "q": [ { "to": 1, "count": 1 } ] } }, "expectHalted": true }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SequenceWindowMatchesTransitionsInOrder()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "sequence": {
                    "ticks": 30,
                    "expect": [
                      { "pin": "y", "from": 0, "to": 1 },
                      { "pin": "y", "from": 1, "to": 0 },
                      { "pin": "y", "from": 0, "to": 1 }
                    ]
                  }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(WaveformProject(), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(30, result.FinalTime);
    }

    [Fact]
    public void SequenceWindowRequiresTheDeclaredOrderNotJustExistence()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "sequence": {
                    "ticks": 30,
                    "expect": [
                      { "pin": "y", "from": 0, "to": 1 },
                      { "pin": "y", "from": 0, "to": 1 },
                      { "pin": "y", "from": 1, "to": 0 }
                    ]
                  }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(WaveformProject(), script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Equal(30, failure.At);
        Assert.Contains("sequence item 3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pin 'y': 1 (1) -> 0 (0)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not observed in order", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SequenceWindowResolvesLabelsAndRejectsImpossibleFilters()
    {
        var builder = new ProjectBuilder("sequence labels");
        builder.EnsureDomain(new SignalDomain(
            "level",
            "Level",
            ["low", "high"],
            new SingleDriverResolution()));
        DefinitionBuilder definition = builder.Define("labels", "Labels");
        Net input = definition.AddPin("a", "A", PinDirection.Input, "level");
        Net output = definition.AddPin("y", "Y", PinDirection.Output, "level");
        definition.AddPrimitive("buf", new LookupSpec(1, [0, 1], delayTicks: 1));
        definition.Connect(input, definition.Pin("buf", "in0"));
        definition.Connect(output, definition.Pin("buf", "out"));
        CircuitProject project = builder.Build("labels");

        SimulationScript labels = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "a": "low" } },
                {
                  "at": 10,
                  "set": { "a": "high" },
                  "sequence": {
                    "ticks": 5,
                    "expect": [
                      { "pin": "a", "from": "low", "to": "high" },
                      { "pin": "y", "from": "low", "to": "high" }
                    ]
                  }
                }
              ]
            }
            """);
        Assert.True(SimulationScriptRunner.Run(project, labels).Passed);

        SimulationScript impossible = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "sequence": { "ticks": 5, "expect": [ { "pin": "y", "from": 1, "to": 1 } ] } }
              ]
            }
            """);
        SimulationScriptResult impossibleResult = SimulationScriptRunner.Run(WaveformProject(), impossible);
        Assert.False(impossibleResult.Passed);
        Assert.Contains("same 'from' and 'to' value", impossibleResult.Failures[0].Message, StringComparison.Ordinal);
        Assert.Contains("no transition can match", impossibleResult.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SequenceWindowRejectsUnknownPinsAsFailures()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "sequence": { "ticks": 5, "expect": [ { "pin": "nope", "to": 1 } ] } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        Assert.Contains("unknown interface pin 'nope'", result.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedSequenceWindows()
    {
        Assert.Contains(
            "'sequence' needs an explicit 'ticks' value",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "sequence": { "expect": [ { "pin": "y", "to": 1 } ] } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "'sequence.ticks' must be non-negative",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "sequence": { "ticks": -1, "expect": [ { "pin": "y", "to": 1 } ] } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "'sequence' needs at least one",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "sequence": { "ticks": 5, "expect": [] } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs a non-blank 'pin'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "sequence": { "ticks": 5, "expect": [ { "to": 1 } ] } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs 'from', 'to', or both",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "sequence": { "ticks": 5, "expect": [ { "pin": "y" } ] } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "must be a number, a string, or null",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "sequence": { "ticks": 5, "expect": [ { "pin": "y", "to": true } ] } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "'sequence.expect[0]' is null",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "sequence": { "ticks": 5, "expect": [ null ] } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "cannot combine 'sequence' with 'within', 'stable', or 'transitions'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    {
                      "sequence": { "ticks": 5, "expect": [ { "pin": "y", "to": 1 } ] },
                      "stable": { "ticks": 5, "expect": { "y": 1 } }
                    }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "cannot combine 'edges' with 'within', 'stable', 'transitions', or 'sequence'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    {
                      "edges": { "ticks": 5, "expect": { "y": [ { "to": 1, "count": 1 } ] } },
                      "sequence": { "ticks": 5, "expect": [ { "pin": "y", "to": 1 } ] }
                    }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "cannot combine a timing window with 'expectHalted: true'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    { "sequence": { "ticks": 5, "expect": [ { "pin": "y", "to": 1 } ] }, "expectHalted": true }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedTransitionWindows()
    {
        Assert.Contains(
            "'transitions' needs an explicit 'ticks' value",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "transitions": { "expect": { "q": 1 } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "must be a non-negative integer count",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "transitions": { "ticks": 5, "expect": { "q": -1 } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "'transitions' needs at least one",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "transitions": { "ticks": 5, "expect": {} } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "cannot combine 'transitions' with 'within' or 'stable'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    {
                      "stable": { "ticks": 5, "expect": { "q": 1 } },
                      "transitions": { "ticks": 5, "expect": { "q": 0 } }
                    }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "cannot combine a timing window with 'expectHalted: true'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    { "transitions": { "ticks": 5, "expect": { "q": 0 } }, "expectHalted": true }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedStableWindows()
    {
        Assert.Contains(
            "'stable' needs an explicit 'ticks' value",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "stable": { "expect": { "q": 1 } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "'stable' needs at least one",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "stable": { "ticks": 5 } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "both 'within' and 'stable'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    {
                      "within": { "ticks": 5, "expect": { "q": 1 } },
                      "stable": { "ticks": 5, "expect": { "q": 1 } }
                    }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "cannot combine a timing window with 'expectHalted: true'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    { "stable": { "ticks": 5, "expect": { "q": 1 } }, "expectHalted": true }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);
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
    public void InternalNetExpectationsResolveRootAndNestedPaths()
    {
        var builder = new ProjectBuilder("internal nets");
        DefinitionBuilder leaf = builder.Define("leaf", "Leaf");
        Net leafInput = leaf.AddPin("a", "A", PinDirection.Input, "binary");
        Net leafOutput = leaf.AddPin("y", "Y", PinDirection.Output, "binary");
        leaf.AddPrimitive("g1", new LookupSpec(1, [0, 1]));
        leaf.AddPrimitive("g2", new LookupSpec(1, [0, 1]));
        Net midpoint = leaf.AddNet("mid", "binary");
        leaf.Connect(leafInput, leaf.Pin("g1", "in0"));
        leaf.Connect(midpoint, leaf.Pin("g1", "out"), leaf.Pin("g2", "in0"));
        leaf.Connect(leafOutput, leaf.Pin("g2", "out"));

        DefinitionBuilder root = builder.Define("root", "Root");
        Net input = root.AddPin("a", "A", PinDirection.Input, "binary");
        Net output = root.AddPin("y", "Y", PinDirection.Output, "binary");
        root.AddPrimitive("g", new LookupSpec(1, [0, 1]));
        root.AddInstance("u1", "leaf");
        Net wire = root.AddNet("w", "binary");
        root.Connect(input, root.Pin("g", "in0"));
        root.Connect(wire, root.Pin("g", "out"), root.Pin("u1", "a"));
        root.Connect(output, root.Pin("u1", "y"));

        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "set": { "a": 1 },
                  "expect": { "y": 1 },
                  "expectNet": { "w": 1, "u1/mid": 1 }
                },
                {
                  "at": 20,
                  "set": { "a": 0 },
                  "expect": { "y": 0 },
                  "expectNet": { "w": 0, "u1/mid": 0 }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(builder.Build("root"), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal("root", result.DefinitionId);
        Assert.Equal(2, result.StepsExecuted);
    }

    [Fact]
    public void InternalNetExpectationsWorkInSetupSettleAndStabilityWindows()
    {
        var builder = new ProjectBuilder("internal windows");
        DefinitionBuilder definition = builder.Define("root", "Root");
        Net input = definition.AddPin("a", "A", PinDirection.Input, "binary");
        Net output = definition.AddPin("y", "Y", PinDirection.Output, "binary");
        definition.AddPrimitive("g1", new LookupSpec(1, [0, 1]));
        definition.AddPrimitive("g2", new LookupSpec(1, [0, 1], delayTicks: 3));
        Net wire = definition.AddNet("w", "binary");
        definition.Connect(input, definition.Pin("g1", "in0"));
        definition.Connect(wire, definition.Pin("g1", "out"), definition.Pin("g2", "in0"));
        definition.Connect(output, definition.Pin("g2", "out"));

        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "a": 0 } },
                {
                  "at": 10,
                  "setup": { "ticks": 3, "expectNet": { "w": 0, "y_net": 0 } },
                  "set": { "a": 1 },
                  "within": { "ticks": 5, "expectNet": { "w": 1, "y_net": 1 } }
                },
                { "at": 30, "stable": { "ticks": 5, "expectNet": { "w": 1, "y_net": 1 } } },
                { "at": 40, "set": { "a": 0 } },
                { "at": 50, "stable": { "ticks": 5, "expectNet": { "w": 0, "y_net": 0 } } },
                {
                  "at": 60,
                  "setup": { "ticks": 3, "expectNet": { "w": 0, "y_net": 0 } },
                  "set": { "a": 1 }
                },
                { "at": 70, "within": { "ticks": 5, "expectNet": { "w": 1, "y_net": 1 } } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(builder.Build("root"), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(7, result.StepsExecuted);
        Assert.Equal(70, result.FinalTime);
    }

    [Fact]
    public void InternalNetsCanBeCountedFilteredAndOrdered()
    {
        var builder = new ProjectBuilder("internal edge windows");
        DefinitionBuilder definition = builder.Define("root", "Root");
        Net input = definition.AddPin("a", "A", PinDirection.Input, "binary");
        Net output = definition.AddPin("y", "Y", PinDirection.Output, "binary");
        definition.AddPrimitive("g1", new LookupSpec(1, [0, 1]));
        definition.AddPrimitive("g2", new LookupSpec(1, [0, 1], delayTicks: 2));
        Net wire = definition.AddNet("w", "binary");
        definition.Connect(input, definition.Pin("g1", "in0"));
        definition.Connect(wire, definition.Pin("g1", "out"), definition.Pin("g2", "in0"));
        definition.Connect(output, definition.Pin("g2", "out"));

        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "a": 0 } },
                {
                  "at": 10,
                  "set": { "a": 1 },
                  "transitions": { "ticks": 0, "expectNet": { "w": 1 } }
                },
                {
                  "at": 20,
                  "set": { "a": 0 },
                  "edges": { "ticks": 0, "expectNet": { "w": [ { "from": 1, "to": 0, "count": 1 } ] } }
                },
                {
                  "at": 30,
                  "set": { "a": 1 },
                  "sequence": {
                    "ticks": 5,
                    "expect": [
                      { "net": "w", "from": 0, "to": 1 },
                      { "net": "y_net", "from": 0, "to": 1 }
                    ]
                  }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(builder.Build("root"), script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(4, result.StepsExecuted);
        Assert.Equal(35, result.FinalTime);
    }

    [Fact]
    public void InternalNetWindowsReportUnknownReferencesAndInvalidValues()
    {
        SimulationScript unknown = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "transitions": { "ticks": 5, "expectNet": { "nope": 0 } } }
              ]
            }
            """);
        SimulationScriptResult unknownResult = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            unknown);
        Assert.False(unknownResult.Passed);
        Assert.Contains("unknown internal net 'nope'", unknownResult.Failures[0].Message, StringComparison.Ordinal);

        SimulationScript invalid = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "set": { "clk": 0 },
                  "edges": { "ticks": 0, "expectNet": { "q_net": [ { "to": 5, "count": 0 } ] } }
                }
              ]
            }
            """);
        SimulationScriptResult invalidResult = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            invalid);
        Assert.False(invalidResult.Passed);
        Assert.Contains("invalid 'to' for net 'q_net'", invalidResult.Failures[0].Message, StringComparison.Ordinal);
        Assert.Contains("outside domain 'binary'", invalidResult.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedInternalNetWindowSignals()
    {
        Assert.Contains(
            "'transitions' needs at least one 'expect' or 'expectNet' entry",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "transitions": { "ticks": 5, "expectNet": {} } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "transitions.expectNet.w' must be a non-negative integer count",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "transitions": { "ticks": 5, "expectNet": { "w": -1 } } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "invalid net reference 'u1//w'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    { "edges": { "ticks": 5, "expectNet": { "u1//w": [ { "to": 1, "count": 1 } ] } } }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "cannot have both 'pin' and 'net'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [
                    { "sequence": { "ticks": 5, "expect": [ { "pin": "q", "net": "q_net", "to": 1 } ] } }
                  ]
                }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs a non-blank 'pin' or 'net'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                {
                  "format": "radix-test",
                  "version": 1,
                  "steps": [ { "sequence": { "ticks": 5, "expect": [ { "to": 1 } ] } } ]
                }
                """)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InternalNetFailureReportsTheReferenceAndDomainValues()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "expectNet": { "ff/nope": 1 } }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Contains("unknown internal net 'ff/nope'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Authored nets available:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedNetReferencesAndEmptyNetWindows()
    {
        Assert.Contains(
            "cannot be blank",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectNet": { "": 1 } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "net id '' is not valid",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectNet": { "u1/": 1 } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "path segment '' is not valid",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectNet": { "u1//w": 1 } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "path segment 'bad net' is not valid",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectNet": { "bad net/w": 1 } } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "empty 'expectNet' object",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectNet": {} } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "at least one 'expect', 'expectLane', 'expectNet', or 'expectRelation'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "within": { "ticks": 5 } } ] }
                """)).Message,
            StringComparison.Ordinal);
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

    [Fact]
    public void RelationAssertionsComparePinsAndInternalNets()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "name": "relations",
              "steps": [
                {
                  "at": 0,
                  "set": { "clk": 0, "d": 1 },
                  "expectRelation": [
                    { "left": "pin:q", "right": "qn", "op": "ne" }
                  ]
                },
                {
                  "at": 20,
                  "set": { "clk": 1 },
                  "expectRelation": [
                    { "left": "q", "right": "qn", "op": "ne" }
                  ]
                },
                {
                  "at": 30,
                  "expectRelation": [
                    { "left": "q", "right": "net:ff/q_net", "op": "eq" },
                    { "left": "q", "right": "qn", "op": "gt" }
                  ]
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(3, result.StepsExecuted);
        Assert.Equal(30, result.FinalTime);
    }

    [Fact]
    public void RelationAssertionsWorkInSetupSettleAndStabilityWindows()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "set": { "clk": 0, "d": 1 } },
                {
                  "at": 20,
                  "set": { "clk": 1 },
                  "setup": {
                    "ticks": 5,
                    "expectRelation": [ { "left": "q", "right": "qn", "op": "ne" } ]
                  },
                  "within": {
                    "ticks": 10,
                    "expectRelation": [ { "left": "q", "right": "qn", "op": "gt" } ]
                  }
                },
                {
                  "at": 40,
                  "stable": {
                    "ticks": 5,
                    "expectRelation": [
                      { "left": "q", "right": "qn", "op": "gt" },
                      { "left": "q", "right": "net:ff/q_net", "op": "eq" }
                    ]
                  }
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures.Select(f => f.Message)));
        Assert.Equal(3, result.StepsExecuted);
        Assert.Equal(45, result.FinalTime);
    }

    [Fact]
    public void RelationFailureReportsBothSignalsAndValues()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "set": { "clk": 0, "d": 1 },
                  "expectRelation": [ { "left": "q", "right": "qn", "op": "eq" } ]
                }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        ScriptFailure failure = Assert.Single(result.Failures);
        Assert.Equal(1, failure.Step);
        Assert.Contains("relation 1 (eq)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("left pin 'q' is 0 (0)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("right pin 'qn' is 1 (1)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the relation does not hold", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RelationRejectsMixedDomainsAndUndrivenSignals()
    {
        var builder = new ProjectBuilder("relation domains");
        DefinitionBuilder definition = builder.Define("root", "Root");
        definition.AddPin("a", "A", PinDirection.Input, "binary");
        definition.AddPin("b", "B", PinDirection.Input, "ternary");
        CircuitProject project = builder.Build("root");

        SimulationScript mixed = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                {
                  "at": 0,
                  "set": { "a": 0, "b": 0 },
                  "expectRelation": [ { "left": "a", "right": "b", "op": "eq" } ]
                }
              ]
            }
            """);
        SimulationScriptResult mixedResult = SimulationScriptRunner.Run(project, mixed);
        Assert.False(mixedResult.Passed);
        Assert.Contains(
            "a relation needs both signals in the same domain",
            mixedResult.Failures[0].Message,
            StringComparison.Ordinal);

        var binaryBuilder = new ProjectBuilder("undriven relations");
        DefinitionBuilder binaryDefinition = binaryBuilder.Define("root", "Root");
        binaryDefinition.AddPin("a", "A", PinDirection.Input, "binary");
        binaryDefinition.AddPin("b", "B", PinDirection.Input, "binary");
        SimulationScript undriven = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "expectRelation": [ { "left": "a", "right": "b", "op": "eq" } ] }
              ]
            }
            """);
        SimulationScriptResult undrivenResult = SimulationScriptRunner.Run(
            binaryBuilder.Build("root"),
            undriven);
        Assert.False(undrivenResult.Passed);
        Assert.Contains("left pin 'a' is undriven", undrivenResult.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RelationReportsUnknownSignalsWithoutThrowing()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [
                { "at": 0, "expectRelation": [ { "left": "nope", "right": "q", "op": "eq" } ] }
              ]
            }
            """);

        SimulationScriptResult result = SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script);

        Assert.False(result.Passed);
        Assert.Contains("relation 1 (eq)", result.Failures[0].Message, StringComparison.Ordinal);
        Assert.Contains("unknown interface pin 'nope'", result.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedRelationAssertions()
    {
        Assert.Contains(
            "empty 'expectRelation' array",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectRelation": [] } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs a non-blank 'left' signal",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectRelation": [ { "right": "q", "op": "eq" } ] } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs a non-blank 'right' signal",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectRelation": [ { "left": "q", "op": "eq" } ] } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs an 'op'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectRelation": [ { "left": "q", "right": "qn" } ] } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "must be eq, ne, lt, le, gt, or ge",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectRelation": [ { "left": "q", "right": "qn", "op": "same" } ] } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "'expectRelation[0].left' is invalid",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "expectRelation": [ { "left": "net:u1//w", "right": "q", "op": "eq" } ] } ] }
                """)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs at least one 'expect', 'expectLane', 'expectNet', or 'expectRelation'",
            Assert.Throws<SimulationScriptException>(() => SimulationScript.LoadFromString(
                """
                { "format": "radix-test", "version": 1, "steps": [ { "within": { "ticks": 5 } } ] }
                """)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RunCanBeCancelledInsideALongStabilityWindow()
    {
        SimulationScript script = SimulationScript.LoadFromString(
            """
            {
              "format": "radix-test",
              "version": 1,
              "steps": [ { "at": 0, "stable": { "ticks": 20000000, "expect": { "q": 0 } } } ]
            }
            """);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Assert.Throws<OperationCanceledException>(() => SimulationScriptRunner.Run(
            ExampleProjects.CreateDFlipFlopDemo(),
            script,
            cancellationToken: cancellation.Token));
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

    private static CircuitProject WaveformProject()
    {
        var builder = new ProjectBuilder("waveform");
        DefinitionBuilder definition = builder.Define("wave", "Wave");
        Net output = definition.AddPin("y", "Y", PinDirection.Output, "binary");
        definition.AddPrimitive("w", new WaveformSpec([10, 10], [0, 1]));
        definition.Connect(output, definition.Pin("w", "out"));
        return builder.Build("wave");
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
