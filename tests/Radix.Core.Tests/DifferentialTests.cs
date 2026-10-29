using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class DifferentialTests
{

    [Theory]
    [InlineData(StandardDomains.TernaryId, 3, 0x7E44)]
    [InlineData(StandardDomains.QuaternaryId, 4, 0x4A11)]
    [InlineData(StandardDomains.BalancedTernaryId, 3, 0xB31A)]
    [InlineData(StandardDomains.FourStateId, 4, 0xF0E5)]
    public void RandomMultiValueNetworksMatchReferenceEvaluator(string domainId, int radix, int seed)
    {
        const int inputCount = 4;
        const int width = 4;
        const int layers = 4;
        var random = new Random(seed);
        int tableSize = radix * radix;

        var builder = new ProjectBuilder($"differential-{domainId}");
        var root = builder.Define("root", "Root");
        var inputNets = new Net[inputCount];
        for (int i = 0; i < inputCount; i++)
        {
            inputNets[i] = root.AddPin($"i{i}", $"I{i}", PinDirection.Input, domainId);
        }

        var outputNets = new Net[width];
        for (int i = 0; i < width; i++)
        {
            outputNets[i] = root.AddPin($"o{i}", $"O{i}", PinDirection.Output, domainId);
        }

        var sourcesA = new int[layers][];
        var sourcesB = new int[layers][];
        var tables = new int[layers][][];
        Net[] previous = inputNets;

        for (int layer = 0; layer < layers; layer++)
        {
            sourcesA[layer] = new int[width];
            sourcesB[layer] = new int[width];
            tables[layer] = new int[width][];
            var current = new Net[width];

            for (int gate = 0; gate < width; gate++)
            {
                sourcesA[layer][gate] = random.Next(previous.Length);
                sourcesB[layer][gate] = random.Next(previous.Length);
                tables[layer][gate] = Enumerable.Range(0, tableSize).Select(_ => random.Next(radix)).ToArray();

                Net output = layer == layers - 1
                    ? outputNets[gate]
                    : root.AddNet($"l{layer}_{gate}", domainId);
                string instance = $"g{layer}_{gate}";
                root.AddPrimitive(instance, new LookupSpec(2, tables[layer][gate], delayTicks: 0));
                root.Connect(previous[sourcesA[layer][gate]], root.Pin(instance, "in0"));
                root.Connect(previous[sourcesB[layer][gate]], root.Pin(instance, "in1"));
                root.Connect(output, root.Pin(instance, "out"));
                current[gate] = output;
            }

            previous = current;
        }

        SimulationEngine engine = TestFixtures.Simulate(builder.Build("root"));

        for (int trial = 0; trial < 20; trial++)
        {
            int[] inputs = Enumerable.Range(0, inputCount).Select(_ => random.Next(radix)).ToArray();
            for (int i = 0; i < inputCount; i++)
            {
                engine.Drive($"i{i}", inputs[i]);
            }

            engine.AdvanceTo(0);

            int[] values = inputs;
            for (int layer = 0; layer < layers; layer++)
            {
                var next = new int[width];
                for (int gate = 0; gate < width; gate++)
                {
                    int index = (values[sourcesA[layer][gate]] * radix) + values[sourcesB[layer][gate]];
                    next[gate] = tables[layer][gate][index];
                }

                values = next;
            }

            int[] actual = Enumerable.Range(0, width).Select(i => engine.Read($"o{i}")).ToArray();
            string context = $"domain '{domainId}', seed 0x{seed:X}, trial {trial}";
            Assert.False(engine.IsHalted, context + ": " + engine.HaltReason);
            Assert.True(
                values.SequenceEqual(actual),
                $"{context}: expected [{string.Join(",", values)}] but read [{string.Join(",", actual)}]");
        }
    }

    [Theory]
    [InlineData("dff-demo.radix")]
    [InlineData("binary-counter.radix")]
    [InlineData("ternary-counter.radix")]
    [InlineData("quaternary-counter.radix")]
    [InlineData("balanced-ternary-accumulator.radix")]
    [InlineData("four-state-bus.radix")]
    public void ExampleReplayAcrossSerializationIsIdentical(string fileName)
    {
        ExampleProject example = ExampleProjects.All().Single(e => e.FileName == fileName);
        CircuitProject reloaded = ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(example.Project));
        var options = new SimulationOptions
        {
            MaxRecordedChanges = 100_000,
            RecordTrace = true,
        };

        SimulationEngine first = TestFixtures.Simulate(example.Project, example.Project.RootDefinitionId, options);
        SimulationEngine second = TestFixtures.Simulate(reloaded, reloaded.RootDefinitionId, options);
        int seed = StableSeed(fileName);
        var random = new Random(seed);
        CircuitDefinition definition = example.Project.FindDefinition(example.Project.RootDefinitionId)!;
        PinDeclaration[] inputs = definition.Pins
            .Where(pin => pin.Direction == PinDirection.Input)
            .ToArray();
        string context = $"example '{fileName}', seed {seed}";

        for (int step = 1; step <= 20; step++)
        {
            if (inputs.Length > 0)
            {
                PinDeclaration pin = inputs[random.Next(inputs.Length)];
                SignalDomain domain = example.Project.FindDomain(pin.DomainId)!;
                int value = random.Next(domain.ValueCount);
                int firstPort = first.FindPortIndex(pin.Id);
                int secondPort = second.FindPortIndex(pin.Id);
                Assert.True(
                    firstPort >= 0 && secondPort >= 0,
                    $"{context}: input pin '{pin.Id}' is not a simulation port");
                first.SetPortValue(firstPort, value);
                second.SetPortValue(secondPort, value);
            }

            long target = step * 10L;
            first.AdvanceTo(target);
            second.AdvanceTo(target);

            Assert.Equal(first.Now, second.Now);
            Assert.True(
                first.EventsProcessed == second.EventsProcessed,
                $"{context}, step {step}: events {first.EventsProcessed} vs {second.EventsProcessed}");
            Assert.Equal(first.IsHalted, second.IsHalted);
        }

        Assert.Equal(first.Trace.Count, second.Trace.Count);
        for (int i = 0; i < first.Trace.Count; i++)
        {
            Assert.Equal(first.Trace[i], second.Trace[i]);
        }

        Assert.Equal(first.Circuit.Nets.Count, second.Circuit.Nets.Count);
        for (int net = 0; net < first.Circuit.Nets.Count; net++)
        {
            Assert.True(
                first.GetNetValue(net) == second.GetNetValue(net),
                $"{context}: net {net} differs at t={first.Now}");
        }
    }

    private static int StableSeed(string value)
    {
        int hash = 17;
        foreach (char character in value)
        {
            hash = unchecked((hash * 31) + character);
        }

        return hash & 0x7FFF_FFFF;
    }

    [Fact]
    public void RandomBinaryNetworksMatchReferenceEvaluator()
    {
        const int inputCount = 6;
        const int width = 6;
        const int layers = 5;
        var random = new Random(0x5EED);

        var builder = new ProjectBuilder("differential-binary");
        var root = builder.Define("root", "Root");
        var inputNets = new Net[inputCount];
        for (int i = 0; i < inputCount; i++)
        {
            inputNets[i] = root.AddPin($"i{i}", $"I{i}", PinDirection.Input, StandardDomains.BinaryId);
        }

        var outputNets = new Net[width];
        for (int i = 0; i < width; i++)
        {
            outputNets[i] = root.AddPin($"o{i}", $"O{i}", PinDirection.Output, StandardDomains.BinaryId);
        }

        var sourcesA = new int[layers][];
        var sourcesB = new int[layers][];
        var tables = new int[layers][][];
        Net[] previous = inputNets;

        for (int layer = 0; layer < layers; layer++)
        {
            sourcesA[layer] = new int[width];
            sourcesB[layer] = new int[width];
            tables[layer] = new int[width][];
            var current = new Net[width];

            for (int gate = 0; gate < width; gate++)
            {
                sourcesA[layer][gate] = random.Next(previous.Length);
                sourcesB[layer][gate] = random.Next(previous.Length);
                tables[layer][gate] = Enumerable.Range(0, 4).Select(_ => random.Next(2)).ToArray();

                Net output = layer == layers - 1
                    ? outputNets[gate]
                    : root.AddNet($"l{layer}_{gate}", StandardDomains.BinaryId);

                string instance = $"g{layer}_{gate}";
                root.AddPrimitive(instance, new LookupSpec(2, tables[layer][gate], delayTicks: 0));
                root.Connect(previous[sourcesA[layer][gate]], root.Pin(instance, "in0"));
                root.Connect(previous[sourcesB[layer][gate]], root.Pin(instance, "in1"));
                root.Connect(output, root.Pin(instance, "out"));
                current[gate] = output;
            }

            previous = current;
        }

        SimulationEngine engine = TestFixtures.Simulate(builder.Build("root"));

        for (int trial = 0; trial < 30; trial++)
        {
            int[] inputs = Enumerable.Range(0, inputCount).Select(_ => random.Next(2)).ToArray();
            for (int i = 0; i < inputCount; i++)
            {
                engine.Drive($"i{i}", inputs[i]);
            }

            engine.AdvanceTo(0);

            int[] expected = EvaluateBinary(inputs, sourcesA, sourcesB, tables, width, layers);
            int[] actual = Enumerable.Range(0, width).Select(i => engine.Read($"o{i}")).ToArray();

            Assert.False(engine.IsHalted, engine.HaltReason);
            Assert.Equal(expected, actual);
        }
    }

    private static int[] EvaluateBinary(
        int[] inputs,
        int[][] sourcesA,
        int[][] sourcesB,
        int[][][] tables,
        int width,
        int layers)
    {
        int[] values = inputs;
        for (int layer = 0; layer < layers; layer++)
        {
            var next = new int[width];
            for (int gate = 0; gate < width; gate++)
            {
                int index = (values[sourcesA[layer][gate]] * 2) + values[sourcesB[layer][gate]];
                next[gate] = tables[layer][gate][index];
            }

            values = next;
        }

        return values;
    }

    [Fact]
    public void RandomTernaryNetworksMatchMixedRadixReference()
    {
        const int inputCount = 4;
        const int width = 4;
        const int layers = 4;
        var random = new Random(0x7E44);

        var builder = new ProjectBuilder("differential-ternary");
        var root = builder.Define("root", "Root");
        var inputNets = new Net[inputCount];
        for (int i = 0; i < inputCount; i++)
        {
            inputNets[i] = root.AddPin($"i{i}", $"I{i}", PinDirection.Input, StandardDomains.TernaryId);
        }

        var outputNets = new Net[width];
        for (int i = 0; i < width; i++)
        {
            outputNets[i] = root.AddPin($"o{i}", $"O{i}", PinDirection.Output, StandardDomains.TernaryId);
        }

        var sourcesA = new int[layers][];
        var sourcesB = new int[layers][];
        var tables = new int[layers][][];
        Net[] previous = inputNets;

        for (int layer = 0; layer < layers; layer++)
        {
            sourcesA[layer] = new int[width];
            sourcesB[layer] = new int[width];
            tables[layer] = new int[width][];
            var current = new Net[width];

            for (int gate = 0; gate < width; gate++)
            {
                sourcesA[layer][gate] = random.Next(previous.Length);
                sourcesB[layer][gate] = random.Next(previous.Length);
                tables[layer][gate] = Enumerable.Range(0, 9).Select(_ => random.Next(3)).ToArray();

                Net output = layer == layers - 1
                    ? outputNets[gate]
                    : root.AddNet($"l{layer}_{gate}", StandardDomains.TernaryId);
                string instance = $"g{layer}_{gate}";
                root.AddPrimitive(instance, new LookupSpec(2, tables[layer][gate], delayTicks: 0));
                root.Connect(previous[sourcesA[layer][gate]], root.Pin(instance, "in0"));
                root.Connect(previous[sourcesB[layer][gate]], root.Pin(instance, "in1"));
                root.Connect(output, root.Pin(instance, "out"));
                current[gate] = output;
            }

            previous = current;
        }

        SimulationEngine engine = TestFixtures.Simulate(builder.Build("root"));

        for (int trial = 0; trial < 30; trial++)
        {
            int[] inputs = Enumerable.Range(0, inputCount).Select(_ => random.Next(3)).ToArray();
            for (int i = 0; i < inputCount; i++)
            {
                engine.Drive($"i{i}", inputs[i]);
            }

            engine.AdvanceTo(0);

            int[] values = inputs;
            for (int layer = 0; layer < layers; layer++)
            {
                var next = new int[width];
                for (int gate = 0; gate < width; gate++)
                {
                    int index = (values[sourcesA[layer][gate]] * 3) + values[sourcesB[layer][gate]];
                    next[gate] = tables[layer][gate][index];
                }

                values = next;
            }

            int[] actual = Enumerable.Range(0, width).Select(i => engine.Read($"o{i}")).ToArray();
            Assert.Equal(values, actual);
        }
    }
}
