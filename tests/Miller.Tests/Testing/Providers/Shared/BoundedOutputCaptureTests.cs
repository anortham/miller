using System.Text;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

/// <summary>
/// The capture bound on a child's stdout/stderr. Before this bound the drain loop appended every chunk to an
/// uncapped <see cref="StringBuilder"/>, and the only limits on how much a child could write were the SILENCE
/// stall guard - which a chatty process never trips - and the 30-minute provider window. A test that logs at
/// 10MB/s therefore grew about 18GB of UTF-16 text inside the CT daemon and took it out.
/// </summary>
public sealed class BoundedOutputCaptureTests
{
    /// <summary>Positions are readable in the text, so head/tail retention is provable, not approximate.</summary>
    private static string Numbered(int characters)
    {
        var builder = new StringBuilder(characters);
        for (var index = 0; builder.Length < characters; index++)
            builder.Append(index.ToString("D9", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        return builder.ToString(0, characters);
    }

    private static BoundedOutputBuffer Filled(int cap, string text)
    {
        var buffer = new BoundedOutputBuffer(cap);
        buffer.Append(text.ToCharArray(), 0, text.Length);
        return buffer;
    }

    [Fact]
    public void Output_under_the_cap_is_returned_unchanged()
    {
        var text = Numbered(900);

        var buffer = Filled(1000, text);

        Assert.Equal(text, buffer.Snapshot());
        Assert.False(buffer.Truncated);
    }

    /// <summary>
    /// A run that fits stays byte-identical whatever the chunk boundaries were, because the drain loop hands
    /// the buffer whatever the pipe gave it and a re-assembly that differed would change every parsed run.
    /// </summary>
    [Fact]
    public void Output_under_the_cap_is_unchanged_across_many_appends()
    {
        var text = Numbered(900);
        var buffer = new BoundedOutputBuffer(1000);

        for (var offset = 0; offset < text.Length; offset += 7)
            buffer.Append(text.ToCharArray(), offset, Math.Min(7, text.Length - offset));

        Assert.Equal(text, buffer.Snapshot());
        Assert.False(buffer.Truncated);
    }

    [Fact]
    public void Output_over_the_cap_keeps_the_head_and_the_rolling_tail()
    {
        var text = Numbered(5000);

        var snapshot = Filled(1000, text).Snapshot();

        // The head carries the launch diagnostics every provider's FailureSummary reads first; the tail
        // carries the failure output and the summary lines a reader actually needs.
        Assert.StartsWith(text[..250], snapshot, StringComparison.Ordinal);
        Assert.EndsWith(text[^750..], snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_over_the_cap_names_the_elided_characters_once()
    {
        var text = Numbered(5000);

        var buffer = Filled(1000, text);
        var snapshot = buffer.Snapshot();

        Assert.True(buffer.Truncated);
        const string Marker = "\n[... 4000 characters elided ...]\n";
        Assert.Contains(Marker, snapshot, StringComparison.Ordinal);
        Assert.Equal(
            snapshot.Length - Marker.Length,
            snapshot.Replace(Marker, string.Empty, StringComparison.Ordinal).Length);
    }

    /// <summary>
    /// The whole point: the retained text is bounded by the cap however much the child wrote. Without this the
    /// buffer grows to whatever a chatty logger produces inside the provider window.
    /// </summary>
    [Fact]
    public void Output_far_over_the_cap_stays_bounded()
    {
        var text = Numbered(400_000);

        var snapshot = Filled(1000, text).Snapshot();

        Assert.True(snapshot.Length <= 1000 + 64, $"the retained text grew to {snapshot.Length} characters.");
    }

    [Fact]
    public void A_non_positive_cap_keeps_everything()
    {
        var text = Numbered(20_000);

        var buffer = Filled(0, text);

        Assert.Equal(text, buffer.Snapshot());
        Assert.False(buffer.Truncated);
    }

    /// <summary>
    /// A chatty-but-live child is still live. The stall clock is stamped BEFORE the append, so hitting the cap
    /// must not stop it: a run that keeps talking past the cap would otherwise be killed as wedged.
    /// </summary>
    [Fact]
    public async Task A_drain_past_the_cap_keeps_stamping_the_output_clock()
    {
        var text = Numbered(5000);
        var buffer = new BoundedOutputBuffer(1000);
        var stamps = 0;

        await TestProcessRunner.DrainAsync(new StringReader(text), buffer, () => stamps++);

        // The reader hands over 4096 characters and then the remaining 904, so the second stamp lands well
        // after the cap was reached.
        Assert.Equal(2, stamps);
        Assert.True(buffer.Truncated);
        Assert.EndsWith(text[^750..], buffer.Snapshot(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_drain_under_the_cap_returns_byte_identical_output()
    {
        var text = Numbered(3000);
        var buffer = new BoundedOutputBuffer(1_000_000);

        await TestProcessRunner.DrainAsync(new StringReader(text), buffer, () => { });

        Assert.Equal(text, buffer.Snapshot());
        Assert.False(buffer.Truncated);
    }

    [Fact]
    public void The_default_capture_cap_is_eight_million_characters_per_stream()
    {
        // Named here so a change to the default is a deliberate edit to a test, not a silent policy shift.
        Assert.Equal(8 * 1024 * 1024, new TestProcessRunnerOptions().MaxCapturedCharactersPerStream);
    }

    [Fact]
    public void A_complete_standard_output_is_handed_to_a_parser_unchanged()
    {
        var result = new TestProcessResult(0, "run output", string.Empty);

        Assert.Equal("run output", result.RequireCompleteStandardOutput("cargo test"));
        Assert.False(result.StandardOutputTruncated);
        Assert.False(result.StandardErrorTruncated);
    }

    /// <summary>
    /// Correctness beats memory. Both stdout result parsers tolerate lines they do not recognise - the xunit
    /// path skips an unparseable JSONL line, and the cargo path ignores any line that matches no pattern - so
    /// an elided middle would silently drop test cases and could turn a red run green. A truncated stream is
    /// therefore refused at the parser rather than parsed.
    /// </summary>
    [Fact]
    public void A_truncated_standard_output_is_refused_by_a_result_parser()
    {
        var result = new TestProcessResult(0, "partial", string.Empty, StandardOutputTruncated: true);

        var failure = Assert.Throws<ContinuousTestProviderException>(
            () => result.RequireCompleteStandardOutput("cargo test"));

        Assert.Contains("cargo test", failure.Message, StringComparison.Ordinal);
    }
}
