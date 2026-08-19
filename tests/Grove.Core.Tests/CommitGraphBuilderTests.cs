using Grove.Core;
using Grove.Core.Graph;

namespace Grove.Core.Tests;

public class CommitGraphBuilderTests
{
    /// <summary>Builds a commit with only the fields the layout algorithm reads.</summary>
    private static Commit C(string sha, params string[] parents) =>
        new(sha, parents, "Ada", "ada@example.com", DateTimeOffset.UnixEpoch,
            "Ada", DateTimeOffset.UnixEpoch, $"subject {sha}", []);

    private static GraphEdge[] Edges(GraphRow row, GraphEdgeKind kind) =>
        [.. row.Edges.Where(e => e.Kind == kind)];

    [Fact]
    public void EmptyHistoryProducesNoRows()
    {
        Assert.Empty(CommitGraphBuilder.Build([]));
    }

    [Fact]
    public void LinearHistoryStaysInOneLane()
    {
        var rows = CommitGraphBuilder.Build([C("a", "b"), C("b", "c"), C("c")]);

        Assert.All(rows, r => Assert.Equal(0, r.Lane));
        Assert.All(rows, r => Assert.Equal(1, r.LaneCount));
        // Every commit shares the first parent's lane, so nothing ever bends sideways.
        Assert.All(rows, r => Assert.All(r.Edges, e => Assert.Equal(e.FromLane, e.ToLane)));
    }

    [Fact]
    public void LinearHistoryLinksEachRowToTheNext()
    {
        var rows = CommitGraphBuilder.Build([C("a", "b"), C("b", "c"), C("c")]);

        Assert.Single(Edges(rows[0], GraphEdgeKind.Outgoing));
        Assert.Empty(Edges(rows[0], GraphEdgeKind.Incoming));

        Assert.Single(Edges(rows[1], GraphEdgeKind.Incoming));
        Assert.Single(Edges(rows[1], GraphEdgeKind.Outgoing));

        // The root has a child above it but no parent below.
        Assert.Single(Edges(rows[2], GraphEdgeKind.Incoming));
        Assert.Empty(Edges(rows[2], GraphEdgeKind.Outgoing));
    }

