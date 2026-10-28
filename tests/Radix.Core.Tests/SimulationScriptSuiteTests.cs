using Radix.Core.Model;
using Radix.Core.Testing;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class SimulationScriptSuiteTests
{
    private const string PassingScript =
        """
        {
          "format": "radix-test",
          "version": 1,
          "name": "passing",
          "steps": [
            { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 0 } }
          ]
        }
        """;

    private const string FailingScript =
        """
        {
          "format": "radix-test",
          "version": 1,
          "name": "failing",
          "steps": [
            { "at": 0, "set": { "clk": 0, "d": 1 }, "expect": { "q": 1 } }
          ]
        }
        """;

    private const string MalformedScript =
        """
        {
          "format": "radix-test",
          "version": 1,
          "steps": [ { "at": 0, "set": { "d": true } } ]
        }
        """;

    [Fact]
    public void DiscoverFindsScriptsRecursivelyInOrdinalOrder()
    {
        string directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            string nested = Path.Combine(directory, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(directory, "b.radixtest"), PassingScript);
            File.WriteAllText(Path.Combine(directory, "a.radixtest"), PassingScript);
            File.WriteAllText(Path.Combine(nested, "c.radixtest"), PassingScript);
            File.WriteAllText(Path.Combine(directory, "ignore.txt"), PassingScript);

            IReadOnlyList<string> scripts = SimulationScriptSuiteRunner.Discover(directory);

            Assert.Equal(3, scripts.Count);
            Assert.EndsWith("a.radixtest", scripts[0], StringComparison.Ordinal);
            Assert.EndsWith("b.radixtest", scripts[1], StringComparison.Ordinal);
            Assert.EndsWith(Path.Combine("nested", "c.radixtest"), scripts[2], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DiscoverRejectsAMissingDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "radix-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => SimulationScriptSuiteRunner.Discover(directory));
    }

    [Fact]
    public void SuiteRunsEveryScriptAndAggregatesPassFailureAndError()
    {
        string directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "fail.radixtest"), FailingScript);
            File.WriteAllText(Path.Combine(directory, "bad.radixtest"), MalformedScript);
            File.WriteAllText(Path.Combine(directory, "pass.radixtest"), PassingScript);

            CircuitProject project = ExampleProjects.CreateDFlipFlopDemo();
            SimulationScriptSuiteResult suite = SimulationScriptSuiteRunner.RunFiles(
                project,
                SimulationScriptSuiteRunner.Discover(directory));

            Assert.Equal(3, suite.Entries.Count);
            Assert.False(suite.Passed);
            Assert.True(suite.HasErrors);
            Assert.Equal(1, suite.PassedCount);
            Assert.Equal(1, suite.FailedCount);
            Assert.Equal(1, suite.ErrorCount);
            Assert.True(suite.HasFailures);

            SimulationScriptSuiteEntry bad = suite.Entries[0];
            Assert.EndsWith("bad.radixtest", bad.Path, StringComparison.Ordinal);
            Assert.NotNull(bad.Error);
            Assert.Null(bad.Result);
            Assert.Contains("must be a number, a string, or null", bad.Error, StringComparison.Ordinal);

            SimulationScriptSuiteEntry failed = suite.Entries[1];
            Assert.EndsWith("fail.radixtest", failed.Path, StringComparison.Ordinal);
            Assert.NotNull(failed.Result);
            Assert.False(failed.Result!.Passed);
            Assert.Contains("pin 'q'", failed.Result.Failures[0].Message, StringComparison.Ordinal);

            SimulationScriptSuiteEntry passed = suite.Entries[2];
            Assert.EndsWith("pass.radixtest", passed.Path, StringComparison.Ordinal);
            Assert.True(passed.Passed);
            Assert.Equal("passing", passed.ScriptName);
            Assert.Equal(1, passed.Result!.StepsExecuted);
            Assert.True(suite.StepsExecuted >= 2);
            Assert.True(suite.EventsProcessed > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SuitePassesWhenEveryScriptPasses()
    {
        string directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "one.radixtest"), PassingScript);
            File.WriteAllText(Path.Combine(directory, "two.radixtest"), PassingScript);

            CircuitProject project = ExampleProjects.CreateDFlipFlopDemo();
            SimulationScriptSuiteResult suite = SimulationScriptSuiteRunner.RunFiles(
                project,
                SimulationScriptSuiteRunner.Discover(directory));

            Assert.True(suite.Passed);
            Assert.Equal(2, suite.PassedCount);
            Assert.Equal(0, suite.FailedCount);
            Assert.Equal(0, suite.ErrorCount);
            Assert.Equal(2, suite.StepsExecuted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SuiteReportsPerScriptProgressAndHonorsCancellation()
    {
        string directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "one.radixtest"), PassingScript);
            File.WriteAllText(Path.Combine(directory, "two.radixtest"), PassingScript);
            CircuitProject project = ExampleProjects.CreateDFlipFlopDemo();
            IReadOnlyList<string> paths = SimulationScriptSuiteRunner.Discover(directory);
            var progress = new RecordingProgress();

            SimulationScriptSuiteResult suite = SimulationScriptSuiteRunner.RunFiles(
                project,
                paths,
                progress: progress);

            Assert.True(suite.Passed);
            Assert.Equal(2, progress.Reports.Count);
            Assert.Equal(1, progress.Reports[0].Completed);
            Assert.Equal(2, progress.Reports[0].Total);
            Assert.EndsWith("one.radixtest", progress.Reports[0].CurrentPath, StringComparison.Ordinal);
            Assert.Equal(1d, progress.Reports[1].Fraction);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => SimulationScriptSuiteRunner.RunFiles(
                project,
                paths,
                cancellationToken: cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingProgress : IProgress<SimulationScriptSuiteProgress>
    {
        public List<SimulationScriptSuiteProgress> Reports { get; } = [];

        public void Report(SimulationScriptSuiteProgress value) => Reports.Add(value);
    }

    private static string TemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), "radix-suite-" + Guid.NewGuid().ToString("N"));
}
