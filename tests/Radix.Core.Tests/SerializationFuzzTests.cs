using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

/// <summary>
/// Bounded, deterministic mutation campaign over serialized projects. Loading untrusted
/// text must either succeed or fail with a documented format/validation exception; any
/// other exception type is a defect because the editor and CLI only handle those two.
/// The campaign is fast enough for the normal test suite and reports everything needed
/// to reproduce a failure (source project, iteration, mutation, and mutated text hash).
/// </summary>
public sealed class SerializationFuzzTests
{
    private static readonly string[] Sources =
    [
        "dff-demo.radix",
        "binary-counter.radix",
        "ternary-counter.radix",
        "four-state-bus.radix",
        "byte-machine.radix",
    ];

    [Fact]
    public void MutatedProjectsOnlyFailWithDocumentedExceptions()
    {
        const int mutationsPerSource = 120;
        var random = new Random(0x5EED_2026);
        int successfulLoads = 0;
        int formatFailures = 0;
        int validationFailures = 0;

        foreach (string fileName in Sources)
        {
            ExampleProject example = ExampleProjects.All().Single(entry => entry.FileName == fileName);
            string original = ProjectSerializer.SaveToString(example.Project);

            for (int iteration = 0; iteration < mutationsPerSource; iteration++)
            {
                (string mutated, string description) = Mutate(original, random);
                string context =
                    $"source '{fileName}', iteration {iteration}, mutation {description}, " +
                    $"length {mutated.Length}, hash {StableHash(mutated):X8}";

                try
                {
                    CircuitProject loaded = ProjectSerializer.LoadFromString(mutated);
                    successfulLoads++;
                    Assert.NotNull(loaded.RootDefinitionId);
                    Assert.NotEmpty(loaded.Definitions);
                }
                catch (ProjectFormatException)
                {
                    formatFailures++;
                }
                catch (CircuitValidationException)
                {
                    validationFailures++;
                }
                catch (Exception exception)
                {
                    Assert.Fail(
                        $"{context}: unexpected {exception.GetType().Name}: {exception.Message}{Environment.NewLine}" +
                        exception.StackTrace);
                }
            }
        }

        Assert.True(
            formatFailures + validationFailures > 0,
            "the campaign did not exercise any failure path; mutations are not effective");
        Assert.True(successfulLoads > 0, "the campaign did not exercise any successful load");
    }

    private static (string Mutated, string Description) Mutate(string text, Random random)
    {
        int kind = random.Next(7);
        switch (kind)
        {
            case 0:
            {
                int cut = random.Next(text.Length);
                return (text[..cut], $"truncate at {cut}");
            }

            case 1:
            {
                int at = random.Next(text.Length);
                return (text.Remove(at, 1), $"delete char at {at}");
            }

            case 2:
            {
                int at = random.Next(text.Length);
                const string replacements = "{}[]\"',:0n-";
                char replacement = replacements[random.Next(replacements.Length)];
                return (text[..at] + replacement + text[(at + 1)..], $"replace char at {at}");
            }

            case 3:
            {
                int at = random.Next(text.Length);
                return (text[..at] + "{\"unexpected\":true}" + text[at..], $"insert object at {at}");
            }

            case 4:
            {
                // Drop a whole line, which often removes a property or an array element.
                int[] starts = LineStarts(text);
                int line = random.Next(starts.Length);
                int start = starts[line];
                int end = line + 1 < starts.Length ? starts[line + 1] : text.Length;
                return (text.Remove(start, end - start), $"remove line {line}");
            }

            case 5:
            {
                // Replace a number token with a large or negative number.
                int[] starts = LineStarts(text);
                int line = random.Next(starts.Length);
                int start = starts[line];
                int end = line + 1 < starts.Length ? starts[line + 1] : text.Length;
                string lineText = text[start..end];
                int colon = lineText.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0)
                {
                    return (text.Remove(start, end - start), $"remove line {line} (no colon)");
                }

                string replacement = random.Next(2) == 0 ? "2147483648" : "-2147483649";
                string mutatedLine = lineText[..(colon + 1)] + " " + replacement + "," + Environment.NewLine;
                return (text[..start] + mutatedLine + text[end..], $"replace line {line} number");
            }

            default:
            {
                // Duplicate a line to create duplicate array elements or properties.
                int[] starts = LineStarts(text);
                int line = random.Next(starts.Length);
                int start = starts[line];
                int end = line + 1 < starts.Length ? starts[line + 1] : text.Length;
                return (text[..end] + text[start..end] + text[end..], $"duplicate line {line}");
            }
        }
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && i + 1 < text.Length)
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    private static uint StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char character in text)
        {
            hash = unchecked((hash ^ character) * 16777619);
        }

        return hash;
    }
}
