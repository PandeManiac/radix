using Avalonia;

namespace Radix.Editor.Rendering;

public sealed class Viewport
{
    public const double MinScale = 0.2;
    public const double MaxScale = 2.5;

    public double Scale { get; private set; } = 1.0;

    public Point Offset { get; private set; } = new(80, 80);

    /// <summary>Raised after any pan, zoom, fit, or explicit restore.</summary>
    public event Action? Changed;

    public Point WorldToScreen(Point world) => new(
        (world.X * Scale) + Offset.X,
        (world.Y * Scale) + Offset.Y);

    public Point ScreenToWorld(Point screen) => new(
        (screen.X - Offset.X) / Scale,
        (screen.Y - Offset.Y) / Scale);

    public Rect VisibleWorld(Size surface)
    {
        Point topLeft = ScreenToWorld(new Point(0, 0));
        Point bottomRight = ScreenToWorld(new Point(surface.Width, surface.Height));
        return new Rect(topLeft, bottomRight);
    }

    public void Pan(Vector screenDelta)
    {
        Offset += screenDelta;
        Changed?.Invoke();
    }

    /// <summary>Applies a saved viewport, clamping the scale and rejecting non-finite values.</summary>
    public void Restore(double scale, Point offset)
    {
        if (!double.IsFinite(scale) || scale <= 0 ||
            !double.IsFinite(offset.X) || !double.IsFinite(offset.Y))
        {
            return;
        }

        Scale = Math.Clamp(scale, MinScale, MaxScale);
        Offset = offset;
        Changed?.Invoke();
    }

    public void ZoomAt(Point screenAnchor, double factor)
    {
        Point worldAnchor = ScreenToWorld(screenAnchor);
        Scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        Offset = new Point(
            screenAnchor.X - (worldAnchor.X * Scale),
            screenAnchor.Y - (worldAnchor.Y * Scale));
        Changed?.Invoke();
    }

    public void SetScaleAroundCenter(Size surface, double scale)
    {
        Point center = new(surface.Width / 2, surface.Height / 2);
        Point worldCenter = ScreenToWorld(center);
        Scale = Math.Clamp(scale, MinScale, MaxScale);
        Offset = new Point(
            center.X - (worldCenter.X * Scale),
            center.Y - (worldCenter.Y * Scale));
        Changed?.Invoke();
    }

    /// <summary>
    /// Pans so the center of <paramref name="worldBounds"/> sits at the surface center.
    /// Scale is left unchanged; this is used to reveal a selected diagnostic target.
    /// </summary>
    public void CenterOn(Rect worldBounds, Size surface)
    {
        if (!double.IsFinite(worldBounds.X) || !double.IsFinite(worldBounds.Y) ||
            !double.IsFinite(worldBounds.Width) || !double.IsFinite(worldBounds.Height) ||
            surface.Width <= 1 || surface.Height <= 1)
        {
            return;
        }

        Point center = worldBounds.Center;
        Offset = new Point(
            (surface.Width / 2) - (center.X * Scale),
            (surface.Height / 2) - (center.Y * Scale));
        Changed?.Invoke();
    }

    public void Fit(Rect worldBounds, Size surface, double margin = 50)
    {
        if (worldBounds.Width <= 0 || worldBounds.Height <= 0 ||
            surface.Width <= margin * 2 || surface.Height <= margin * 2)
        {
            return;
        }

        double scaleX = (surface.Width - (margin * 2)) / worldBounds.Width;
        double scaleY = (surface.Height - (margin * 2)) / worldBounds.Height;
        Scale = Math.Clamp(Math.Min(scaleX, scaleY), MinScale, MaxScale);
        Offset = new Point(
            (surface.Width - (worldBounds.Width * Scale)) / 2 - (worldBounds.X * Scale),
            (surface.Height - (worldBounds.Height * Scale)) / 2 - (worldBounds.Y * Scale));
        Changed?.Invoke();
    }
}
