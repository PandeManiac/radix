using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Differential campaign for conservative lookup evaluation. Each case has at least
/// two different input domains and a random table. The oracle is deliberately
/// independent of the runtime's completion scan: it decodes every table entry,
/// keeps only entries matching the currently driven digits, and requires all
/// remaining possible outputs to agree. Patterns exercise fully driven, partially
/// driven, and fully undriven inputs; an ambiguous step must retain the previous
/// output exactly as the specification requires.
/// </summary>
public sealed class RandomizedLookupOracleTests
{
    private const int Seed = 0x51A7_2026;
    private const int CaseCount = 200;
    private const int StepsPerCase = 7;

    private static readonly (string Id, int Radix)[] DomainChoices =
    [
        (StandardDomains.BinaryId, 2),
        (StandardDomains.TernaryId, 3),
        (StandardDomains.QuaternaryId, 4),
        (StandardDomains.FourStateId, 4),
    ];

    [Fact]
    public void RandomizedMixedDomainPartialDrivesMatchBruteForceOracle()
    {
        var random = new Random(Seed);
        int determinedSteps = 0;
        int ambiguousSteps = 0;

        for (int caseIndex = 0; caseIndex < CaseCount; caseIndex++)
        {
            int inputCount = random.Next(2, 6);
            var domains = new (string Id, int Radix)[inputCount];
            domains[0] = DomainChoices[random.Next(DomainChoices.Length)];
            do
            {
                domains[1] = DomainChoices[random.Next(DomainChoices.Length)];
            }
            while (string.Equals(domains[1].Id, domains[0].Id, StringComparison.Ordinal));

            for (int i = 2; i < inputCount; i++)
            {
                domains[i] = DomainChoices[random.Next(DomainChoices.Length)];
            }

            (string Id, int Radix) outputDomain = DomainChoices[random.Next(DomainChoices.Length)];
            var radices = new int[inputCount];
            int tableSize = 1;
            for (int i = 0; i < inputCount; i++)
            {
                radices[i] = domains[i].Radix;
                tableSize *= radices[i];
            }

            var table = new int[tableSize];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = random.Next(outputDomain.Radix);
            }

            SimulationEngine engine = BuildLookup(domains, outputDomain.Id, table);
            int expected = SignalValue.UndrivenIndex;

            for (int step = 0; step < StepsPerCase; step++)
            {
                if (step > 0)
                {
                    engine.AdvanceTo(step);
                }

                int?[] drives = MakeDrives(random, domains, step);
                for (int i = 0; i < inputCount; i++)
                {
                    engine.Drive($"in{i}", drives[i] ?? SignalValue.UndrivenIndex);
                }

                engine.AdvanceTo(step);

                bool determined = TryOracle(radices, table, drives, out int oracleOutput);
                if (determined)
                {
                    expected = oracleOutput;
                    determinedSteps++;
                }
                else
                {
                    ambiguousSteps++;
                }

                int actual = engine.Read("y");
                if (engine.IsHalted || actual != expected)
                {
                    Assert.Fail(Describe(
                        caseIndex,
                        step,
                        domains,
                        outputDomain.Id,
                        table,
                        drives,
                        determined,
                        oracleOutput,
                        expected,
                        actual,
                        engine.HaltReason));
                }
            }

            Assert.True(
                engine.RuntimeDiagnostics.Count == 0,
                $"seed 0x{Seed:X8}, case {caseIndex}: unexpected runtime diagnostics: " +
                string.Join("; ", engine.RuntimeDiagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }

        Assert.True(determinedSteps > 0, "the campaign never exercised a determined output");
        Assert.True(ambiguousSteps > 0, "the campaign never exercised an ambiguous output");
    }

    /// <summary>
    /// Brute-force oracle: decode every table index, reject entries inconsistent with a
    /// driven input, and collect the outputs of all remaining completions. No completion
    /// enumeration or stride logic is shared with the runtime.
    /// </summary>
    private static bool TryOracle(int[] radices, int[] table, int?[] drives, out int output)
    {
        output = 0;
        bool found = false;
        for (int entry = 0; entry < table.Length; entry++)
        {
            int remainder = entry;
            bool matches = true;
            for (int i = radices.Length - 1; i >= 0; i--)
            {
                int digit = remainder % radices[i];
                remainder /= radices[i];
                if (drives[i] is int driven && driven != digit)
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            int value = table[entry];
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

        return found;
    }

    private static int?[] MakeDrives(
        Random random,
        (string Id, int Radix)[] domains,
        int step)
    {
        var drives = new int?[domains.Length];
        bool fullyDriven = step == 0 || step == StepsPerCase / 2;
        for (int i = 0; i < drives.Length; i++)
        {
            drives[i] = !fullyDriven && random.Next(4) == 0
                ? null
                : random.Next(domains[i].Radix);
        }

        if (!fullyDriven && step != StepsPerCase - 1)
        {
            // Partial patterns must actually contain an undriven input.
            drives[random.Next(drives.Length)] = null;
        }

        if (step == StepsPerCase - 1)
        {
            Array.Fill(drives, (int?)null);
        }

        return drives;
    }

    private static SimulationEngine BuildLookup(
        (string Id, int Radix)[] domains,
        string outputDomain,
        int[] table)
    {
        var builder = new ProjectBuilder("randomized lookup oracle");
        DefinitionBuilder root = builder.Define("root", "Root");
        var inputNets = new Net[domains.Length];
        for (int i = 0; i < domains.Length; i++)
        {
            inputNets[i] = root.AddPin($"in{i}", $"IN{i}", PinDirection.Input, domains[i].Id);
        }

        Net output = root.AddPin("y", "Y", PinDirection.Output, outputDomain);
        root.AddPrimitive("g", new LookupSpec(domains.Length, table));
        for (int i = 0; i < domains.Length; i++)
        {
            root.Connect(inputNets[i], root.Pin("g", $"in{i}"));
        }

        root.Connect(output, root.Pin("g", "out"));
        return TestFixtures.Simulate(builder.Build("root"), "root");
    }

    private static string Describe(
        int caseIndex,
        int step,
        (string Id, int Radix)[] domains,
        string outputDomain,
        int[] table,
        int?[] drives,
        bool determined,
        int oracleOutput,
        int expected,
        int actual,
        string? haltReason)
    {
        string tableText = table.Length <= 48
            ? string.Join(",", table)
            : string.Join(",", table.Take(48)) + $",...({table.Length} entries)";
        string driveText = string.Join(
            ",",
            drives.Select(value => value?.ToString() ?? "undriven"));
        string inputText = string.Join(
            ",",
            domains.Select(domain => $"{domain.Id}:{domain.Radix}"));

        return $"seed 0x{Seed:X8}, case {caseIndex}, step {step}, inputs [{inputText}], " +
               $"output '{outputDomain}', table [{tableText}], drives [{driveText}], " +
               $"oracle {(determined ? oracleOutput.ToString() : "ambiguous")}, " +
               $"expected {expected}, actual {actual}" +
               (haltReason is null ? string.Empty : $", halt reason: {haltReason}");
    }
}
