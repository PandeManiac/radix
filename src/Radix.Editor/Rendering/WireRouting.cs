using Avalonia;
using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Editor.Rendering;

/// <summary>
/// Orthogonal wire router. It is deliberately editor-only: routing never changes
/// connectivity or simulation.
/// </summary>
/// <remarks>
/// Automatic routes are found with A* over a sparse orthogonal grid whose lines are
/// derived from component edges, pin extensions, and a few routing channels. Bodies
/// are obstacles; crossings are allowed but penalised. Multi-terminal nets grow a
/// shared tree, so a clock net branches to every sink without retracing the whole
/// harness. Authored bendpoints bypass the search and are honoured literally.
/// </remarks>
internal static class WireRouter
{
    private const double PinStub = 22;
    private const double ObstaclePadding = 3;
    private const double Channel = 10;
    private const int MaxAxisCoordinates = 150;

    public static RoutedWire Route(
        Net net,
        int netOrdinal,
        IReadOnlyList<WireTerminal> terminals,
        IReadOnlyList<ComponentBox> components,
        IReadOnlyList<PortBox> ports,
        IReadOnlyList<(Point A, Point B)> routedSegments)
    {
        var obstacles = BuildObstacles(components, ports);
        if (terminals.Count == 0)
        {
            return RoutedWire.Empty;
        }

        if (net.RoutePoints.Count > 0)
        {
            var builder = new PathBuilder();
            Point label = RouteAuthored(net, terminals, builder);
            return Finish(builder.Points, builder.Segments, builder.Junctions, label, net);
        }

        if (terminals.Count == 1)
        {
            var builder = new PathBuilder();
            Point label = RouteSingle(terminals[0], obstacles, builder);
            return Finish(builder.Points, builder.Segments, [], label, net);
        }

        var router = new GridRouter(terminals, obstacles, routedSegments);
        RoutedWire? routed = router.Route();
        if (routed is null || routed.Segments.Count == 0)
        {
            var builder = new PathBuilder();
            Point label = RouteFallback(terminals, builder);
            return Finish(builder.Points, builder.Segments, [], label, net);
        }

        return routed with { AuthoredPoints = net.RoutePoints.ToArray() };
    }

    private static RoutedWire Finish(
        IReadOnlyList<Point> points,
        IReadOnlyList<(Point A, Point B)> segments,
        IReadOnlyList<Point> junctions,
        Point label,
        Net net)
    {
        if (segments.Count == 0)
        {
            return RoutedWire.Empty;
        }

        if (label == default)
        {
            label = MidpointOfLongestSegment(segments);
        }

        return new RoutedWire(points, segments, junctions, label, net.RoutePoints.ToArray());
    }

    private static List<Obstacle> BuildObstacles(
        IReadOnlyList<ComponentBox> components,
        IReadOnlyList<PortBox> ports)
    {
        var obstacles = new List<Obstacle>(components.Count + ports.Count);
        foreach (ComponentBox component in components)
        {
            obstacles.Add(new Obstacle(component.Bounds.Inflate(ObstaclePadding), false));
        }

        foreach (PortBox port in ports)
        {
            obstacles.Add(new Obstacle(port.Bounds.Inflate(2), true));
        }

        return obstacles;
    }

    private static Point RouteAuthored(Net net, IReadOnlyList<WireTerminal> terminals, PathBuilder builder)
    {
        WireTerminal first = terminals[0];
        builder.Add(first.Position);
        builder.Add(first.Position + new Vector(first.ExtendDirection * PinStub, 0));

        bool horizontalFirst = true;
        foreach (NetWaypoint waypoint in net.RoutePoints)
        {
            builder.ConnectTo(new Point(waypoint.X, waypoint.Y), horizontalFirst);
            horizontalFirst = !horizontalFirst;
        }

        for (int i = 1; i < terminals.Count; i++)
        {
            WireTerminal terminal = terminals[i];
            builder.ConnectTo(
                terminal.Position + new Vector(terminal.ExtendDirection * PinStub, 0),
                horizontalFirst: i % 2 == 0);
            builder.Add(terminal.Position);
        }

        if (terminals.Count == 1)
        {
            Point end = builder.Last;
            builder.Add(end + new Vector(first.ExtendDirection * 24, 0));
        }

        return MidpointOfLongestSegment(builder.Segments);
    }

