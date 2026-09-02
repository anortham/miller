namespace Miller.Testing;

/// <summary>
/// Which discovered frameworks continuous testing can actually run, and the plain reason for the ones it
/// cannot.
///
/// <para>A framework this refuses is one Miller can NAME but never execute. Discovery still reports such a
/// project — silently dropping it would put a reader back where the raw process error left them, hunting a
/// build that is not broken — but <c>tests enable</c> never records it, so no daemon ever tries to run it.</para>
///
/// <para>The reason travels with the framework value rather than with each call site, so the enable refusal,
/// the mixed-enable report, the status project line, and the provider factory's unsupported provider all say
/// the same sentence.</para>
/// </summary>
public static class ContinuousTestFrameworkSupport
{
    /// <summary>
    /// An xunit project whose packages are xunit v2, not <c>xunit.v3</c>.
    ///
    /// <para>It is a separate framework value rather than a flag beside <c>xunit</c> because the framework
    /// string is what already reaches every consumer that has to tell them apart: the provider factory
    /// resolves a provider from it, <c>ct.db</c> stores it, and the JSON contract publishes it. CT runs the
    /// built self-executing test assembly, which only xUnit v3 / Microsoft.Testing.Platform produces; a v2
    /// project builds a dll plus <c>testhost.exe</c> and has no such executable.</para>
    /// </summary>
    public const string XunitV2 = "xunit-v2";

    public const string Minitest = "minitest";

    public const string MinitestReason =
        "Minitest has no per-test machine-readable runner surface CT can consume";

    public const string MinitestRemedy = "Add rspec, or run the suite directly with rake test";

    public const string GdUnit4 = "gdunit4";

    public const string GdUnit4Reason =
        "gdUnit4 is detected; Miller CT does not yet support its runner";

    public const string GdUnit4Remedy =
        "run it with its own runner; CT support is planned";

    public const string GutUnsupported = "gut-unsupported";

    public const string GutUnsupportedReason = "Godot 4 with GUT 9 was not detected";

    public const string GutUnsupportedRemedy =
        "Upgrade or configure Godot 4 with GUT 9, or run GUT directly";

    /// <summary>
    /// The one-line reason, kept short enough for a <c>ct.db</c> status column and a compact project line.
    /// </summary>
    public const string XunitV2Reason = "xUnit v2 detected; CT needs the v3 self-executing assembly";

    /// <summary>What to do about it, appended where a message has room for more than the reason.</summary>
    public const string XunitV2Remedy =
        "Migrate the project to xunit.v3, or run it directly with dotnet test. "
        + "(dotnet new xunit still scaffolds v2; dotnet new xunit3 scaffolds v3.)";

    private static readonly Dictionary<string, (string Reason, string Remedy)> Unsupported =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [XunitV2] = (XunitV2Reason, XunitV2Remedy),
            [Minitest] = (MinitestReason, MinitestRemedy),
            [GdUnit4] = (GdUnit4Reason, GdUnit4Remedy),
            [GutUnsupported] = (GutUnsupportedReason, GutUnsupportedRemedy),
        };

    /// <summary>True when continuous testing can run a project with this framework value.</summary>
    public static bool IsSupported(string? framework) => ReasonFor(framework) is null;

    /// <summary>
    /// Why continuous testing cannot run a project with this framework value, or null when it can. An
    /// unrecognized framework answers null: this names the shapes Miller classified and refused, not every
    /// framework no provider happens to serve.
    /// </summary>
    public static string? ReasonFor(string? framework) => Lookup(framework)?.Reason;

    /// <summary>What the reader can do instead, or null when the framework is supported.</summary>
    public static string? RemedyFor(string? framework) => Lookup(framework)?.Remedy;

    private static (string Reason, string Remedy)? Lookup(string? framework) =>
        framework is not null && Unsupported.TryGetValue(framework.Trim(), out (string, string) entry)
            ? entry
            : null;
}
