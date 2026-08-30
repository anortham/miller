using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Dashboard;
using Miller.Dashboard.Components;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Drift guard for the dashboard busy cue.
///
/// <para>htmx puts <c>.htmx-request</c> on the element that ISSUED the request, and every polled panel
/// issues its own poll, so an unscoped <c>.htmx-request</c> rule dims and freezes whole panels on a
/// timer nobody pressed. The Workspaces panel polls every 30 s and its fragment takes about 3 s, so an
/// unscoped rule made it half-opacity and click-proof for three seconds out of every thirty.</para>
///
/// <para>The cue must also cover <c>:disabled</c>, because <c>hx-disabled-elt</c> sets a real disabled
/// attribute, and both <c>:hover</c> forms, because the accent hover rule would otherwise repaint an
/// inert button. Design: <c>docs/plans/2026-08-29-dashboard-smoothness-design.md</c> section C3.</para>
/// </summary>
public sealed class DashboardStylesheetGuardTests
{
    private const string HtmxRequestClass = ".htmx-request";

    [Fact]
    public void EveryHtmxRequestRuleIsScopedToAControl()
    {
        string[] mentions = [.. StyleRules(ReadStylesheet())
            .SelectMany(static rule => Selectors(rule))
            .Where(static selector => selector.Contains(HtmxRequestClass, StringComparison.Ordinal))];

        string[] unscoped = [.. mentions.Where(static selector => !NamesAControl(selector))];

        Assert.True(
            unscoped.Length == 0,
            $"these rules apply to every requesting element: {string.Join(" | ", unscoped)}. "
            + "htmx marks the element that issued the request, and every polled panel issues its own "
            + "poll, so an unscoped .htmx-request rule dims and freezes whole panels on a timer. "
            + "Scope the rule to a control the reader presses.");

        Assert.NotEmpty(mentions);
    }

    [Fact]
    public void TheBusyCueCoversTheRequestAndDisabledStatesAndBothHoverForms()
    {
        List<StyleRule> cues = [.. StyleRules(ReadStylesheet())
            .Where(static rule => Selectors(rule).Contains(".refresh-button.htmx-request"))];

        Assert.True(
            cues.Count == 1,
            $"expected exactly one .refresh-button.htmx-request rule, found {cues.Count}");

        string[] selectors = Selectors(cues[0]);
        Assert.Contains(".refresh-button.htmx-request:hover", selectors);
        Assert.Contains(".refresh-button:disabled", selectors);
        Assert.Contains(".refresh-button:disabled:hover", selectors);

        Assert.Contains("background:", cues[0].Declarations, StringComparison.Ordinal);
        Assert.Contains("border-color:", cues[0].Declarations, StringComparison.Ordinal);
        Assert.DoesNotContain("--accent", cues[0].Declarations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOpenFolderAndTelemetryRefreshButtonsDisableThemselvesWhileTheirRequestIsInFlight()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "ready", null, 1, 1, 1, 100, 42, "2026-06-12T09:00:00Z", "fresh",
            Array.Empty<DashboardLanguageStat>(), Array.Empty<DashboardSymbolKindStat>());

        string detailHtml = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = facts,
        });
        string telemetryHtml = await RenderComponentAsync<TelemetryPanel>(new Dictionary<string, object?>
        {
            ["Telemetry"] = new DashboardTelemetrySummary("ws-a", [], 0, null, null, []),
            ["SelectedWorkspaceId"] = "ws-a",
        });

        Assert.Contains(
            "hx-disabled-elt=\"this\"",
            TagAround(detailHtml, "open-folder-button"),
            StringComparison.Ordinal);
        Assert.Contains(
            "hx-disabled-elt=\"this\"",
            TagAround(telemetryHtml, "hx-target=\"#telemetry-panel\""),
            StringComparison.Ordinal);
    }

    private readonly record struct StyleRule(string Prelude, string Declarations);

    private static string ReadStylesheet() =>
        File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src", "Miller.Dashboard", "wwwroot", "dashboard.css"));

    private static string[] Selectors(StyleRule rule) =>
        [.. rule.Prelude
            .Split(',')
            .Select(static part => part.Trim())
            .Where(static part => part.Length > 0)];

    /// <summary>
    /// True when the selector names something besides the requesting element itself. Pseudo-classes and
    /// their arguments are dropped first, so <c>.htmx-request:hover</c> and <c>.htmx-request:has(.foo)</c>
    /// both still read as unscoped.
    /// </summary>
    private static bool NamesAControl(string selector)
    {
        string remainder = selector.Replace(HtmxRequestClass, " ", StringComparison.Ordinal);
        remainder = Regex.Replace(remainder, @":{1,2}[a-zA-Z-]+(\([^)]*\))?", " ");
        return remainder.Any(static c => char.IsLetterOrDigit(c) || c is '.' or '#' or '[' or '_');
    }

    /// <summary>
    /// Every style rule in the sheet, paired with its declarations. At-rule preludes are skipped rather
    /// than consumed, so the rules nested inside <c>@media</c> are collected like any other.
    /// </summary>
    private static List<StyleRule> StyleRules(string css)
    {
        var rules = new List<StyleRule>();
        var prelude = new StringBuilder();
        int index = 0;

        while (index < css.Length)
        {
            char c = css[index];

            if (c == '/' && index + 1 < css.Length && css[index + 1] == '*')
            {
                int commentEnd = css.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = commentEnd < 0 ? css.Length : commentEnd + 2;
                continue;
            }

            if (c is '"' or '\'')
            {
                int quoteEnd = css.IndexOf(c, index + 1);
                if (quoteEnd < 0)
                    quoteEnd = css.Length - 1;
                prelude.Append(css, index, quoteEnd - index + 1);
                index = quoteEnd + 1;
                continue;
            }

            if (c == '{')
            {
                string selectors = prelude.ToString().Trim();
                prelude.Clear();
                index++;
                if (selectors.Length == 0 || selectors[0] == '@')
                    continue;

                int blockEnd = css.IndexOf('}', index);
                if (blockEnd < 0)
                    blockEnd = css.Length;
                rules.Add(new StyleRule(selectors, css[index..blockEnd]));
                index = blockEnd + 1;
                continue;
            }

            if (c is '}' or ';')
            {
                prelude.Clear();
                index++;
                continue;
            }

            prelude.Append(c);
            index++;
        }

        return rules;
    }

    private static string TagAround(string html, string marker)
    {
        int at = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the rendered markup does not contain '{marker}'");
        int open = html.LastIndexOf('<', at);
        int close = html.IndexOf('>', at);
        return html[open..(close + 1)];
    }

    private static async Task<string> RenderComponentAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The detail panel's remove form embeds <AntiforgeryToken/>; outside a real HTTP request the provider
        // is this fixed-token stub so the hidden input still renders (the value is never validated here).
        services.AddSingleton<Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider>(
            new FixedAntiforgeryStateProvider());
        IServiceProvider provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider,
            provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }

    private sealed class FixedAntiforgeryStateProvider :
        Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider
    {
        public override Microsoft.AspNetCore.Components.Forms.AntiforgeryRequestToken? GetAntiforgeryToken() =>
            new("render-test-token", "__RequestVerificationToken");
    }
}
