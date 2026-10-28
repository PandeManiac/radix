using System.Globalization;
using Radix.Core;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;

namespace Radix.Editor.Services;

public sealed record LibraryImportResult(
    bool Success,
    string Message,
    string? RootDefinitionId = null)
{
    public static LibraryImportResult Fail(string message) => new(false, message);
}

/// <summary>
/// Portable component libraries. A library is a normal RADIX project restricted to
/// one exported definition and its dependency closure, so the same validator and
/// serializer rules apply. Import merges domains by identity and never overwrites
/// an existing definition: structurally equal parts are reused, conflicting IDs get
/// a fresh local ID.
/// </summary>
public static class ComponentLibraryService
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

    public static LibraryImportResult Import(CircuitProject target, string json)
    {
        ArgumentNullException.ThrowIfNull(target);
        CircuitProject library;
        try
        {
            library = ProjectSerializer.LoadFromString(json);
        }
        catch (Exception exception) when (exception is ProjectFormatException or CircuitValidationException)
        {
            return LibraryImportResult.Fail("Library is not a valid RADIX project: " + exception.Message);
        }

        if (library.Definitions.Count == 0 || library.FindDefinition(library.RootDefinitionId) is null)
        {
            return LibraryImportResult.Fail("Library has no root definition.");
        }

        // All domain checks happen before any mutation.
        foreach (SignalDomain domain in library.Domains)
        {
            SignalDomain? existing = target.FindDomain(domain.Id);
            if (existing is not null && !DomainsEquivalent(existing, domain))
            {
                return LibraryImportResult.Fail(
                    $"Domain '{domain.Id}' already exists with different values or driver policy.");
            }
        }

        var existingDefinitions = target.Definitions
            .ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        var usedIds = new HashSet<string>(existingDefinitions.Keys, StringComparer.Ordinal);
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new List<CircuitDefinition>();

        foreach (CircuitDefinition definition in library.Definitions)
        {
            if (existingDefinitions.TryGetValue(definition.Id, out CircuitDefinition? existing))
            {
                if (DefinitionsEquivalent(existing, definition))
                {
                    idMap[definition.Id] = existing.Id;
                    continue;
                }

                string renamed = DefinitionEditor.UniqueId(definition.Id, id => usedIds.Contains(id));
                idMap[definition.Id] = renamed;
                usedIds.Add(renamed);
                pending.Add(definition);
                continue;
            }

            idMap[definition.Id] = definition.Id;
            usedIds.Add(definition.Id);
            pending.Add(definition);
        }

        foreach (CircuitDefinition definition in pending)
        {
            foreach (ComponentInstance instance in definition.Instances)
            {
                if (instance.DefinitionId is string reference &&
                    !idMap.ContainsKey(reference) &&
                    target.FindDefinition(reference) is null)
                {
                    return LibraryImportResult.Fail(
                        $"Definition '{definition.Id}' references missing dependency '{reference}'.");
                }
            }
        }

        string? rootId = idMap.GetValueOrDefault(library.RootDefinitionId);
        if (rootId is null)
        {
            return LibraryImportResult.Fail("Library root could not be mapped into this project.");
        }

        // Custom formats are project assets referenced by lane IDs. Reuse equivalent
        // formats, rename conflicting ones, and rewrite lane references in the clones.
        var usedFormatIds = pending
            .SelectMany(definition => definition.NumericLanes)
            .Select(lane => lane.FormatId)
            .ToHashSet(StringComparer.Ordinal);
        var formatMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingFormats = new List<(NumericFormatDefinition Source, string Id)>();
        foreach (string formatId in usedFormatIds)
        {
            NumericFormatDefinition? sourceFormat = library.FindCustomNumericFormat(formatId);
            if (sourceFormat is null)
            {
                if (!NumericFormatCatalog.IsBuiltInId(formatId))
                {
                    return LibraryImportResult.Fail($"Library refers to missing numeric format '{formatId}'.");
                }

                continue;
            }

            NumericFormatDefinition? existing = target.FindCustomNumericFormat(formatId);
            if (existing is not null && NumericFormatCatalog.IsEquivalent(existing, sourceFormat))
            {
                formatMap[formatId] = existing.Id;
                continue;
            }

            string mappedId = formatId;
            if (existing is not null || NumericFormatCatalog.IsBuiltInId(formatId))
            {
                mappedId = DefinitionEditor.UniqueId(formatId, candidate =>
                    target.FindCustomNumericFormat(candidate) is not null ||
                    NumericFormatCatalog.IsBuiltInId(candidate) ||
                    usedFormatIds.Contains(candidate) ||
                    formatMap.ContainsValue(candidate));
            }

            formatMap[formatId] = mappedId;
            pendingFormats.Add((sourceFormat, mappedId));
        }

        var beforeErrors = ErrorSignatures(target);
        var addedDomains = new List<SignalDomain>();
        var addedFormats = new List<NumericFormatDefinition>();
        var addedDefinitions = new List<CircuitDefinition>();
        try
        {
            foreach (SignalDomain domain in library.Domains)
            {
                if (target.FindDomain(domain.Id) is null)
                {
                    target.Domains.Add(domain);
                    addedDomains.Add(domain);
                }
            }

            foreach ((NumericFormatDefinition source, string mappedId) in pendingFormats)
            {
                var clone = new NumericFormatDefinition(
                    mappedId,
                    source.Name,
                    source.DomainId,
                    source.DigitValues,
                    source.Radix,
                    source.Weighting,
                    source.LeastSignificantFirst,
                    source.NegativeSignValue,
                    source.Description);
                target.CustomNumericFormats.Add(clone);
                addedFormats.Add(clone);
            }

            foreach (CircuitDefinition definition in pending)
            {
                CircuitDefinition clone = CloneDefinition(definition);
                clone.Id = idMap[definition.Id];
                foreach (ComponentInstance instance in clone.Instances)
                {
                    if (instance.DefinitionId is string reference && idMap.TryGetValue(reference, out string? mapped))
                    {
                        instance.DefinitionId = mapped;
                    }
                }

                foreach (NumericLane lane in clone.NumericLanes)
                {
                    if (formatMap.TryGetValue(lane.FormatId, out string? mappedFormat))
                    {
                        lane.FormatId = mappedFormat;
                    }
                }

                target.Definitions.Add(clone);
                addedDefinitions.Add(clone);
            }

            DiagnosticBag diagnostics = CircuitValidator.Validate(target);
            string[] newErrors = diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(ErrorSignature)
                .Where(signature => !beforeErrors.Contains(signature))
                .ToArray();
            if (newErrors.Length > 0)
            {
                Rollback(target, addedDomains, addedFormats, addedDefinitions);
                return LibraryImportResult.Fail(
                    "Import would introduce validation errors: " + string.Join("; ", newErrors.Take(4)));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Rollback(target, addedDomains, addedFormats, addedDefinitions);
            return LibraryImportResult.Fail("Import failed: " + exception.Message);
        }

        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"Imported {addedDefinitions.Count} definitions, {addedFormats.Count} formats, and " +
            $"{addedDomains.Count} domains from '{library.Name}'");
        return new LibraryImportResult(true, message, rootId);
    }

    private static void Rollback(
        CircuitProject target,
        IReadOnlyList<SignalDomain> domains,
        IReadOnlyList<NumericFormatDefinition> formats,
        IReadOnlyList<CircuitDefinition> definitions)
    {
        foreach (CircuitDefinition definition in definitions)
        {
            target.Definitions.Remove(definition);
        }

        foreach (NumericFormatDefinition format in formats)
        {
            target.CustomNumericFormats.Remove(format);
        }

        foreach (SignalDomain domain in domains)
        {
            target.Domains.Remove(domain);
        }
    }

    private static HashSet<string> ErrorSignatures(CircuitProject project)
    {
        DiagnosticBag diagnostics = CircuitValidator.Validate(project);
        return diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(ErrorSignature)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ErrorSignature(Diagnostic diagnostic) =>
        $"{diagnostic.Code}|{diagnostic.Location}|{diagnostic.Message}";

    private static bool DomainsEquivalent(SignalDomain left, SignalDomain right)
    {
        if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal) ||
            left.ValueCount != right.ValueCount ||
            !left.ValueNames.SequenceEqual(right.ValueNames, StringComparer.Ordinal) ||
            !string.Equals(left.Drivers.Kind, right.Drivers.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        return (left.Drivers, right.Drivers) switch
        {
            (PriorityDriverResolution a, PriorityDriverResolution b) =>
                a.Priorities.SequenceEqual(b.Priorities) && a.ConflictValue == b.ConflictValue,
            (SingleDriverResolution, SingleDriverResolution) => true,
            _ => false,
        };
    }

    /// <summary>
    /// Electrical and interface equality. Layout coordinates, labels, wire colors,
    /// and routing hints are deliberately ignored: importing the same part twice
    /// should reuse it even if the local copy was moved around.
    /// </summary>
    private static bool DefinitionsEquivalent(CircuitDefinition left, CircuitDefinition right)
    {
        if (left.Pins.Count != right.Pins.Count ||
            left.Instances.Count != right.Instances.Count ||
            left.Nets.Count != right.Nets.Count ||
            left.Ports.Count != right.Ports.Count)
        {
            return false;
        }

        if (!left.Pins.OrderBy(p => p.Id, StringComparer.Ordinal)
                .Zip(right.Pins.OrderBy(p => p.Id, StringComparer.Ordinal))
                .All(pair =>
                    pair.First.Id == pair.Second.Id &&
                    pair.First.Direction == pair.Second.Direction &&
                    pair.First.DomainId == pair.Second.DomainId))
        {
            return false;
        }

        if (!left.Instances.OrderBy(i => i.Id, StringComparer.Ordinal)
                .Zip(right.Instances.OrderBy(i => i.Id, StringComparer.Ordinal))
                .All(pair =>
                    pair.First.Id == pair.Second.Id &&
                    pair.First.DefinitionId == pair.Second.DefinitionId &&
                    PrimitivesEquivalent(pair.First.Primitive, pair.Second.Primitive)))
        {
            return false;
        }

        var rightNets = right.Nets.ToDictionary(net => net.Id, StringComparer.Ordinal);
        foreach (Net net in left.Nets)
        {
            if (!rightNets.TryGetValue(net.Id, out Net? other) ||
                net.DomainId != other.DomainId ||
                net.InitialValue != other.InitialValue ||
                !net.Pins.ToHashSet().SetEquals(other.Pins))
            {
                return false;
            }
        }

        return left.Ports
            .OrderBy(port => port.PinId, StringComparer.Ordinal)
            .Zip(right.Ports.OrderBy(port => port.PinId, StringComparer.Ordinal))
            .All(pair => pair.First == pair.Second);
    }

    private static bool PrimitivesEquivalent(PrimitiveSpec? left, PrimitiveSpec? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (ConstantSpec a, ConstantSpec b) => a.Value == b.Value,
            (LookupSpec a, LookupSpec b) =>
                a.InputCount == b.InputCount &&
                a.DelayTicks == b.DelayTicks &&
                a.Table.SequenceEqual(b.Table),
            (DelaySpec a, DelaySpec b) => a.DelayTicks == b.DelayTicks && a.InitialValue == b.InitialValue,
            (WaveformSpec a, WaveformSpec b) =>
                a.Durations.SequenceEqual(b.Durations) &&
                a.Values.SequenceEqual(b.Values),
            (ProbeSpec a, ProbeSpec b) => string.Equals(a.Label, b.Label, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static CircuitDefinition CloneDefinition(CircuitDefinition source)
    {
        var definition = new CircuitDefinition
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
        };
        foreach (PinDeclaration pin in source.Pins)
        {
            definition.Pins.Add(new PinDeclaration
            {
                Id = pin.Id,
                Name = pin.Name,
                Direction = pin.Direction,
                DomainId = pin.DomainId,
            });
        }

        foreach (ComponentInstance instance in source.Instances)
        {
            definition.Instances.Add(new ComponentInstance
            {
                Id = instance.Id,
                DefinitionId = instance.DefinitionId,
                Primitive = instance.Primitive,
                Label = instance.Label,
                X = instance.X,
                Y = instance.Y,
            });
        }

        foreach (Net net in source.Nets)
        {
            var clone = new Net
            {
                Id = net.Id,
                DomainId = net.DomainId,
                Label = net.Label,
                InitialValue = net.InitialValue,
                WireColor = net.WireColor,
            };
            clone.RoutePoints.AddRange(net.RoutePoints);
            clone.Pins.AddRange(net.Pins);
            definition.Nets.Add(clone);
        }

        definition.Ports.AddRange(source.Ports);
        foreach (NumericLane lane in source.NumericLanes)
        {
            var clone = new NumericLane
            {
                Id = lane.Id,
                Name = lane.Name,
                FormatId = lane.FormatId,
            };
            clone.PinIds.AddRange(lane.PinIds);
            definition.NumericLanes.Add(clone);
        }

        return definition;
    }
}
