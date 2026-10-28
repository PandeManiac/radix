using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Radix.Core.Signals;
using Radix.Editor.Rendering;
using Radix.Editor.ViewModels;

namespace Radix.Editor.Controls;

/// <summary>
/// Zooming, pannable schematic surface. The control renders immutable layout
/// snapshots and forwards gestures to the view model; it owns no circuit state.
/// </summary>
public sealed class CircuitCanvas : Control
{
    private static readonly IBrush CanvasBackground = Brush.Parse("#F8FAFC");
    private static readonly IBrush GridBrush = Brush.Parse("#E9EEF3");
    private static readonly IBrush BoxFill = Brush.Parse("#FFFDFC");
    private static readonly IBrush BoxHeader = Brush.Parse("#F1F5F9");
    private static readonly IBrush BoxBorder = Brush.Parse("#9BAAB8");
    private static readonly IBrush Accent = Brush.Parse("#2B6CB0");
    private static readonly IBrush AccentSoft = Brush.Parse("#DCE9F7");
    private static readonly IBrush Ink = Brush.Parse("#1F2A36");
    private static readonly IBrush Muted = Brush.Parse("#6F8092");
    private static readonly IBrush PortFill = Brush.Parse("#EEF3F8");
    private static readonly IBrush PinFill = Brush.Parse("#4A5A6A");
    private static readonly IBrush HighBrush = Brush.Parse("#1F9D72");
    private static readonly IBrush LowBrush = Brush.Parse("#8091A3");
    private static readonly IBrush UndrivenBrush = Brush.Parse("#B7C2CC");
    private static readonly IBrush TernaryOne = Brush.Parse("#2B6CB0");
    private static readonly IBrush TernaryTwo = Brush.Parse("#D97706");
    private static readonly IBrush TooltipFill = Brush.Parse("#1F2A36");

    private EditorViewModel? _viewModel;
    private bool _attached;
    private bool _needsInitialFit = true;
    private bool _panning;
    private bool _dragging;
    private Point _lastScreen;
    private Point _pointerScreen;
    private Point _pointerWorld;
    private Point _connectAnchor;
    private EditorPin? _connectStart;
    private string? _draggedInstance;
    private Point _dragOffset;
    private PinHit? _hoverPin;

