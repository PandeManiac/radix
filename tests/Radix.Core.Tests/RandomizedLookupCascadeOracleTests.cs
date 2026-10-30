using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Randomized differential campaign for a ring of lookup gates. Each gate combines
/// the previous gate's binary output with a mixed-domain external input; every gate
/// has a positive transport delay, and the last output is fed back into the first
/// gate through an initialized net. The reference below is a small independent
/// event model over the conservative table semantics: it never calls the engine or
/// the lookup runtime, it decomposes the ring with an explicit event queue, and it
/// permits periodic behavior. Every gate output is compared at every simulated tick.
/// </summary>
public sealed class RandomizedLookupCascadeOracleTests
{
    private const int Seed = 0xCA5CADE;
    private const int CaseCount = 80;
    private const int StepCount = 24;
    private const int ExplicitDelaySeed = 0xDE1A7;
    private const int ExplicitDelayCaseCount = 64;
    private const int ExplicitDelayStepCount = 28;

    private static readonly (string Id, int Radix)[] DomainChoices =
    [
        (StandardDomains.BinaryId, 2),
        (StandardDomains.TernaryId, 3),
        (StandardDomains.QuaternaryId, 4),
        (StandardDomains.FourStateId, 4),
    ];

    [Fact]
    public void RandomizedDelayedCascadeWithFeedbackMatchesReference()
    {
        var random = new Random(Seed);
        int determinedEvaluations = 0;
        int ambiguousEvaluations = 0;
        int outputTransitions = 0;
        int feedbackChanges = 0;
        int undrivenAtTimeZero = 0;
        int casesWithMultiTickDelay = 0;

        for (int caseIndex = 0; caseIndex < CaseCount; caseIndex++)
        {
            int gateCount = random.Next(3, 7);
            var domains = new (string Id, int Radix)[gateCount];
            domains[0] = DomainChoices[random.Next(DomainChoices.Length)];
            do
            {
                domains[1] = DomainChoices[random.Next(DomainChoices.Length)];
            }
            while (string.Equals(domains[1].Id, domains[0].Id, StringComparison.Ordinal));

            for (int i = 2; i < gateCount; i++)
            {
                domains[i] = DomainChoices[random.Next(DomainChoices.Length)];
            }

            var tables = new int[gateCount][];
            var delays = new long[gateCount];
            bool hasMultiTickDelay = false;
            for (int gate = 0; gate < gateCount; gate++)
            {
                delays[gate] = random.Next(1, 4);
                hasMultiTickDelay |= delays[gate] > 1;
                tables[gate] = new int[domains[gate].Radix * 2];
                for (int entry = 0; entry < tables[gate].Length; entry++)
                {
                    tables[gate][entry] = random.Next(2);
                }
            }

            if (hasMultiTickDelay)
            {
                casesWithMultiTickDelay++;
            }

            int feedbackInitial = random.Next(2);
            SimulationEngine engine = BuildCascade(domains, tables, delays, feedbackInitial);
            var reference = new CascadeReference(
                domains.Select(domain => domain.Radix).ToArray(),
                tables,
                delays,
                feedbackInitial);

            int previousFeedback = feedbackInitial;
            for (int step = 0; step < StepCount; step++)
            {
                if (step > 0)
                {
                    engine.AdvanceTo(step);
                    reference.AdvanceTo(step);
                }

                int?[] drives = MakeDrives(random, domains, step);
                for (int gate = 0; gate < gateCount; gate++)
                {
                    int value = drives[gate] ?? SignalValue.UndrivenIndex;
                    engine.Drive($"in{gate}", value);
                    reference.Drive(gate, value);
                }

                engine.AdvanceTo(step);
                reference.AdvanceTo(step);

                if (engine.IsHalted)
                {
                    Assert.Fail(Describe(
                        "engine halted",
                        caseIndex,
                        step,
                        domains,
                        tables,
                        delays,
                        feedbackInitial,
                        drives,
                        reference.Outputs(),
                        engine,
                        expectedGate: -1,
                        expected: 0,
                        actual: 0));
                }

                for (int gate = 0; gate < gateCount; gate++)
                {
                    int expected = reference.OutputValue(gate);
                    int actual = engine.Read($"y{gate}");
                    if (actual != expected)
                    {
                        Assert.Fail(Describe(
                            "output mismatch",
                            caseIndex,
                            step,
                            domains,
                            tables,
                            delays,
                            feedbackInitial,
                            drives,
                            reference.Outputs(),
                            engine,
                            gate,
                            expected,
                            actual));
                    }

                    if (step == 0 && expected == SignalValue.UndrivenIndex)
                    {
                        undrivenAtTimeZero++;
                    }
                }

                int feedback = reference.OutputValue(gateCount - 1);
                if (feedback != previousFeedback)
                {
                    feedbackChanges++;
                    previousFeedback = feedback;
                }
            }

            determinedEvaluations += reference.DeterminedEvaluations;
            ambiguousEvaluations += reference.AmbiguousEvaluations;
            outputTransitions += reference.OutputTransitions;

            Assert.True(
                engine.RuntimeDiagnostics.Count == 0,
                $"seed 0x{Seed:X8}, case {caseIndex}: unexpected runtime diagnostics: " +
                string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }

        Assert.True(determinedEvaluations > 0, "the campaign never exercised a determined lookup");
        Assert.True(ambiguousEvaluations > 0, "the campaign never exercised an ambiguous lookup");
        Assert.True(outputTransitions > 0, "the campaign never observed an output transition");
        Assert.True(feedbackChanges > 0, "the feedback output never changed");
        Assert.True(undrivenAtTimeZero > 0, "positive transport delays did not postpone the initial outputs");
        Assert.True(casesWithMultiTickDelay > 0, "the campaign never used a transport delay above one tick");
    }

    [Fact]
    public void RandomizedExplicitDelayFeedbackWithMultiplePathsMatchesReference()
    {
        var random = new Random(ExplicitDelaySeed);
        int determinedEvaluations = 0;
        int ambiguousEvaluations = 0;
        int gateTransitions = 0;
        int linkTransitions = 0;
        int casesWithMultiTickLinkDelay = 0;
        int distinctSourceCases = 0;
        int undrivenGateOutputsAtTimeZero = 0;

        for (int caseIndex = 0; caseIndex < ExplicitDelayCaseCount; caseIndex++)
        {
            int gateCount = random.Next(3, 7);
            int tapOffset = random.Next(2, gateCount);
            int linkCount = gateCount * 2;
            var tables = new int[gateCount][];
            var gateDelays = new long[gateCount];
            for (int gate = 0; gate < gateCount; gate++)
            {
                gateDelays[gate] = random.Next(1, 3);
                tables[gate] = new int[8];
                for (int entry = 0; entry < tables[gate].Length; entry++)
                {
                    tables[gate][entry] = random.Next(2);
                }
            }

            var linkDelays = new long[linkCount];
            var linkInitials = new int[linkCount];
            bool hasMultiTickLinkDelay = false;
            for (int link = 0; link < linkCount; link++)
            {
                linkDelays[link] = random.Next(1, 4);
                linkInitials[link] = random.Next(2);
                hasMultiTickLinkDelay |= linkDelays[link] > 1;
            }

            if (hasMultiTickLinkDelay)
            {
                casesWithMultiTickLinkDelay++;
            }

            var linkSources = new int[linkCount];
            bool distinctSources = true;
            for (int gate = 0; gate < gateCount; gate++)
            {
                linkSources[(gate * 2) + 0] = (gate + gateCount - 1) % gateCount;
                linkSources[(gate * 2) + 1] = (gate + gateCount - tapOffset) % gateCount;
                distinctSources &= linkSources[(gate * 2) + 0] != linkSources[(gate * 2) + 1];
            }

            if (distinctSources)
            {
                distinctSourceCases++;
            }

            (SimulationEngine engine, int[] linkNets) = BuildExplicitDelayFeedback(
                tables,
                gateDelays,
                linkDelays,
                linkInitials,
                linkSources);
            var reference = new ExplicitDelayFeedbackReference(
                tables,
                gateDelays,
                linkDelays,
                linkInitials,
                linkSources);

            for (int step = 0; step < ExplicitDelayStepCount; step++)
            {
                if (step > 0)
                {
                    engine.AdvanceTo(step);
                    reference.AdvanceTo(step);
                }

                int?[] drives = MakeExplicitDelayDrives(random, gateCount, step);
                for (int gate = 0; gate < gateCount; gate++)
                {
                    int value = drives[gate] ?? SignalValue.UndrivenIndex;
                    engine.Drive($"in{gate}", value);
                    reference.Drive(gate, value);
                }

                engine.AdvanceTo(step);
                reference.AdvanceTo(step);

                if (engine.IsHalted)
                {
                    Assert.Fail(DescribeExplicitDelayCase(
                        "engine halted",
                        caseIndex,
                        step,
                        gateCount,
                        tapOffset,
                        gateDelays,
                        linkDelays,
                        linkInitials,
                        linkSources,
                        tables,
                        drives,
                        reference.GateOutputs(),
                        Array.Empty<int>(),
                        reference.LinkOutputs(),
                        Array.Empty<int>(),
                        engine,
                        "none",
                        expected: 0,
                        actual: 0));
                }

                int[] expectedGates = reference.GateOutputs();
                int[] expectedLinks = reference.LinkOutputs();
                var actualGates = new int[gateCount];
                var actualLinks = new int[linkCount];
                for (int gate = 0; gate < gateCount; gate++)
                {
                    actualGates[gate] = engine.Read($"y{gate}");
                }

                for (int link = 0; link < linkCount; link++)
                {
                    actualLinks[link] = engine.GetNetValue(linkNets[link]);
                }

                for (int gate = 0; gate < gateCount; gate++)
                {
                    if (actualGates[gate] != expectedGates[gate])
                    {
                        Assert.Fail(DescribeExplicitDelayCase(
                            "gate output mismatch",
                            caseIndex,
                            step,
                            gateCount,
                            tapOffset,
                            gateDelays,
                            linkDelays,
                            linkInitials,
                            linkSources,
                            tables,
                            drives,
                            expectedGates,
                            actualGates,
                            expectedLinks,
                            actualLinks,
                            engine,
                            $"g{gate}",
                            expectedGates[gate],
                            actualGates[gate]));
                    }

                    if (step == 0 && expectedGates[gate] == SignalValue.UndrivenIndex)
                    {
                        undrivenGateOutputsAtTimeZero++;
                    }
                }

                for (int link = 0; link < linkCount; link++)
                {
                    if (actualLinks[link] != expectedLinks[link])
                    {
                        Assert.Fail(DescribeExplicitDelayCase(
                            "explicit delay output mismatch",
                            caseIndex,
                            step,
                            gateCount,
                            tapOffset,
                            gateDelays,
                            linkDelays,
                            linkInitials,
                            linkSources,
                            tables,
                            drives,
                            expectedGates,
                            actualGates,
                            expectedLinks,
                            actualLinks,
                            engine,
                            $"d{link}",
                            expectedLinks[link],
                            actualLinks[link]));
                    }
                }
            }

            determinedEvaluations += reference.DeterminedEvaluations;
            ambiguousEvaluations += reference.AmbiguousEvaluations;
            gateTransitions += reference.GateTransitions;
            linkTransitions += reference.LinkTransitions;

            Assert.True(
                engine.RuntimeDiagnostics.Count == 0,
                $"seed 0x{ExplicitDelaySeed:X8}, case {caseIndex}: unexpected runtime diagnostics: " +
                string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }

        Assert.True(determinedEvaluations > 0, "the campaign never exercised a determined lookup");
        Assert.True(ambiguousEvaluations > 0, "the campaign never exercised an ambiguous lookup");
        Assert.True(gateTransitions > 0, "the campaign never observed a lookup output transition");
        Assert.True(linkTransitions > 0, "explicit delay outputs never changed");
        Assert.True(casesWithMultiTickLinkDelay > 0, "the campaign never used an explicit delay above one tick");
        Assert.True(
            distinctSourceCases == ExplicitDelayCaseCount,
            "a gate received both feedback paths from the same source gate");
        Assert.True(
            undrivenGateOutputsAtTimeZero > 0,
            "gate outputs were defined before their delayed lookup commit");
    }

    private static int?[] MakeDrives(
        Random random,
        (string Id, int Radix)[] domains,
        int step)
    {
        var drives = new int?[domains.Length];
        bool fullyDriven = step == 0 || step == StepCount / 2;
        for (int i = 0; i < drives.Length; i++)
        {
            drives[i] = !fullyDriven && random.Next(4) == 0
                ? null
                : random.Next(domains[i].Radix);
        }

        if (!fullyDriven && step != StepCount - 1)
        {
            drives[random.Next(drives.Length)] = null;
        }

        if (step == StepCount - 1)
        {
            Array.Fill(drives, (int?)null);
        }

        return drives;
    }

    private static int?[] MakeExplicitDelayDrives(Random random, int gateCount, int step)
    {
        var drives = new int?[gateCount];
        bool fullyDriven = step == 0 || step == ExplicitDelayStepCount / 2;
        for (int gate = 0; gate < gateCount; gate++)
        {
            drives[gate] = !fullyDriven && random.Next(4) == 0
                ? null
                : random.Next(2);
        }

        if (!fullyDriven && step != ExplicitDelayStepCount - 1)
        {
            drives[random.Next(gateCount)] = null;
        }

        if (step == ExplicitDelayStepCount - 1)
        {
            Array.Fill(drives, (int?)null);
        }

        return drives;
    }

    private static (SimulationEngine Engine, int[] LinkNets) BuildExplicitDelayFeedback(
        int[][] tables,
        long[] gateDelays,
        long[] linkDelays,
        int[] linkInitials,
        int[] linkSources)
    {
        int gateCount = tables.Length;
        var builder = new ProjectBuilder("randomized explicit-delay feedback");
        DefinitionBuilder root = builder.Define("root", "Root");
        var externalNets = new Net[gateCount];
        var outputNets = new Net[gateCount];
        for (int gate = 0; gate < gateCount; gate++)
        {
            externalNets[gate] = root.AddPin(
                $"in{gate}",
                $"IN{gate}",
                PinDirection.Input,
                StandardDomains.BinaryId);
            outputNets[gate] = root.AddPin(
                $"y{gate}",
                $"Y{gate}",
                PinDirection.Output,
                StandardDomains.BinaryId);
        }

        var linkNets = new Net[linkDelays.Length];
        for (int link = 0; link < linkNets.Length; link++)
        {
            linkNets[link] = root.AddNet($"l{link}", StandardDomains.BinaryId);
            root.SetInitial(linkNets[link], linkInitials[link]);
        }

        for (int gate = 0; gate < gateCount; gate++)
        {
            string instance = $"g{gate}";
            root.AddPrimitive(instance, new LookupSpec(3, tables[gate], gateDelays[gate]));
            root.Connect(linkNets[gate * 2], root.Pin(instance, "in0"));
            root.Connect(linkNets[(gate * 2) + 1], root.Pin(instance, "in1"));
            root.Connect(externalNets[gate], root.Pin(instance, "in2"));
            root.Connect(outputNets[gate], root.Pin(instance, "out"));
        }

        for (int link = 0; link < linkNets.Length; link++)
        {
            root.AddPrimitive($"d{link}", new DelaySpec(linkDelays[link], linkInitials[link]));
        }

        for (int link = 0; link < linkNets.Length; link++)
        {
            string instance = $"d{link}";
            root.Connect(outputNets[linkSources[link]], root.Pin(instance, "in0"));
            root.Connect(linkNets[link], root.Pin(instance, "out"));
        }

        SimulationEngine engine = TestFixtures.Simulate(builder.Build("root"), "root");
        var compiledLinks = new int[linkNets.Length];
        for (int link = 0; link < linkNets.Length; link++)
        {
            if (!engine.Circuit.TryGetNet(
                new NetLocation(string.Empty, $"l{link}"),
                out compiledLinks[link]))
            {
                throw new InvalidOperationException(
                    $"Compiled circuit did not expose explicit delay net 'l{link}'.");
            }
        }

        return (engine, compiledLinks);
    }

    private static string DescribeExplicitDelayCase(
        string reason,
        int caseIndex,
        int step,
        int gateCount,
        int tapOffset,
        long[] gateDelays,
        long[] linkDelays,
        int[] linkInitials,
        int[] linkSources,
        int[][] tables,
        int?[] drives,
        int[] expectedGates,
        int[] actualGates,
        int[] expectedLinks,
        int[] actualLinks,
        SimulationEngine engine,
        string target,
        int expected,
        int actual)
    {
        string driveText = string.Join(
            ",",
            drives.Select(value => value?.ToString() ?? "undriven"));
        string expectedGateText = string.Join(",", expectedGates);
        string actualGateText = string.Join(",", actualGates);
        string expectedLinkText = string.Join(",", expectedLinks);
        string actualLinkText = string.Join(",", actualLinks);
        string gateTopology = string.Join(
            "; ",
            Enumerable.Range(0, gateCount).Select(gate =>
                $"g{gate}:delay={gateDelays[gate]}," +
                $"prev=d{gate * 2}<-g{linkSources[gate * 2]}," +
                $"tap=d{(gate * 2) + 1}<-g{linkSources[(gate * 2) + 1]}," +
                $"table=[{string.Join(",", tables[gate])}]"));
        string linkTopology = string.Join(
            "; ",
            Enumerable.Range(0, linkDelays.Length).Select(link =>
                $"d{link}:source=g{linkSources[link]},delay={linkDelays[link]}," +
                $"init={linkInitials[link]}"));

        return $"seed 0x{ExplicitDelaySeed:X8}, case {caseIndex}, step {step}, reason {reason}, " +
               $"tap offset {tapOffset}, drives [{driveText}], " +
               $"expected gates [{expectedGateText}], actual gates [{actualGateText}], " +
               $"expected links [{expectedLinkText}], actual links [{actualLinkText}], " +
               $"{target}: expected {expected}, actual {actual}, " +
               $"gate topology [{gateTopology}], link topology [{linkTopology}], " +
               $"diagnostics [{string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}]";
    }

    private static SimulationEngine BuildCascade(
        (string Id, int Radix)[] domains,
        int[][] tables,
        long[] delays,
        int feedbackInitial)
    {
        var builder = new ProjectBuilder("randomized lookup cascade");
        DefinitionBuilder root = builder.Define("root", "Root");
        var externalNets = new Net[domains.Length];
        var outputNets = new Net[domains.Length];
        for (int i = 0; i < domains.Length; i++)
        {
            externalNets[i] = root.AddPin(
                $"in{i}",
                $"IN{i}",
                PinDirection.Input,
                domains[i].Id);
            outputNets[i] = root.AddPin(
                $"y{i}",
                $"Y{i}",
                PinDirection.Output,
                StandardDomains.BinaryId);
        }

        for (int gate = 0; gate < domains.Length; gate++)
        {
            string instance = $"g{gate}";
            root.AddPrimitive(instance, new LookupSpec(2, tables[gate], delays[gate]));
            int previous = (gate + domains.Length - 1) % domains.Length;
            root.Connect(outputNets[previous], root.Pin(instance, "in0"));
            root.Connect(externalNets[gate], root.Pin(instance, "in1"));
            root.Connect(outputNets[gate], root.Pin(instance, "out"));
        }

        root.SetInitial(outputNets[domains.Length - 1], feedbackInitial);
        return TestFixtures.Simulate(builder.Build("root"), "root");
    }

    private static string Describe(
        string reason,
        int caseIndex,
        int step,
        (string Id, int Radix)[] domains,
        int[][] tables,
        long[] delays,
        int feedbackInitial,
        int?[] drives,
        int[] outputs,
        SimulationEngine engine,
        int expectedGate,
        int expected,
        int actual)
    {
        string driveText = string.Join(
            ",",
            drives.Select(value => value?.ToString() ?? "undriven"));
        string outputText = string.Join(",", outputs);
        string topology = string.Join(
            "; ",
            domains.Select((domain, gate) =>
                $"g{gate}:{domain.Id}({domain.Radix}),delay={delays[gate]}," +
                $"table=[{string.Join(",", tables[gate])}]"));

        return $"seed 0x{Seed:X8}, case {caseIndex}, step {step}, reason {reason}, " +
               $"feedback initial {feedbackInitial}, drives [{driveText}], " +
               $"reference outputs [{outputText}], " +
               (expectedGate >= 0
                   ? $"gate g{expectedGate}: expected {expected}, actual {actual}, "
                   : string.Empty) +
               $"topology [{topology}], diagnostics [{string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}]";
    }

    private enum ReferenceEventKind
    {
        Evaluate,
        Commit,
    }

    private readonly record struct ReferenceEvent(
        long Time,
        long Sequence,
        ReferenceEventKind Kind,
        int Gate,
        int Value);

    /// <summary>
    /// Independent event model of the ring. Each gate samples its two inputs when it
    /// is evaluated and schedules a commit after its own transport delay. Undriven
    /// inputs are completed by a direct nested scan of the table; a non-unanimous
    /// completion produces no commit, so the net retains its previous value.
    /// </summary>
    private sealed class CascadeReference
    {
        private readonly int _gateCount;
        private readonly int[] _radices;
        private readonly int[][] _tables;
        private readonly long[] _delays;
        private readonly int[] _outputs;
        private readonly int[] _external;
        private readonly bool[] _pendingEvaluation;
        private readonly PriorityQueue<ReferenceEvent, (long Time, long Sequence)> _queue = new();
        private long _now;
        private long _sequence;

        public CascadeReference(
            int[] radices,
            int[][] tables,
            long[] delays,
            int feedbackInitial)
        {
            _gateCount = radices.Length;
            _radices = radices;
            _tables = tables;
            _delays = delays;
            _outputs = new int[_gateCount];
            _external = new int[_gateCount];
            _pendingEvaluation = new bool[_gateCount];
            Array.Fill(_outputs, SignalValue.UndrivenIndex);
            Array.Fill(_external, SignalValue.UndrivenIndex);
            _outputs[_gateCount - 1] = feedbackInitial;

            for (int gate = 0; gate < _gateCount; gate++)
            {
                _pendingEvaluation[gate] = true;
                Enqueue(0, ReferenceEventKind.Evaluate, gate, 0);
            }
        }

        public int DeterminedEvaluations { get; private set; }

        public int AmbiguousEvaluations { get; private set; }

        public int OutputTransitions { get; private set; }

        public int OutputValue(int gate) => _outputs[gate];

        public int[] Outputs() => (int[])_outputs.Clone();

        public void Drive(int gate, int value)
        {
            if (_external[gate] == value)
            {
                return;
            }

            _external[gate] = value;
            ScheduleEvaluation(gate);
        }

        public void AdvanceTo(long target)
        {
            while (_queue.TryPeek(out ReferenceEvent next, out _) && next.Time <= target)
            {
                _queue.Dequeue();
                _now = next.Time;
                if (next.Kind == ReferenceEventKind.Evaluate)
                {
                    Evaluate(next.Gate);
                }
                else
                {
                    Commit(next.Gate, next.Value);
                }
            }

            _now = target;
        }

        private void Evaluate(int gate)
        {
            if (!_pendingEvaluation[gate])
            {
                return;
            }

            _pendingEvaluation[gate] = false;
            int ring = _outputs[(gate + _gateCount - 1) % _gateCount];
            int external = _external[gate];
            if (!TryLookup(_tables[gate], _radices[gate], ring, external, out int value))
            {
                AmbiguousEvaluations++;
                return;
            }

            DeterminedEvaluations++;
            Enqueue(
                _now + _delays[gate],
                ReferenceEventKind.Commit,
                gate,
                value);
        }

        private void Commit(int gate, int value)
        {
            if (_outputs[gate] == value)
            {
                return;
            }

            _outputs[gate] = value;
            OutputTransitions++;
            ScheduleEvaluation((gate + 1) % _gateCount);
        }

        private void ScheduleEvaluation(int gate)
        {
            if (_pendingEvaluation[gate])
            {
                return;
            }

            _pendingEvaluation[gate] = true;
            Enqueue(_now, ReferenceEventKind.Evaluate, gate, 0);
        }

        private void Enqueue(long time, ReferenceEventKind kind, int gate, int value)
        {
            var next = new ReferenceEvent(time, _sequence++, kind, gate, value);
            _queue.Enqueue(next, (next.Time, next.Sequence));
        }

        private static bool TryLookup(
            int[] table,
            int externalRadix,
            int ringValue,
            int externalValue,
            out int output)
        {
            output = 0;
            bool found = false;
            for (int ring = 0; ring < 2; ring++)
            {
                if (ringValue >= 0 && ringValue != ring)
                {
                    continue;
                }

                for (int external = 0; external < externalRadix; external++)
                {
                    if (externalValue >= 0 && externalValue != external)
                    {
                        continue;
                    }

                    int value = table[(ring * externalRadix) + external];
                    if (!found)
                    {
                        output = value;
                        found = true;
                    }
                    else if (value != output)
                    {
                        return false;
                    }
                }
            }

            return found;
        }
    }

    private enum ExplicitDelayEventKind
    {
        EvaluateGate,
        EvaluateDelay,
        CommitGate,
        CommitDelay,
    }

    private readonly record struct ExplicitDelayEvent(
        long Time,
        long Sequence,
        ExplicitDelayEventKind Kind,
        int Index,
        int Value);

    /// <summary>
    /// Independent event model of the explicit-delay feedback ring. Gates sample two
    /// different delayed feedback nets plus their external input, then commit after
    /// their own lookup delay. Each feedback edge is a separate delay component with
    /// its own initial output. Both gate outputs and every delay output are compared by
    /// the caller. As in the engine, a pending evaluation coalesces multiple commits at
    /// one timestamp, while a later commit can schedule another evaluation.
    /// </summary>
    private sealed class ExplicitDelayFeedbackReference
    {
        private readonly int _gateCount;
        private readonly int _linkCount;
        private readonly int[][] _tables;
        private readonly long[] _gateDelays;
        private readonly long[] _linkDelays;
        private readonly int[] _linkSources;
        private readonly int[] _gateValues;
        private readonly int[] _linkValues;
        private readonly int[] _external;
        private readonly bool[] _gatePending;
        private readonly bool[] _delayPending;
        private readonly PriorityQueue<ExplicitDelayEvent, (long Time, long Sequence)> _queue = new();
        private long _now;
        private long _sequence;

        public ExplicitDelayFeedbackReference(
            int[][] tables,
            long[] gateDelays,
            long[] linkDelays,
            int[] linkInitials,
            int[] linkSources)
        {
            _gateCount = tables.Length;
            _linkCount = linkDelays.Length;
            _tables = tables;
            _gateDelays = gateDelays;
            _linkDelays = linkDelays;
            _linkSources = linkSources;
            _gateValues = Enumerable.Repeat(SignalValue.UndrivenIndex, _gateCount).ToArray();
            _linkValues = (int[])linkInitials.Clone();
            _external = Enumerable.Repeat(SignalValue.UndrivenIndex, _gateCount).ToArray();
            _gatePending = new bool[_gateCount];
            _delayPending = new bool[_linkCount];

            for (int gate = 0; gate < _gateCount; gate++)
            {
                _gatePending[gate] = true;
                Enqueue(0, ExplicitDelayEventKind.EvaluateGate, gate, 0);
            }

            for (int link = 0; link < _linkCount; link++)
            {
                _delayPending[link] = true;
                Enqueue(0, ExplicitDelayEventKind.EvaluateDelay, link, 0);
            }
        }

        public int DeterminedEvaluations { get; private set; }

        public int AmbiguousEvaluations { get; private set; }

        public int GateTransitions { get; private set; }

        public int LinkTransitions { get; private set; }

        public int GateValue(int gate) => _gateValues[gate];

        public int LinkValue(int link) => _linkValues[link];

        public int[] GateOutputs() => (int[])_gateValues.Clone();

        public int[] LinkOutputs() => (int[])_linkValues.Clone();

        public void Drive(int gate, int value)
        {
            if (_external[gate] == value)
            {
                return;
            }

            _external[gate] = value;
            ScheduleGateEvaluation(gate);
        }

        public void AdvanceTo(long target)
        {
            while (_queue.TryPeek(out ExplicitDelayEvent next, out _) && next.Time <= target)
            {
                _queue.Dequeue();
                _now = next.Time;
                switch (next.Kind)
                {
                    case ExplicitDelayEventKind.EvaluateGate:
                        EvaluateGate(next.Index);
                        break;
                    case ExplicitDelayEventKind.EvaluateDelay:
                        EvaluateDelay(next.Index);
                        break;
                    case ExplicitDelayEventKind.CommitGate:
                        CommitGate(next.Index, next.Value);
                        break;
                    case ExplicitDelayEventKind.CommitDelay:
                        CommitDelay(next.Index, next.Value);
                        break;
                }
            }

            _now = target;
        }

        private void EvaluateGate(int gate)
        {
            if (!_gatePending[gate])
            {
                return;
            }

            _gatePending[gate] = false;
            int previous = _linkValues[gate * 2];
            int tap = _linkValues[(gate * 2) + 1];
            if (!TryLookup(_tables[gate], previous, tap, _external[gate], out int value))
            {
                AmbiguousEvaluations++;
                return;
            }

            DeterminedEvaluations++;
            Enqueue(
                _now + _gateDelays[gate],
                ExplicitDelayEventKind.CommitGate,
                gate,
                value);
        }

        private void CommitGate(int gate, int value)
        {
            if (_gateValues[gate] == value)
            {
                return;
            }

            _gateValues[gate] = value;
            GateTransitions++;
            for (int link = 0; link < _linkCount; link++)
            {
                if (_linkSources[link] == gate)
                {
                    ScheduleDelayEvaluation(link);
                }
            }
        }

        private void EvaluateDelay(int link)
        {
            if (!_delayPending[link])
            {
                return;
            }

            _delayPending[link] = false;
            int input = _gateValues[_linkSources[link]];
            if (input < 0)
            {
                return;
            }

            Enqueue(
                _now + _linkDelays[link],
                ExplicitDelayEventKind.CommitDelay,
                link,
                input);
        }

        private void CommitDelay(int link, int value)
        {
            if (_linkValues[link] == value)
            {
                return;
            }

            _linkValues[link] = value;
            LinkTransitions++;
            ScheduleGateEvaluation(link / 2);
        }

        private void ScheduleGateEvaluation(int gate)
        {
            if (_gatePending[gate])
            {
                return;
            }

            _gatePending[gate] = true;
            Enqueue(_now, ExplicitDelayEventKind.EvaluateGate, gate, 0);
        }

        private void ScheduleDelayEvaluation(int link)
        {
            if (_delayPending[link])
            {
                return;
            }

            _delayPending[link] = true;
            Enqueue(_now, ExplicitDelayEventKind.EvaluateDelay, link, 0);
        }

        private void Enqueue(long time, ExplicitDelayEventKind kind, int index, int value)
        {
            var next = new ExplicitDelayEvent(time, _sequence++, kind, index, value);
            _queue.Enqueue(next, (next.Time, next.Sequence));
        }

        private static bool TryLookup(
            int[] table,
            int previousValue,
            int tapValue,
            int externalValue,
            out int output)
        {
            output = 0;
            bool found = false;
            for (int previous = 0; previous < 2; previous++)
            {
                if (previousValue >= 0 && previousValue != previous)
                {
                    continue;
                }

                for (int tap = 0; tap < 2; tap++)
                {
                    if (tapValue >= 0 && tapValue != tap)
                    {
                        continue;
                    }

                    for (int external = 0; external < 2; external++)
                    {
                        if (externalValue >= 0 && externalValue != external)
                        {
                            continue;
                        }

                        int value = table[((previous * 2) + tap) * 2 + external];
                        if (!found)
                        {
                            output = value;
                            found = true;
                        }
                        else if (value != output)
                        {
                            return false;
                        }
                    }
                }
            }

            return found;
        }
    }
}
