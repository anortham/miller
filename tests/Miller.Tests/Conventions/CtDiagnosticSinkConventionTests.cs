using Miller.Tests.Testing;
using Xunit;

namespace Miller.Tests.Conventions;

/// <summary>
/// Drift guard for CT's diagnostic sinks.
///
/// <para><see cref="Miller.Testing.ContinuousTestProviderFactory.CreateDefault"/> and the
/// <c>ContinuousTestCoordinator</c> constructor both take an optional <c>onDiagnostic</c> sink. Both
/// default to NULL, and null is silent. That default is right for a unit test, which has no workspace
/// root to write to, and wrong for every production call site: an uncontained provider, a build
/// generation directory the reap could not remove, and a workspace over its disk budget all read
/// exactly like a clean run when the sink is unwired.</para>
///
/// <para>Both sinks WERE unwired in production. <c>CtDaemonLog</c> was written, unit-tested, and
/// called from nowhere in <c>src/</c>. Nothing failed, no test went red, and the degradations the
/// sinks exist to report were discarded at the call site. A type-level test cannot catch that: the
/// seam is correct, only the caller is wrong. So this guard reads the production call sites.</para>
///
/// <para>The scan runs on COMMENT-STRIPPED source, so a doc-comment mention of <c>onDiagnostic</c>
/// cannot satisfy it. Each construction site is checked on its own, and the count is asserted, so
/// deleting the last call site cannot turn this guard vacuously green.</para>
/// </summary>
public sealed class CtDiagnosticSinkConventionTests
{
    private const string CoordinatorConstruction = "newContinuousTestCoordinator(";
    private const string FactoryConstruction = "ContinuousTestProviderFactory.CreateDefault(";
    private const string Sink = "onDiagnostic:";

    [Fact]
    public void Every_production_coordinator_construction_passes_a_diagnostic_sink() =>
        AssertEveryConstructionPassesTheSink(CoordinatorConstruction, expectedSites: 2);

    [Fact]
    public void Every_production_provider_factory_construction_passes_a_diagnostic_sink() =>
        AssertEveryConstructionPassesTheSink(FactoryConstruction, expectedSites: 2);

    private static void AssertEveryConstructionPassesTheSink(string construction, int expectedSites)
    {
        var unwired = new List<string>();
        var sites = 0;

        foreach (string path in ProductionSources())
        {
            string code = Collapse(StripComments(File.ReadAllText(path)));
            var searchFrom = 0;
            while (true)
            {
                int start = code.IndexOf(construction, searchFrom, StringComparison.Ordinal);
                if (start < 0)
                    break;

                sites++;
                searchFrom = start + construction.Length;
                if (!ArgumentList(code, start + construction.Length).Contains(Sink, StringComparison.Ordinal))
                    unwired.Add($"{Path.GetFileName(path)} (offset {start})");
            }
        }

        Assert.True(
            unwired.Count == 0,
            $"these production `{construction}` sites pass no `{Sink}` sink, so every degradation they "
            + $"report is discarded: {string.Join(", ", unwired)}. Pass "
            + "`onDiagnostic: message => CtDaemonLog.Write(root, message)` so CT reports through one channel.");

        // Non-vacuity: a guard that scans zero sites passes for the wrong reason.
        Assert.Equal(expectedSites, sites);
    }

    /// <summary>
    /// The argument list that opens at <paramref name="openIndex"/>, to its matching close paren. Nested
    /// parens are tracked so an inner call's arguments still count as part of this list - the factory is
    /// constructed INSIDE the coordinator's argument list at one site, and a scan that stopped at the first
    /// close paren would read only half of it.
    /// </summary>
    private static string ArgumentList(string code, int openIndex)
    {
        var depth = 1;
        for (int i = openIndex; i < code.Length; i++)
        {
            char c = code[i];
            if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
                return code[openIndex..i];
        }

        return code[openIndex..];
    }

    private static IEnumerable<string> ProductionSources()
    {
        string sourceRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "src");
        return Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string Collapse(string code) => string.Concat(code.Where(static c => !char.IsWhiteSpace(c)));

    private static string StripComments(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        int i = 0, n = source.Length;
        bool inBlock = false;
        while (i < n)
        {
            if (inBlock)
            {
                if (i + 1 < n && source[i] == '*' && source[i + 1] == '/') { inBlock = false; i += 2; }
                else i++;
                continue;
            }
            if (i + 1 < n && source[i] == '/' && source[i + 1] == '*') { inBlock = true; i += 2; continue; }
            if (i + 1 < n && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n') i++;
                continue;
            }
            sb.Append(source[i]);
            i++;
        }
        return sb.ToString();
    }
}