    private static Point RouteSingle(
        WireTerminal terminal,
        IReadOnlyList<Obstacle> obstacles,
        PathBuilder builder)
    {
        double stub = StubLength(terminal, obstacles);
        Point start = terminal.Position;
        Point end = start + new Vector(terminal.ExtendDirection * stub, 0);
        builder.Add(start);
        builder.Add(end);
        return new Point((start.X + end.X) / 2, start.Y);
    }

    private static Point RouteFallback(IReadOnlyList<WireTerminal> terminals, PathBuilder builder)
    {
        WireTerminal first = terminals[0];
        builder.Add(first.Position);
        Point cursor = first.Position + new Vector(first.ExtendDirection * PinStub, 0);
        builder.Add(cursor);
        for (int i = 1; i < terminals.Count; i++)
        {
            WireTerminal terminal = terminals[i];
            builder.ConnectTo(terminal.Position, horizontalFirst: i % 2 == 0);
        }

        return cursor;
    }

    /// <summary>
    /// A pin stub must leave the component without cutting through a neighbouring
    /// body. When boxes are packed tightly, the available gap is shorter than the
    /// nominal stub and the route starts in that gap instead.
    /// </summary>
    internal static double StubLength(
        WireTerminal terminal,
        IReadOnlyList<Obstacle> obstacles,
        double preferred = PinStub)
    {
        double length = preferred;
        foreach (Obstacle obstacle in obstacles)
        {
            Rect bounds = obstacle.Bounds;
            if (bounds.Contains(terminal.Position))
            {
                continue;
            }

            if (terminal.Position.Y <= bounds.Top + 0.5 || terminal.Position.Y >= bounds.Bottom - 0.5)
            {
                continue;
            }

            if (terminal.ExtendDirection > 0 && bounds.Left >= terminal.Position.X)
            {
                length = Math.Min(length, bounds.Left - terminal.Position.X - 1);
            }
            else if (terminal.ExtendDirection < 0 && bounds.Right <= terminal.Position.X)
            {
                length = Math.Min(length, terminal.Position.X - bounds.Right - 1);
            }
        }

        return Math.Clamp(length, 3, PinStub);
    }

    internal static bool Intersects(Rect rect, Point from, Point to)
    {
        double minX = Math.Min(from.X, to.X);
        double maxX = Math.Max(from.X, to.X);
        double minY = Math.Min(from.Y, to.Y);
        double maxY = Math.Max(from.Y, to.Y);
        return maxX > rect.Left + 0.5 &&
               minX < rect.Right - 0.5 &&
               maxY > rect.Top + 0.5 &&
               minY < rect.Bottom - 0.5;
    }

    private static bool CrossesInterior(Rect rect, Point from, Point to)
    {
        const double epsilon = 0.2;
        double minX = Math.Min(from.X, to.X);
        double maxX = Math.Max(from.X, to.X);
        double minY = Math.Min(from.Y, to.Y);
        double maxY = Math.Max(from.Y, to.Y);
        return maxX > rect.Left + epsilon &&
               minX < rect.Right - epsilon &&
               maxY > rect.Top + epsilon &&
               minY < rect.Bottom - epsilon;
    }

    private static bool Overlaps(Point a1, Point a2, Point b1, Point b2)
    {
        bool aHorizontal = Math.Abs(a1.Y - a2.Y) < 0.5;
        bool bHorizontal = Math.Abs(b1.Y - b2.Y) < 0.5;
        if (aHorizontal != bHorizontal)
        {
            return false;
        }

        if (aHorizontal)
        {
            if (Math.Abs(a1.Y - b1.Y) > 0.5)
            {
                return false;
            }

            return Math.Min(Math.Max(a1.X, a2.X), Math.Max(b1.X, b2.X)) >
                   Math.Max(Math.Min(a1.X, a2.X), Math.Min(b1.X, b2.X)) + 0.5;
        }

        if (Math.Abs(a1.X - b1.X) > 0.5)
        {
            return false;
        }

        return Math.Min(Math.Max(a1.Y, a2.Y), Math.Max(b1.Y, b2.Y)) >
               Math.Max(Math.Min(a1.Y, a2.Y), Math.Min(b1.Y, b2.Y)) + 0.5;
    }

