using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Miller.Tests.Indexing.Resolution;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing.Reads;

[Trait("Category", "Scale")]
public sealed class QueryTimeResolutionDirectionBenchmarks
{
    [Fact]
    public void Family_store_reverse_read_preserves_inbound_evidence_without_forward_work()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/Direction.cs");
        string[] seeds = Populate(
            (id, name, kind, parent) => fixture.AddSymbol(1, id, name, kind, "src/Direction.cs", parentId: parent),
            (id, name, source, offset) => fixture.AddIdentifier(1, id, name, "src/Direction.cs", kind: "call", containingSymbolId: source, startByte: offset, endByte: offset + name.Length));
        using SqliteConnection connection = fixture.OpenRead();
        Measure("family", connection, seeds, () => new QueryTimeResolutionReader(
            RevisionFactCache.Load(connection, fixture.Visibility()), fixture.Visibility()));
    }

    [Fact]
    public void Legacy_artifact_reverse_read_preserves_inbound_evidence_without_forward_work()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("file", "src/Direction.cs");
        string[] seeds = Populate(
            (id, name, kind, parent) => fixture.AddSymbol("file", id, name, kind, "src/Direction.cs", parentId: parent),
            (id, name, source, offset) => fixture.AddIdentifier("file", id, name, "src/Direction.cs", kind: "call", containingSymbolId: source, startByte: offset, endByte: offset + name.Length));
        using SqliteConnection connection = fixture.OpenRead();
        Measure("legacy", connection, seeds, () => new QueryTimeResolutionReader(
            RevisionFactCache.LoadFromArtifact(connection), visibility: null));
    }

    private static string[] Populate(Action<string, string, string, string?> symbol,
        Action<string, string, string, long> identifier)
    {
        symbol("root", "Root", "class", null);
        symbol("external", "External", "function", "root");
        string[] seeds = Enumerable.Range(0, 128).Select(i => "seed-" + i).ToArray();
        for (int i = 0; i < seeds.Length; i++)
        {
            symbol(seeds[i], "Seed" + i, "function", "root");
            symbol("caller-" + i, "Caller" + i, "method", "root");
            identifier("out-" + i, "External", seeds[i], i * 100 + 1);
            for (int j = 0; j < 4; j++)
                identifier($"in-{i}-{j}", "Seed" + i, "caller-" + i, i * 100 + j * 10 + 10);
        }
        return seeds;
    }

    private static void Measure(string mode, SqliteConnection connection, string[] seeds,
        Func<QueryTimeResolutionReader> create)
    {
        var samples = new List<object>();
        string[]? expected = null;
        foreach (Direction direction in new[] { Direction.Both, Direction.Reverse })
        {
            for (int sample = 0; sample < 6; sample++)
            {
                QueryTimeResolutionReader reader = create();
                long started = Stopwatch.GetTimestamp();
                IReadOnlyList<FamilyGraphResolutionEdge> edges = reader.ReadResolutionEdges(connection, seeds, direction, null);
                double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                string[] inbound = edges.Where(edge => seeds.Contains(edge.ToId, StringComparer.Ordinal))
                    .Select(edge => $"{edge.FromId}|{edge.ToId}|{edge.Kind}|{edge.Confidence:R}|{edge.Source}")
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                Assert.NotEmpty(inbound);
                expected ??= inbound;
                Assert.Equal(expected, inbound);
                Assert.Equal(direction == Direction.Both ? 1 : 0, reader.Counters.ForwardPasses);
                Assert.Equal(1, reader.Counters.ReversePasses);
                if (sample > 0)
                    samples.Add(new { direction = direction.ToString(), sample, milliseconds,
                        reader.Counters.ForwardPasses, reader.Counters.ReversePasses,
                        reader.Counters.IdentifierDetailCommands, reader.Counters.IdentifierDetailRows,
                        inbound_edges = inbound.Length });
            }
        }
        TestContext.Current.TestOutputHelper!.WriteLine(JsonSerializer.Serialize(new
        {
            mode, seeds = seeds.Length, identifiers = seeds.Length * 5,
            baseline = "same resolver using the old Both direction; fixture and cache loading excluded",
            samples,
        }));
    }
}
