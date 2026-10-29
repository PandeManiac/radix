using Radix.Core.Interpretation;
using Radix.Core.Model;

namespace Radix.Core.Serialization;

/// <summary>
/// Builds portable component libraries. A library is an ordinary RADIX project
/// restricted to one root definition and the definitions, domains, and custom
/// formats that its dependency closure actually references. Nothing here is a new
/// project-format feature; the result uses the same serializer and validator rules
/// as a normal project.
/// </summary>
public static class ProjectLibrary
{
    public const string Extension = ".radixlib";

    public static string Export(CircuitProject project, string definitionId)
    {
        ArgumentNullException.ThrowIfNull(project);
        CircuitDefinition definition = project.FindDefinition(definitionId)
            ?? throw new InvalidOperationException($"Unknown definition '{definitionId}'.");

        // Round-trip first so the exported file is a deep, validated snapshot.
        CircuitProject clone = ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(project));
        var closure = new HashSet<string>(StringComparer.Ordinal) { definitionId };
        var queue = new Queue<string>();
        queue.Enqueue(definitionId);
        while (queue.TryDequeue(out string? id))
        {
            CircuitDefinition? current = clone.FindDefinition(id);
            if (current is null)
            {
                continue;
            }

            foreach (ComponentInstance instance in current.Instances)
            {
                if (instance.DefinitionId is string dependency && closure.Add(dependency))
                {
                    queue.Enqueue(dependency);
                }
            }
        }

        clone.Definitions.RemoveAll(candidate => !closure.Contains(candidate.Id));

        // Only ship formats and domains the exported closure actually references. An
        // unrelated custom format in the source project must not block import.
        var usedFormats = new HashSet<string>(StringComparer.Ordinal);
        foreach (CircuitDefinition part in clone.Definitions)
        {
            foreach (NumericLane lane in part.NumericLanes)
            {
                usedFormats.Add(lane.FormatId);
            }
        }

        clone.CustomNumericFormats.RemoveAll(format => !usedFormats.Contains(format.Id));

        var usedDomains = new HashSet<string>(StringComparer.Ordinal);
        foreach (CircuitDefinition part in clone.Definitions)
        {
            foreach (PinDeclaration pin in part.Pins)
            {
                usedDomains.Add(pin.DomainId);
            }

            foreach (Net net in part.Nets)
            {
                usedDomains.Add(net.DomainId);
            }
        }

        foreach (NumericFormatDefinition format in clone.CustomNumericFormats)
        {
            usedDomains.Add(format.DomainId);
        }

        if (usedDomains.Count > 0)
        {
            clone.Domains.RemoveAll(domain => !usedDomains.Contains(domain.Id));
        }

        clone.RootDefinitionId = definitionId;
        clone.Name = definition.Name;
        clone.Description = $"Reusable component library entry: {definition.Name}.";
        return ProjectSerializer.SaveToString(clone);
    }
}