    internal static bool Intersects(Point a1, Point a2, Point b1, Point b2, out Point crossing)
    {
        crossing = default;
        bool aHorizontal = Math.Abs(a1.Y - a2.Y) < 0.5;
        bool bHorizontal = Math.Abs(b1.Y - b2.Y) < 0.5;
        if (aHorizontal == bHorizontal)
        {
            return false;
        }

        Point horizontalStart = aHorizontal ? a1 : b1;
        Point horizontalEnd = aHorizontal ? a2 : b2;
        Point verticalStart = aHorizontal ? b1 : a1;
        Point verticalEnd = aHorizontal ? b2 : a2;

        double x = verticalStart.X;
        double y = horizontalStart.Y;
        double hMin = Math.Min(horizontalStart.X, horizontalEnd.X);
        double hMax = Math.Max(horizontalStart.X, horizontalEnd.X);
        double vMin = Math.Min(verticalStart.Y, verticalEnd.Y);
        double vMax = Math.Max(verticalStart.Y, verticalEnd.Y);
        if (x <= hMin + 0.5 || x >= hMax - 0.5 || y <= vMin + 0.5 || y >= vMax - 0.5)
        {
            return false;
        }

        crossing = new Point(x, y);
        return true;
    }

    private static Point MidpointOfLongestSegment(IReadOnlyList<(Point A, Point B)> segments)
    {
        if (segments.Count == 0)
        {
            return default;
        }

        (Point A, Point B) longest = segments[0];
        double best = 0;
        foreach ((Point A, Point B) segment in segments)
        {
            double length = Math.Abs(segment.A.X - segment.B.X) + Math.Abs(segment.A.Y - segment.B.Y);
            if (length > best)
            {
                best = length;
                longest = segment;
            }
        }

        return new Point((longest.A.X + longest.B.X) / 2, (longest.A.Y + longest.B.Y) / 2);
    }

    internal readonly record struct Obstacle(Rect Bounds, bool IsPort);

    internal sealed record RoutedWire(
        IReadOnlyList<Point> Points,
        IReadOnlyList<(Point A, Point B)> Segments,
        IReadOnlyList<Point> Junctions,
        Point LabelPoint,
        IReadOnlyList<NetWaypoint> AuthoredPoints)
    {
        public static RoutedWire Empty { get; } = new([], [], [], default, []);
    }

    /// <summary>Sparse orthogonal grid A* shared by every terminal of one net.</summary>
    private sealed class GridRouter
    {
        private readonly IReadOnlyList<WireTerminal> _terminals;
        private readonly IReadOnlyList<Obstacle> _obstacles;
        private readonly IReadOnlyList<(Point A, Point B)> _otherWires;
        private readonly double[] _xs;
        private readonly double[] _ys;
        private readonly HashSet<int> _treeNodes = [];
        private readonly List<(Point A, Point B)> _segments = [];
        private readonly List<Point> _treePoints = [];
        private double _treeMinX = double.MaxValue;
        private double _treeMinY = double.MaxValue;
        private double _treeMaxX = double.MinValue;
        private double _treeMaxY = double.MinValue;

        public GridRouter(
            IReadOnlyList<WireTerminal> terminals,
            IReadOnlyList<Obstacle> obstacles,
            IReadOnlyList<(Point A, Point B)> otherWires)
        {
            _terminals = terminals;
            _obstacles = obstacles;
            _otherWires = otherWires;

            var extras = new List<Point>(terminals.Count * 2);
            foreach (WireTerminal terminal in terminals)
            {
                extras.Add(terminal.Position);
                double stub = StubLength(terminal, obstacles);
                extras.Add(terminal.Position + new Vector(terminal.ExtendDirection * stub, 0));
            }

            _xs = BuildAxis(extras.Select(p => p.X), obstacles.Select(o => o.Bounds.Left), obstacles.Select(o => o.Bounds.Right));
            _ys = BuildAxis(extras.Select(p => p.Y), obstacles.Select(o => o.Bounds.Top), obstacles.Select(o => o.Bounds.Bottom));
        }

