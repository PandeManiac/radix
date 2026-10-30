using System.Collections;
using System.Globalization;

namespace Radix.Core;

/// <summary>Severity of a validation, compile, or runtime diagnostic.</summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Whether a diagnostic comes from structure, runtime, or a recommendation.</summary>
public enum DiagnosticNature
{
    Structural,
    Runtime,
    Heuristic,
}

/// <summary>A structured message with an optional location in a project.</summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? DefinitionId = null,
    string? InstanceId = null,
    string? NetId = null,
    string? PinId = null,
    string? Path = null)
{
    /// <summary>Electrical or modelling assumption behind this diagnostic.</summary>
    public string? Assumption { get; init; }

    /// <summary>Concrete corrective action, when one is justified.</summary>
    public string? Suggestion { get; init; }

    /// <summary>Whether simulation can still run, or null when that is not established.</summary>
    public bool? SimulationRemainsValid { get; init; }

    /// <summary>Structural, runtime, or heuristic origin, when that has been classified.</summary>
    public DiagnosticNature? Nature { get; init; }

    /// <summary>Formatted location fragment such as "definition 'cpu' / instance 'alu'".</summary>
    public string Location
    {
        get
        {
            var parts = new List<string>(5);
            if (!string.IsNullOrEmpty(Path))
            {
                parts.Add($"path '{Path}'");
            }

            if (DefinitionId is not null)
            {
                parts.Add($"definition '{DefinitionId}'");
            }

            if (InstanceId is not null)
            {
                parts.Add($"instance '{InstanceId}'");
            }

            if (PinId is not null)
            {
                parts.Add($"pin '{PinId}'");
            }

            if (NetId is not null)
            {
                parts.Add($"net '{NetId}'");
            }

            return parts.Count == 0 ? string.Empty : string.Join(" / ", parts);
        }
    }

    public override string ToString()
    {
        string location = Location;
        string suffix = location.Length > 0 ? $" [{location}]" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Severity.ToString().ToUpperInvariant()} {Code}: {Message}{suffix}");
    }
}

/// <summary>A mutable collection of diagnostics with convenience helpers.</summary>
public sealed class DiagnosticBag : IReadOnlyList<Diagnostic>
{
    private readonly List<Diagnostic> _items = [];

    public int Count => _items.Count;

    public Diagnostic this[int index] => _items[index];

    public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

    public int ErrorCount => _items.Count(d => d.Severity == DiagnosticSeverity.Error);

    public int WarningCount => _items.Count(d => d.Severity == DiagnosticSeverity.Warning);

    public void Add(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _items.Add(diagnostic);
    }

    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            Add(diagnostic);
        }
    }

    /// <summary>Adds a diagnostic unless the exact same record is already present.</summary>
    public bool AddUnique(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (_items.Contains(diagnostic))
        {
            return false;
        }

        _items.Add(diagnostic);
        return true;
    }

    public void Clear() => _items.Clear();

    public void Info(string code, string message, string? definitionId = null, string? instanceId = null,
        string? netId = null, string? pinId = null, string? path = null) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Info, message, definitionId, instanceId, netId, pinId, path));

    public void Warning(string code, string message, string? definitionId = null, string? instanceId = null,
        string? netId = null, string? pinId = null, string? path = null) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Warning, message, definitionId, instanceId, netId, pinId, path));

    public void Error(string code, string message, string? definitionId = null, string? instanceId = null,
        string? netId = null, string? pinId = null, string? path = null) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Error, message, definitionId, instanceId, netId, pinId, path));

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Thrown when a project fails validation.</summary>
public class CircuitValidationException : Exception
{
    public CircuitValidationException(DiagnosticBag diagnostics)
        : base(BuildMessage("Circuit validation failed", diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public DiagnosticBag Diagnostics { get; }

    internal static string BuildMessage(string prefix, DiagnosticBag diagnostics)
    {
        string detail = string.Join(Environment.NewLine, diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        return detail.Length == 0 ? prefix : $"{prefix}:{Environment.NewLine}{detail}";
    }
}

/// <summary>Thrown when a structurally valid project cannot be compiled for simulation.</summary>
public sealed class CircuitCompileException : CircuitValidationException
{
    public CircuitCompileException(DiagnosticBag diagnostics)
        : base(diagnostics)
    {
    }
}
