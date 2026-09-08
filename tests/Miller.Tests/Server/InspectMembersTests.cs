using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class InspectMembersTests
{
    private static (InspectTool Tool, ISymbolLookupIndex Index, IndexedSymbol Parent, List<IndexedSymbol> Children) CreateClassWithMembers(int memberCount, bool longSignatures = false)
    {
        var parent = new IndexedSymbol(
            0, "p0000000000000000000000000000001", "MyService", "public class MyService", "class", "csharp", "src/MyService.cs", 1, 500, null, false);

        var children = new List<IndexedSymbol>(memberCount);
        for (int i = 0; i < memberCount; i++)
        {
            string vis = (i % 3) switch
            {
                0 => "public",
                1 => "protected",
                _ => "private",
            };
            string kind = (i % 2) == 0 ? "method" : "field";
            string sig = kind == "method" ? $"{vis} void DoWork{i}()" : $"{vis} int _field{i}";
            if (longSignatures)
                sig += new string('界', 200);
            children.Add(new IndexedSymbol(
                i + 1,
                $"m0000000000000000000000000000{i:D4}",
                kind == "method" ? $"DoWork{i}" : $"_field{i}",
                sig,
                kind,
                "csharp",
                "src/MyService.cs",
                10 + i * 2,
                11 + i * 2,
                parent.SymbolId,
                false,
                Visibility: vis));
        }

        var allSymbols = new List<IndexedSymbol> { parent };
        allSymbols.AddRange(children);

        var index = MillerRepositoryIndex.Build(allSymbols);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index,
                "symbols.db",
                "ws-members",
                "/workspace"));

        return (new InspectTool(provider), index, parent, children);
    }

    [Fact]
    public void Inspect_ViewMembers_ReturnsDirectChildrenWithoutBodiesOrRelations()
    {
        var (tool, _, parent, _) = CreateClassWithMembers(5);

        string output = tool.Inspect(parent.Name, view: "members");

        Assert.Contains("# MyService  (class)", output);
        Assert.Contains("members (5 of 5)", output);
        Assert.Contains("DoWork0", output);
        Assert.Contains("_field1", output);
        Assert.DoesNotContain("## body", output);
        Assert.DoesNotContain("## references", output);
        Assert.DoesNotContain("## callers", output);
    }

    [Fact]
    public void Inspect_ViewMembers_DefaultLimitIs40_MaxIs100()
    {
        var (tool, _, parent, _) = CreateClassWithMembers(116);

        // Default limit should be 40
        string defaultOutput = tool.Inspect(parent.Name, view: "members");
        Assert.Contains("members (40 of 116)", defaultOutput);
        Assert.Contains("… 76 more members", defaultOutput);
        Assert.Contains("continuation=", defaultOutput);

        // Requested 100 limit should return 100 rows
        string maxOutput = tool.Inspect(parent.Name, view: "members", limit: 100);
        Assert.Contains("members (100 of 116)", maxOutput);
        Assert.Contains("… 16 more members", maxOutput);

        // Requested 500 limit should be clamped to 100
        string clampedOutput = tool.Inspect(parent.Name, view: "members", limit: 500);
        Assert.Contains("members (100 of 116)", clampedOutput);
        Assert.Contains("… 16 more members", clampedOutput);
    }

    [Fact]
    public void Inspect_ViewMembers_PaginationContinuation()
    {
        var (tool, _, parent, _) = CreateClassWithMembers(50);

        string page1 = tool.Inspect(parent.Name, view: "members", limit: 20, format: "json");
        using var doc1 = JsonDocument.Parse(page1);
        Assert.Equal(20, doc1.RootElement.GetProperty("members_returned_count").GetInt32());
        Assert.Equal(50, doc1.RootElement.GetProperty("members_total_count").GetInt32());
        Assert.True(doc1.RootElement.GetProperty("members_truncated").GetBoolean());
        string continuation = doc1.RootElement.GetProperty("continuation").GetString()!;
        Assert.NotNull(continuation);

        string page2 = tool.Inspect(parent.Name, view: "members", limit: 20, continuation: continuation, format: "json");
        using var doc2 = JsonDocument.Parse(page2);
        Assert.Equal(20, doc2.RootElement.GetProperty("members_returned_count").GetInt32());
        Assert.Equal(20, doc2.RootElement.GetProperty("page_offset").GetInt32());
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("json")]
    public void Inspect_ViewMembers_LongSignaturesPageWithinBudgetWithoutLosingMembers(string format)
    {
        var (tool, _, parent, _) = CreateClassWithMembers(116, longSignatures: true);
        var names = new List<string>();
        string? continuation = null;
        do
        {
            string output = tool.Inspect(parent.Name, view: "members", limit: 100,
                format: format, continuation: continuation);
            Assert.DoesNotContain("output_metadata_too_large", output);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.InspectMcpMaxBytes);
            if (format == "json")
            {
                using var document = JsonDocument.Parse(output);
                names.AddRange(document.RootElement.GetProperty("members").EnumerateArray()
                    .Select(member => member.GetProperty("name").GetString()!));
                continuation = document.RootElement.GetProperty("continuation").GetString();
            }
            else
            {
                names.AddRange(System.Text.RegularExpressions.Regex.Matches(output, @"(?:DoWork|_field)\d+")
                    .Select(match => match.Value).Distinct());
                var token = System.Text.RegularExpressions.Regex.Match(output, "continuation=\"([^\"]+)\"");
                continuation = token.Success ? token.Groups[1].Value : null;
            }
        }
        while (continuation is not null);
        Assert.Equal(116, names.Count);
        Assert.Equal(116, names.Distinct().Count());
    }

    [Fact]
    public void Inspect_ViewMembers_ZeroChildren_ReturnsCleanMessage()
    {
        var (tool, _, parent, _) = CreateClassWithMembers(0);

        string output = tool.Inspect(parent.Name, view: "members");
        Assert.Contains("Symbol has no child members.", output);
    }

    [Fact]
    public void Inspect_ViewMembers_Json_OutputsMemberStructure()
    {
        var (tool, _, parent, _) = CreateClassWithMembers(3);

        string json = tool.Inspect(parent.Name, view: "members", format: "json");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("MyService", root.GetProperty("symbol").GetProperty("name").GetString());
        Assert.Equal(3, root.GetProperty("members").GetArrayLength());
        Assert.Equal(3, root.GetProperty("members_total_count").GetInt32());
        Assert.Equal(3, root.GetProperty("members_returned_count").GetInt32());
        Assert.Equal(0, root.GetProperty("members_omitted_count").GetInt32());
        Assert.False(root.GetProperty("members_truncated").GetBoolean());
        Assert.True(root.GetProperty("continuation").ValueKind == JsonValueKind.Null);
    }
}