        public RoutedWire? Route()
        {
            if (_xs.Length < 2 || _ys.Length < 2)
            {
                return null;
            }

            List<WireTerminal> ordered = OrderTerminals();
            StartTree(ordered[0]);
            for (int i = 1; i < ordered.Count; i++)
            {
                ConnectTerminal(ordered[i]);
            }

            if (_segments.Count == 0)
            {
                return null;
            }

            List<(Point A, Point B)> compressed = CompressSegments(_segments);
            var points = new List<Point>();
            foreach ((Point a, Point b) in compressed)
            {
                if (!points.Any(p => Near(p, a)))
                {
                    points.Add(a);
                }

                if (!points.Any(p => Near(p, b)))
                {
                    points.Add(b);
                }
            }

            IReadOnlyList<Point> junctions = FindJunctions(compressed);
            return new RoutedWire(
                points,
                compressed,
                junctions,
                MidpointOfLongestSegment(compressed),
                []);
        }

        private List<WireTerminal> OrderTerminals()
        {
            WireTerminal first = _terminals.FirstOrDefault(t => t.Direction == PinDirection.Output && !t.IsPort);
            if (first == default)
            {
                first = _terminals.FirstOrDefault(t => t.Direction == PinDirection.Output);
            }

            if (first.Position == default)
            {
                first = _terminals[0];
            }

            return _terminals
                .OrderByDescending(t => Near(t.Position, first.Position))
                .ThenBy(t => Distance(t.Position, first.Position))
                .ToList();
        }

        private void StartTree(WireTerminal terminal)
        {
            double stub = StubLength(terminal, _obstacles);
            Point extension = terminal.Position + new Vector(terminal.ExtendDirection * stub, 0);
            AddSegment(terminal.Position, extension);
            MarkTree(terminal.Position, extension);
            _treeMinX = Math.Min(terminal.Position.X, extension.X);
            _treeMaxX = Math.Max(terminal.Position.X, extension.X);
            _treeMinY = Math.Min(terminal.Position.Y, extension.Y);
            _treeMaxY = Math.Max(terminal.Position.Y, extension.Y);
        }

        private void ConnectTerminal(WireTerminal terminal)
        {
            double stub = StubLength(terminal, _obstacles);
            Point extension = terminal.Position + new Vector(terminal.ExtendDirection * stub, 0);
            int start = NodeIndex(extension);
            if (start < 0)
            {
                AddFallback(terminal, extension);
                return;
            }

            if (_treeNodes.Contains(start))
            {
                AddSegment(terminal.Position, extension);
                MarkTree(terminal.Position, extension);
                return;
            }

            List<Point>? path = FindPath(start);
            if (path is null || path.Count == 0)
            {
                AddFallback(terminal, extension);
                return;
            }

            var points = new List<Point>(path.Count + 1) { terminal.Position };
            points.AddRange(path);
            List<Point> compressed = CompressPath(points);
            for (int i = 0; i + 1 < compressed.Count; i++)
            {
                AddSegment(compressed[i], compressed[i + 1]);
                MarkTree(compressed[i], compressed[i + 1]);
            }
        }

        private void AddFallback(WireTerminal terminal, Point extension)
        {
            Point target = new(
                Math.Clamp(extension.X, _treeMinX, _treeMaxX),
                Math.Clamp(extension.Y, _treeMinY, _treeMaxY));
            Point[] corners = [terminal.Position, extension, new Point(extension.X, target.Y), target];
            for (int i = 0; i + 1 < corners.Length; i++)
            {
                AddSegment(corners[i], corners[i + 1]);
                MarkTree(corners[i], corners[i + 1]);
            }
        }

