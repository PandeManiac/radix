using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Radix.Core.Model;
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
    private IBrush _canvasBackground = Brushes.White;
    private IBrush _gridBrush = Brushes.Gray;
    private IBrush _boxFill = Brushes.White;
    private IBrush _boxHeader = Brushes.WhiteSmoke;
    private IBrush _boxBorder = Brushes.Gray;
    private IBrush _accent = Brushes.SteelBlue;
    private IBrush _accentSoft = Brushes.LightSteelBlue;
    private IBrush _ink = Brushes.Black;
    private IBrush _muted = Brushes.Gray;
    private IBrush _portFill = Brushes.WhiteSmoke;
    private IBrush _pinFill = Brushes.DimGray;
    private IBrush _highBrush = Brushes.SeaGreen;
    private IBrush _lowBrush = Brushes.SlateGray;
    private IBrush _undrivenBrush = Brushes.LightGray;
    private IBrush _ternaryOne = Brushes.SteelBlue;
    private IBrush _ternaryTwo = Brushes.DarkOrange;
    private IBrush _signalThree = Brushes.MediumPurple;
    private IBrush _signalFour = Brushes.MediumVioletRed;
    private IBrush _tooltipFill = Brushes.Black;
    private IBrush _tooltipText = Brushes.White;
    private IBrush _badgeBackground = Brushes.WhiteSmoke;
    private IBrush _wireLabelBackground = Brushes.WhiteSmoke;
    private IBrush _zoomBadgeBackground = Brushes.WhiteSmoke;
    private IBrush _panelBackground = Brushes.White;

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
    private PinHit? _hoverPin;
    private NetWire? _hoverWire;
    private ComponentBox? _hoverComponent;
    private string? _hoverNetId;
    private string? _draggedWaypointNet;
    private int _draggedWaypointIndex = -1;
    private bool _draggingWaypoint;
    private readonly Dictionary<string, Point> _dragStarts = new(StringComparer.Ordinal);
    private Point _dragStartWorld;
    private bool _rubberBanding;
    private Point _rubberBandStart;
    private Point _rubberBandCurrent;

    public CircuitCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
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
        LoadTheme();
        context.FillRectangle(_canvasBackground, new Rect(Bounds.Size));
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
        DrawRubberBand(context);
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
        var pen = new Pen(_gridBrush, 1);
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
        HashSet<string> focus = FocusNets(viewModel, layout);
        bool hasFocus = focus.Count > 0;
        double dot = Viewport.Scale < 0.5 ? 2.2 : 3.2;

        foreach (NetWire wire in layout.Wires.OrderBy(w => w.NetId, StringComparer.Ordinal))
        {
            (int value, SignalDomain? domain) = ReadNet(viewModel, wire.NetId);
            Net? net = viewModel.Definition.FindNet(wire.NetId);
            IBrush signalBrush = WireBrush(domain, value);
            IBrush brush = net?.WireColor is string custom && custom.Length == 7
                ? Brush.Parse(custom)
                : signalBrush;
            bool selected = string.Equals(viewModel.SelectedNetId, wire.NetId, StringComparison.Ordinal);
            bool hovered = ReferenceEquals(_hoverWire, wire);
            bool focused = focus.Contains(wire.NetId);
            bool dim = hasFocus && !focused;
            double opacity = dim ? 0.22 : 1.0;

            if (selected)
            {
                var halo = new Pen(_accentSoft, 8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                DrawWireSegments(context, halo, wire, drawBreaks: false);
            }
            else if (hovered)
            {
                var halo = new Pen(WithOpacity(_accentSoft, 0.55), 6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                DrawWireSegments(context, halo, wire, drawBreaks: false);
            }

            bool undriven = value < 0;
            IBrush lineBrush = WithOpacity(brush, opacity);
            var pen = undriven
                ? new Pen(lineBrush, selected ? 2.1 : 1.6, new DashStyle([4, 3], 0))
                : new Pen(
                    selected ? _accent : lineBrush,
                    selected ? 2.8 : dim ? 1.6 : 2.1,
                    lineCap: PenLineCap.Round,
                    lineJoin: PenLineJoin.Round);
            DrawWireSegments(context, pen, wire, drawBreaks: true);

            foreach (Point junction in wire.Junctions)
            {
                context.DrawEllipse(lineBrush, null, Viewport.WorldToScreen(junction), dot, dot);
            }

            if (Viewport.Scale >= 0.72 && (!dim || focused))
            {
                string label = net?.Label ?? wire.NetId;
                string valueText = value < 0 ? "z" : FormatValue(value, domain);
                DrawWireLabel(context, wire.LabelPoint, $"{label} = {valueText}", lineBrush);
            }
        }

        DrawRouteHandles(context, viewModel, layout);
    }

    private HashSet<string> FocusNets(EditorViewModel viewModel, CircuitLayout layout)
    {
        var focus = new HashSet<string>(StringComparer.Ordinal);
        if (viewModel.SelectedNetId.Length > 0)
        {
            focus.Add(viewModel.SelectedNetId);
        }

        foreach (string instanceId in viewModel.SelectedInstanceIds)
        {
            if (layout.Components.FirstOrDefault(c => c.Instance.Id == instanceId) is ComponentBox box)
            {
                focus.UnionWith(layout.NetsForInstance(box.Instance));
            }
        }

        if (_hoverNetId is not null)
        {
            focus.Add(_hoverNetId);
        }

        if (_hoverWire is not null)
        {
            focus.Add(_hoverWire.NetId);
        }

        if (_hoverComponent is not null)
        {
            focus.UnionWith(layout.NetsForInstance(_hoverComponent.Instance));
        }

        if (_hoverPin is not null)
        {
            focus.UnionWith(layout.NetsForPin(_hoverPin.Value.Pin));
        }

        return focus;
    }

    private void DrawWireSegments(DrawingContext context, IPen pen, NetWire wire, bool drawBreaks)
    {
        foreach ((Point a, Point b) in wire.Segments)
        {
            Point sa = Viewport.WorldToScreen(a);
            Point sb = Viewport.WorldToScreen(b);
            bool horizontal = Math.Abs(sa.Y - sb.Y) < 0.5;
            double start = horizontal ? sa.X : sa.Y;
            double end = horizontal ? sb.X : sb.Y;
            if (Math.Abs(end - start) < 0.2)
            {
                continue;
            }

            double direction = Math.Sign(end - start);
            double low = Math.Min(start, end);
            double high = Math.Max(start, end);
            var breakCoordinates = new List<double>();
            if (drawBreaks)
            {
                foreach (Point breakWorld in wire.Breaks)
                {
                    Point screen = Viewport.WorldToScreen(breakWorld);
                    double along = horizontal ? screen.X : screen.Y;
                    double across = horizontal ? screen.Y - sa.Y : screen.X - sa.X;
                    if (Math.Abs(across) < 0.8 && along > low + 1 && along < high - 1)
                    {
                        breakCoordinates.Add(along);
                    }
                }
            }

            breakCoordinates.Sort();
            double cursor = start;
            const double gap = 4.5;
            foreach (double breakAt in breakCoordinates)
            {
                double stop = breakAt - gap;
                if ((stop - cursor) * direction > 0.5)
                {
                    DrawAlong(context, pen, sa, sb, horizontal, cursor, stop);
                }

                cursor = breakAt + gap;
            }

            if ((end - cursor) * direction > 0.5)
            {
                DrawAlong(context, pen, sa, sb, horizontal, cursor, end);
            }
        }
    }

    private static void DrawAlong(
        DrawingContext context,
        IPen pen,
        Point a,
        Point b,
        bool horizontal,
        double from,
        double to)
    {
        Point start = horizontal ? new Point(from, a.Y) : new Point(a.X, from);
        Point end = horizontal ? new Point(to, a.Y) : new Point(a.X, to);
        context.DrawLine(pen, start, end);
    }

    private void DrawRouteHandles(DrawingContext context, EditorViewModel viewModel, CircuitLayout layout)
    {
        if (viewModel.SelectedNetId.Length == 0 || Viewport.Scale < 0.35)
        {
            return;
        }

        NetWire? wire = layout.FindWire(viewModel.SelectedNetId);
        if (wire is null)
        {
            return;
        }

        foreach (NetWaypoint point in wire.RoutePoints)
        {
            Point screen = Viewport.WorldToScreen(new Point(point.X, point.Y));
            context.DrawEllipse(_panelBackground, new Pen(_accent, 1.8), screen, 4.6, 4.6);
            context.DrawEllipse(_accent, null, screen, 1.6, 1.6);
        }

        Point label = Viewport.WorldToScreen(wire.LabelPoint);
        var hint = new Rect(label.X + 3, label.Y + 8, 15, 15);
        context.DrawRectangle(_panelBackground, new Pen(WithOpacity(_accent, 0.6), 1, new DashStyle([2, 2], 0)), hint, 3, 3);
        FormattedText plus = Text("+", 11, _accent, FontWeight.SemiBold);
        context.DrawText(plus, new Point(hint.X + 4, hint.Y - 1));
    }

    private void DrawWireLabel(DrawingContext context, Point world, string text, IBrush accent)
    {
        Point position = Viewport.WorldToScreen(world);
        FormattedText formatted = Text(text, 10, _ink);
        var background = new Rect(
            position.X + 3,
            position.Y - 8,
            formatted.Width + 8,
            formatted.Height + 3);
        context.DrawRectangle(_wireLabelBackground, new Pen(accent, 0.8), background, 3, 3);
        context.DrawText(formatted, new Point(background.X + 4, background.Y + 1));
    }

    private static IBrush WithOpacity(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;

    private void DrawComponents(DrawingContext context, EditorViewModel viewModel, CircuitLayout layout)
    {
        double scale = Viewport.Scale;
        bool detailed = scale >= 0.32;
        double fontSize = Math.Clamp(12 * scale, 8.5, 14);
        double idSize = Math.Clamp(9 * scale, 7, 10);
        HashSet<string> focus = FocusNets(viewModel, layout);

        foreach (ComponentBox component in layout.Components)
        {
            Rect screen = WorldRect(component.Bounds);
            bool selected = viewModel.SelectedInstanceIds.Contains(component.Instance.Id);
            bool hovered = ReferenceEquals(_hoverComponent, component);
            var border = selected
                ? new Pen(_accent, 2.4)
                : hovered
                    ? new Pen(WithOpacity(_accent, 0.75), 1.8)
                    : new Pen(_boxBorder, 1.1);
            context.DrawRectangle(_boxFill, border, screen, 6, 6);
            if (hovered && !selected)
            {
                context.DrawRectangle(null, new Pen(WithOpacity(_accentSoft, 0.9), 4), screen.Inflate(2), 7, 7);
            }

            double header = Math.Min(screen.Height, CircuitLayout.HeaderHeight * scale);
            if (header > 3)
            {
                using (context.PushClip(new Rect(screen.X, screen.Y, screen.Width, header)))
                {
                    context.FillRectangle(_boxHeader, new Rect(screen.X, screen.Y, screen.Width, header));
                }
            }

            if (!detailed)
            {
                continue;
            }

            Point titleOrigin = new(screen.X + (8 * Math.Max(scale, 0.6)), screen.Y + (5 * scale));
            FormattedText title = Text(component.Title, fontSize, _ink, FontWeight.SemiBold);
            title.MaxTextWidth = Math.Max(10, screen.Width - (16 * Math.Max(scale, 0.6)));
            title.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(title, titleOrigin);

            if (scale >= 0.85)
            {
                FormattedText id = Text(component.Instance.Id, idSize, _muted);
                id.MaxTextWidth = Math.Max(10, screen.Width - 16);
                id.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(id, new Point(titleOrigin.X, titleOrigin.Y + title.Height + 1));
            }

            double pinSize = Math.Clamp(10.5 * scale, 7.5, 12);
            foreach (PinVisual pin in component.Pins)
            {
                Point center = Viewport.WorldToScreen(pin.Position);
                (int value, SignalDomain? domain) = ReadPin(viewModel, EditorPin.ForInstance(component.Instance.Id, pin.PinId));
                IBrush pinBrush = value < 0 ? _undrivenBrush : ValueBrush(domain, value);
                double radius = Math.Clamp(3.4 * scale, 2.2, 4.2);
                bool pinFocused = focus.Any(netId =>
                    layout.Definition.FindNet(netId)?.Pins.Any(p =>
                        string.Equals(p.InstanceId, component.Instance.Id, StringComparison.Ordinal) &&
                        string.Equals(p.PinId, pin.PinId, StringComparison.Ordinal)) == true);
                if (pinFocused)
                {
                    context.DrawEllipse(null, new Pen(WithOpacity(_accent, 0.7), 2.2), center, radius + 3.4, radius + 3.4);
                }

                context.DrawEllipse(_pinFill, null, center, radius + 0.8, radius + 0.8);
                context.DrawEllipse(pinBrush, null, center, radius, radius);

                FormattedText label = Text(pin.Name, pinSize, _muted);
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
                    context.DrawRectangle(_badgeBackground, new Pen(pinBrush, 0.6), badge, 3, 3);
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
            var border = selected ? new Pen(_accent, 2.2) : new Pen(_boxBorder, 1.1);
            context.DrawRectangle(_portFill, border, screen, screen.Height / 2, screen.Height / 2);

            (int value, SignalDomain? domain) = ReadNet(viewModel, port.NetId);
            IBrush valueBrush = value < 0 ? _undrivenBrush : ValueBrush(domain, value);
            string direction = port.Pin.Direction == PinDirection.Input ? "IN" : "OUT";
            FormattedText label = Text($"{direction}  {port.Pin.Name}", fontSize, _ink);
            label.MaxTextWidth = screen.Width - 12;
            label.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(label, new Point(screen.X + 8, screen.Center.Y - (label.Height / 2)));

            Point connector = Viewport.WorldToScreen(port.Connector);
            context.DrawEllipse(_pinFill, null, connector, 4.2, 4.2);
            context.DrawEllipse(valueBrush, null, connector, 3.1, 3.1);
        }
    }

    private void DrawRubberBand(DrawingContext context)
    {
        if (!_rubberBanding)
        {
            return;
        }

        Point a = Viewport.WorldToScreen(_rubberBandStart);
        Point b = Viewport.WorldToScreen(_rubberBandCurrent);
        var rect = new Rect(a, b);
        context.DrawRectangle(WithOpacity(_accentSoft, 0.35), new Pen(_accent, 1.2, new DashStyle([4, 3], 0)), rect, 2, 2);
    }

    private void DrawConnectionPreview(DrawingContext context)
    {
        if (_connectStart is null)
        {
            return;
        }

        Point start = Viewport.WorldToScreen(_connectAnchor);
        Point end = Viewport.WorldToScreen(_pointerWorld);
        var pen = new Pen(_accent, 2, new DashStyle([5, 4], 0), lineCap: PenLineCap.Round);
        double mid = (start.X + end.X) / 2;
        context.DrawLine(pen, start, new Point(mid, start.Y));
        context.DrawLine(pen, new Point(mid, start.Y), new Point(mid, end.Y));
        context.DrawLine(pen, new Point(mid, end.Y), end);
        context.DrawEllipse(_accent, null, end, 3, 3);

        PinHit? hover = _viewModel?.Layout?.HitPin(_pointerWorld, 12 / Math.Max(Viewport.Scale, 0.2));
        if (hover is not null)
        {
            Point target = Viewport.WorldToScreen(hover.Value.Position);
            context.DrawEllipse(null, new Pen(_accent, 2), target, 7, 7);
        }
    }

    private void DrawZoomBadge(DrawingContext context)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"{Viewport.Scale * 100:0}%  ·  ctrl+wheel zoom  ·  middle/alt drag pan");
        FormattedText formatted = Text(text, 10, _muted);
        var rect = new Rect(12, Bounds.Height - 26, formatted.Width + 14, 19);
        context.DrawRectangle(_zoomBadgeBackground, new Pen(_gridBrush, 1), rect, 4, 4);
        context.DrawText(formatted, new Point(rect.X + 7, rect.Y + 3));
    }

    private void DrawHint(DrawingContext context, string message)
    {
        FormattedText formatted = Text(message, 14, _muted);
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
        FormattedText formatted = Text(text, 10.5, _tooltipText);
        var rect = new Rect(
            Math.Min(_pointerScreen.X + 14, Bounds.Width - formatted.Width - 20),
            Math.Min(_pointerScreen.Y + 12, Bounds.Height - 28),
            formatted.Width + 12,
            formatted.Height + 7);
        context.DrawRectangle(_tooltipFill, null, rect, 4, 4);
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
            SelectUnderPointer(world, tolerance);
            InvalidateVisual();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Bendpoint handles take priority over the wire beneath them.
        if (_viewModel.SelectedNetId.Length > 0 &&
            _viewModel.Layout.FindWire(_viewModel.SelectedNetId) is NetWire selectedWire)
        {
            int? waypoint = HitWaypoint(selectedWire, world);
            if (waypoint is int index)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                {
                    _viewModel.RemoveNetWaypoint(selectedWire.NetId, index);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }

                _draggedWaypointNet = selectedWire.NetId;
                _draggedWaypointIndex = index;
                _draggingWaypoint = true;
                _viewModel.BeginDrag();
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            Point labelScreen = Viewport.WorldToScreen(selectedWire.LabelPoint);
            var addHandle = new Point(labelScreen.X + 10, labelScreen.Y + 15);
            if (CircuitLayout.Distance(screen, addHandle) < 11)
            {
                _viewModel.AddNetWaypoint(selectedWire.NetId, selectedWire.LabelPoint);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
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
            bool additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (additive)
            {
                _viewModel.SelectInstance(component.Instance.Id, additive: true);
            }
            else if (!_viewModel.SelectedInstanceIds.Contains(component.Instance.Id))
            {
                _viewModel.SelectInstance(component.Instance.Id);
            }

            if (e.ClickCount == 2 && component.Instance.DefinitionId is string definitionId)
            {
                _viewModel.OpenDefinition(definitionId);
                return;
            }

            _dragStartWorld = world;
            _dragStarts.Clear();
            foreach (string id in _viewModel.SelectedInstanceIds)
            {
                if (_viewModel.Definition.FindInstance(id) is ComponentInstance instance)
                {
                    _dragStarts[id] = new Point(instance.X, instance.Y);
                }
            }

            _dragging = _dragStarts.Count > 0;
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
            if (e.ClickCount == 2 && NearestPointOnWire(wire, world) is Point nearest)
            {
                _viewModel.AddNetWaypoint(wire.NetId, nearest);
            }

            e.Handled = true;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _viewModel.ClearSelection();
        }

        _rubberBanding = true;
        _rubberBandStart = world;
        _rubberBandCurrent = world;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void SelectUnderPointer(Point world, double tolerance)
    {
        if (_viewModel?.Layout is null)
        {
            return;
        }

        if (_viewModel.Layout.HitPin(world, tolerance) is PinHit pin)
        {
            _viewModel.SelectNet(_viewModel.Simulator.NetFor(pin.Pin)?.Id);
            return;
        }

        if (_viewModel.Layout.HitComponent(world) is ComponentBox component)
        {
            _viewModel.SelectInstance(component.Instance.Id);
            return;
        }

        if (_viewModel.Layout.HitPortBody(world) is PortBox port)
        {
            _viewModel.SelectNet(port.NetId);
            return;
        }

        if (_viewModel.Layout.HitWire(world, tolerance) is NetWire wire)
        {
            _viewModel.SelectNet(wire.NetId);
        }
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

        if (_draggingWaypoint && _draggedWaypointNet is not null && _viewModel is not null)
        {
            _viewModel.MoveNetWaypoint(_draggedWaypointNet, _draggedWaypointIndex, world);
            return;
        }

        if (_rubberBanding)
        {
            _rubberBandCurrent = world;
            InvalidateVisual();
            return;
        }

        if (_dragging && _viewModel is not null)
        {
            double dx = world.X - _dragStartWorld.X;
            double dy = world.Y - _dragStartWorld.Y;
            foreach ((string id, Point start) in _dragStarts)
            {
                _viewModel.SetInstanceState(id, start.X + dx, start.Y + dy);
            }

            return;
        }

        if (_connectStart is not null)
        {
            InvalidateVisual();
            return;
        }

        bool changed = false;
        PinHit? hover = _viewModel?.Layout?.HitPin(world, 11 / Math.Max(Viewport.Scale, 0.2));
        if (!Nullable.Equals(hover, _hoverPin))
        {
            _hoverPin = hover;
            changed = true;
        }

        ComponentBox? hoverComponent = hover is null ? _viewModel?.Layout?.HitComponent(world) : null;
        if (!ReferenceEquals(hoverComponent, _hoverComponent))
        {
            _hoverComponent = hoverComponent;
            changed = true;
        }

        PortBox? hoverPort = hover is null && hoverComponent is null ? _viewModel?.Layout?.HitPortBody(world) : null;
        string? hoverNet = hoverPort?.NetId;
        NetWire? hoverWire = hover is null && hoverComponent is null && hoverPort is null
            ? _viewModel?.Layout?.HitWire(world, 8 / Math.Max(Viewport.Scale, 0.2))
            : null;
        if (hoverNet is null && hoverWire is not null)
        {
            hoverNet = hoverWire.NetId;
        }

        if (!ReferenceEquals(hoverWire, _hoverWire))
        {
            _hoverWire = hoverWire;
            changed = true;
        }

        if (!string.Equals(hoverNet, _hoverNetId, StringComparison.Ordinal))
        {
            _hoverNetId = hoverNet;
            changed = true;
        }

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

        if (_draggingWaypoint)
        {
            _viewModel?.EndDrag();
            _draggingWaypoint = false;
            _draggedWaypointNet = null;
            _draggedWaypointIndex = -1;
        }

        if (_rubberBanding)
        {
            Point start = Viewport.WorldToScreen(_rubberBandStart);
            bool dragged = CircuitLayout.Distance(screen, start) > 5;
            if (_viewModel?.Layout is CircuitLayout layout)
            {
                Rect worldRect = new(_rubberBandStart, _rubberBandCurrent);
                if (dragged)
                {
                    IEnumerable<string> hits = layout.Components
                        .Where(c => c.Bounds.Intersects(worldRect))
                        .Select(c => c.Instance.Id);
                    _viewModel.SelectInstances(hits, additive: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                }
            }

            _rubberBanding = false;
        }

        if (_connectStart is EditorPin startPin && _viewModel is not null)
        {
            double tolerance = 11 / Math.Max(Viewport.Scale, 0.2);
            PinHit? targetPin = _viewModel.Layout?.HitPin(world, tolerance);
            NetWire? targetWire = targetPin is null ? _viewModel.Layout?.HitWire(world, tolerance) : null;
            if (targetPin is not null && targetPin.Value.Pin != startPin)
            {
                _viewModel.Connect(startPin, targetPin.Value.Pin);
            }
            else if (targetWire is not null)
            {
                _viewModel.AttachPinToNet(startPin, targetWire.NetId);
            }

            _connectStart = null;
        }

        if (_dragging)
        {
            _viewModel?.EndDrag();
        }

        _dragging = false;
        _dragStarts.Clear();
        _panning = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    private static int? HitWaypoint(NetWire wire, Point world)
    {
        for (int i = 0; i < wire.RoutePoints.Count; i++)
        {
            NetWaypoint point = wire.RoutePoints[i];
            if (CircuitLayout.Distance(world, new Point(point.X, point.Y)) <= 9)
            {
                return i;
            }
        }

        return null;
    }

    private static Point? NearestPointOnWire(NetWire wire, Point world)
    {
        Point? best = null;
        double bestDistance = 14;
        foreach ((Point a, Point b) in wire.Segments)
        {
            Point candidate = NearestPointOnSegment(world, a, b);
            double distance = CircuitLayout.Distance(world, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static Point NearestPointOnSegment(Point point, Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared < 1e-9)
        {
            return a;
        }

        double t = (((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        return new Point(a.X + (t * dx), a.Y + (t * dy));
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

    private IBrush WireBrush(SignalDomain? domain, int value)
    {
        if (value < 0)
        {
            return _undrivenBrush;
        }

        if (domain is null)
        {
            return _muted;
        }

        if (domain.ValueCount == 2)
        {
            return value == 0 ? _lowBrush : _highBrush;
        }

        if (domain.ValueCount == 3)
        {
            return value switch
            {
                0 => _lowBrush,
                1 => _ternaryOne,
                _ => _ternaryTwo,
            };
        }

        return ValueBrush(domain, value);
    }

    private IBrush ValueBrush(SignalDomain? domain, int value)
    {
        if (value < 0)
        {
            return _undrivenBrush;
        }

        if (domain is null || domain.ValueCount <= 1)
        {
            return _muted;
        }

        int bucket = value % 6;
        return bucket switch
        {
            0 => _lowBrush,
            1 => _highBrush,
            2 => _ternaryOne,
            3 => _ternaryTwo,
            4 => _signalThree,
            _ => _signalFour,
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

    /// <summary>Resolves semantic theme tokens once per frame for custom drawing.</summary>
    private void LoadTheme()
    {
        _canvasBackground = ThemeBrushes.Get(this, "App.CanvasBackground", "#F8FAFC");
        _gridBrush = ThemeBrushes.Get(this, "App.CanvasGrid", "#E9EEF3");
        _boxFill = ThemeBrushes.Get(this, "App.ComponentFill", "#FFFDFC");
        _boxHeader = ThemeBrushes.Get(this, "App.ComponentHeader", "#F1F5F9");
        _boxBorder = ThemeBrushes.Get(this, "App.ComponentBorder", "#9BAAB8");
        _accent = ThemeBrushes.Get(this, "App.Accent", "#2B6CB0");
        _accentSoft = ThemeBrushes.Get(this, "App.AccentSoft", "#DCE9F7");
        _ink = ThemeBrushes.Get(this, "App.TextPrimary", "#1F2A36");
        _muted = ThemeBrushes.Get(this, "App.TextMuted", "#6F8092");
        _portFill = ThemeBrushes.Get(this, "App.PortFill", "#EEF3F8");
        _pinFill = ThemeBrushes.Get(this, "App.PinFill", "#4A5A6A");
        _highBrush = ThemeBrushes.Get(this, "App.SignalHigh", "#1F9D72");
        _lowBrush = ThemeBrushes.Get(this, "App.SignalLow", "#8091A3");
        _undrivenBrush = ThemeBrushes.Get(this, "App.SignalUndriven", "#B7C2CC");
        _ternaryOne = ThemeBrushes.Get(this, "App.SignalOne", "#2B6CB0");
        _ternaryTwo = ThemeBrushes.Get(this, "App.SignalTwo", "#D97706");
        _signalThree = ThemeBrushes.Get(this, "App.SignalThree", "#7C5CBF");
        _signalFour = ThemeBrushes.Get(this, "App.SignalFour", "#B0427A");
        _tooltipFill = ThemeBrushes.Get(this, "App.TooltipBackground", "#1F2A36");
        _tooltipText = ThemeBrushes.Get(this, "App.TooltipText", "#FFFFFF");
        _badgeBackground = ThemeBrushes.Get(this, "App.PinBadgeBackground", "#F9FBFD");
        _wireLabelBackground = ThemeBrushes.Get(this, "App.WireLabelBackground", "#F2F5F8");
        _zoomBadgeBackground = ThemeBrushes.Get(this, "App.ZoomBadgeBackground", "#EEF3F8");
        _panelBackground = ThemeBrushes.Get(this, "App.PanelBackground", "#FFFFFF");
    }
}
