namespace Radix.Core.Serialization;

/// <summary>Thrown when a project file cannot be parsed or uses an unsupported format feature.</summary>
public sealed class ProjectFormatException : Exception
{
    public ProjectFormatException(string message)
        : base(message)
    {
    }

    public ProjectFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
