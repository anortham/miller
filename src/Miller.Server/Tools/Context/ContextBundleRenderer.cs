using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Graph;
using Miller.Core.Tokenization;
using Miller.Server.Telemetry;

namespace Miller.Server.Tools.Context;

internal static class ContextBundleRenderer
{
    internal static IReadOnlyList<Candidate> SelectOrdinary(
        IReadOnlyList<Candidate> candidates,
        int tokenBudget,
        CancellationToken cancellationToken)
    {
        var packCandidates = new List<PackCandidate<Candidate>>(candidates.Count);
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            packCandidates.Add(new PackCandidate<Candidate>(
                candidate,
                (int)TokenEstimator.Count(CompactCostLine(candidate)),
                AllocationTier: candidate.IsPivot ? 0 : 2));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ContextPacker.PackAllocated(packCandidates, tokenBudget);
    }

    internal static string RenderOrdinary(
        IReadOnlyList<Candidate> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query,
        int tokenBudget,
        bool json,
        out int selectedCount,
        CancellationToken cancellationToken)
    {
        Func<IReadOnlyList<Candidate>, string> renderer = json
            ? items => RenderJson(items, anchorDiagnostics, query, boundOptionalFields: false)
            : items => RenderCompact(items, anchorDiagnostics, query);
        Func<IReadOnlyList<Candidate>, string> boundedRenderer = json
            ? items => RenderJson(items, anchorDiagnostics, query, boundOptionalFields: true)
            : items => RenderCompact(items, anchorDiagnostics, query);
        return RenderWithinBudget(
            selected,
            tokenBudget,
            renderer,
            boundedRenderer,
            out selectedCount,
            cancellationToken);
    }

    internal static long EstimateReferenceTokens(
        IReadOnlyList<ReferenceContextItem> items,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query,
        bool json) =>
        TokenEstimator.Count(
            json
                ? RenderReferenceJson(items, anchorDiagnostics, query, boundOptionalFields: true)
                : RenderReferenceCompact(items, anchorDiagnostics, query));

