using Radix.Core.Model;

namespace Radix.Core.Interpretation;

/// <summary>
/// Resolves numeric format IDs against the built-in catalog first, then the
/// project's custom format definitions. These lookups are display-side only.
/// </summary>
public static class NumericFormatCatalog
{
    public static INumericFormat? Find(CircuitProject project, string? formatId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(formatId))
        {
            return null;
        }

        INumericFormat? builtIn = NumericFormats.Find(formatId);
        if (builtIn is not null)
        {
            return builtIn;
        }

        NumericFormatDefinition? custom = project.CustomNumericFormats.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, formatId, StringComparison.Ordinal));
        return custom?.ToFormat();
    }

    public static IEnumerable<INumericFormat> All(CircuitProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return NumericFormats.All.Concat(project.CustomNumericFormats.Select(definition => definition.ToFormat()));
    }

    public static bool IsBuiltInId(string formatId) => NumericFormats.Find(formatId) is not null;

    public static bool IsEquivalent(NumericFormatDefinition left, NumericFormatDefinition right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
               string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
               string.Equals(left.DomainId, right.DomainId, StringComparison.Ordinal) &&
               left.Radix == right.Radix &&
               left.Weighting == right.Weighting &&
               left.LeastSignificantFirst == right.LeastSignificantFirst &&
               left.NegativeSignValue == right.NegativeSignValue &&
               left.DigitValues.SequenceEqual(right.DigitValues);
    }
}
