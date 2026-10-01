using Mermaider;
using Mermaider.Layout;
using Mermaider.Models;
using Mostlylucid.Dagre;

namespace WpfMarkdownViewer.Mermaid;

/// <summary>
/// A Mermaider layout provider that re-lays-out flowcharts with the dagre algorithm (Mostlylucid.Dagre) for
/// noticeably better node placement and edge routing than Mermaider's built-in layout. It reuses Mermaider's
/// own node sizing (via its internal default provider) and only overrides positions + edge geometry, and
/// falls back to the default layout for anything dagre can't handle (subgraphs, errors). Class/ER diagrams
/// delegate unchanged.
/// </summary>
public sealed class DagreLayoutProvider : IGraphLayoutProvider
{
    // Built once; null if Mermaider's internal default provider can't be reflected (then we leave Mermaider's own layout in place).
    public static readonly DagreLayoutProvider? Instance = TryCreate();

    private readonly IGraphLayoutProvider _default;

    private DagreLayoutProvider(IGraphLayoutProvider @default) => _default = @default;

    private static DagreLayoutProvider? TryCreate()
    {
        try
        {
            var type = typeof(MermaidRenderer).Assembly.GetType("Mermaider.Layout.DefaultLayoutProvider");
            if (type is null || Activator.CreateInstance(type, nonPublic: true) is not IGraphLayoutProvider def)
                return null;
            return new DagreLayoutProvider(def);
        }
        catch
        {
            return null;
        }
    }

    public PositionedClassDiagram LayoutClass(ClassDiagram diagram) => _default.LayoutClass(diagram);

    public PositionedErDiagram LayoutEr(ErDiagram diagram) => _default.LayoutEr(diagram);

    public PositionedGraph LayoutFlowchart(MermaidGraph graph, RenderOptions? options = null, StrictModeOptions? strict = null)
    {
        var basePg = _default.LayoutFlowchart(graph, options, strict);

        // Subgraphs (compound layout) and trivial graphs stay on Mermaider's layout.
        if (graph.Subgraphs.Count > 0 || basePg.Nodes.Count < 2)
            return basePg;

        try
        {
            return RelayoutWithDagre(graph, basePg);
        }
        catch
        {
            return basePg; // never worse than Mermaider's own layout
        }
    }