        private List<Point>? FindPath(int start)
        {
            int nx = _xs.Length;
            int ny = _ys.Length;
            int nodeCount = nx * ny;
            int stateCount = nodeCount * 3;
            var g = new double[stateCount];
            Array.Fill(g, double.MaxValue);
            var cameFrom = new int[stateCount];
            Array.Fill(cameFrom, -1);
            var closed = new bool[stateCount];
            var queue = new PriorityQueue<int, double>();

            int startIy = start / nx;
            int startIx = start % nx;
            g[start * 3] = 0;
            queue.Enqueue(start * 3, Heuristic(startIx, startIy));

            int goalState = -1;
            int expanded = 0;
            while (queue.TryDequeue(out int state, out _))
            {
                if (closed[state])
                {
                    continue;
                }

                closed[state] = true;
                int node = state / 3;
                int direction = state % 3;
                int ix = node % nx;
                int iy = node / nx;

                if (_treeNodes.Contains(node) && node != start)
                {
                    goalState = state;
                    break;
                }

                if (++expanded > 250_000)
                {
                    break;
                }

                TryNeighbor(ix - 1, iy, node, state, direction, 1, g, cameFrom, queue, start);
                TryNeighbor(ix + 1, iy, node, state, direction, 1, g, cameFrom, queue, start);
                TryNeighbor(ix, iy - 1, node, state, direction, 2, g, cameFrom, queue, start);
                TryNeighbor(ix, iy + 1, node, state, direction, 2, g, cameFrom, queue, start);
            }

            if (goalState < 0)
            {
                return null;
            }

            var path = new List<Point>();
            int cursor = goalState;
            while (cursor >= 0)
            {
                int node = cursor / 3;
                path.Add(new Point(_xs[node % nx], _ys[node / nx]));
                if (node == start)
                {
                    break;
                }

                cursor = cameFrom[cursor];
            }

            path.Reverse();
            return path;
        }

        private void TryNeighbor(
            int ix,
            int iy,
            int node,
            int state,
            int direction,
            int newDirection,
            double[] g,
            int[] cameFrom,
            PriorityQueue<int, double> queue,
            int start)
        {
            int nx = _xs.Length;
            int ny = _ys.Length;
            if (ix < 0 || iy < 0 || ix >= nx || iy >= ny)
            {
                return;
            }

            int neighbor = (iy * nx) + ix;
            if (Blocked(ix, iy))
            {
                return;
            }

            Point from = new(_xs[node % nx], _ys[node / nx]);
            Point to = new(_xs[ix], _ys[iy]);
            if (EdgeBlocked(from, to))
            {
                return;
            }

            double step = Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y);
            double turn = direction != 0 && direction != newDirection ? 7 : 0;
            double wirePenalty = 0;
            foreach ((Point a, Point b) in _otherWires)
            {
                if (Overlaps(from, to, a, b))
                {
                    wirePenalty += 35;
                }
                else if (Intersects(from, to, a, b, out _))
                {
                    wirePenalty += 1.5;
                }
            }

            int nextState = (neighbor * 3) + newDirection;
            double tentative = g[state] + step + turn + wirePenalty;
            if (tentative >= g[nextState] - 0.001)
            {
                return;
            }

            g[nextState] = tentative;
            cameFrom[nextState] = state;
            double priority = tentative + Heuristic(ix, iy);
            queue.Enqueue(nextState, priority);
        }

        private double Heuristic(int ix, int iy)
        {
            double x = _xs[ix];
            double y = _ys[iy];
            double dx = x < _treeMinX ? _treeMinX - x : x > _treeMaxX ? x - _treeMaxX : 0;
            double dy = y < _treeMinY ? _treeMinY - y : y > _treeMaxY ? y - _treeMaxY : 0;
            return dx + dy;
        }

        private bool Blocked(int ix, int iy)
        {
            Point point = new(_xs[ix], _ys[iy]);
            foreach (Obstacle obstacle in _obstacles)
            {
                const double epsilon = 0.2;
                Rect bounds = obstacle.Bounds;
                if (point.X > bounds.Left + epsilon &&
                    point.X < bounds.Right - epsilon &&
                    point.Y > bounds.Top + epsilon &&
                    point.Y < bounds.Bottom - epsilon)
                {
                    return true;
                }
            }

            return false;
        }

        private bool EdgeBlocked(Point from, Point to)
        {
            foreach (Obstacle obstacle in _obstacles)
            {
                if (CrossesInterior(obstacle.Bounds, from, to))
                {
                    return true;
                }
            }

            return false;
        }

        private int NodeIndex(Point point)
        {
            int ix = NearestIndex(_xs, point.X);
            int iy = NearestIndex(_ys, point.Y);
            if (ix < 0 || iy < 0)
            {
                return -1;
            }

            Point snapped = new(_xs[ix], _ys[iy]);
            return Near(snapped, point) ? (iy * _xs.Length) + ix : -1;
        }

