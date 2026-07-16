namespace Miller.Server.Tools;

/// <summary>
/// Width limits shared by the compact renderers. These are cross-tool contracts, not per-tool preferences: a
/// symbol rendered by <c>search</c>, <c>inspect</c>, and <c>context</c> must come out the same width in all
/// three, because an agent reads them as one output vocabulary. A private copy per tool drifts silently — the
/// value changes in one renderer, the others keep the old width, and no test notices.
/// <c>SignatureMaxLengthConventionTests</c> pins the single-home rule.
/// </summary>
internal static class ToolRenderLimits
{
    /// <summary>
    /// Longest signature rendered inline on a compact result row, in characters. Beyond this the signature is
    /// truncated with an ellipsis so one long generic method cannot blow out a whole result list.
    /// </summary>
    internal const int SignatureMaxLength = 110;
}