    public CircuitCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        DataContextChanged += (_, _) => AttachViewModel();
        SizeChanged += (_, _) =>
        {
            if (_needsInitialFit && Bounds.Width > 20 && Bounds.Height > 20)
            {
                FitToContent();
            }
        };
    }

    public Viewport Viewport { get; } = new();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        AttachViewModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        DetachViewModel();
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachViewModel()
    {
        if (!_attached)
        {
            return;
        }

        var viewModel = DataContext as EditorViewModel;
        if (ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.VisualInvalidated += OnVisualInvalidated;
            _viewModel.FitRequested += OnFitRequested;
            _needsInitialFit = true;
            if (Bounds.Width > 20 && Bounds.Height > 20)
            {
                FitToContent();
            }
        }

        InvalidateVisual();
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.VisualInvalidated -= OnVisualInvalidated;
            _viewModel.FitRequested -= OnFitRequested;
            _viewModel = null;
        }
    }

    private void OnVisualInvalidated() => InvalidateVisual();

    private void OnFitRequested() => FitToContent();

    public void FitToContent()
    {
        if (_viewModel?.Layout is null || Bounds.Width <= 20 || Bounds.Height <= 20)
        {
            return;
        }

        Viewport.Fit(_viewModel.Layout.ContentBounds, Bounds.Size);
        _needsInitialFit = false;
        InvalidateVisual();
    }

    public void ZoomIn() => ZoomAroundCenter(1.2);

    public void ZoomOut() => ZoomAroundCenter(1 / 1.2);

    public void ResetZoom()
    {
        Viewport.SetScaleAroundCenter(Bounds.Size, 1.0);
        InvalidateVisual();
    }

    private void ZoomAroundCenter(double factor)
    {
        if (Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        Viewport.ZoomAt(center, factor);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(CanvasBackground, new Rect(Bounds.Size));
        DrawGrid(context);

        EditorViewModel? viewModel = _viewModel;
        CircuitLayout? layout = viewModel?.Layout;
        if (viewModel is null || layout is null)
        {
            DrawHint(context, "No circuit loaded.");
            return;
        }

        DrawWires(context, viewModel, layout);
        DrawComponents(context, viewModel, layout);
        DrawPorts(context, viewModel, layout);
        DrawHoverTooltip(context);
        DrawConnectionPreview(context);
        DrawZoomBadge(context);

        if (layout.Components.Count == 0 && layout.Ports.Count == 0)
        {
            DrawHint(context, "Pick a part from the library, or add an interface pin.");
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        Rect visible = Viewport.VisibleWorld(Bounds.Size);
        double spacing = Viewport.Scale < 0.45 ? 80 : Viewport.Scale > 1.4 ? 20 : 40;
        var pen = new Pen(GridBrush, 1);
        double startX = Math.Floor(visible.X / spacing) * spacing;
        double startY = Math.Floor(visible.Y / spacing) * spacing;
        int limit = 500;

        for (double x = startX; x <= visible.Right && limit-- > 0; x += spacing)
        {
            Point screen = Viewport.WorldToScreen(new Point(x, visible.Y));
            context.DrawLine(pen, new Point(screen.X, 0), new Point(screen.X, Bounds.Height));
        }

        limit = 500;
        for (double y = startY; y <= visible.Bottom && limit-- > 0; y += spacing)
        {
            Point screen = Viewport.WorldToScreen(new Point(visible.X, y));
            context.DrawLine(pen, new Point(0, screen.Y), new Point(Bounds.Width, screen.Y));
        }
    }

    private void DrawWires(DrawingContext context, EditorViewModel viewModel, CircuitLayout layout)
    {
        foreach (NetWire wire in layout.Wires)
        {
            (int value, SignalDomain? domain) = ReadNet(viewModel, wire.NetId);
            IBrush brush = WireBrush(domain, value);
            bool undriven = value < 0;
            var pen = undriven
                ? new Pen(brush, 1.6, new DashStyle([4, 4], 0))
                : new Pen(brush, 2.1, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

            bool selected = string.Equals(viewModel.SelectedNetId, wire.NetId, StringComparison.Ordinal);
            if (selected)
            {
                var halo = new Pen(AccentSoft, 7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                DrawPolyline(context, halo, wire.Points);
                pen = new Pen(Accent, 2.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            }

            DrawPolyline(context, pen, wire.Points);
            double dot = Viewport.Scale < 0.5 ? 2 : 3;
            foreach (Point junction in wire.Junctions)
            {
                context.DrawEllipse(brush, null, Viewport.WorldToScreen(junction), dot, dot);
            }

            if (Viewport.Scale >= 0.75)
            {
                string label = viewModel.Definition.FindNet(wire.NetId)?.Label ?? wire.NetId;
                string valueText = value < 0 ? "z" : FormatValue(value, domain);
                DrawWireLabel(context, wire.LabelPoint, $"{label} = {valueText}", brush);
            }
        }
    }

    private void DrawPolyline(DrawingContext context, IPen pen, IReadOnlyList<Point> points)
    {
        for (int i = 0; i + 1 < points.Count; i++)
        {
            context.DrawLine(pen, Viewport.WorldToScreen(points[i]), Viewport.WorldToScreen(points[i + 1]));
        }
    }

    private void DrawWireLabel(DrawingContext context, Point world, string text, IBrush accent)
    {
        Point position = Viewport.WorldToScreen(world);
        FormattedText formatted = Text(text, 10, Ink);
        var background = new Rect(
            position.X + 3,
            position.Y - 8,
            formatted.Width + 8,
            formatted.Height + 3);
        context.DrawRectangle(Brush.Parse("#F2F5F8"), new Pen(accent, 0.8), background, 3, 3);
        context.DrawText(formatted, new Point(background.X + 4, background.Y + 1));
    }

    private void DrawComponents(DrawingContext context, EditorViewModel viewModel, CircuitLayout layout)
    {
        double scale = Viewport.Scale;
        bool detailed = scale >= 0.32;
        double fontSize = Math.Clamp(12 * scale, 8.5, 14);
        double idSize = Math.Clamp(9 * scale, 7, 10);

        foreach (ComponentBox component in layout.Components)
        {
            Rect screen = WorldRect(component.Bounds);
            bool selected = string.Equals(viewModel.SelectedInstanceId, component.Instance.Id, StringComparison.Ordinal);
            var border = selected ? new Pen(Accent, 2.4) : new Pen(BoxBorder, 1.1);
            context.DrawRectangle(BoxFill, border, screen, 6, 6);

            double header = Math.Min(screen.Height, CircuitLayout.HeaderHeight * scale);
            if (header > 3)
            {
                using (context.PushClip(new Rect(screen.X, screen.Y, screen.Width, header)))
                {
                    context.FillRectangle(BoxHeader, new Rect(screen.X, screen.Y, screen.Width, header));
                }
            }

            if (!detailed)
            {
                continue;
            }

            Point titleOrigin = new(screen.X + (8 * Math.Max(scale, 0.6)), screen.Y + (5 * scale));
            FormattedText title = Text(component.Title, fontSize, Ink, FontWeight.SemiBold);
            title.MaxTextWidth = Math.Max(10, screen.Width - (16 * Math.Max(scale, 0.6)));
            title.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(title, titleOrigin);

            if (scale >= 0.85)
            {
                FormattedText id = Text(component.Instance.Id, idSize, Muted);
                id.MaxTextWidth = Math.Max(10, screen.Width - 16);
                id.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(id, new Point(titleOrigin.X, titleOrigin.Y + title.Height + 1));
            }

            double pinSize = Math.Clamp(10.5 * scale, 7.5, 12);
            foreach (PinVisual pin in component.Pins)
            {
                Point center = Viewport.WorldToScreen(pin.Position);
                (int value, SignalDomain? domain) = ReadPin(viewModel, EditorPin.ForInstance(component.Instance.Id, pin.PinId));
                IBrush pinBrush = value < 0 ? UndrivenBrush : ValueBrush(domain, value);
                double radius = Math.Clamp(3.4 * scale, 2.2, 4.2);
                context.DrawEllipse(PinFill, null, center, radius + 0.8, radius + 0.8);
                context.DrawEllipse(pinBrush, null, center, radius, radius);

                FormattedText label = Text(pin.Name, pinSize, Muted);
                double labelX = pin.Direction == PinDirection.Input
                    ? center.X + (6 * scale)
                    : center.X - label.Width - (6 * scale);
                context.DrawText(label, new Point(labelX, center.Y - (label.Height * 0.65)));

                if (scale >= 0.55)
                {
                    FormattedText valueText = Text(
                        value < 0 ? "z" : FormatValue(value, domain),
                        Math.Clamp(9.5 * scale, 7, 10),
                        pinBrush,
                        FontWeight.SemiBold);
                    double badgeWidth = valueText.Width + 7;
                    double badgeX = pin.Direction == PinDirection.Input
                        ? center.X - 5 - badgeWidth
                        : center.X + 5;
                    var badge = new Rect(badgeX, center.Y - ((valueText.Height + 3) / 2), badgeWidth, valueText.Height + 3);
                    context.DrawRectangle(Brush.Parse("#F9FBFD"), new Pen(pinBrush, 0.6), badge, 3, 3);
                    context.DrawText(valueText, new Point(badge.X + 3.5, badge.Y + 1.2));
                }
            }
        }
    }

    private void DrawPorts(DrawingContext context, EditorViewModel viewModel, CircuitLayout layout)
    {
        if (Viewport.Scale < 0.3)
        {
            return;
        }

        double scale = Viewport.Scale;
        double fontSize = Math.Clamp(11.5 * scale, 8.5, 13);
        foreach (PortBox port in layout.Ports)
        {
            Rect screen = WorldRect(port.Bounds);
            bool selected = string.Equals(
                viewModel.SelectedNetId,
                viewModel.Definition.FindPortNet(port.Pin.Id)?.Id,
                StringComparison.Ordinal);
            var border = selected ? new Pen(Accent, 2.2) : new Pen(BoxBorder, 1.1);
            context.DrawRectangle(PortFill, border, screen, screen.Height / 2, screen.Height / 2);

            (int value, SignalDomain? domain) = ReadNet(viewModel, port.NetId);
            IBrush valueBrush = value < 0 ? UndrivenBrush : ValueBrush(domain, value);
            string direction = port.Pin.Direction == PinDirection.Input ? "IN" : "OUT";
            FormattedText label = Text($"{direction}  {port.Pin.Name}", fontSize, Ink);
            label.MaxTextWidth = screen.Width - 12;
            label.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(label, new Point(screen.X + 8, screen.Center.Y - (label.Height / 2)));

            Point connector = Viewport.WorldToScreen(port.Connector);
            context.DrawEllipse(PinFill, null, connector, 4.2, 4.2);
            context.DrawEllipse(valueBrush, null, connector, 3.1, 3.1);
        }
    }

    private void DrawConnectionPreview(DrawingContext context)
    {
        if (_connectStart is null)
        {
            return;
        }

        Point start = Viewport.WorldToScreen(_connectAnchor);
        Point end = Viewport.WorldToScreen(_pointerWorld);
        var pen = new Pen(Accent, 2, new DashStyle([5, 4], 0), lineCap: PenLineCap.Round);
        double mid = (start.X + end.X) / 2;
        context.DrawLine(pen, start, new Point(mid, start.Y));
        context.DrawLine(pen, new Point(mid, start.Y), new Point(mid, end.Y));
        context.DrawLine(pen, new Point(mid, end.Y), end);
        context.DrawEllipse(Accent, null, end, 3, 3);

        PinHit? hover = _viewModel?.Layout?.HitPin(_pointerWorld, 12 / Math.Max(Viewport.Scale, 0.2));
        if (hover is not null)
        {
            Point target = Viewport.WorldToScreen(hover.Value.Position);
            context.DrawEllipse(null, new Pen(Accent, 2), target, 7, 7);
        }
    }

    private void DrawZoomBadge(DrawingContext context)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"{Viewport.Scale * 100:0}%  ·  ctrl+wheel zoom  ·  middle/alt drag pan");
        FormattedText formatted = Text(text, 10, Muted);
        var rect = new Rect(12, Bounds.Height - 26, formatted.Width + 14, 19);
        context.DrawRectangle(Brush.Parse("#EEF3F8"), new Pen(GridBrush, 1), rect, 4, 4);
        context.DrawText(formatted, new Point(rect.X + 7, rect.Y + 3));
    }

    private void DrawHint(DrawingContext context, string message)
    {
        FormattedText formatted = Text(message, 14, Muted);
        context.DrawText(formatted, new Point(
            (Bounds.Width - formatted.Width) / 2,
            (Bounds.Height - formatted.Height) / 2));
    }

    private void DrawHoverTooltip(DrawingContext context)
    {
        if (_hoverPin is null)
        {
            return;
        }

        PinHit hover = _hoverPin.Value;
        (int value, SignalDomain? domain) = hover.Pin.Kind == EditorPinKind.Port
            ? ReadNet(_viewModel!, _viewModel!.Simulator.NetFor(hover.Pin)?.Id ?? string.Empty)
            : ReadPin(_viewModel!, hover.Pin);
        string text = $"{hover.Pin.PinId} · {hover.DeclaredDomain ?? domain?.Id ?? "domain from net"} · {(value < 0 ? "z" : FormatValue(value, domain))}";
        FormattedText formatted = Text(text, 10.5, Brushes.White);
        var rect = new Rect(
            Math.Min(_pointerScreen.X + 14, Bounds.Width - formatted.Width - 20),
            Math.Min(_pointerScreen.Y + 12, Bounds.Height - 28),
            formatted.Width + 12,
            formatted.Height + 7);
        context.DrawRectangle(TooltipFill, null, rect, 4, 4);
        context.DrawText(formatted, new Point(rect.X + 6, rect.Y + 3));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_viewModel?.Layout is null)
        {
            return;
        }

        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        Point screen = e.GetPosition(this);
        Point world = Viewport.ScreenToWorld(screen);
        _pointerScreen = screen;
        _pointerWorld = world;
        double tolerance = 11 / Math.Max(Viewport.Scale, 0.2);

        if (point.Properties.IsMiddleButtonPressed ||
            (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            _panning = true;
            _lastScreen = screen;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            _connectStart = null;
            InvalidateVisual();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        PinHit? pin = _viewModel.Layout.HitPin(world, tolerance);
        if (pin is not null)
        {
            _connectStart = pin.Value.Pin;
            _connectAnchor = pin.Value.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        ComponentBox? component = _viewModel.Layout.HitComponent(world);
        if (component is not null)
        {
            _viewModel.SelectInstance(component.Instance.Id);
            if (e.ClickCount == 2 && component.Instance.DefinitionId is string definitionId)
            {
                _viewModel.OpenDefinition(definitionId);
                return;
            }

            _dragging = true;
            _draggedInstance = component.Instance.Id;
            _dragOffset = world - new Point(component.Instance.X, component.Instance.Y);
            _viewModel.BeginDrag();
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        PortBox? port = _viewModel.Layout.HitPortBody(world);
        if (port is not null)
        {
            _viewModel.SelectNet(port.NetId);
            e.Handled = true;
            return;
        }

        NetWire? wire = _viewModel.Layout.HitWire(world, tolerance);
        if (wire is not null)
        {
            _viewModel.SelectNet(wire.NetId);
            e.Handled = true;
            return;
        }

        _viewModel.ClearSelection();
        _panning = true;
        _lastScreen = screen;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point screen = e.GetPosition(this);
        Point world = Viewport.ScreenToWorld(screen);
        _pointerScreen = screen;
        _pointerWorld = world;
        _viewModel?.SetPlacementPoint(world);

        if (_panning)
        {
            Vector delta = screen - _lastScreen;
            Viewport.Pan(delta);
            _lastScreen = screen;
            InvalidateVisual();
            return;
        }

        if (_dragging && _draggedInstance is not null)
        {
            _viewModel?.MoveInstance(
                _draggedInstance,
                world.X - _dragOffset.X,
                world.Y - _dragOffset.Y);
            return;
        }

        if (_connectStart is not null)
        {
            InvalidateVisual();
            return;
        }

        PinHit? hover = _viewModel?.Layout?.HitPin(world, 11 / Math.Max(Viewport.Scale, 0.2));
        bool changed = !Nullable.Equals(hover, _hoverPin);
        _hoverPin = hover;
        if (changed)
        {
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Point screen = e.GetPosition(this);
        Point world = Viewport.ScreenToWorld(screen);
        _pointerWorld = world;

        if (_connectStart is EditorPin start && _viewModel is not null)
        {
            double tolerance = 11 / Math.Max(Viewport.Scale, 0.2);
            PinHit? targetPin = _viewModel.Layout?.HitPin(world, tolerance);
            NetWire? targetWire = targetPin is null ? _viewModel.Layout?.HitWire(world, tolerance) : null;
            if (targetPin is not null && targetPin.Value.Pin != start)
            {
                _viewModel.Connect(start, targetPin.Value.Pin);
            }
            else if (targetWire is not null)
            {
                _viewModel.AttachPinToNet(start, targetWire.NetId);
            }

            _connectStart = null;
        }

        if (_dragging)
        {
            _viewModel?.EndDrag();
        }

        _dragging = false;
        _draggedInstance = null;
        _panning = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point screen = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Viewport.ZoomAt(screen, e.Delta.Y > 0 ? 1.12 : 1 / 1.12);
        }
        else
        {
            Viewport.Pan(new Vector(e.Delta.X * 28, e.Delta.Y * 28));
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_viewModel is null)
        {
            return;
        }

        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (control && e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _viewModel.Redo();
            e.Handled = true;
        }
        else if (control && e.Key == Key.Z)
        {
            _viewModel.Undo();
            e.Handled = true;
        }
        else if (control && e.Key == Key.Y)
        {
            _viewModel.Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _viewModel.DeleteSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _connectStart = null;
            _viewModel.ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            FitToContent();
            e.Handled = true;
        }
        else if ((e.Key is Key.Left or Key.Right or Key.Up or Key.Down) && _viewModel.HasSelectedInstance)
        {
            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            _viewModel.NudgeInstance(_viewModel.SelectedInstanceId, dx, dy);
            e.Handled = true;
        }
    }

    private Rect WorldRect(Rect world)
    {
        Point topLeft = Viewport.WorldToScreen(world.TopLeft);
        return new Rect(topLeft.X, topLeft.Y, world.Width * Viewport.Scale, world.Height * Viewport.Scale);
    }

    private static (int Value, SignalDomain? Domain) ReadNet(EditorViewModel viewModel, string netId)
    {
        if (viewModel.Simulator.Engine is null ||
            !viewModel.Simulator.TryNetIndex(netId, out int index, out SignalDomain? domain))
        {
            return (SignalValue.UndrivenIndex, null);
        }

        return (viewModel.Simulator.Engine.GetNetValue(index), domain);
    }

    private static (int Value, SignalDomain? Domain) ReadPin(EditorViewModel viewModel, EditorPin pin)
    {
        if (viewModel.Simulator.Engine is null || !viewModel.Simulator.TryRead(pin, out int value, out SignalDomain? domain))
        {
            return (SignalValue.UndrivenIndex, viewModel.Simulator.DomainFor(pin));
        }

        return (value, domain);
    }

    private static string FormatValue(int value, SignalDomain? domain)
    {
        if (value < 0)
        {
            return "z";
        }

        return domain is not null && domain.IsValidValue(value) ? domain.ValueName(value) : "!";
    }

    private static IBrush WireBrush(SignalDomain? domain, int value)
    {
        if (value < 0)
        {
            return UndrivenBrush;
        }

        if (domain is null)
        {
            return Muted;
        }

        if (domain.ValueCount == 2)
        {
            return value == 0 ? LowBrush : HighBrush;
        }

        if (domain.ValueCount == 3)
        {
            return value switch
            {
                0 => LowBrush,
                1 => TernaryOne,
                _ => TernaryTwo,
            };
        }

        return ValueBrush(domain, value);
    }

    private static IBrush ValueBrush(SignalDomain? domain, int value)
    {
        if (value < 0)
        {
            return UndrivenBrush;
        }

        if (domain is null || domain.ValueCount <= 1)
        {
            return Muted;
        }

        int bucket = value % 6;
        return bucket switch
        {
            0 => LowBrush,
            1 => HighBrush,
            2 => TernaryOne,
            3 => TernaryTwo,
            4 => Brush.Parse("#7C5CBF"),
            _ => Brush.Parse("#B0427A"),
        };
    }

    private static FormattedText Text(
        string text,
        double size,
        IBrush brush,
        FontWeight weight = FontWeight.Normal)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight, FontStretch.Normal),
            size,
            brush);
    }
}