        private void MarkTree(Point from, Point to)
        {
            int nx = _xs.Length;
            int ix1 = NearestIndex(_xs, from.X);
            int iy1 = NearestIndex(_ys, from.Y);
            int ix2 = NearestIndex(_xs, to.X);
            int iy2 = NearestIndex(_ys, to.Y);
            if (ix1 < 0 || iy1 < 0 || ix2 < 0 || iy2 < 0)
            {
                return;
            }

            if (ix1 == ix2)
            {
                for (int iy = Math.Min(iy1, iy2); iy <= Math.Max(iy1, iy2); iy++)
                {
                    _treeNodes.Add((iy * nx) + ix1);
                }
            }
            else if (iy1 == iy2)
            {
                for (int ix = Math.Min(ix1, ix2); ix <= Math.Max(ix1, ix2); ix++)
                {
                    _treeNodes.Add((iy1 * nx) + ix);
                }
            }
            else
            {
                _treeNodes.Add((iy1 * nx) + ix1);
                _treeNodes.Add((iy2 * nx) + ix2);
            }

            foreach (Point point in new[] { from, to })
            {
                _treeMinX = Math.Min(_treeMinX, point.X);
                _treeMinY = Math.Min(_treeMinY, point.Y);
                _treeMaxX = Math.Max(_treeMaxX, point.X);
                _treeMaxY = Math.Max(_treeMaxY, point.Y);
            }
        }

        private void AddSegment(Point from, Point to)
        {
            if (Near(from, to))
            {
                return;
            }

            if (_segments.Any(s => (Near(s.A, from) && Near(s.B, to)) || (Near(s.A, to) && Near(s.B, from))))
            {
                return;
            }

            _segments.Add((from, to));
            if (!_treePoints.Any(p => Near(p, from)))
            {
                _treePoints.Add(from);
            }

            if (!_treePoints.Any(p => Near(p, to)))
            {
                _treePoints.Add(to);
            }
        }

        private double[] BuildAxis(
            IEnumerable<double> extras,
            IEnumerable<double> lows,
            IEnumerable<double> highs)
        {
            var values = new SortedSet<double>();
            double min = double.MaxValue;
            double max = double.MinValue;
            foreach (double low in lows)
            {
                values.Add(low - Channel);
                values.Add(low - 1);
                min = Math.Min(min, low);
            }

            foreach (double high in highs)
            {
                values.Add(high + Channel);
                values.Add(high + 1);
                max = Math.Max(max, high);
            }

            foreach (double extra in extras)
            {
                values.Add(extra);
                min = Math.Min(min, extra);
                max = Math.Max(max, extra);
            }

            if (min == double.MaxValue || max == double.MinValue)
            {
                min = 0;
                max = 100;
            }

            values.Add(min - 36);
            values.Add(max + 36);

            var list = values.Where(double.IsFinite).ToList();
            // Add one middle lane in wide gaps so the search has a natural channel.
            for (int i = list.Count - 1; i > 0; i--)
            {
                double gap = list[i] - list[i - 1];
                if (gap > 26)
                {
                    list.Insert(i, list[i - 1] + (gap / 2));
                }
            }

            if (list.Count > MaxAxisCoordinates)
            {
                int step = (int)Math.Ceiling(list.Count / (double)MaxAxisCoordinates);
                list = list.Where((_, index) => index % step == 0 || index == list.Count - 1).ToList();
            }

            return list.ToArray();
        }

        private static List<Point> CompressPath(List<Point> points)
        {
            if (points.Count <= 2)
            {
                return points;
            }

            var result = new List<Point> { points[0] };
            for (int i = 1; i + 1 < points.Count; i++)
            {
                Point previous = result[^1];
                Point current = points[i];
                Point next = points[i + 1];
                bool sameX = Math.Abs(previous.X - current.X) < 0.01 && Math.Abs(current.X - next.X) < 0.01;
                bool sameY = Math.Abs(previous.Y - current.Y) < 0.01 && Math.Abs(current.Y - next.Y) < 0.01;
                if (!sameX && !sameY)
                {
                    result.Add(current);
                }
            }

            result.Add(points[^1]);
            return result;
        }

