using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Radix.Editor.Rendering;

/// <summary>
/// Resolves semantic colour tokens from the active theme dictionary for custom
/// drawing. XAML controls use the same keys through DynamicResource, so chrome
/// and canvas cannot drift apart.
/// </summary>
internal static class ThemeBrushes
{
    public static IBrush Get(StyledElement owner, string key, string fallback)
    {
        if (owner.TryFindResource(key, owner.ActualThemeVariant, out object? value) && value is IBrush brush)
        {
            return brush;
        }

        return Brush.Parse(fallback);
    }
}
