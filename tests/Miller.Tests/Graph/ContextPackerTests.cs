using Miller.Core.Graph;
using Xunit;

namespace Miller.Tests.Graph;

/// <summary>
/// The pure token-budget selector (M5 decision D6 packing step). Pins the greedy keep-scanning policy: walk the
/// pre-ordered candidates, take each whose cumulative cost stays within budget, and — crucially — KEEP scanning
/// past an item that would overflow so a later cheaper item can still be taken (maximising budget use without
/// reordering the agent-facing priority). Boundary cases: exact fit, zero/negative budget, empty input, and a
/// single item larger than the whole budget. Asserts on the selected id sequence, never just a count.
/// </summary>
public sealed class ContextPackerTests
{
    private static PackCandidate<string> C(string id, int cost) => new(id, cost);

    [Fact]
    public void Pack_AllFitWithinBudget_ReturnsAllInOrder()
    {
        var selected = ContextPacker.Pack([C("a", 10), C("b", 20), C("c", 30)], budget: 100);

        Assert.Equal(["a", "b", "c"], selected);
    }

    [Fact]
    public void Pack_ExactFit_IncludesTheBoundaryItem()
    {
        // 10 + 20 + 30 == 60 exactly; the third item lands on the boundary and must be included.
        var selected = ContextPacker.Pack([C("a", 10), C("b", 20), C("c", 30)], budget: 60);

        Assert.Equal(["a", "b", "c"], selected);
    }

    [Fact]
    public void Pack_OneItemOverflows_SkipsItButKeepsLaterCheaperItem()
    {
        // Budget 100. a(60) taken → 60 used. b(50) would overflow (110) → skipped, scanning continues.
        // c(30) fits (90) → taken. The expensive middle item is skipped without truncating the scan.
        var selected = ContextPacker.Pack([C("a", 60), C("b", 50), C("c", 30)], budget: 100);

        Assert.Equal(["a", "c"], selected);
    }

    [Fact]
    public void Pack_ZeroBudget_ReturnsEmpty()
    {
        var selected = ContextPacker.Pack([C("a", 1)], budget: 0);

        Assert.Empty(selected);
    }

    [Fact]
    public void Pack_NegativeBudget_ReturnsEmpty()
    {
        var selected = ContextPacker.Pack([C("a", 1)], budget: -5);

        Assert.Empty(selected);
    }

    [Fact]
    public void Pack_EmptyInput_ReturnsEmpty()
    {
        var selected = ContextPacker.Pack(Array.Empty<PackCandidate<string>>(), budget: 100);

        Assert.Empty(selected);
    }

    [Fact]
    public void Pack_SingleItemLargerThanBudget_ReturnsEmpty()
    {
        var selected = ContextPacker.Pack([C("big", 500)], budget: 100);

        Assert.Empty(selected);
    }

    [Fact]
    public void Pack_PreservesCandidateOrder_NotCostOrder()
    {
        // The packer must NOT reorder by cost: the caller's priority order is authoritative. Here the cheapest
        // item is last and everything fits, so it stays last.
        var selected = ContextPacker.Pack([C("hi", 40), C("mid", 30), C("lo", 5)], budget: 100);

        Assert.Equal(["hi", "mid", "lo"], selected);
    }

    [Fact]
    public void Pack_ZeroCostItems_AreAllTaken()
    {
        // A free item never overflows, even on a tight budget.
        var selected = ContextPacker.Pack([C("a", 0), C("b", 100), C("c", 0)], budget: 100);

        Assert.Equal(["a", "b", "c"], selected);
    }

    [Fact]
    public void Pack_CarriesOpaquePayloadThroughSelection()
    {
        // The payload type is opaque to the packer; an int payload survives selection in order.
        var selected = ContextPacker.Pack(
            [new PackCandidate<int>(1, 10), new PackCandidate<int>(2, 200), new PackCandidate<int>(3, 10)],
            budget: 50);

        Assert.Equal([1, 3], selected);
    }
}