        private static List<(Point A, Point B)> CompressSegments(IReadOnlyList<(Point A, Point B)> segments)
        {
            var result = new List<(Point A, Point B)>(segments.Count);
            foreach ((Point a, Point b) in segments)
            {
                if (result.Count > 0 && Near(result[^1].B, a))
                {
                    (Point previousA, Point previousB) = result[^1];
                    bool previousHorizontal = Math.Abs(previousA.Y - previousB.Y) < 0.01;
                    bool currentHorizontal = Math.Abs(a.Y - b.Y) < 0.01;
                    if (previousHorizontal == currentHorizontal)
                    {
                        result[^1] = (previousA, b);
                        continue;
                    }
                }

                result.Add((a, b));
            }

            return result;
        }

        private static IReadOnlyList<Point> FindJunctions(IReadOnlyList<(Point A, Point B)> segments)
        {
            var candidates = new List<Point>();
            foreach ((Point a, Point b) in segments)
            {
                if (!candidates.Any(p => Near(p, a)))
                {
                    candidates.Add(a);
                }

                if (!candidates.Any(p => Near(p, b)))
                {
                    candidates.Add(b);
                }
            }

            var junctions = new List<Point>();
            foreach (Point point in candidates)
            {
                int degree = 0;
                foreach ((Point a, Point b) in segments)
                {
                    if (Near(point, a) || Near(point, b))
                    {
                        degree++;
                    }
                    else if (OnSegment(point, a, b))
                    {
                        degree += 2;
                    }
                }

                if (degree >= 3)
                {
                    junctions.Add(point);
                }
            }

            return junctions;
        }

        private static bool OnSegment(Point point, Point a, Point b)
        {
            return Math.Abs(a.Y - b.Y) < 0.01
                ? Math.Abs(point.Y - a.Y) < 0.01 && point.X > Math.Min(a.X, b.X) + 0.01 && point.X < Math.Max(a.X, b.X) - 0.01
                : Math.Abs(point.X - a.X) < 0.01 && point.Y > Math.Min(a.Y, b.Y) + 0.01 && point.Y < Math.Max(a.Y, b.Y) - 0.01;
        }
    }

    private static int NearestIndex(double[] values, double target)
    {
        int index = Array.BinarySearch(values, target);
        if (index >= 0)
        {
            return index;
        }

        index = ~index;
        if (index == 0)
        {
            return Math.Abs(values[0] - target) < 0.05 ? 0 : -1;
        }

        if (index >= values.Length)
        {
            return Math.Abs(values[^1] - target) < 0.05 ? values.Length - 1 : -1;
        }

        int before = index - 1;
        double best = Math.Abs(values[before] - target) <= Math.Abs(values[index] - target) ? values[before] : values[index];
        if (Math.Abs(best - target) >= 0.05)
        {
            return -1;
        }

        return Math.Abs(values[before] - target) <= Math.Abs(values[index] - target) ? before : index;
    }

    private static double Distance(Point a, Point b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static bool Near(Point a, Point b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;
}

internal sealed class PathBuilder
{
    private readonly List<Point> _points = [];
    private readonly List<(Point A, Point B)> _segments = [];
    private readonly List<Point> _junctions = [];

    public IReadOnlyList<Point> Points => _points;

    public IReadOnlyList<(Point A, Point B)> Segments => _segments;

    public IReadOnlyList<Point> Junctions => _junctions;

    public Point Last => _points.Count == 0 ? default : _points[^1];

    public void Add(Point point)
    {
        if (_points.Count == 0)
        {
            _points.Add(point);
            return;
        }

        Point last = _points[^1];
        if (Near(last, point))
        {
            return;
        }

        AddSegment(last, point);
    }

    public void AddSegment(Point from, Point to)
    {
        if (Near(from, to))
        {
            return;
        }

        if (_points.Count == 0 || !Near(_points[^1], from))
        {
            _points.Add(from);
        }

        _points.Add(to);
        _segments.Add((from, to));
    }

    public void AddJunction(Point point) => _junctions.Add(point);

    public void ConnectTo(Point target, bool horizontalFirst)
    {
        Point current = Last;
        if (Near(current, target))
        {
            return;
        }

        Point bend = horizontalFirst
            ? new Point(target.X, current.Y)
            : new Point(current.X, target.Y);
        Add(bend);
        Add(target);
    }

    private static bool Near(Point a, Point b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;
}

internal readonly record struct WireTerminal(
    Point Position,
    int ExtendDirection,
    bool IsPort,
    PinDirection Direction);
