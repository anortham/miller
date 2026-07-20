using Miller.Tests.Indexing;

namespace Miller.Tests.Server;

/// <summary>
/// Artifact fixtures for the opt-in Type-2 (near-duplicate) arm, shared by the `metrics clones` tests and the
/// `miller report` rollup tests so both exercise the same bodies and therefore the same recorded group count.
/// </summary>
internal static class NearDuplicateFixtures
{
    public const string PairAId = "aa11223344556677889900aabbccnd01";
    public const string PairBId = "aa11223344556677889900aabbccnd02";
    public const string PairCId = "aa11223344556677889900aabbccnd03";
    public const string PairDId = "aa11223344556677889900aabbccnd04";

    private const string OriginalBody = """
        {
            var totalCount = 0;
            foreach (var order in orders)
            {
                if (order.Status == "open" && order.Amount > 10)
                {
                    totalCount = totalCount + order.Amount;
                }
                else
                {
                    logger.Warn("skipped order");
                }
            }
            return totalCount;
        }
        """;

    private const string RenamedBody = """
        {
            var runningSum = 0;
            foreach (var invoice in invoices)
            {
                if (invoice.State == "pending" && invoice.Value > 25)
                {
                    runningSum = runningSum + invoice.Value;
                }
                else
                {
                    tracer.Warn("ignored invoice");
                }
            }
            return runningSum;
        }
        """;

    private const string SecondPairBody = """
        {
            var cache = new Dictionary<string, int>();
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                cache[next.Key] = next.Weight * 3;
                if (cache.Count > 128)
                {
                    cache.Clear();
                }
            }
            return cache.Count;
        }
        """;

    private const string SecondPairRenamedBody = """
        {
            var lookup = new Dictionary<string, int>();
            while (pending.Count > 0)
            {
                var head = pending.Dequeue();
                lookup[head.Name] = head.Score * 7;
                if (lookup.Count > 256)
                {
                    lookup.Clear();
                }
            }
            return lookup.Count;
        }
        """;

    /// <summary>
    /// One (or, with <paramref name="secondPair"/>, two) Type-2 pair(s): bodies that differ only in identifier
    /// names and literal values, written to disk so the hash-verified body read succeeds.
    /// </summary>
    public static JulieDbFixture CreatePairs(bool secondPair = false, bool revisions = false)
    {
        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<JulieDbFixture.SymbolRow>();

        (string Path, string Name, string Id, string Body)[] members = secondPair
            ?
            [
                ("src/Orders.cs", "SumOrders", PairAId, OriginalBody),
                ("src/Invoices.cs", "SumInvoices", PairBId, RenamedBody),
                ("src/Queues.cs", "DrainQueue", PairCId, SecondPairBody),
                ("src/Pending.cs", "DrainPending", PairDId, SecondPairRenamedBody),
            ]
            :
            [
                ("src/Orders.cs", "SumOrders", PairAId, OriginalBody),
                ("src/Invoices.cs", "SumInvoices", PairBId, RenamedBody),
            ];

        foreach ((string path, string name, string id, string body) in members)
        {
            string header = $"class Holder\n{{\n    int {name}()\n";
            string text = header + body + "\n}\n";
            content[path] = text;
            rows.Add(new JulieDbFixture.SymbolRow(id, name, "method", "csharp", path, $"int {name}()", 3, null)
            {
                EndLine = 3 + body.Split('\n').Length,
                StartByte = 0,
                EndByte = text.Length,
                BodyStartByte = header.Length,
                BodyEndByte = header.Length + body.Length,
                BodyStartLine = 4,
                BodyEndLine = 3 + body.Split('\n').Length,
            });
        }

        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows,
            fileContent: content,
            revisions: revisions ? new[] { new JulieDbFixture.RevisionRow(1) } : null);
    }
}
