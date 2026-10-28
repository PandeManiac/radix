using Radix.Core.Model;

namespace Radix.Editor.Services;

/// <summary>What a project search row navigates to.</summary>
public enum ProjectSearchKind
{
    Definition,
    Instance,
    Net,
}

/// <summary>
/// One navigation target found by <see cref="ProjectSearch"/>. Definition IDs and,
/// when present, instance and net IDs are stable authored identities, so a row can
/// be activated later without guessing from its display text.
/// </summary>
public sealed record ProjectSearchResult(
    ProjectSearchKind Kind,
    string DefinitionId,
    string? InstanceId,
    string? NetId,
    string Title,
    string Detail,
    string Context)
{
    public string KindLabel => Kind switch
    {
        ProjectSearchKind.Definition => "DEF",
        ProjectSearchKind.Instance => "PART",
        _ => "NET",
    };
}

/// <summary>Ordered search rows plus the number of matches before the result cap.</summary>
public sealed class ProjectSearchResults
{
    public static ProjectSearchResults Empty { get; } = new([], 0);

    public ProjectSearchResults(IReadOnlyList<ProjectSearchResult> items, int totalMatches)
    {
        Items = items;
        TotalMatches = totalMatches;
    }

    public IReadOnlyList<ProjectSearchResult> Items { get; }

    /// <summary>Total matches found, which may exceed <see cref="Items"/>.</summary>
    public int TotalMatches { get; }

    public bool IsTruncated => Items.Count < TotalMatches;
}

/// <summary>
/// Read-only, deterministic search across every definition in a project. The active
/// definition is used only to rank its own instances and nets ahead of equally good
/// matches elsewhere; it never narrows the search. No project or simulation state is
/// mutated, and results are ordered by match quality, target kind, context, and then
/// authored order so repeated searches and tests see the same rows.
/// </summary>
public static class ProjectSearch
{
    public const int DefaultLimit = 60;

    public static ProjectSearchResults Search(
        CircuitProject project,
        string? activeDefinitionId,
        string? query,
        int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (limit <= 0)
        {
            return ProjectSearchResults.Empty;
        }

        string text = (query ?? string.Empty).Trim();
        string[] terms = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var candidates = new List<Candidate>();
        int order = 0;
        foreach (CircuitDefinition definition in project.Definitions)
        {
            AddDefinition(project, definition, activeDefinitionId, text, terms, order++, candidates);

            foreach (ComponentInstance instance in definition.Instances)
            {
                AddInstance(project, definition, instance, activeDefinitionId, text, terms, order++, candidates);
            }

            foreach (Net net in definition.Nets)
            {
                AddNet(project, definition, net, activeDefinitionId, text, terms, order++, candidates);
            }
        }

        IReadOnlyList<ProjectSearchResult> items = candidates
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Result.Kind switch
            {
                ProjectSearchKind.Definition => 0,
                ProjectSearchKind.Instance => 1,
                _ => 2,
            })
            .ThenBy(candidate => candidate.ContextOrder)
            .ThenBy(candidate => candidate.Order)
            .Take(limit)
            .Select(candidate => candidate.Result)
            .ToArray();

