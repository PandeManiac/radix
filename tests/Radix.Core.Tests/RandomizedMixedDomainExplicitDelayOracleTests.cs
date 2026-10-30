using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Randomized differential campaign for explicit-delay feedback with heterogeneous
/// gate domains and wider fan-in. Each gate combines two or three distinct delayed
/// feedback taps with an external input. Every link is a separate Delay primitive,
/// and both gate outputs and every internal delay output are compared at every tick
/// against an independent mixed-radix event model.
/// </summary>
public sealed class RandomizedMixedDomainExplicitDelayOracleTests
{
    private const int Seed = 0x3ED1A7;
    private const int CaseCount = 48;
    private const int StepCount = 28;

    private static readonly (string Id, int Radix)[] DomainChoices =
    [
        (StandardDomains.BinaryId, 2),
        (StandardDomains.TernaryId, 3),
        (StandardDomains.QuaternaryId, 4),
        (StandardDomains.FourStateId, 4),
    ];

    [Fact]
    public void RandomizedMixedDomainExplicitDelayWithWideFanInMatchesReference()
    {
        var random = new Random(Seed);
        int determinedEvaluations = 0;
        int ambiguousEvaluations = 0;
        int gateTransitions = 0;
        int linkTransitions = 0;
        int multiTickLinkDelayCases = 0;
        int wideFanInCases = 0;
        int mixedGateDomainCases = 0;
        int undrivenGateOutputsAtTimeZero = 0;
        var gateDomainIds = new HashSet<string>(StringComparer.Ordinal);
        var externalDomainIds = new HashSet<string>(StringComparer.Ordinal);

        for (int caseIndex = 0; caseIndex < CaseCount; caseIndex++)
        {
            int gateCount = random.Next(3, 7);
            int fanIn = gateCount == 3 ? 2 : random.Next(2, 4);
            var gateDomains = new (string Id, int Radix)[gateCount];
            gateDomains[0] = DomainChoices[random.Next(DomainChoices.Length)];
            do
            {
                gateDomains[1] = DomainChoices[random.Next(DomainChoices.Length)];
            }
            while (string.Equals(gateDomains[1].Id, gateDomains[0].Id, StringComparison.Ordinal));

            for (int gate = 2; gate < gateCount; gate++)
            {
                gateDomains[gate] = DomainChoices[random.Next(DomainChoices.Length)];
            }

            var externalDomains = new (string Id, int Radix)[gateCount];
            for (int gate = 0; gate < gateCount; gate++)
            {
                externalDomains[gate] = DomainChoices[random.Next(DomainChoices.Length)];
            }

            if (gateDomains.Select(domain => domain.Id).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                mixedGateDomainCases++;
            }

            if (fanIn == 3)
            {
                wideFanInCases++;
            }

            foreach ((string id, _) in gateDomains)
            {
                gateDomainIds.Add(id);
            }

            foreach ((string id, _) in externalDomains)
            {
                externalDomainIds.Add(id);
            }

            var gateDelays = new long[gateCount];
            for (int gate = 0; gate < gateCount; gate++)
            {
                gateDelays[gate] = random.Next(1, 3);
            }

            var feedbackSources = new int[gateCount][];
            for (int gate = 0; gate < gateCount; gate++)
            {
                feedbackSources[gate] = PickFeedbackSources(random, gate, gateCount, fanIn);
            }

            int linkCount = gateCount * fanIn;
            var linkSources = new int[linkCount];
            var linkDestinations = new int[linkCount];
            var linkDelays = new long[linkCount];
            var linkInitials = new int[linkCount];
            bool hasMultiTickLinkDelay = false;
            for (int gate = 0; gate < gateCount; gate++)
            {
                for (int tap = 0; tap < fanIn; tap++)
                {
                    int link = (gate * fanIn) + tap;
                    int source = feedbackSources[gate][tap];
                    linkSources[link] = source;
                    linkDestinations[link] = gate;
                    linkDelays[link] = random.Next(1, 4);
                    linkInitials[link] = random.Next(gateDomains[source].Radix);
                    hasMultiTickLinkDelay |= linkDelays[link] > 1;
                }
            }

            if (hasMultiTickLinkDelay)
            {
                multiTickLinkDelayCases++;
            }

            var tables = new int[gateCount][];
            for (int gate = 0; gate < gateCount; gate++)
            {
                int entries = externalDomains[gate].Radix;
                for (int tap = 0; tap < fanIn; tap++)
                {
                    entries *= gateDomains[feedbackSources[gate][tap]].Radix;
                }

                tables[gate] = new int[entries];
                for (int entry = 0; entry < entries; entry++)
                {
                    tables[gate][entry] = random.Next(gateDomains[gate].Radix);
                }
            }

            (SimulationEngine engine, int[] linkNets) = BuildFeedbackCircuit(
                gateDomains,
                externalDomains,
                tables,
                gateDelays,
                fanIn,
                linkSources,
                linkDelays,
                linkInitials);
            var reference = new MixedDomainExplicitDelayReference(
                gateDomains.Select(domain => domain.Radix).ToArray(),
                externalDomains.Select(domain => domain.Radix).ToArray(),
                tables,
                gateDelays,
                fanIn,
                linkSources,
                linkDestinations,
                linkDelays,
                linkInitials);

            for (int step = 0; step < StepCount; step++)
            {
                if (step > 0)
                {
                    engine.AdvanceTo(step);
                    reference.AdvanceTo(step);
                }

                int?[] drives = MakeDrives(random, externalDomains, step);
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
                    Assert.Fail(DescribeCase(
                        "engine halted",
                        caseIndex,
                        step,
                        fanIn,
                        gateDomains,
                        externalDomains,
                        gateDelays,
                        linkSources,
                        linkDestinations,
                        linkDelays,
                        linkInitials,
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
                        Assert.Fail(DescribeCase(
                            "gate output mismatch",
                            caseIndex,
                            step,
                            fanIn,
                            gateDomains,
                            externalDomains,
                            gateDelays,
                            linkSources,
                            linkDestinations,
                            linkDelays,
                            linkInitials,
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
                        Assert.Fail(DescribeCase(
                            "explicit delay output mismatch",
                            caseIndex,
                            step,
                            fanIn,
                            gateDomains,
                            externalDomains,
                            gateDelays,
                            linkSources,
                            linkDestinations,
                            linkDelays,
                            linkInitials,
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
                $"seed 0x{Seed:X8}, case {caseIndex}: unexpected runtime diagnostics: " +
                string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }

        Assert.True(determinedEvaluations > 0, "the campaign never exercised a determined lookup");
        Assert.True(ambiguousEvaluations > 0, "the campaign never exercised an ambiguous lookup");
        Assert.True(gateTransitions > 0, "the campaign never observed a lookup output transition");
        Assert.True(linkTransitions > 0, "explicit delay outputs never changed");
        Assert.True(multiTickLinkDelayCases > 0, "the campaign never used an explicit delay above one tick");
        Assert.True(wideFanInCases > 0, "the campaign never used three feedback taps on one gate");
        Assert.True(
            mixedGateDomainCases == CaseCount,
            "a case did not use heterogeneous lookup output domains");
        Assert.True(
            undrivenGateOutputsAtTimeZero > 0,
            "gate outputs were defined before their delayed lookup commit");
        Assert.True(
            gateDomainIds.Count == DomainChoices.Length,
            $"campaign gate domains covered [{string.Join(", ", gateDomainIds)}]");
        Assert.True(
            externalDomainIds.Count == DomainChoices.Length,
            $"campaign external domains covered [{string.Join(", ", externalDomainIds)}]");
    }

    private static int[] PickFeedbackSources(Random random, int gate, int gateCount, int fanIn)
    {
        int[] candidates = Enumerable.Range(0, gateCount)
            .Where(candidate => candidate != gate)
            .ToArray();
        for (int i = candidates.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        return candidates[..fanIn];
    }

    private static int?[] MakeDrives(
        Random random,
        (string Id, int Radix)[] domains,
        int step)
    {
        var drives = new int?[domains.Length];
        bool fullyDriven = step == 0 || step == StepCount / 2;
        for (int gate = 0; gate < drives.Length; gate++)
        {
            drives[gate] = !fullyDriven && random.Next(4) == 0
                ? null
                : random.Next(domains[gate].Radix);
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

    private static (SimulationEngine Engine, int[] LinkNets) BuildFeedbackCircuit(
        (string Id, int Radix)[] gateDomains,
        (string Id, int Radix)[] externalDomains,
        int[][] tables,
        long[] gateDelays,
        int fanIn,
        int[] linkSources,
        long[] linkDelays,
        int[] linkInitials)
    {
        int gateCount = gateDomains.Length;
        var builder = new ProjectBuilder("randomized mixed-domain explicit-delay feedback");
        DefinitionBuilder root = builder.Define("root", "Root");
        var externalNets = new Net[gateCount];
        var outputNets = new Net[gateCount];
        for (int gate = 0; gate < gateCount; gate++)
        {
            externalNets[gate] = root.AddPin(
                $"in{gate}",
                $"IN{gate}",
                PinDirection.Input,
                externalDomains[gate].Id);
            outputNets[gate] = root.AddPin(
                $"y{gate}",
                $"Y{gate}",
                PinDirection.Output,
                gateDomains[gate].Id);
        }

        var linkNets = new Net[linkSources.Length];
        for (int link = 0; link < linkNets.Length; link++)
        {
            linkNets[link] = root.AddNet($"l{link}", gateDomains[linkSources[link]].Id);
            root.SetInitial(linkNets[link], linkInitials[link]);
        }

        for (int gate = 0; gate < gateCount; gate++)
        {
            string instance = $"g{gate}";
            root.AddPrimitive(instance, new LookupSpec(fanIn + 1, tables[gate], gateDelays[gate]));
            for (int tap = 0; tap < fanIn; tap++)
            {
                root.Connect(linkNets[(gate * fanIn) + tap], root.Pin(instance, $"in{tap}"));
            }

            root.Connect(externalNets[gate], root.Pin(instance, $"in{fanIn}"));
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

    private static string DescribeCase(
        string reason,
        int caseIndex,
        int step,
        int fanIn,
        (string Id, int Radix)[] gateDomains,
        (string Id, int Radix)[] externalDomains,
        long[] gateDelays,
        int[] linkSources,
        int[] linkDestinations,
        long[] linkDelays,
        int[] linkInitials,
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
        string gateTopology = string.Join(
            "; ",
            Enumerable.Range(0, gateDomains.Length).Select(gate =>
            {
                string taps = string.Join(
                    ",",
                    Enumerable.Range(0, fanIn).Select(tap =>
                    {
                        int link = (gate * fanIn) + tap;
                        return $"in{tap}=d{link}<-g{linkSources[link]}";
                    }));
                return $"g{gate}:domain={gateDomains[gate].Id},delay={gateDelays[gate]}," +
                       $"inputs=[{taps},in{fanIn}=external({externalDomains[gate].Id})]," +
                       $"table=[{string.Join(",", tables[gate])}]";
            }));
        string linkTopology = string.Join(
            "; ",
            Enumerable.Range(0, linkSources.Length).Select(link =>
                $"d{link}:source=g{linkSources[link]},destination=g{linkDestinations[link]}," +
                $"delay={linkDelays[link]},init={linkInitials[link]}"));

        return $"seed 0x{Seed:X8}, case {caseIndex}, step {step}, reason {reason}, " +
               $"fan-in {fanIn}, drives [{driveText}], " +
               $"expected gates [{string.Join(",", expectedGates)}], " +
               $"actual gates [{string.Join(",", actualGates)}], " +
               $"expected links [{string.Join(",", expectedLinks)}], " +
               $"actual links [{string.Join(",", actualLinks)}], " +
               $"{target}: expected {expected}, actual {actual}, " +
               $"gate topology [{gateTopology}], link topology [{linkTopology}], " +
               $"diagnostics [{string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}]";
    }

    private enum EventKind
    {
        EvaluateGate,
        EvaluateDelay,
        CommitGate,
        CommitDelay,
    }

    private readonly record struct ScheduledEvent(
        long Time,
        long Sequence,
        EventKind Kind,
        int Index,
        int Value);

    /// <summary>
    /// Independent event model of the mixed-domain explicit-delay network. Gate
    /// lookups enumerate undriven input completions directly over per-input radices,
    /// delays hold on undriven sources, and pending evaluations coalesce exactly as
    /// the engine documents. Delay output nets start at their declared initial value.
    /// </summary>
    private sealed class MixedDomainExplicitDelayReference
    {
        private readonly int _gateCount;
        private readonly int _linkCount;
        private readonly int _fanIn;
        private readonly int[][] _tables;
        private readonly long[] _gateDelays;
        private readonly int[] _linkSources;
        private readonly int[] _linkDestinations;
        private readonly long[] _linkDelays;
        private readonly int[] _gateValues;
        private readonly int[] _linkValues;
        private readonly int[] _external;
        private readonly int[] _inputScratch;
        private readonly int[][] _inputRadices;
        private readonly bool[] _gatePending;
        private readonly bool[] _delayPending;
        private readonly PriorityQueue<ScheduledEvent, (long Time, long Sequence)> _queue = new();
        private long _now;
        private long _sequence;

        public MixedDomainExplicitDelayReference(
            int[] gateRadices,
            int[] externalRadices,
            int[][] tables,
            long[] gateDelays,
            int fanIn,
            int[] linkSources,
            int[] linkDestinations,
            long[] linkDelays,
            int[] linkInitials)
        {
            _gateCount = gateRadices.Length;
            _linkCount = linkSources.Length;
            _fanIn = fanIn;
            _tables = tables;
            _gateDelays = gateDelays;
            _linkSources = linkSources;
            _linkDestinations = linkDestinations;
            _linkDelays = linkDelays;
            _gateValues = Enumerable.Repeat(SignalValue.UndrivenIndex, _gateCount).ToArray();
            _linkValues = (int[])linkInitials.Clone();
            _external = Enumerable.Repeat(SignalValue.UndrivenIndex, _gateCount).ToArray();
            _inputScratch = new int[fanIn + 1];
            _inputRadices = new int[_gateCount][];
            _gatePending = new bool[_gateCount];
            _delayPending = new bool[_linkCount];

            for (int gate = 0; gate < _gateCount; gate++)
            {
                var radices = new int[fanIn + 1];
                for (int tap = 0; tap < fanIn; tap++)
                {
                    radices[tap] = gateRadices[_linkSources[(gate * fanIn) + tap]];
                }

                radices[fanIn] = externalRadices[gate];
                _inputRadices[gate] = radices;
            }

            for (int gate = 0; gate < _gateCount; gate++)
            {
                _gatePending[gate] = true;
                Enqueue(0, EventKind.EvaluateGate, gate, 0);
            }

            for (int link = 0; link < _linkCount; link++)
            {
                _delayPending[link] = true;
                Enqueue(0, EventKind.EvaluateDelay, link, 0);
            }
        }

        public int DeterminedEvaluations { get; private set; }

        public int AmbiguousEvaluations { get; private set; }

        public int GateTransitions { get; private set; }

        public int LinkTransitions { get; private set; }

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
            while (_queue.TryPeek(out ScheduledEvent next, out _) && next.Time <= target)
            {
                _queue.Dequeue();
                _now = next.Time;
                switch (next.Kind)
                {
                    case EventKind.EvaluateGate:
                        EvaluateGate(next.Index);
                        break;
                    case EventKind.EvaluateDelay:
                        EvaluateDelay(next.Index);
                        break;
                    case EventKind.CommitGate:
                        CommitGate(next.Index, next.Value);
                        break;
                    case EventKind.CommitDelay:
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
            for (int tap = 0; tap < _fanIn; tap++)
            {
                _inputScratch[tap] = _linkValues[(gate * _fanIn) + tap];
            }

            _inputScratch[_fanIn] = _external[gate];
            if (!TryLookup(_tables[gate], _inputRadices[gate], _inputScratch, out int value))
            {
                AmbiguousEvaluations++;
                return;
            }

            DeterminedEvaluations++;
            Enqueue(_now + _gateDelays[gate], EventKind.CommitGate, gate, value);
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

            Enqueue(_now + _linkDelays[link], EventKind.CommitDelay, link, input);
        }

        private void CommitDelay(int link, int value)
        {
            if (_linkValues[link] == value)
            {
                return;
            }

            _linkValues[link] = value;
            LinkTransitions++;
            ScheduleGateEvaluation(_linkDestinations[link]);
        }

        private void ScheduleGateEvaluation(int gate)
        {
            if (_gatePending[gate])
            {
                return;
            }

            _gatePending[gate] = true;
            Enqueue(_now, EventKind.EvaluateGate, gate, 0);
        }

        private void ScheduleDelayEvaluation(int link)
        {
            if (_delayPending[link])
            {
                return;
            }

            _delayPending[link] = true;
            Enqueue(_now, EventKind.EvaluateDelay, link, 0);
        }

        private void Enqueue(long time, EventKind kind, int index, int value)
        {
            var next = new ScheduledEvent(time, _sequence++, kind, index, value);
            _queue.Enqueue(next, (next.Time, next.Sequence));
        }

        private static bool TryLookup(
            int[] table,
            int[] radices,
            int[] values,
            out int output)
        {
            int result = -1;
            bool found = false;
            bool consistent = Visit(0, 0);
            output = result;
            return consistent && found;

            bool Visit(int input, int index)
            {
                if (input == radices.Length)
                {
                    int entryValue = table[index];
                    if (!found)
                    {
                        result = entryValue;
                        found = true;
                        return true;
                    }

                    return entryValue == result;
                }

                int radix = radices[input];
                int value = values[input];
                if (value >= 0)
                {
                    return value < radix && Visit(input + 1, (index * radix) + value);
                }

                for (int digit = 0; digit < radix; digit++)
                {
                    if (!Visit(input + 1, (index * radix) + digit))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
