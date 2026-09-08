using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class CandidateOutputTests
{
    [Fact]
    public void CandidateOutput_SameFileAmbiguity_SuggestsParentMember()
    {
        var parent = new IndexedSymbol(
            0, "p0000000000000000000000000000001", "OrderService", null, "class", "csharp", "src/Orders.cs", 10, 100, null, false);
        var child1 = new IndexedSymbol(
            1, "c0000000000000000000000000000001", "Process", "void Process(int id)", "method", "csharp", "src/Orders.cs", 20, 30, parent.SymbolId, false);
        var child2 = new IndexedSymbol(
            2, "c0000000000000000000000000000002", "OtherProcess", "void OtherProcess()", "method", "csharp", "src/Orders.cs", 40, 50, parent.SymbolId, false);

        var index = SymbolSearchProjection.Build([parent, child1, child2]);

        var examples = CandidateOutput.RerunExamples("Process", [child1], supportsScope: true, "inspect", index);
        Assert.Single(examples);
        Assert.Equal("inspect target=\"OrderService.Process\"", examples[0]);
    }

    [Fact]
    public void CandidateOutput_OverloadAmbiguity_RetainsOpaqueSymbolIds()
    {
        var parent = new IndexedSymbol(
            0, "p0000000000000000000000000000001", "OrderService", null, "class", "csharp", "src/Orders.cs", 10, 100, null, false);
        var overload1 = new IndexedSymbol(
            1, "c0000000000000000000000000000001", "Process", "void Process(int id)", "method", "csharp", "src/Orders.cs", 20, 30, parent.SymbolId, false);
        var overload2 = new IndexedSymbol(
            2, "c0000000000000000000000000000002", "Process", "void Process(string name)", "method", "csharp", "src/Orders.cs", 40, 50, parent.SymbolId, false);

        var index = SymbolSearchProjection.Build([parent, overload1, overload2]);

        var examples = CandidateOutput.RerunExamples("Process", [overload1, overload2], supportsScope: true, "inspect", index);
        Assert.Equal(2, examples.Count);
        Assert.Equal($"inspect target=\"{overload1.SymbolId}\"", examples[0]);
        Assert.Equal($"inspect target=\"{overload2.SymbolId}\"", examples[1]);
    }

    [Fact]
    public void CandidateOutput_MultipleFiles_SuggestsScope()
    {
        var sym1 = new IndexedSymbol(
            1, "s0000000000000000000000000000001", "Handler", null, "class", "csharp", "src/A/Handler.cs", 5, 20, null, false);
        var sym2 = new IndexedSymbol(
            2, "s0000000000000000000000000000002", "Handler", null, "class", "csharp", "src/B/Handler.cs", 5, 20, null, false);

        var examples = CandidateOutput.RerunExamples("Handler", [sym1, sym2], supportsScope: true, "inspect");
        Assert.Equal(2, examples.Count);
        Assert.Equal("inspect target=\"Handler\" scope=\"src/A/Handler.cs\"", examples[0]);
        Assert.Equal("inspect target=\"Handler\" scope=\"src/B/Handler.cs\"", examples[1]);
    }
}
