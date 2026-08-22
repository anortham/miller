using System.Text;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

public sealed class CtArgvChunkingTests
{
    private static readonly Func<IReadOnlyList<string>, int> Cost = CtArgvChunking.ArgvCost;

    private static IReadOnlyList<IReadOnlyList<string>> MethodUnits(int count, int nameLength = 100) =>
        Enumerable.Range(0, count)
            .Select(i => (IReadOnlyList<string>)["-method", Fqn(i, nameLength)])
            .ToArray();

    private static string Fqn(int i, int length)
    {
        string suffix = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "Miller.Tests.Some.Namespace.SomeTests.Method" + new string('x', Math.Max(1, length - suffix.Length - 44)) + suffix;
    }

    [Fact]
    public void An_empty_selection_produces_no_invocations()
    {
        Assert.Empty(CtArgvChunking.Chunk(Array.Empty<IReadOnlyList<string>>(), Cost));
    }

    [Fact]
    public void A_selection_under_both_bounds_stays_a_single_invocation()
    {
        var chunks = CtArgvChunking.Chunk(MethodUnits(5), Cost);
        Assert.Single(chunks);
        Assert.Equal(5, chunks[0].Count);
    }

    [Fact]
    public void The_unit_count_bound_splits_the_selection()
    {
        // Short names so the byte bound cannot be what splits this.
        var chunks = CtArgvChunking.Chunk(MethodUnits(250, nameLength: 46), Cost, maxUnits: 120, maxBytes: 1_000_000);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(120, chunks[0].Count);
        Assert.Equal(120, chunks[1].Count);
        Assert.Equal(10, chunks[2].Count);
    }

    [Fact]
    public void The_byte_bound_splits_the_selection_before_the_unit_bound_is_reached()
    {
        var chunks = CtArgvChunking.Chunk(MethodUnits(100), Cost, maxUnits: 1000, maxBytes: 1024);
        Assert.True(chunks.Count > 1, "a 100-unit selection of 100-char names must exceed a 1 KB bound");
        foreach (var chunk in chunks)
            Assert.True(chunk.Sum(Cost) <= 1024 || chunk.Count == 1);
    }

    [Fact]
    public void A_single_unit_larger_than_the_byte_bound_gets_its_own_invocation_and_is_never_dropped()
    {
        IReadOnlyList<string> huge = ["-method", new string('z', 9000)];
        var units = new List<IReadOnlyList<string>> { MethodUnits(1)[0], huge, MethodUnits(1)[0] };

        var chunks = CtArgvChunking.Chunk(units, Cost, maxUnits: 120, maxBytes: 1024);

        Assert.Equal(3, chunks.Count);
        Assert.Same(huge, Assert.Single(chunks[1]));
    }

    [Fact]
    public void Chunking_preserves_order_and_loses_nothing()
    {
        var units = MethodUnits(1000);
        var flattened = CtArgvChunking.Chunk(units, Cost).SelectMany(chunk => chunk).ToArray();
        Assert.Equal(units.Count, flattened.Length);
        Assert.Equal(units.Select(u => u[1]), flattened.Select(u => u[1]));
    }

    [Fact]
    public void Chunk_facts_count_every_unit_and_bound_the_name_manifest()
    {
        var units = MethodUnits(250, nameLength: 46);
        var chunks = CtArgvChunking.Chunk(units, Cost, maxUnits: 120, maxBytes: 1_000_000);

        ContinuousTestProviderChunkProgress progress = CtArgvChunking.Describe(
            chunks,
            static unit => unit[1],
            currentPart: 2);

        Assert.Equal(250, progress.RequestedUniqueUnitCount);
        Assert.Equal(3, progress.ChunkCount);
        Assert.Equal(2, progress.CurrentPart);
        Assert.Equal(120, progress.CurrentPartUnitCount);
        Assert.Equal(8, progress.NameSamples.Count);
        Assert.True(progress.NamesTruncated);
        Assert.Equal(250, chunks.Sum(static chunk => chunk.Count));
        Assert.Equal(250, chunks.SelectMany(static chunk => chunk).Select(static unit => unit[1]).Distinct().Count());
        Assert.False(string.IsNullOrWhiteSpace(progress.NameDigest));
    }

    [Fact]
    public void Empty_chunk_manifest_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CtArgvChunking.Describe(
            Array.Empty<IReadOnlyList<string>>(),
            static unit => unit,
            currentPart: 1));
    }

    [Fact]
    public void Chunk_name_digest_is_stable_and_bounded()
    {
        IReadOnlyList<IReadOnlyList<string>> chunks =
        [
            ["alpha"],
            ["beta"],
        ];

        ContinuousTestProviderChunkProgress progress = CtArgvChunking.Describe(
            chunks,
            static unit => unit,
            currentPart: 1);

        Assert.Equal(64, progress.NameDigest.Length);
        Assert.Equal("bbfb79e82216bd2db1ad2c507d44ddf80aeb12f64f9562056afe93aad43154d9", progress.NameDigest);
        Assert.Equal(progress.NameDigest, CtArgvChunking.Describe(chunks, static unit => unit, 2).NameDigest);
    }

    [Fact]
    public void No_invocation_is_empty()
    {
        foreach (var chunk in CtArgvChunking.Chunk(MethodUnits(361), Cost))
            Assert.NotEmpty(chunk);
    }

    [Fact]
    public void A_flag_is_never_split_from_its_value()
    {
        foreach (var chunk in CtArgvChunking.Chunk(MethodUnits(500), Cost))
        {
            foreach (IReadOnlyList<string> unit in chunk)
            {
                Assert.Equal(2, unit.Count);
                Assert.Equal("-method", unit[0]);
            }
        }
    }

    /// <summary>
    /// The bound that matters: Miller's own suite is ~6,000 xunit methods whose fully-qualified names
    /// average ~100 characters, which is a 644 KB command line unchunked - 20x the 32,767 Windows cap
    /// and 78x the 8,191 cap that applies when a runner is reached through a .cmd shim. Every chunk
    /// must fit the SMALLER of the two.
    /// </summary>
    [Fact]
    public void Every_invocation_fits_the_cmd_exe_command_line_cap_for_a_full_Miller_sized_suite()
    {
        const int CmdExeCap = 8191;
        var units = MethodUnits(6047);

        var chunks = CtArgvChunking.Chunk(units, Cost);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            // Joined the way a command line actually is, plus a generous fixed prefix for the
            // executable path, -noLogo/-noColor/-reporter json, -jUnit <path>, and trait exclusions.
            int joined = chunk.Sum(Cost) + 512;
            Assert.True(joined <= CmdExeCap, $"chunk of {chunk.Count} units joined to {joined} chars, over the {CmdExeCap} cap");
        }

        Assert.Equal(6047, chunks.Sum(chunk => chunk.Count));
    }
}
