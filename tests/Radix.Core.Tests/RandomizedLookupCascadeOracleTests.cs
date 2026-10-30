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
}
