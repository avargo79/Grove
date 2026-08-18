namespace GitFork.Core.Graph;

/// <summary>How a line segment crosses one row of the graph.</summary>
public enum GraphEdgeKind
{
    /// <summary>Passes straight through the row without touching the commit dot.</summary>
    Through,
    /// <summary>Comes down from the row above and terminates at this row's dot (a child linking to us).</summary>
    Incoming,
    /// <summary>Leaves this row's dot heading down toward a parent.</summary>
    Outgoing,
}

public sealed record GraphEdge(int FromLane, int ToLane, int ColorIndex, GraphEdgeKind Kind);

/// <summary>Layout for a single commit row: where its dot sits and which lines cross the row.</summary>
public sealed class GraphRow
{
    public required int Lane { get; init; }
    public required int ColorIndex { get; init; }
    public required bool IsMerge { get; init; }
    public required IReadOnlyList<GraphEdge> Edges { get; init; }
    /// <summary>Number of lane slots in use on this row, used to size the graph column.</summary>
    public required int LaneCount { get; init; }
}

/// <summary>
/// Assigns commits to vertical lanes and produces the line segments to draw between them.
/// Input must be in topological order (parents after children), i.e. <c>git log --topo-order</c>.
/// Lane indices are stable: a lane never shifts sideways once allocated, so lines stay straight
/// and only branch/merge points curve.
/// </summary>
public static class CommitGraphBuilder
{
    public const int ColorCount = 8;

    private sealed class Lane
    {
        public required string ExpectedSha { get; set; }
        public required int ColorIndex { get; init; }
    }

    public static IReadOnlyList<GraphRow> Build(IReadOnlyList<Commit> commits)
    {
        var rows = new List<GraphRow>(commits.Count);
        var lanes = new List<Lane?>();
        var nextColor = 0;

        foreach (var commit in commits)
        {
            var edges = new List<GraphEdge>();
            var laneCountBefore = OccupiedWidth(lanes);

            // Parent lanes are deduplicated below, so at most one lane can be waiting on us.
            var waiting = IndexOfLaneExpecting(lanes, commit.Sha);

            int nodeLane, nodeColor;
            if (waiting >= 0)
            {
                // A child reserved this lane for us; take it over and close the reservation.
                nodeLane = waiting;
                nodeColor = lanes[waiting]!.ColorIndex;
                edges.Add(new GraphEdge(waiting, nodeLane, nodeColor, GraphEdgeKind.Incoming));
                lanes[waiting] = null;
            }
            else
            {
                // No child in view: a branch tip starting a fresh lane.
                nodeLane = FindFreeLane(lanes);
                nodeColor = nextColor++ % ColorCount;
            }

            // Straight pass-throughs: lanes untouched by this commit.
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] is { } lane && i != nodeLane)
                    edges.Add(new GraphEdge(i, i, lane.ColorIndex, GraphEdgeKind.Through));

            // Reserve parent lanes. The first parent inherits the dot's lane and colour so
            // mainline history renders as one unbroken vertical line.
            for (var p = 0; p < commit.ParentShas.Count; p++)
            {
                var parentSha = commit.ParentShas[p];
                var parentLane = IndexOfLaneExpecting(lanes, parentSha);

                if (parentLane < 0)
                {
                    if (p == 0)
                    {
                        parentLane = nodeLane;
                        SetLane(lanes, parentLane, new Lane { ExpectedSha = parentSha, ColorIndex = nodeColor });
                    }
                    else
                    {
                        parentLane = FindFreeLane(lanes);
                        SetLane(lanes, parentLane, new Lane { ExpectedSha = parentSha, ColorIndex = nextColor++ % ColorCount });
                    }
                }

                edges.Add(new GraphEdge(nodeLane, parentLane, lanes[parentLane]!.ColorIndex, GraphEdgeKind.Outgoing));
            }

            TrimTrailingNulls(lanes);

            rows.Add(new GraphRow
            {
                Lane = nodeLane,
                ColorIndex = nodeColor,
                IsMerge = commit.IsMerge,
                Edges = edges,
                LaneCount = Math.Max(Math.Max(laneCountBefore, OccupiedWidth(lanes)), nodeLane + 1),
            });
        }

        return rows;
    }

    private static int IndexOfLaneExpecting(List<Lane?> lanes, string sha)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i]?.ExpectedSha == sha)
                return i;
        return -1;
    }

    private static int FindFreeLane(List<Lane?> lanes)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i] is null)
                return i;
        return lanes.Count;
    }

    private static void SetLane(List<Lane?> lanes, int index, Lane lane)
    {
        while (lanes.Count <= index)
            lanes.Add(null);
        lanes[index] = lane;
    }

    private static void TrimTrailingNulls(List<Lane?> lanes)
    {
        while (lanes.Count > 0 && lanes[^1] is null)
            lanes.RemoveAt(lanes.Count - 1);
    }

    private static int OccupiedWidth(List<Lane?> lanes)
    {
        for (var i = lanes.Count - 1; i >= 0; i--)
            if (lanes[i] is not null)
                return i + 1;
        return 0;
    }
}
