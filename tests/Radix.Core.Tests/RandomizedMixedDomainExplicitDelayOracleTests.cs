using Radix.Core.Compilation;
using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Randomized differential campaigns for explicit-delay feedback with heterogeneous
/// gate domains and wider fan-in. The primary campaign combines two or three distinct
/// delayed feedback taps with one external input per gate. A second campaign adds two
/// or three independent external inputs per gate so lookups genuinely evaluate several
/// undriven inputs at once. A third holds lookup delays at zero while keeping positive
/// Delay primitives on every feedback edge, exercising same-timestamp evaluation and
/// commit chains. Every link is a separate Delay primitive. Gate outputs and delay
/// outputs are compared at every tick against an independent mixed-radix event model,
/// and every gate/delay transition is compared in time order against a reference trace,
/// including the bounded terminal drain.
/// </summary>
public sealed class RandomizedMixedDomainExplicitDelayOracleTests
{
    private const int Seed = 0x3ED1A7;
    private const int CaseCount = 48;
    private const int StepCount = 28;
    private const int TailStepCount = 32;
    private const int MultiUndrivenSeed = 0x3ED1B7;
    private const int MultiUndrivenCaseCount = 16;
    private const int MultiUndrivenStepCount = 20;
    private const int ZeroDelaySeed = 0x3ED1C9;
    private const int ZeroDelayCaseCount = 16;

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
        int terminalTailEvents = 0;
        int settledDuringTail = 0;
        int traceEvents = 0;
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
                externalDomains.Select(domain => new[] { domain }).ToArray(),
                tables,
                gateDelays,
                fanIn,
                linkSources,
                linkDelays,
                linkInitials);
            var reference = new MixedDomainExplicitDelayReference(
                gateDomains.Select(domain => domain.Radix).ToArray(),
                externalDomains.Select(domain => new[] { domain.Radix }).ToArray(),
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

            long eventsBeforeTail = engine.EventsProcessed;
            var tailDrives = Enumerable.Repeat<int?>(null, gateCount).ToArray();
            int tailStep = StepCount;
            while ((engine.HasPendingEvents || reference.HasPendingEvents) &&
                   tailStep < StepCount + TailStepCount)
            {
                tailStep++;
                engine.AdvanceTo(tailStep);
                reference.AdvanceTo(tailStep);

                if (engine.IsHalted)
                {
                    Assert.Fail(DescribeCase(
                        "engine halted during terminal drain",
                        caseIndex,
                        tailStep,
                        fanIn,
                        gateDomains,
                        externalDomains,
                        gateDelays,
                        linkSources,
                        linkDestinations,
                        linkDelays,
                        linkInitials,
                        tables,
                        tailDrives,
                        reference.GateOutputs(),
                        Array.Empty<int>(),
                        reference.LinkOutputs(),
                        Array.Empty<int>(),
                        engine,
                        "none",
                        expected: 0,
                        actual: 0));
                }

                int[] expectedTailGates = reference.GateOutputs();
                int[] expectedTailLinks = reference.LinkOutputs();
                var actualTailGates = new int[gateCount];
                var actualTailLinks = new int[linkCount];
                for (int gate = 0; gate < gateCount; gate++)
                {
                    actualTailGates[gate] = engine.Read($"y{gate}");
                }

                for (int link = 0; link < linkCount; link++)
                {
                    actualTailLinks[link] = engine.GetNetValue(linkNets[link]);
                }

                for (int gate = 0; gate < gateCount; gate++)
                {
                    if (actualTailGates[gate] != expectedTailGates[gate])
                    {
                        Assert.Fail(DescribeCase(
                            "gate output mismatch during terminal drain",
                            caseIndex,
                            tailStep,
                            fanIn,
                            gateDomains,
                            externalDomains,
                            gateDelays,
                            linkSources,
                            linkDestinations,
                            linkDelays,
                            linkInitials,
                            tables,
                            tailDrives,
                            expectedTailGates,
                            actualTailGates,
                            expectedTailLinks,
                            actualTailLinks,
                            engine,
                            $"g{gate}",
                            expectedTailGates[gate],
                            actualTailGates[gate]));
                    }
                }

                for (int link = 0; link < linkCount; link++)
                {
                    if (actualTailLinks[link] != expectedTailLinks[link])
                    {
                        Assert.Fail(DescribeCase(
                            "explicit delay output mismatch during terminal drain",
                            caseIndex,
                            tailStep,
                            fanIn,
                            gateDomains,
                            externalDomains,
                            gateDelays,
                            linkSources,
                            linkDestinations,
                            linkDelays,
                            linkInitials,
                            tables,
                            tailDrives,
                            expectedTailGates,
                            actualTailGates,
                            expectedTailLinks,
                            actualTailLinks,
                            engine,
                            $"d{link}",
                            expectedTailLinks[link],
                            actualTailLinks[link]));
                    }
                }
            }

            terminalTailEvents += (int)(engine.EventsProcessed - eventsBeforeTail);
            if (!engine.HasPendingEvents && !reference.HasPendingEvents)
            {
                settledDuringTail++;
            }

            traceEvents += AssertTraceMatches(engine, reference, linkNets, gateCount, Seed, caseIndex);

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
        Assert.True(terminalTailEvents > 0, "the terminal drain processed no delayed events");
        Assert.True(settledDuringTail > 0, "no mixed-domain case settled during the terminal drain");
        Assert.True(traceEvents > 0, "the mixed-domain trace comparison saw no gate or delay transitions");
    }

    [Fact]
    public void RandomizedMixedDomainMultiUndrivenExplicitDelayMatchesReference()
    {
        var random = new Random(MultiUndrivenSeed);
        int determinedEvaluations = 0;
        int ambiguousEvaluations = 0;
        int multiWildcardEvaluations = 0;
        int multiWildcardDeterminedEvaluations = 0;
        int multiWildcardAmbiguousEvaluations = 0;
        int gateTransitions = 0;
        int linkTransitions = 0;
        int multiUndrivenSteps = 0;
        int multiTickLinkDelayCases = 0;
        int terminalTailEvents = 0;
        int settledDuringTail = 0;
        int traceEvents = 0;
        var gateDomainIds = new HashSet<string>(StringComparer.Ordinal);
        var externalDomainIds = new HashSet<string>(StringComparer.Ordinal);

        for (int caseIndex = 0; caseIndex < MultiUndrivenCaseCount; caseIndex++)
        {
            int gateCount = random.Next(3, 6);
            int fanIn = gateCount == 3 ? 2 : random.Next(2, 4);
            int externalCount = random.Next(2, 4);
            var gateDomains = new (string Id, int Radix)[gateCount];
            for (int gate = 0; gate < gateCount; gate++)
            {
                gateDomains[gate] = DomainChoices[(caseIndex + gate) % DomainChoices.Length];
                gateDomainIds.Add(gateDomains[gate].Id);
            }

            var externalDomains = new (string Id, int Radix)[gateCount][];
            for (int gate = 0; gate < gateCount; gate++)
            {
                externalDomains[gate] = new (string Id, int Radix)[externalCount];
                for (int external = 0; external < externalCount; external++)
                {
                    externalDomains[gate][external] =
                        DomainChoices[(caseIndex + gate + external + 1) % DomainChoices.Length];
                    externalDomainIds.Add(externalDomains[gate][external].Id);
                }
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
                    linkDelays[link] = random.Next(1, 5);
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
                int entries = 1;
                for (int external = 0; external < externalCount; external++)
                {
                    entries *= externalDomains[gate][external].Radix;
                }

                for (int tap = 0; tap < fanIn; tap++)
                {
                    entries *= gateDomains[feedbackSources[gate][tap]].Radix;
                }

                tables[gate] = new int[entries];
                for (int entry = 0; entry < entries; entry++)
                {
                    tables[gate][entry] = random.Next(gateDomains[gate].Radix);
                }

                if (caseIndex == 0 && gate == 0)
                {
                    // Guarantee a determined lookup with more than one undriven input.
                    Array.Fill(tables[gate], 0);
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
                externalDomains.Select(domains => domains.Select(domain => domain.Radix).ToArray()).ToArray(),
                tables,
                gateDelays,
                fanIn,
                linkSources,
                linkDestinations,
                linkDelays,
                linkInitials);

            void CompareState(int compareStep, int?[][] compareDrives)
            {
                if (engine.IsHalted)
                {
                    Assert.Fail(DescribeMultiCase(
                        "engine halted",
                        caseIndex,
                        compareStep,
                        fanIn,
                        externalCount,
                        gateDomains,
                        externalDomains,
                        gateDelays,
                        linkSources,
                        linkDestinations,
                        linkDelays,
                        linkInitials,
                        tables,
                        compareDrives,
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
                        Assert.Fail(DescribeMultiCase(
                            "gate output mismatch",
                            caseIndex,
                            compareStep,
                            fanIn,
                            externalCount,
                            gateDomains,
                            externalDomains,
                            gateDelays,
                            linkSources,
                            linkDestinations,
                            linkDelays,
                            linkInitials,
                            tables,
                            compareDrives,
                            expectedGates,
                            actualGates,
                            expectedLinks,
                            actualLinks,
                            engine,
                            $"g{gate}",
                            expectedGates[gate],
                            actualGates[gate]));
                    }
                }

                for (int link = 0; link < linkCount; link++)
                {
                    if (actualLinks[link] != expectedLinks[link])
                    {
                        Assert.Fail(DescribeMultiCase(
                            "explicit delay output mismatch",
                            caseIndex,
                            compareStep,
                            fanIn,
                            externalCount,
                            gateDomains,
                            externalDomains,
                            gateDelays,
                            linkSources,
                            linkDestinations,
                            linkDelays,
                            linkInitials,
                            tables,
                            compareDrives,
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

            for (int step = 0; step < MultiUndrivenStepCount; step++)
            {
                if (step > 0)
                {
                    engine.AdvanceTo(step);
                    reference.AdvanceTo(step);
                }

                int?[][] drives = MakeMultiUndrivenDrives(
                    random,
                    externalDomains,
                    step,
                    MultiUndrivenStepCount);
                for (int gate = 0; gate < gateCount; gate++)
                {
                    for (int external = 0; external < externalCount; external++)
                    {
                        int value = drives[gate][external] ?? SignalValue.UndrivenIndex;
                        engine.Drive(ExternalPinId(gate, external), value);
                        reference.Drive(gate, external, value);
                    }
                }

                engine.AdvanceTo(step);
                reference.AdvanceTo(step);
                CompareState(step, drives);

                if (drives.Any(row => row.Count(value => value is null) >= 2))
                {
                    multiUndrivenSteps++;
                }
            }

            long eventsBeforeTail = engine.EventsProcessed;
            var tailDrives = Enumerable.Range(0, gateCount)
                .Select(_ => Enumerable.Repeat<int?>(null, externalCount).ToArray())
                .ToArray();
            int tailStep = MultiUndrivenStepCount;
            while ((engine.HasPendingEvents || reference.HasPendingEvents) &&
                   tailStep < MultiUndrivenStepCount + TailStepCount)
            {
                tailStep++;
                engine.AdvanceTo(tailStep);
                reference.AdvanceTo(tailStep);
                CompareState(tailStep, tailDrives);
            }

            terminalTailEvents += (int)(engine.EventsProcessed - eventsBeforeTail);
            if (!engine.HasPendingEvents && !reference.HasPendingEvents)
            {
                settledDuringTail++;
            }

            traceEvents += AssertTraceMatches(engine, reference, linkNets, gateCount, MultiUndrivenSeed, caseIndex);

            determinedEvaluations += reference.DeterminedEvaluations;
            ambiguousEvaluations += reference.AmbiguousEvaluations;
            multiWildcardEvaluations += reference.MultiWildcardEvaluations;
            multiWildcardDeterminedEvaluations += reference.MultiWildcardDeterminedEvaluations;
            multiWildcardAmbiguousEvaluations += reference.MultiWildcardAmbiguousEvaluations;
            gateTransitions += reference.GateTransitions;
            linkTransitions += reference.LinkTransitions;

            Assert.True(
                engine.RuntimeDiagnostics.Count == 0,
                $"seed 0x{MultiUndrivenSeed:X8}, case {caseIndex}: unexpected runtime diagnostics: " +
                string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }

        Assert.True(determinedEvaluations > 0, "the multi-undriven campaign never exercised a determined lookup");
        Assert.True(ambiguousEvaluations > 0, "the multi-undriven campaign never exercised an ambiguous lookup");
        Assert.True(
            multiWildcardEvaluations > 0,
            "the multi-undriven campaign never evaluated a lookup with several undriven inputs");
        Assert.True(
            multiWildcardDeterminedEvaluations > 0,
            "no lookup with several undriven inputs resolved to one output");
        Assert.True(
            multiWildcardAmbiguousEvaluations > 0,
            "no lookup with several undriven inputs held on disagreement");
        Assert.True(gateTransitions > 0, "the multi-undriven campaign never observed a lookup transition");
        Assert.True(linkTransitions > 0, "the multi-undriven campaign never observed a delay transition");
        Assert.True(multiTickLinkDelayCases > 0, "the multi-undriven campaign never used a delay above one tick");
        Assert.True(multiUndrivenSteps > 0, "the campaign never drove several inputs of one gate undriven at once");
        Assert.True(terminalTailEvents > 0, "the terminal drain processed no delayed events");
        Assert.True(settledDuringTail > 0, "no multi-undriven case settled during the terminal drain");
        Assert.True(traceEvents > 0, "the multi-undriven trace comparison saw no gate or delay transitions");
        Assert.True(
            gateDomainIds.Count == DomainChoices.Length,
            $"multi-undriven gate domains covered [{string.Join(", ", gateDomainIds)}]");
        Assert.True(
            externalDomainIds.Count == DomainChoices.Length,
            $"multi-undriven external domains covered [{string.Join(", ", externalDomainIds)}]");
    }

    [Fact]
    public void RandomizedMixedDomainZeroDelayBetweenExplicitDelaysMatchesReference()
    {
        var random = new Random(ZeroDelaySeed);
        int determinedEvaluations = 0;
        int ambiguousEvaluations = 0;
        int gateTransitions = 0;
        int zeroDelayGateCommits = 0;
        int linkTransitions = 0;
        int multiTickLinkDelayCases = 0;
        int terminalTailEvents = 0;
        int settledDuringTail = 0;
        int traceEvents = 0;
        var gateDomainIds = new HashSet<string>(StringComparer.Ordinal);
        var externalDomainIds = new HashSet<string>(StringComparer.Ordinal);

        for (int caseIndex = 0; caseIndex < ZeroDelayCaseCount; caseIndex++)
        {
            int gateCount = random.Next(3, 7);
            int fanIn = gateCount == 3 ? 2 : random.Next(2, 4);
            var gateDomains = new (string Id, int Radix)[gateCount];
            for (int gate = 0; gate < gateCount; gate++)
            {
                gateDomains[gate] = DomainChoices[(caseIndex + gate) % DomainChoices.Length];
                gateDomainIds.Add(gateDomains[gate].Id);
            }

            var externalDomains = new (string Id, int Radix)[gateCount];
            for (int gate = 0; gate < gateCount; gate++)
            {
                externalDomains[gate] = DomainChoices[(caseIndex + gate + 1) % DomainChoices.Length];
                externalDomainIds.Add(externalDomains[gate].Id);
            }

            var gateDelays = new long[gateCount];
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
                    linkDelays[link] = random.Next(1, 5);
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
                externalDomains.Select(domain => new[] { domain }).ToArray(),
                tables,
                gateDelays,
                fanIn,
                linkSources,
                linkDelays,
                linkInitials);
            var reference = new MixedDomainExplicitDelayReference(
                gateDomains.Select(domain => domain.Radix).ToArray(),
                externalDomains.Select(domain => new[] { domain.Radix }).ToArray(),
                tables,
                gateDelays,
                fanIn,
                linkSources,
                linkDestinations,
                linkDelays,
                linkInitials);

            void CompareState(int compareStep, int?[] compareDrives)
            {
                if (engine.IsHalted)
                {
                    Assert.Fail(DescribeCase(
                        "engine halted",
                        caseIndex,
                        compareStep,
                        fanIn,
                        gateDomains,
                        externalDomains,
                        gateDelays,
                        linkSources,
                        linkDestinations,
                        linkDelays,
                        linkInitials,
                        tables,
                        compareDrives,
                        reference.GateOutputs(),
                        Array.Empty<int>(),
                        reference.LinkOutputs(),
                        Array.Empty<int>(),
                        engine,
                        "none",
                        expected: 0,
                        actual: 0,
                        seed: ZeroDelaySeed));
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
                            compareStep,
                            fanIn,
                            gateDomains,
                            externalDomains,
                            gateDelays,
                            linkSources,
                            linkDestinations,
                            linkDelays,
                            linkInitials,
                            tables,
                            compareDrives,
                            expectedGates,
                            actualGates,
                            expectedLinks,
                            actualLinks,
                            engine,
                            $"g{gate}",
                            expectedGates[gate],
                            actualGates[gate],
                            seed: ZeroDelaySeed));
                    }
                }

                for (int link = 0; link < linkCount; link++)
                {
                    if (actualLinks[link] != expectedLinks[link])
                    {
                        Assert.Fail(DescribeCase(
                            "explicit delay output mismatch",
                            caseIndex,
                            compareStep,
                            fanIn,
                            gateDomains,
                            externalDomains,
                            gateDelays,
                            linkSources,
                            linkDestinations,
                            linkDelays,
                            linkInitials,
                            tables,
                            compareDrives,
                            expectedGates,
                            actualGates,
                            expectedLinks,
                            actualLinks,
                            engine,
                            $"d{link}",
                            expectedLinks[link],
                            actualLinks[link],
                            seed: ZeroDelaySeed));
                    }
                }
            }

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
                CompareState(step, drives);
            }

            long eventsBeforeTail = engine.EventsProcessed;
            var tailDrives = Enumerable.Repeat<int?>(null, gateCount).ToArray();
            int tailStep = StepCount;
            while ((engine.HasPendingEvents || reference.HasPendingEvents) &&
                   tailStep < StepCount + TailStepCount)
            {
                tailStep++;
                engine.AdvanceTo(tailStep);
                reference.AdvanceTo(tailStep);
                CompareState(tailStep, tailDrives);
            }

            terminalTailEvents += (int)(engine.EventsProcessed - eventsBeforeTail);
            if (!engine.HasPendingEvents && !reference.HasPendingEvents)
            {
                settledDuringTail++;
            }

            traceEvents += AssertTraceMatches(engine, reference, linkNets, gateCount, ZeroDelaySeed, caseIndex);

            determinedEvaluations += reference.DeterminedEvaluations;
            ambiguousEvaluations += reference.AmbiguousEvaluations;
            gateTransitions += reference.GateTransitions;
            zeroDelayGateCommits += reference.ZeroDelayGateCommits;
            linkTransitions += reference.LinkTransitions;

            Assert.True(
                engine.RuntimeDiagnostics.Count == 0,
                $"seed 0x{ZeroDelaySeed:X8}, case {caseIndex}: unexpected runtime diagnostics: " +
                string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }

        Assert.True(determinedEvaluations > 0, "the zero-delay campaign never exercised a determined lookup");
        Assert.True(ambiguousEvaluations > 0, "the zero-delay campaign never exercised an ambiguous lookup");
        Assert.True(gateTransitions > 0, "the zero-delay campaign never observed a lookup transition");
        Assert.True(
            zeroDelayGateCommits == gateTransitions,
            $"only {zeroDelayGateCommits} of {gateTransitions} gate transitions came from zero-delay lookups");
        Assert.True(linkTransitions > 0, "the zero-delay campaign never observed a delay transition");
        Assert.True(multiTickLinkDelayCases > 0, "the zero-delay campaign never used a delay above one tick");
        Assert.True(terminalTailEvents > 0, "the terminal drain processed no delayed events");
        Assert.True(settledDuringTail > 0, "no zero-delay case settled during the terminal drain");
        Assert.True(traceEvents > 0, "the zero-delay trace comparison saw no gate or delay transitions");
        Assert.True(
            gateDomainIds.Count == DomainChoices.Length,
            $"zero-delay gate domains covered [{string.Join(", ", gateDomainIds)}]");
        Assert.True(
            externalDomainIds.Count == DomainChoices.Length,
            $"zero-delay external domains covered [{string.Join(", ", externalDomainIds)}]");
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

    private static int?[][] MakeMultiUndrivenDrives(
        Random random,
        (string Id, int Radix)[][] domains,
        int step,
        int stepCount)
    {
        var drives = new int?[domains.Length][];
        bool fullyDriven = step == 0 || step == stepCount / 2;
        for (int gate = 0; gate < drives.Length; gate++)
        {
            drives[gate] = new int?[domains[gate].Length];
            for (int external = 0; external < drives[gate].Length; external++)
            {
                drives[gate][external] = !fullyDriven && random.Next(3) == 0
                    ? null
                    : random.Next(domains[gate][external].Radix);
            }
        }

        if (!fullyDriven && step != stepCount - 1)
        {
            int gate = random.Next(drives.Length);
            for (int external = 0; external < drives[gate].Length; external++)
            {
                drives[gate][external] = null;
            }
        }

        if (step == stepCount - 1)
        {
            foreach (int?[] gateDrives in drives)
            {
                Array.Fill(gateDrives, (int?)null);
            }
        }

        return drives;
    }

    private static string ExternalPinId(int gate, int externalIndex) =>
        externalIndex == 0 ? $"in{gate}" : $"x{gate}_{externalIndex}";

    private static (SimulationEngine Engine, int[] LinkNets) BuildFeedbackCircuit(
        (string Id, int Radix)[] gateDomains,
        (string Id, int Radix)[][] externalDomains,
        int[][] tables,
        long[] gateDelays,
        int fanIn,
        int[] linkSources,
        long[] linkDelays,
        int[] linkInitials)
    {
        int gateCount = gateDomains.Length;
        int externalCount = externalDomains[0].Length;
        var builder = new ProjectBuilder("randomized mixed-domain explicit-delay feedback");
        DefinitionBuilder root = builder.Define("root", "Root");
        var externalNets = new Net[gateCount][];
        var outputNets = new Net[gateCount];
        for (int gate = 0; gate < gateCount; gate++)
        {
            externalNets[gate] = new Net[externalCount];
            for (int external = 0; external < externalCount; external++)
            {
                string pinId = ExternalPinId(gate, external);
                externalNets[gate][external] = root.AddPin(
                    pinId,
                    pinId.ToUpperInvariant(),
                    PinDirection.Input,
                    externalDomains[gate][external].Id);
            }

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
            root.AddPrimitive(instance, new LookupSpec(fanIn + externalCount, tables[gate], gateDelays[gate]));
            for (int tap = 0; tap < fanIn; tap++)
            {
                root.Connect(linkNets[(gate * fanIn) + tap], root.Pin(instance, $"in{tap}"));
            }

            for (int external = 0; external < externalCount; external++)
            {
                root.Connect(externalNets[gate][external], root.Pin(instance, $"in{fanIn + external}"));
            }

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

    private static int AssertTraceMatches(
        SimulationEngine engine,
        MixedDomainExplicitDelayReference reference,
        int[] linkNets,
        int gateCount,
        int seed,
        int caseIndex)
    {
        Assert.False(
            engine.Trace.IsTruncated,
            $"seed 0x{seed:X8}, case {caseIndex}: trace capacity {engine.Trace.Capacity} truncated " +
            $"{engine.Trace.TotalRecorded} recorded transitions");

        var names = new Dictionary<int, string>(linkNets.Length + gateCount);
        for (int link = 0; link < linkNets.Length; link++)
        {
            names[linkNets[link]] = $"d{link}";
        }

        for (int gate = 0; gate < gateCount; gate++)
        {
            names[engine.Circuit.Ports[engine.Port($"y{gate}")].Net] = $"g{gate}";
        }

        var linkNetSet = linkNets.ToHashSet();
        var actual = new List<ReferenceNetChange>();
        foreach (NetChange change in engine.Trace)
        {
            if (!names.TryGetValue(change.Net, out string? name))
            {
                continue;
            }

            // Net construction records each initialized delay net before time-zero
            // evaluation; the reference starts from those values instead of emitting
            // a transition for them.
            if (change.Time == 0 &&
                linkNetSet.Contains(change.Net) &&
                change.OldValue == SignalValue.UndrivenIndex)
            {
                continue;
            }

            actual.Add(new ReferenceNetChange(change.Time, name, change.OldValue, change.NewValue));
        }

        IReadOnlyList<ReferenceNetChange> expected = reference.Trace;
        Assert.True(
            expected.Count == actual.Count,
            $"seed 0x{seed:X8}, case {caseIndex}: expected {expected.Count} scoped trace events, " +
            $"actual {actual.Count}");

        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i] != actual[i])
            {
                Assert.Fail(
                    $"seed 0x{seed:X8}, case {caseIndex}: trace mismatch at event {i}; " +
                    $"expected {expected[i]}, actual {actual[i]}");
            }
        }

        return actual.Count;
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
        int actual,
        int seed = Seed)
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

        return $"seed 0x{seed:X8}, case {caseIndex}, step {step}, reason {reason}, " +
               $"fan-in {fanIn}, drives [{driveText}], " +
               $"expected gates [{string.Join(",", expectedGates)}], " +
               $"actual gates [{string.Join(",", actualGates)}], " +
               $"expected links [{string.Join(",", expectedLinks)}], " +
               $"actual links [{string.Join(",", actualLinks)}], " +
               $"{target}: expected {expected}, actual {actual}, " +
               $"gate topology [{gateTopology}], link topology [{linkTopology}], " +
               $"diagnostics [{string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}]";
    }

    private static string DescribeMultiCase(
        string reason,
        int caseIndex,
        int step,
        int fanIn,
        int externalCount,
        (string Id, int Radix)[] gateDomains,
        (string Id, int Radix)[][] externalDomains,
        long[] gateDelays,
        int[] linkSources,
        int[] linkDestinations,
        long[] linkDelays,
        int[] linkInitials,
        int[][] tables,
        int?[][] drives,
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
            ", ",
            drives.SelectMany((row, gate) => row.Select((value, external) =>
                $"{ExternalPinId(gate, external)}={(value?.ToString() ?? "undriven")}")));
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
                string externals = string.Join(
                    ",",
                    Enumerable.Range(0, externalCount).Select(external =>
                        $"in{fanIn + external}={ExternalPinId(gate, external)}" +
                        $"({externalDomains[gate][external].Id})"));
                return $"g{gate}:domain={gateDomains[gate].Id},delay={gateDelays[gate]}," +
                       $"inputs=[{taps},{externals}],table=[{string.Join(",", tables[gate])}]";
            }));
        string linkTopology = string.Join(
            "; ",
            Enumerable.Range(0, linkSources.Length).Select(link =>
                $"d{link}:source=g{linkSources[link]},destination=g{linkDestinations[link]}," +
                $"delay={linkDelays[link]},init={linkInitials[link]}"));

        return $"seed 0x{MultiUndrivenSeed:X8}, case {caseIndex}, step {step}, reason {reason}, " +
               $"fan-in {fanIn}, external-count {externalCount}, drives [{driveText}], " +
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

    private readonly record struct ReferenceNetChange(
        long Time,
        string Net,
        int OldValue,
        int NewValue);

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
        private readonly int _externalCount;
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
        private readonly List<ReferenceNetChange> _trace = [];
        private readonly PriorityQueue<ScheduledEvent, (long Time, long Sequence)> _queue = new();
        private long _now;
        private long _sequence;

        public MixedDomainExplicitDelayReference(
            int[] gateRadices,
            int[][] externalRadices,
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
            _externalCount = externalRadices[0].Length;
            _tables = tables;
            _gateDelays = gateDelays;
            _linkSources = linkSources;
            _linkDestinations = linkDestinations;
            _linkDelays = linkDelays;
            _gateValues = Enumerable.Repeat(SignalValue.UndrivenIndex, _gateCount).ToArray();
            _linkValues = (int[])linkInitials.Clone();
            _external = Enumerable.Repeat(SignalValue.UndrivenIndex, _gateCount * _externalCount).ToArray();
            _inputScratch = new int[fanIn + _externalCount];
            _inputRadices = new int[_gateCount][];
            _gatePending = new bool[_gateCount];
            _delayPending = new bool[_linkCount];

            for (int gate = 0; gate < _gateCount; gate++)
            {
                var radices = new int[fanIn + _externalCount];
                for (int tap = 0; tap < fanIn; tap++)
                {
                    radices[tap] = gateRadices[_linkSources[(gate * fanIn) + tap]];
                }

                for (int external = 0; external < _externalCount; external++)
                {
                    radices[fanIn + external] = externalRadices[gate][external];
                }

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

        public int ZeroDelayGateCommits { get; private set; }

        public int MultiWildcardEvaluations { get; private set; }

        public int MultiWildcardDeterminedEvaluations { get; private set; }

        public int MultiWildcardAmbiguousEvaluations { get; private set; }

        public IReadOnlyList<ReferenceNetChange> Trace => _trace;

        public bool HasPendingEvents => _queue.Count > 0;

        public int[] GateOutputs() => (int[])_gateValues.Clone();

        public int[] LinkOutputs() => (int[])_linkValues.Clone();

        public void Drive(int gate, int value) => Drive(gate, 0, value);

        public void Drive(int gate, int externalIndex, int value)
        {
            int index = (gate * _externalCount) + externalIndex;
            if (_external[index] == value)
            {
                return;
            }

            _external[index] = value;
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

            for (int external = 0; external < _externalCount; external++)
            {
                _inputScratch[_fanIn + external] = _external[(gate * _externalCount) + external];
            }

            int undriven = 0;
            for (int input = 0; input < _inputScratch.Length; input++)
            {
                if (_inputScratch[input] < 0)
                {
                    undriven++;
                }
            }

            bool determined = TryLookup(_tables[gate], _inputRadices[gate], _inputScratch, out int value);
            if (undriven > 1)
            {
                MultiWildcardEvaluations++;
                if (determined)
                {
                    MultiWildcardDeterminedEvaluations++;
                }
                else
                {
                    MultiWildcardAmbiguousEvaluations++;
                }
            }

            if (!determined)
            {
                AmbiguousEvaluations++;
                return;
            }

            DeterminedEvaluations++;
            Enqueue(_now + _gateDelays[gate], EventKind.CommitGate, gate, value);
        }

        private void CommitGate(int gate, int value)
        {
            int oldValue = _gateValues[gate];
            if (oldValue == value)
            {
                return;
            }

            _gateValues[gate] = value;
            _trace.Add(new ReferenceNetChange(_now, $"g{gate}", oldValue, value));
            GateTransitions++;
            if (_gateDelays[gate] == 0)
            {
                ZeroDelayGateCommits++;
            }
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
            int oldValue = _linkValues[link];
            if (oldValue == value)
            {
                return;
            }

            _linkValues[link] = value;
            _trace.Add(new ReferenceNetChange(_now, $"d{link}", oldValue, value));
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
