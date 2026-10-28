using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Core.Tests;

internal static class TestCircuits
{
    public static CircuitProject ValidProject()
    {
        var builder = new ProjectBuilder("valid");
        var root = builder.Define("main", "Main");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("buf", new LookupSpec(1, [0, 1]));
        root.Connect(a, root.Pin("buf", "in0"));
        root.Connect(y, root.Pin("buf", "out"));
        return builder.Build("main");
    }

    /// <summary>A root definition with the given lookup and matching binary input/output pins.</summary>
    public static CircuitProject LookupRoot(int inputs, int[] table, long delay = 0)
    {
        if (inputs is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(inputs));
        }

        var builder = new ProjectBuilder("lookup");
        var root = builder.Define("root", "Root");
        var inputNets = new Net[inputs];
        inputNets[0] = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        if (inputs == 2)
        {
            inputNets[1] = root.AddPin("b", "B", PinDirection.Input, StandardDomains.BinaryId);
        }

        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("g", new LookupSpec(inputs, table, delay));
        for (int i = 0; i < inputs; i++)
        {
            root.Connect(inputNets[i], root.Pin("g", $"in{i}"));
        }

        root.Connect(y, root.Pin("g", "out"));
        return builder.Build("root");
    }

    /// <summary>A chain of identity buffers with configurable per-stage delay.</summary>
    public static CircuitProject BufferChain(int length, long delay = 0)
    {
        var builder = new ProjectBuilder("chain");
        var root = builder.Define("root", "Chain");
        Net input = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net output = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);

        Net current = input;
        for (int i = 0; i < length; i++)
        {
            Net next = i == length - 1 ? output : root.AddNet($"w{i}", StandardDomains.BinaryId);
            string instance = $"g{i}";
            root.AddPrimitive(instance, new LookupSpec(1, [0, 1], delay));
            root.Connect(current, root.Pin(instance, "in0"));
            root.Connect(next, root.Pin(instance, "out"));
            current = next;
        }

        return builder.Build("root");
    }

    /// <summary>A root definition whose only pin is driven by a constant.</summary>
    public static CircuitProject ConstantRoot(int value, string domainId = StandardDomains.BinaryId)
    {
        var builder = new ProjectBuilder("constant");
        var root = builder.Define("root", "Constant");
        Net y = root.AddPin("y", "Y", PinDirection.Output, domainId);
        root.AddPrimitive("c", new ConstantSpec(value));
        root.Connect(y, root.Pin("c", "out"));
        return builder.Build("root");
    }

    /// <summary>A clock waveform on a root output pin.</summary>
    public static CircuitProject ClockRoot(IEnumerable<long> durations, IEnumerable<int> values)
    {
        var builder = new ProjectBuilder("clock");
        var root = builder.Define("root", "Clock");
        Net y = root.AddPin("y", "Y", PinDirection.Output, StandardDomains.BinaryId);
        root.AddPrimitive("w", new WaveformSpec(durations, values));
        root.Connect(y, root.Pin("w", "out"));
        return builder.Build("root");
    }

    public static string ErrorCodes(Core.DiagnosticBag bag) =>
        string.Join(",", bag.Where(d => d.Severity == Core.DiagnosticSeverity.Error).Select(d => d.Code));
}