        return new ProjectSearchResults(items, candidates.Count);
    }

    private static void AddDefinition(
        CircuitProject project,
        CircuitDefinition definition,
        string? activeDefinitionId,
        string query,
        string[] terms,
        int order,
        List<Candidate> candidates)
    {
        string haystack = Join(
            definition.Name,
            definition.Id,
            definition.Description);
        if (!Matches(haystack, terms))
        {
            return;
        }

        int uses = project.Definitions.Sum(parent => parent.Instances.Count(instance =>
            string.Equals(instance.DefinitionId, definition.Id, StringComparison.Ordinal)));
        string counts = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{definition.Pins.Count} pins · {definition.Instances.Count} parts · {definition.Nets.Count} nets");
        string detail = string.Equals(definition.Id, project.RootDefinitionId, StringComparison.Ordinal)
            ? $"root · {counts}"
            : $"definition · {counts}";
        if (uses > 0)
        {
            detail += string.Create(System.Globalization.CultureInfo.InvariantCulture, $" · used ×{uses}");
        }

        var result = new ProjectSearchResult(
            ProjectSearchKind.Definition,
            definition.Id,
            null,
            null,
            definition.Name,
            detail,
            definition.Id);
        candidates.Add(new Candidate(
            result,
            Score(definition.Name, definition.Id, query, terms),
            string.Equals(definition.Id, activeDefinitionId, StringComparison.Ordinal) ? 0 : 1,
            order));
    }

    private static void AddInstance(
        CircuitProject project,
        CircuitDefinition parent,
        ComponentInstance instance,
        string? activeDefinitionId,
        string query,
        string[] terms,
        int order,
        List<Candidate> candidates)
    {
        string typeName = TypeName(project, instance);
        string haystack = Join(
            instance.Id,
            instance.Label,
            instance.DefinitionId,
            typeName,
            instance.Primitive?.Kind,
            instance.Primitive?.Summary,
            parent.Name,
            parent.Id);
        if (!Matches(haystack, terms))
        {
            return;
        }

        string title = string.IsNullOrWhiteSpace(instance.Label) ? typeName : instance.Label!;
        string detail = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"instance of {typeName} · {instance.Id}");
        var result = new ProjectSearchResult(
            ProjectSearchKind.Instance,
            parent.Id,
            instance.Id,
            null,
            title,
            detail,
            $"in {parent.Name}");
        candidates.Add(new Candidate(
            result,
            Score(title, instance.Id, query, terms),
            string.Equals(parent.Id, activeDefinitionId, StringComparison.Ordinal) ? 0 : 1,
            order));
    }

    private static void AddNet(
        CircuitProject project,
        CircuitDefinition parent,
        Net net,
        string? activeDefinitionId,
        string query,
        string[] terms,
        int order,
        List<Candidate> candidates)
    {
        SignalDomainInfo domain = DomainInfo(project, net.DomainId);
        string haystack = Join(
            net.Id,
            net.Label,
            domain.Id,
            domain.Name,
            parent.Name,
            parent.Id);
        if (!Matches(haystack, terms))
        {
            return;
        }

        string title = string.IsNullOrWhiteSpace(net.Label) ? net.Id : net.Label!;
        string detail = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"net · {domain.Name} · {net.Pins.Count} pins{(string.IsNullOrWhiteSpace(net.Label) ? string.Empty : $" · {net.Id}")}");
        var result = new ProjectSearchResult(
            ProjectSearchKind.Net,
            parent.Id,
            null,
            net.Id,
            title,
            detail,
            $"in {parent.Name}");
        candidates.Add(new Candidate(
            result,
            Score(title, net.Id, query, terms),
            string.Equals(parent.Id, activeDefinitionId, StringComparison.Ordinal) ? 0 : 1,
            order));
    }

    private static bool Matches(string haystack, string[] terms) =>
        terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static int Score(string title, string id, string query, string[] terms)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (id.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (id.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (terms.Length > 1 && terms.All(term => title.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return 6;
        }

        return 7;
    }

    private static string TypeName(CircuitProject project, ComponentInstance instance)
    {
        if (instance.DefinitionId is string definitionId)
        {
            return project.FindDefinition(definitionId)?.Name ?? definitionId;
        }

        return instance.Primitive?.Kind switch
        {
            "constant" => "Constant",
            "lookup" => "Lookup table",
            "delay" => "Delay line",
            "waveform" => "Waveform",
            "probe" => "Probe",
            _ => instance.Primitive?.Kind ?? "Primitive",
        };
    }

    private static SignalDomainInfo DomainInfo(CircuitProject project, string domainId)
    {
        foreach (var domain in project.Domains)
        {
            if (string.Equals(domain.Id, domainId, StringComparison.Ordinal))
            {
                return new SignalDomainInfo(domain.Id, domain.Name);
            }
        }

        return new SignalDomainInfo(domainId, domainId);
    }

    private static string Join(params string?[] parts) =>
        string.Join('\n', parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private readonly record struct SignalDomainInfo(string Id, string Name);

    private sealed record Candidate(
        ProjectSearchResult Result,
        int Score,
        int ContextOrder,
        int Order);
}