    [Fact]
    public void RootCommitWithNoChildrenOccupiesItsOwnLane()
    {
        var rows = CommitGraphBuilder.Build([C("root")]);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Lane);
        Assert.Equal(1, row.LaneCount);
        Assert.Empty(row.Edges);
    }

    [Fact]
    public void SecondBranchTipGetsItsOwnLaneAndColour()
    {
        // feature and main both sit on top of shared history at "b".
        var rows = CommitGraphBuilder.Build([C("feature", "b"), C("main", "b"), C("b", "c"), C("c")]);

        Assert.Equal(0, rows[0].Lane);
        Assert.Equal(1, rows[1].Lane);
        Assert.NotEqual(rows[0].ColorIndex, rows[1].ColorIndex);
        Assert.Equal(2, rows[1].LaneCount);
    }

    [Fact]
    public void SecondBranchTipCurvesBackIntoTheLaneAlreadyWaitingOnTheSharedParent()
    {
        var rows = CommitGraphBuilder.Build([C("feature", "b"), C("main", "b"), C("b", "c"), C("c")]);

        // "main" sits in lane 1 but its parent link bends back into lane 0, which already expects "b".
        var outgoing = Assert.Single(Edges(rows[1], GraphEdgeKind.Outgoing));
        Assert.Equal(1, outgoing.FromLane);
        Assert.Equal(0, outgoing.ToLane);

        // Only one lane may reserve a commit, so "b" is reached through lane 0 alone.
        var incoming = Assert.Single(Edges(rows[2], GraphEdgeKind.Incoming));
        Assert.Equal(0, incoming.FromLane);
        Assert.Equal(0, rows[2].Lane);
    }

    [Fact]
    public void ACommitIsNeverReservedByMoreThanOneLane()
    {
        // Three tips over shared history: each extra tip must bend into the single reserved lane.
        var rows = CommitGraphBuilder.Build(
            [C("t1", "shared"), C("t2", "shared"), C("t3", "shared"), C("shared")]);

        Assert.All(rows, r => Assert.True(Edges(r, GraphEdgeKind.Incoming).Length <= 1));
        Assert.All(rows.Take(3), r => Assert.Equal(0, Edges(r, GraphEdgeKind.Outgoing)[0].ToLane));
    }

    [Fact]
    public void PassThroughLanesAreDrawnStraightOnUnrelatedRows()
    {
        // "other" is an unrelated tip whose lane must keep flowing past the rows in between.
        var rows = CommitGraphBuilder.Build([C("a", "b"), C("other", "z"), C("b", "c"), C("c"), C("z")]);

        var through = Edges(rows[2], GraphEdgeKind.Through);
        Assert.Single(through);
        Assert.Equal(through[0].FromLane, through[0].ToLane);
        Assert.Equal(1, through[0].FromLane);
    }

    [Fact]
    public void MergeCommitIsFlaggedAndForksIntoTwoLanes()
    {
        var rows = CommitGraphBuilder.Build([C("merge", "main", "feature"), C("main", "base"), C("feature", "base"), C("base")]);

        Assert.True(rows[0].IsMerge);
        var outgoing = Edges(rows[0], GraphEdgeKind.Outgoing);
        Assert.Equal(2, outgoing.Length);

        // First parent inherits the merge's own lane; the second opens a new one.
        Assert.Equal(rows[0].Lane, outgoing[0].ToLane);
        Assert.NotEqual(rows[0].Lane, outgoing[1].ToLane);
    }

    [Fact]
    public void MergeSecondParentGetsADistinctColour()
    {
        var rows = CommitGraphBuilder.Build([C("merge", "main", "feature"), C("main", "base"), C("feature", "base"), C("base")]);

        var outgoing = Edges(rows[0], GraphEdgeKind.Outgoing);
        Assert.NotEqual(outgoing[0].ColorIndex, outgoing[1].ColorIndex);
    }

    [Fact]
    public void OctopusMergeOpensALaneForEveryParent()
    {
        var rows = CommitGraphBuilder.Build(
            [C("octopus", "p1", "p2", "p3"), C("p1"), C("p2"), C("p3")]);

        var outgoing = Edges(rows[0], GraphEdgeKind.Outgoing);
        Assert.Equal(3, outgoing.Length);
        Assert.Equal(3, outgoing.Select(e => e.ToLane).Distinct().Count());
        Assert.Equal(3, rows[0].LaneCount);
    }

    [Fact]
    public void LaneIsReusedAfterTheBranchOccupyingItEnds()
    {
        // "side" finishes at "sideRoot", freeing lane 1 for the later unrelated tip "late".
        var rows = CommitGraphBuilder.Build(
            [C("a", "b"), C("side", "sideRoot"), C("sideRoot"), C("late", "b"), C("b")]);

        Assert.Equal(1, rows[1].Lane);
        Assert.Equal(1, rows[2].Lane);
        Assert.Equal(1, rows[3].Lane); // lane 1 recycled once "sideRoot" closed it
    }

    [Fact]
    public void NodeLaneAlwaysFitsInsideTheReportedLaneCount()
    {
        var rows = CommitGraphBuilder.Build(
        [
            C("m", "a", "b"),
            C("a", "c"),
            C("b", "c"),
            C("x", "y"),
            C("c", "d"),
            C("y"),
            C("d"),
        ]);

        Assert.All(rows, r =>
        {
            Assert.InRange(r.Lane, 0, r.LaneCount - 1);
            Assert.All(r.Edges, e =>
            {
                Assert.InRange(e.FromLane, 0, r.LaneCount - 1);
                Assert.InRange(e.ToLane, 0, r.LaneCount - 1);
            });
        });
    }

    [Fact]
    public void ColourIndicesStayInsideThePalette()
    {
        // More simultaneous branch tips than palette entries must wrap rather than overflow.
        var commits = new List<Commit>();
        for (var i = 0; i < CommitGraphBuilder.ColorCount + 4; i++)
            commits.Add(C($"tip{i}", "shared"));
        commits.Add(C("shared"));

        var rows = CommitGraphBuilder.Build(commits);

        Assert.All(rows, r => Assert.InRange(r.ColorIndex, 0, CommitGraphBuilder.ColorCount - 1));
        Assert.All(rows, r => Assert.All(r.Edges,
            e => Assert.InRange(e.ColorIndex, 0, CommitGraphBuilder.ColorCount - 1)));
    }

    [Fact]
    public void TruncatedHistoryDoesNotThrowWhenParentsAreMissing()
    {
        // git log --max-count cuts the tail off, so parents can reference commits we never see.
        var rows = CommitGraphBuilder.Build([C("a", "missing-1"), C("b", "missing-2")]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Single(Edges(r, GraphEdgeKind.Outgoing)));
    }

    [Fact]
    public void FirstParentKeepsTheMergeLaneColourSoMainlineStaysContinuous()
    {
        var rows = CommitGraphBuilder.Build([C("merge", "main", "feature"), C("main", "base"), C("feature", "base"), C("base")]);

        var firstParentEdge = Edges(rows[0], GraphEdgeKind.Outgoing)[0];
        Assert.Equal(rows[0].ColorIndex, firstParentEdge.ColorIndex);
        Assert.Equal(rows[0].ColorIndex, rows[1].ColorIndex);
    }
}
