namespace Radix.Core.Model;

/// <summary>
/// Identifier syntax for definitions, instances, nets, pins, and domains. IDs are
/// stable references in the project format and hierarchy paths use '/', so it is
/// forbidden inside an ID.
/// </summary>
public static class Identifiers
{
    public const int MaxLength = 128;

    public const string Rule = "IDs must start with a letter, digit, or underscore, contain only letters, digits, '_', '-', or '.', and be at most 128 characters.";

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
        {
            return false;
        }

        char first = id[0];
        if (!char.IsAsciiLetterOrDigit(first) && first != '_')
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Creates a readable, valid instance ID from a definition ID plus a number.</summary>
    public static string Suggest(string prefix, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        string stem = new(prefix.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_').ToArray());
        if (string.IsNullOrEmpty(stem) || !char.IsAsciiLetterOrDigit(stem[0]) && stem[0] != '_')
        {
            stem = "u" + stem;
        }

        if (stem.Length > 100)
        {
            stem = stem[..100];
        }

        for (int i = 1; i < int.MaxValue; i++)
        {
            string candidate = $"{stem}_{i}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique identifier.");
    }
}
