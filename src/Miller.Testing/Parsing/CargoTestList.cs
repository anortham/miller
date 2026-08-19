using System.Text.RegularExpressions;

namespace Miller.Testing.Parsing;

/// <summary>
/// Parses the stdout of <c>cargo test … -- --list --format terse</c> (stable; only
/// <c>--format json</c> is nightly). Each enumerable libtest test is one <c>path::to::test: test</c>
/// line (benches end in <c>: benchmark</c>); the binary "Running …" header goes to stderr, so a
/// per-target invocation's stdout is attributable to exactly that target.
///
/// <para>A test-capable target whose <c>--list</c> yields ZERO parseable lines is un-enumerable
/// (a <c>harness = false</c> custom main prints its own output; verified against real cargo 1.96.0)
/// and is represented by a single whole-target aggregate case, preserving whole-surface coverage.</para>
/// </summary>
public static partial class CargoTestList
{
    /// <summary>The enumerated libtest test names, in listing order (empty when un-enumerable).</summary>
    public static IReadOnlyList<string> ParseTestNames(string? standardOutput)
    {
        if (string.IsNullOrEmpty(standardOutput))
            return [];

        var names = new List<string>();
        foreach (var raw in standardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var match = ListLine().Match(raw);
            if (match.Success)
                names.Add(match.Groups["name"].Value);
        }

        return names;
    }

    [GeneratedRegex(@"^(?<name>.+): (?:test|benchmark)$")]
    private static partial Regex ListLine();
}