    private static PositionedGraph RelayoutWithDagre(MermaidGraph graph, PositionedGraph basePg)
    {
        var dg = new DagreInputGraph { VerticalLayout = graph.Direction is Direction.TD or Direction.TB or Direction.BT };

        // dagre's high-level API hardcodes node/rank separation; pad the boxes we hand it to open up the gaps
        // (more room for edge labels and arrowheads). We render at the real size, centered in the padded slot.
        const float padW = 30f, padH = 42f;

        var dn = new Dictionary<string, DagreInputNode>();
        foreach (var n in basePg.Nodes)
            dn[n.Id] = dg.AddNode(n.Id, (float)n.Width + padW, (float)n.Height + padH);

        // dagre 2.0.1 throws on cycles, so reverse back-edges ourselves (acyclify) and undo on output.
        var back = DetectBackEdges(basePg.Edges);
        var dEdges = new DagreInputEdge?[basePg.Edges.Count];
        for (int i = 0; i < basePg.Edges.Count; i++)
        {
            var e = basePg.Edges[i];
            if (e.Source == e.Target || !dn.ContainsKey(e.Source) || !dn.ContainsKey(e.Target))
                continue; // self-loop / unknown endpoint: keep base geometry
            dEdges[i] = back.Contains(i)
                ? dg.AddEdge(dn[e.Target], dn[e.Source], 1)
                : dg.AddEdge(dn[e.Source], dn[e.Target], 1);
        }

        dg.Layout(null);

        var nodes = basePg.Nodes
            .Select(n => n with { X = dn[n.Id].X + padW / 2, Y = dn[n.Id].Y + padH / 2 }) // center the real node in its padded slot
            .ToList();
        var geo = nodes.ToDictionary(n => n.Id, n => (Cx: n.X + n.Width / 2, Cy: n.Y + n.Height / 2, Hw: n.Width / 2, Hh: n.Height / 2, n.Shape));

        var edges = new List<PositionedEdge>(basePg.Edges.Count);
        for (int i = 0; i < basePg.Edges.Count; i++)
        {
            var e = basePg.Edges[i];
            if (dEdges[i] is not { } de || de.Points is not { Length: > 0 })
            {
                edges.Add(e); // unchanged base geometry
                continue;
            }
            var pts = de.Points.Select(p => new Point(p.X, p.Y)).ToList();
            if (back.Contains(i))
                pts.Reverse();

            // dagre anchors endpoints on the rectangular bounding box; pull them onto the actual node shape
            // (diamond/circle) so the line/arrow emanates from the node border, not from empty space beside it.
            if (geo.TryGetValue(e.Source, out var s))
                pts[0] = ClipFromCenter(s.Cx, s.Cy, s.Hw, s.Hh, s.Shape, pts.Count > 1 ? pts[1] : new Point(s.Cx, s.Cy));
            if (geo.TryGetValue(e.Target, out var t))
                pts[^1] = ClipFromCenter(t.Cx, t.Cy, t.Hw, t.Hh, t.Shape, pts.Count > 1 ? pts[^2] : new Point(t.Cx, t.Cy));

            Point? label = !string.IsNullOrEmpty(e.Label) ? MidpointByLength(pts) : null;
            edges.Add(e with { Points = pts, LabelPosition = label });
        }

        const double margin = 8;
        double maxX = nodes.Max(n => n.X + n.Width);
        double maxY = nodes.Max(n => n.Y + n.Height);
        foreach (var pe in edges)
            foreach (var p in pe.Points)
            {
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

        return basePg with { Width = maxX + margin, Height = maxY + margin, Nodes = nodes, Edges = edges };
    }

    /// <summary>Point on a node's shape border along the ray from its center toward <paramref name="toward"/>.</summary>
    private static Point ClipFromCenter(double cx, double cy, double hw, double hh, NodeShape shape, Point toward)
    {
        double dx = toward.X - cx, dy = toward.Y - cy;
        if (hw <= 0 || hh <= 0 || (dx == 0 && dy == 0))
            return new Point(cx, cy);

        double t = shape switch
        {
            NodeShape.Diamond => 1.0 / (Math.Abs(dx) / hw + Math.Abs(dy) / hh),
            NodeShape.Circle or NodeShape.DoubleCircle => 1.0 / Math.Sqrt(dx * dx / (hw * hw) + dy * dy / (hh * hh)),
            _ => 1.0 / Math.Max(Math.Abs(dx) / hw, Math.Abs(dy) / hh), // rectangle-ish
        };
        return new Point(cx + dx * t, cy + dy * t);
    }

    /// <summary>The point halfway along the polyline by arc length — the natural spot for an edge label (vs a corner control point).</summary>
    private static Point MidpointByLength(List<Point> pts)
    {
        if (pts.Count == 1)
            return pts[0];

        double total = 0;
        for (int i = 1; i < pts.Count; i++)
            total += Dist(pts[i - 1], pts[i]);

        double half = total / 2;
        double acc = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double seg = Dist(pts[i - 1], pts[i]);
            if (acc + seg >= half && seg > 0)
            {
                double t = (half - acc) / seg;
                return new Point(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t,
                                 pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t);
            }
            acc += seg;
        }
        return pts[pts.Count / 2];

        static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    /// <summary>DFS edge classification: an edge to a node currently on the recursion stack is a back-edge (part of a cycle).</summary>
    private static HashSet<int> DetectBackEdges(IReadOnlyList<PositionedEdge> edges)
    {
        var adj = new Dictionary<string, List<(string To, int Index)>>();
        void Ensure(string id) { if (!adj.ContainsKey(id)) adj[id] = new List<(string, int)>(); }
        for (int i = 0; i < edges.Count; i++)
        {
            Ensure(edges[i].Source);
            Ensure(edges[i].Target);
            if (edges[i].Source != edges[i].Target)
                adj[edges[i].Source].Add((edges[i].Target, i));
        }

        var color = new Dictionary<string, int>(); // 0 unseen, 1 on-stack, 2 done
        var back = new HashSet<int>();

        void Visit(string u)
        {
            color[u] = 1;
            foreach (var (v, idx) in adj[u])
            {
                int c = color.GetValueOrDefault(v);
                if (c == 1)
                    back.Add(idx);
                else if (c == 0)
                    Visit(v);
            }
            color[u] = 2;
        }

        foreach (var id in adj.Keys.ToList())
            if (color.GetValueOrDefault(id) == 0)
                Visit(id);

        return back;
    }
}
