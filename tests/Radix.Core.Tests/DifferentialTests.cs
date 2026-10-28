using Radix.Core.Model;
using Radix.Core.Signals;
using Radix.Core.Simulation;
using Xunit;

namespace Radix.Core.Tests;

public sealed class DifferentialTests
{
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
