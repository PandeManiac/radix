using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Radix.Cli.Tests;

/// <summary>
/// The machine-readable report is a contract. Tooling reads these names and this exit code, so a
/// rename or a relaxed status would break callers without failing anything in the suite - which is
/// exactly what happened while the timing block was being added. These tests run the built binary, so
/// they cover argument parsing and the process exit code as well as the JSON shape.
/// </summary>
public sealed class DriveJsonContractTests
{
    private const string Sweep = "examples/electrical-ternary-sweep.radix";

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Radix.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string Configuration() =>
        AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

    private static (int Exit, string Output, string Error) Run(params string[] arguments)
    {
        string root = Root();
        string cli = Path.Combine(root, "src", "Radix.Cli", "bin", Configuration(), "net10.0", "radix.dll");
        Assert.True(File.Exists(cli), $"The CLI was not built at {cli}.");

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        start.ArgumentList.Add(cli);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("dotnet did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result, error.Result);
    }

    [Fact]
    public void TheSweepReportsItsMeasuredTimingAndAgrees()
    {
        (int exit, string output, string error) =
            Run("drive", Sweep, "--simulate", "--stop", "2.5e-7", "--steps", "2500", "--json");

        Assert.True(exit == 0, $"exit {exit}; {error}");
        using JsonDocument report = JsonDocument.Parse(output);
        JsonElement timing = report.RootElement.GetProperty("timing");

        Assert.Equal("a", timing.GetProperty("input").GetString());
        Assert.Equal("y", timing.GetProperty("output").GetString());
        Assert.Equal(0, timing.GetProperty("declaredTicks").GetInt32());
        Assert.Equal(0, timing.GetProperty("toleranceTicks").GetInt32());
        Assert.True(timing.GetProperty("agreed").GetBoolean());

        int[] ticks = [.. timing.GetProperty("steps").EnumerateArray()
            .Select(step => step.GetProperty("ticks").GetInt32())];
        int[] tolerances = [.. timing.GetProperty("steps").EnumerateArray()
            .Select(step => step.GetProperty("tolerance").GetInt32())];

        Assert.Equal(new[] { 23, 44 }, ticks);
        Assert.Equal(new[] { 23, 44 }, tolerances);

        Assert.Empty(timing.GetProperty("mismatches").EnumerateArray());
        Assert.Empty(timing.GetProperty("unusedTolerances").EnumerateArray());
        Assert.Empty(timing.GetProperty("unknownPinTolerances").EnumerateArray());
    }

    [Fact]
    public void AToleranceNamingAnUnknownPinFailsTheJsonRun()
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), Sweep)))!;
        root["electricalCircuits"]!.AsArray()[0]!["delayTolerances"]!.AsArray().Add(new JsonObject
        {
            ["pin"] = "zzz",
            ["from"] = 2,
            ["to"] = 1,
            ["ticks"] = 0,
        });

        string path = Path.Combine(Path.GetTempPath(), $"radix-json-{Guid.NewGuid():N}.radix");
        try
        {
            File.WriteAllText(path, root.ToJsonString());
            (int exit, string output, string error) =
                Run("drive", path, "--simulate", "--stop", "2.5e-7", "--steps", "2500", "--json");

            // A tolerance that can never apply is a mistake in the file, so the JSON path has to
            // fail the way the text path does.
            Assert.True(exit == 1, $"exit {exit}; {error}");
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Single(report.RootElement
                .GetProperty("timing")
                .GetProperty("unknownPinTolerances")
                .EnumerateArray());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
