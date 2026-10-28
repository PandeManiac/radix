using Avalonia;

namespace Radix.Editor.Rendering;

/// <summary>
/// Maps world-space content into a fixed screen-space overview plot while preserving
/// aspect ratio. The projection is pure and presentation-only: it cannot affect the
/// circuit, routing, or simulation. Invalid or empty content yields no map rather than
/// dividing by zero.
/// </summary>
public sealed class OverviewMap
{
    private OverviewMap(Rect content, Rect plot, double scale, Rect contentFrame)
    {
        Content = content;
        Plot = plot;
        Scale = scale;
        ContentFrame = contentFrame;
    }

    public Rect Content { get; }

    /// <summary>Screen-space area available for the scaled content.</summary>
    public Rect Plot { get; }

    /// <summary>Screen pixels per world unit.</summary>
    public double Scale { get; }

    /// <summary>Screen-space rectangle occupied by all content.</summary>
    public Rect ContentFrame { get; }

    public static OverviewMap? Create(Rect content, Rect plot)
    {
        if (content.Width <= 0 || content.Height <= 0 ||
            plot.Width <= 0 || plot.Height <= 0 ||
            !double.IsFinite(content.X) || !double.IsFinite(content.Y) ||
            !double.IsFinite(content.Width) || !double.IsFinite(content.Height) ||
            !double.IsFinite(plot.X) || !double.IsFinite(plot.Y) ||
            !double.IsFinite(plot.Width) || !double.IsFinite(plot.Height))
        {
            return null;
        }

        double scale = Math.Min(plot.Width / content.Width, plot.Height / content.Height);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return null;
        }

        double width = content.Width * scale;
        double height = content.Height * scale;
        var frame = new Rect(
            plot.X + ((plot.Width - width) / 2),
            plot.Y + ((plot.Height - height) / 2),
            width,
            height);
        return new OverviewMap(content, plot, scale, frame);
    }

    public Point ToScreen(Point world) => new(
        ContentFrame.X + ((world.X - Content.X) * Scale),
        ContentFrame.Y + ((world.Y - Content.Y) * Scale));

    /// <summary>Maps a pointer back to content, clamped so an edge press cannot escape.</summary>
    public Point ToWorld(Point screen) => new(
        Math.Clamp(Content.X + ((screen.X - ContentFrame.X) / Scale), Content.Left, Content.Right),
        Math.Clamp(Content.Y + ((screen.Y - ContentFrame.Y) / Scale), Content.Top, Content.Bottom));

    public Rect MapRect(Rect world)
    {
        Point topLeft = ToScreen(world.TopLeft);
        Point bottomRight = ToScreen(world.BottomRight);
        return new Rect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Abs(bottomRight.X - topLeft.X),
            Math.Abs(bottomRight.Y - topLeft.Y));
    }

    /// <summary>
    /// Maps the visible world rectangle and clips it to authored content, so a viewport
    /// that extends far past the circuit still shows a finite marker.
    /// </summary>
    public Rect ViewportFrame(Rect visibleWorld)
    {
        Rect clipped = Intersect(visibleWorld, Content);
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            return default;
        }

        return MapRect(clipped);
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        double left = Math.Max(a.Left, b.Left);
        double top = Math.Max(a.Top, b.Top);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);
        return right <= left || bottom <= top
            ? default
            : new Rect(left, top, right - left, bottom - top);
    }
}