    internal static IReadOnlyList<ReferenceContextItem> SelectReference(
        IReadOnlyList<ReferenceContextItem> items,
        int tokenBudget,
        CancellationToken cancellationToken)
    {
        var packCandidates = new List<PackCandidate<ReferenceContextItem>>(items.Count);
        foreach (ReferenceContextItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            packCandidates.Add(new PackCandidate<ReferenceContextItem>(
                item,
                (int)TokenEstimator.Count(ContextBundleBuilder.ReferenceCostLine(item)),
                AllocationTier: ContextBundleBuilder.ReferenceAllocationTier(item)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ContextPacker.PackAllocated(packCandidates, tokenBudget);
    }

    internal static string RenderReference(
        IReadOnlyList<ReferenceContextItem> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query,
        int tokenBudget,
        bool json,
        out int selectedCount,
        CancellationToken cancellationToken)
    {
        Func<IReadOnlyList<ReferenceContextItem>, string> renderer = json
            ? items => RenderReferenceJson(items, anchorDiagnostics, query, boundOptionalFields: false)
            : items => RenderReferenceCompact(items, anchorDiagnostics, query);
        Func<IReadOnlyList<ReferenceContextItem>, string> boundedRenderer = json
            ? items => RenderReferenceJson(items, anchorDiagnostics, query, boundOptionalFields: true)
            : items => RenderReferenceCompact(items, anchorDiagnostics, query);
        return RenderWithinBudget(
            selected,
            tokenBudget,
            renderer,
            boundedRenderer,
            out selectedCount,
            cancellationToken);
    }

    private static string RenderWithinBudget<T>(
        IReadOnlyList<T> initiallySelected,
        int tokenBudget,
        Func<IReadOnlyList<T>, string> renderer,
        Func<IReadOnlyList<T>, string> boundedRenderer,
        out int selectedCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<T> empty = Array.Empty<T>();
        string emptyOutput = renderer(empty);
        if (tokenBudget <= 0)
        {
            selectedCount = 0;
            return string.Empty;
        }
        int renderBudget = tokenBudget >= 512
            ? Math.Max(1, tokenBudget * 3 / 4)
            : tokenBudget;
        if (TokenEstimator.Count(emptyOutput) > renderBudget)
        {
            selectedCount = 0;
            return emptyOutput.StartsWith('{') && TokenEstimator.Count("{}") <= renderBudget
                ? "{}"
                : string.Empty;
        }

        string fullOutput = renderer(initiallySelected);
        if (TokenEstimator.Count(fullOutput) <= renderBudget)
        {
            selectedCount = initiallySelected.Count;
            return fullOutput;
        }

        T[] retained = initiallySelected.ToArray();
        int lowestCandidateCount = 1;
        int highestCandidateCount = retained.Length;
        int bestCount = 0;
        string bestOutput = emptyOutput;
        while (lowestCandidateCount <= highestCandidateCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int candidateCount = lowestCandidateCount + ((highestCandidateCount - lowestCandidateCount) / 2);
            var prefix = new ArraySegment<T>(retained, 0, candidateCount);
            string output = boundedRenderer(prefix);
            if (TokenEstimator.Count(output) <= renderBudget)
            {
                bestCount = candidateCount;
                bestOutput = output;
                lowestCandidateCount = candidateCount + 1;
            }
            else
            {
                highestCandidateCount = candidateCount - 1;
            }
        }

        selectedCount = bestCount;
        return bestOutput;
    }

    internal static string BoundFinalOutput(string output, int tokenBudget, bool json)
    {
        if (tokenBudget <= 0)
            return string.Empty;
        if (TokenEstimator.Count(output) <= tokenBudget)
            return output;
        if (json)
            return TokenEstimator.Count("{}") <= tokenBudget ? "{}" : string.Empty;

        int lineEnd = output.LastIndexOf('\n');
        while (lineEnd >= 0)
        {
            string prefix = output[..lineEnd];
            if (TokenEstimator.Count(prefix) <= tokenBudget)
                return prefix;
            lineEnd = output.LastIndexOf('\n', lineEnd - 1);
        }
        return TokenEstimator.Count("…") <= tokenBudget ? "…" : string.Empty;
    }

    internal static string RenderNoPivots(
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        int tokenBudget,
        bool json)
    {
        string output;
        if (!json)
        {
            var builder = new StringBuilder("No pivots — nothing to anchor on.");
            if (anchorDiagnostics.Count > 0)
                builder.Append('\n');
            AppendAnchorDiagnosticsCompact(builder, anchorDiagnostics);
            output = builder.ToString().TrimEnd('\n');
        }
        else
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteString("note", "no pivots — nothing to anchor on.");
                writer.WritePropertyName("bundle");
                writer.WriteStartArray();
                writer.WriteEndArray();
                WriteAnchorDiagnosticsJson(writer, anchorDiagnostics);
                WriteDispositionJson(
                    writer,
                    new ContextEvidenceDisposition("insufficient", "no_pivot_resolved"));
                writer.WriteEndObject();
            }
            output = Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        return BoundFinalOutput(output, tokenBudget, json);
    }

    private sealed record ContextNextAction(string Call, string Reason);

    private static string CompactCostLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
          .Append(s.FilePath).Append(':').Append(s.StartLine)
          .Append("  hop=").Append(c.Hop);
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, ToolRenderLimits.SignatureMaxLength));
        if (!string.IsNullOrEmpty(c.Body))
            sb.Append("  ").Append(c.Body);
        return sb.ToString();
    }

    private static string GroupedCandidateLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append("  :").Append(s.StartLine).Append(' ')
          .Append(s.Name).Append(' ')
          .Append(s.Kind);
        if (c.Hop > 0)
            sb.Append(" hop=").Append(c.Hop);
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, ToolRenderLimits.SignatureMaxLength));
        return sb.ToString();
    }

    private const int NextInspectCount = 3;

    private static string RenderCompact(
        IReadOnlyList<Candidate> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query)
    {
        if (selected.Count == 0)
        {
            var empty = new StringBuilder("No evidence fit token_budget.");
            if (anchorDiagnostics.Count == 0)
                return empty.ToString();
            empty.Append('\n');
            AppendAnchorDiagnosticsCompact(empty, anchorDiagnostics);
            ContextEvidenceDisposition emptyDisposition = ContextBundleBuilder.DispositionFor(selected);
            empty.Append("## disposition\n")
                .Append("evidence=")
                .Append(emptyDisposition.Status)
                .Append("  reason=")
                .Append(emptyDisposition.Reason);
            return empty.ToString();
        }

        var pivots = new List<Candidate>();
        var neighbours = new List<Candidate>();
        foreach (Candidate candidate in selected)
        {
            if (candidate.Hop == 0)
                pivots.Add(candidate);
            else
                neighbours.Add(candidate);
        }

        var sb = new StringBuilder();
        sb.Append("# context bundle (").Append(selected.Count).Append(")\n");

        AppendAnchorDiagnosticsCompact(sb, anchorDiagnostics);

        if (pivots.Count > 0)
        {
            sb.Append("## pivots\n");
            foreach (Candidate pivot in pivots)
                sb.Append(PivotLine(pivot)).Append('\n');
        }

        Candidate[] implementations = pivots
            .Where(static candidate => candidate.Body is not null)
            .ToArray();
        if (implementations.Length > 0)
        {
            sb.Append("## implementations\n");
            foreach (Candidate implementation in implementations)
            {
                sb.Append(implementation.Symbol.Name)
                    .Append("  ")
                    .Append(implementation.Symbol.FilePath)
                    .Append(':')
                    .Append(implementation.Symbol.StartLine)
                    .Append('\n');
                foreach (string line in implementation.Body!.Split('\n'))
                    sb.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
                if (implementation.BodyTruncated)
                    sb.Append("    … body truncated to fit allocation\n");
            }
        }

        if (neighbours.Count > 0)
        {
            sb.Append("## neighbours\n");
            var groups = new List<(string FilePath, List<Candidate> Candidates)>();
            for (int i = 0; i < neighbours.Count; i++)
            {
                Candidate candidate = neighbours[i];
                int groupIndex = groups.FindIndex(group => group.FilePath == candidate.Symbol.FilePath);
                if (groupIndex >= 0)
                    groups[groupIndex].Candidates.Add(candidate);
                else
                    groups.Add((candidate.Symbol.FilePath, new List<Candidate> { candidate }));
            }

            foreach (var group in groups)
            {
                sb.Append(group.FilePath).Append(':').Append('\n');
                foreach (Candidate candidate in group.Candidates)
                    sb.Append(GroupedCandidateLine(candidate)).Append('\n');
            }

        }

        ContextEvidenceDisposition disposition = ContextBundleBuilder.DispositionFor(selected);
        sb.Append("## disposition\n")
            .Append("evidence=")
            .Append(disposition.Status)
            .Append("  reason=")
            .Append(disposition.Reason)
            .Append('\n');

        ContextNextAction[] nextActions = BuildDiscoveryNextActions(pivots, disposition, query);
        if (nextActions.Length > 0)
        {
            sb.Append("## next inspect\n");
            foreach (ContextNextAction action in nextActions)
                sb.Append(action.Call).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string NextInspectLine(string name, string filePath) =>
        "inspect(target=\"" + EscapeCallString(name) +
        "\", scope=\"" + EscapeCallString(filePath) +
        "\", depth=\"overview\")";

    private static string NextSourceSearchLine(string query) =>
        "search(query=\"" + EscapeDiagnosticQuery(query) + "\", mode=\"source\")";

    private static ContextNextAction[] BuildReferenceDiscoveryNextActions(
        IReadOnlyList<ReferenceContextItem> selected,
        ContextEvidenceDisposition disposition,
        string query)
    {
        ReferenceContextItem[] pivots = selected
            .Where(static item => item.ItemType == "symbol" && item.Role == "pivot")
            .ToArray();
        if (disposition.Status == "sufficient" || pivots.Length == 0)
            return [];

        ReferenceContextItem[] implementationPivots = pivots
            .Where(static item => ContextBundleBuilder.CarriesImplementationKind(item.Kind))
            .ToArray();
        bool anyImplementation = implementationPivots.Length > 0;
        bool suggestSource =
            !string.IsNullOrWhiteSpace(query) &&
            (!anyImplementation ||
             disposition.Reason is "pivot_value_declaration_only" or "discovery_implementation_present"
                 or "symbol_and_relation_evidence_only");

        var actions = new List<ContextNextAction>(NextInspectCount + 1);
        if (!anyImplementation)
        {
            if (suggestSource)
            {
                actions.Add(new ContextNextAction(
                    NextSourceSearchLine(query),
                    "source or docs may hold conceptual language beyond value declarations"));
            }
            return actions.ToArray();
        }

        int inspectCount = Math.Min(NextInspectCount, implementationPivots.Length);
        for (int i = 0; i < inspectCount; i++)
        {
            ReferenceContextItem pivot = implementationPivots[i];
            actions.Add(new ContextNextAction(
                NextInspectLine(pivot.Name, pivot.File),
                "inspect a pivot implementation"));
        }

        if (suggestSource)
        {
            actions.Add(new ContextNextAction(
                NextSourceSearchLine(query),
                "source or docs may hold conceptual language beyond value declarations"));
        }

        return actions.ToArray();
    }

    private static ContextNextAction[] BuildDiscoveryNextActions(
        IReadOnlyList<Candidate> pivots,
        ContextEvidenceDisposition disposition,
        string query)
    {
        if (disposition.Status == "sufficient" || pivots.Count == 0)
            return [];

        Candidate[] implementationPivots = pivots
            .Where(static pivot => ContextBundleBuilder.CarriesImplementation(pivot.Symbol))
            .ToArray();
        bool anyImplementation = implementationPivots.Length > 0;
        bool suggestSource =
            !string.IsNullOrWhiteSpace(query) &&
            (!anyImplementation ||
             disposition.Reason is "pivot_value_declaration_only" or "discovery_implementation_present");

        var actions = new List<ContextNextAction>(NextInspectCount + 1);
        if (!anyImplementation)
        {
            if (suggestSource)
            {
                actions.Add(new ContextNextAction(
                    NextSourceSearchLine(query),
                    "source or docs may hold conceptual language beyond value declarations"));
            }
            return actions.ToArray();
        }

        int inspectCount = Math.Min(NextInspectCount, implementationPivots.Length);
        for (int i = 0; i < inspectCount; i++)
        {
            actions.Add(new ContextNextAction(
                NextInspectLine(
                    implementationPivots[i].Symbol.Name,
                    implementationPivots[i].Symbol.FilePath),
                "inspect a pivot implementation"));
        }

        if (suggestSource)
        {
            actions.Add(new ContextNextAction(
                NextSourceSearchLine(query),
                "source or docs may hold conceptual language beyond value declarations"));
        }

        return actions.ToArray();
    }

    private static string EscapeCallString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeDiagnosticQuery(string value)
    {
        return ToolDiagnosticText.EscapeCallArgument(value);
    }

    private static string PivotLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
          .Append(s.FilePath).Append(':').Append(s.StartLine).Append("  pivot");
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, ToolRenderLimits.SignatureMaxLength));
        if (c.AnchorLine is int anchorLine)
            sb.Append("  anchor_line=").Append(anchorLine);
        return sb.ToString();
    }

    private static string RenderJson(
        IReadOnlyList<Candidate> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query,
        bool boundOptionalFields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WritePropertyName("bundle");
            w.WriteStartArray();
            foreach (var c in selected)
            {
                var s = c.Symbol;
                w.WriteStartObject();
                w.WriteString("item_type", "symbol");
                w.WriteString("name", s.Name);
                w.WriteString("kind", s.Kind);
                w.WriteString("file", s.FilePath);
                w.WriteNumber("line", s.StartLine);
                w.WriteNumber("hop", c.Hop);
                w.WriteString("role", c.IsPivot ? "pivot" : "neighbour");
                w.WriteString("reason", c.Reason);
                w.WriteString("confidence", "exact");
                if (c.AnchorLine is int anchorLine)
                    w.WriteNumber("anchor_line", anchorLine);
                if (s.Signature is null) w.WriteNull("signature");
                else w.WriteString("signature", boundOptionalFields
                    ? Truncate(s.Signature, ToolRenderLimits.SignatureMaxLength)
                    : s.Signature);
                w.WriteString("symbol_id", s.SymbolId);
                if (c.Body is not null)
                {
                    w.WriteString("body", c.Body);
                    w.WriteBoolean("body_truncated", c.BodyTruncated);
                }
                else if (c.BodyUnavailableReason is not null)
                {
                    w.WriteString("body_unavailable_reason", c.BodyUnavailableReason);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            WriteAnchorDiagnosticsJson(w, anchorDiagnostics);
            ContextEvidenceDisposition disposition = ContextBundleBuilder.DispositionFor(selected);
            WriteDispositionJson(w, disposition);
            if (disposition.Status != "sufficient")
            {
                Candidate[] pivots = selected
                    .Where(static candidate => candidate.IsPivot)
                    .ToArray();
                ContextNextAction[] nextActions = BuildDiscoveryNextActions(pivots, disposition, query);
                if (nextActions.Length > 0)
                {
                    w.WritePropertyName("next_actions");
                    w.WriteStartArray();
                    foreach (ContextNextAction action in nextActions)
                    {
                        w.WriteStartObject();
                        w.WriteString("call", action.Call);
                        w.WriteString("reason", action.Reason);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string ReferenceCompactLine(ReferenceContextItem item)
    {
        var sb = new StringBuilder();
        sb.Append("  :").Append(item.Line).Append(' ')
          .Append(item.Name).Append(' ')
          .Append(item.Kind)
          .Append(" reason=").Append(item.Reason)
          .Append(" confidence=").Append(item.Confidence);
        if (item.Hop is not null)
            sb.Append(" hop=").Append(item.Hop.Value);
        if (!string.IsNullOrEmpty(item.Signature))
            sb.Append("  ").Append(Truncate(item.Signature!, ToolRenderLimits.SignatureMaxLength));
        else if (!string.IsNullOrEmpty(item.Snippet))
            sb.Append("  ").Append(Truncate(item.Snippet!, ToolRenderLimits.SignatureMaxLength));
        if (item.ResolutionStatus is not null)
            sb.Append(" resolution=").Append(item.ResolutionStatus);
        if (item.Provenance is not null)
            sb.Append(" source=").Append(item.Provenance);
        if (item.EvidenceConfidence is not null)
            sb.Append(" evidence_confidence=")
                .Append(item.EvidenceConfidence.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        if (item.AnchorReason is not null)
            sb.Append(" anchor=").Append(item.AnchorReason);
        if (item.Role is not null)
            sb.Append(" role=").Append(item.Role);
        return sb.ToString();
    }

    private static string RenderReferenceCompact(
        IReadOnlyList<ReferenceContextItem> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query)
    {
        if (selected.Count == 0)
        {
            var empty = new StringBuilder("No evidence fit token_budget.");
            if (anchorDiagnostics.Count == 0)
                return empty.ToString();
            empty.Append('\n');
            AppendAnchorDiagnosticsCompact(empty, anchorDiagnostics);
            ContextEvidenceDisposition emptyDisposition = ContextBundleBuilder.DispositionForReference(selected);
            empty.Append("## disposition\n")
                .Append("evidence=")
                .Append(emptyDisposition.Status)
                .Append("  reason=")
                .Append(emptyDisposition.Reason);
            return empty.ToString();
        }

        var sb = new StringBuilder();
        sb.Append("# context bundle (").Append(selected.Count).Append(")\n");
        var groups = new List<(string FilePath, List<ReferenceContextItem> Items)>();
        foreach (ReferenceContextItem item in selected)
        {
            int groupIndex = groups.FindIndex(group => group.FilePath == item.File);
            if (groupIndex >= 0)
                groups[groupIndex].Items.Add(item);
            else
                groups.Add((item.File, new List<ReferenceContextItem> { item }));
        }

        foreach (var group in groups)
        {
            sb.Append(group.FilePath).Append(':').Append('\n');
            foreach (ReferenceContextItem item in group.Items)
                sb.Append(ReferenceCompactLine(item)).Append('\n');
        }

        AppendAnchorDiagnosticsCompact(sb, anchorDiagnostics);
        ContextEvidenceDisposition disposition = ContextBundleBuilder.DispositionForReference(selected);
        sb.Append("## disposition\n")
            .Append("evidence=")
            .Append(disposition.Status)
            .Append("  reason=")
            .Append(disposition.Reason)
            .Append('\n');
        ContextNextAction[] nextActions = BuildReferenceDiscoveryNextActions(selected, disposition, query);
        if (nextActions.Length > 0)
        {
            sb.Append("## next inspect\n");
            foreach (ContextNextAction action in nextActions)
                sb.Append(action.Call).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderReferenceJson(
        IReadOnlyList<ReferenceContextItem> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query,
        bool boundOptionalFields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WritePropertyName("bundle");
            w.WriteStartArray();
            foreach (ReferenceContextItem item in selected)
            {
                w.WriteStartObject();
                w.WriteString("item_type", item.ItemType);
                w.WriteString("reason", item.Reason);
                w.WriteString("confidence", item.Confidence);
                w.WriteString("name", item.Name);
                w.WriteString("kind", item.Kind);
                w.WriteString("file", item.File);
                w.WriteNumber("line", item.Line);
                if (item.Hop is int hop)
                    w.WriteNumber("hop", hop);
                if (item.Signature is null) w.WriteNull("signature");
                else w.WriteString("signature", boundOptionalFields
                    ? Truncate(item.Signature, ToolRenderLimits.SignatureMaxLength)
                    : item.Signature);
                if (item.SymbolId is not null)
                    w.WriteString("symbol_id", item.SymbolId);
                if (item.ContainingSymbolId is not null)
                    w.WriteString("containing_symbol_id", item.ContainingSymbolId);
                if (item.TargetSymbolId is not null)
                    w.WriteString("target_symbol_id", item.TargetSymbolId);
                if (item.ResolutionStatus is not null)
                    w.WriteString("resolution_status", item.ResolutionStatus);
                if (item.Provenance is not null)
                    w.WriteString("provenance", item.Provenance);
                if (item.EvidenceConfidence is not null)
                    w.WriteNumber("evidence_confidence", item.EvidenceConfidence.Value);
                if (item.AnchorReason is not null)
                    w.WriteString("anchor_reason", item.AnchorReason);
                if (item.Role is not null)
                    w.WriteString("role", item.Role);
                if (item.SourceId is not null)
                    w.WriteString("source_id", item.SourceId);
                if (item.ChunkId is not null)
                    w.WriteString("chunk_id", item.ChunkId);
                if (item.LineStart is int lineStart)
                    w.WriteNumber("line_start", lineStart);
                if (item.LineEnd is int lineEnd)
                    w.WriteNumber("line_end", lineEnd);
                if (item.Snippet is not null)
                    w.WriteString("snippet", boundOptionalFields
                        ? Truncate(item.Snippet, ToolRenderLimits.SignatureMaxLength)
                        : item.Snippet);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            WriteAnchorDiagnosticsJson(w, anchorDiagnostics);
            ContextEvidenceDisposition disposition = ContextBundleBuilder.DispositionForReference(selected);
            WriteDispositionJson(w, disposition);
            if (disposition.Status != "sufficient")
            {
                ContextNextAction[] nextActions =
                    BuildReferenceDiscoveryNextActions(selected, disposition, query);
                if (nextActions.Length > 0)
                {
                    w.WritePropertyName("next_actions");
                    w.WriteStartArray();
                    foreach (ContextNextAction action in nextActions)
                    {
                        w.WriteStartObject();
                        w.WriteString("call", action.Call);
                        w.WriteString("reason", action.Reason);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void AppendAnchorDiagnosticsCompact(
        StringBuilder builder,
        IReadOnlyList<ContextAnchorDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;

        builder.Append("## anchor diagnostics\n");
        foreach (ContextAnchorDiagnostic diagnostic in diagnostics)
        {
            string value = Truncate(
                diagnostic.Value.Replace('\r', ' ').Replace('\n', ' '),
                ToolRenderLimits.SignatureMaxLength);
            builder.Append(diagnostic.Kind)
                .Append("  ")
                .Append(value)
                .Append("  reason=")
                .Append(diagnostic.Reason)
                .Append('\n');
        }
    }

    private static void WriteAnchorDiagnosticsJson(
        Utf8JsonWriter writer,
        IReadOnlyList<ContextAnchorDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;

        writer.WritePropertyName("anchor_diagnostics");
        writer.WriteStartArray();
        foreach (ContextAnchorDiagnostic diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", diagnostic.Kind);
            writer.WriteString("value", Truncate(diagnostic.Value, ToolRenderLimits.SignatureMaxLength));
            writer.WriteString("reason", diagnostic.Reason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteDispositionJson(
        Utf8JsonWriter writer,
        ContextEvidenceDisposition disposition)
    {
        writer.WritePropertyName("disposition");
        writer.WriteStartObject();
        writer.WriteString("status", disposition.Status);
        writer.WriteString("reason", disposition.Reason);
        writer.WriteEndObject();
    }

    private static string Truncate(string value, int max) => ContextTextBounds.Truncate(value, max);
}
