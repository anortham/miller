namespace Miller.Core.Contracts;

/// <summary>
/// julie's <c>test_role</c> classification, read from <c>symbols.metadata</c> (a NEW 28/2 field, computed by julie's
/// cross-language test-role classifier). Carried as the verbatim role string rather than mapped to a fixed C# enum:
/// the contract publishes whatever role julie persisted, and M4 uses it only as a predicate (exclude a test-role
/// HttpClient url literal from the route bridge; a future M5 input), never branching on a specific role name. Keeping
/// the raw value keeps Miller honest to julie's role vocabulary across its 30+ languages instead of hardcoding a
/// guessed variant list.
///
/// <para><b>Every value julie writes here IS a test role.</b> Verified against julie's <c>TestRole</c> enum
/// (<c>julie-extractors/src/base/kinds.rs</c>): the variants are exactly <c>test_case</c>, <c>parameterized_test</c>,
/// <c>fixture_setup</c>, <c>fixture_teardown</c>, <c>test_container</c> — all test code. julie writes a
/// <c>test_role</c> only for test symbols; production code carries no such field. So the presence of a (non-blank)
/// <c>test_role</c> is itself the test signal — <see cref="IsTest"/> does NOT use a "contains test" substring (which
/// would wrongly drop <c>fixture_setup</c>/<c>fixture_teardown</c>), it treats any present role as test.</para>
/// </summary>
/// <param name="Value">
/// The verbatim <c>test_role</c> string julie persisted (e.g. <c>test_case</c>, <c>fixture_setup</c>). Never null
/// here — absence of the field is represented by a null <see cref="TestRole"/> reference, not by this string.
/// </param>
public sealed record TestRole(string Value)
{
    /// <summary>
    /// True when this role denotes test code — the single semantic M4 needs (to exclude test HttpClient url literals
    /// from the route bridge). julie persists a <c>test_role</c> ONLY for test symbols and every variant
    /// (<c>test_case</c>/<c>parameterized_test</c>/<c>fixture_setup</c>/<c>fixture_teardown</c>/<c>test_container</c>)
    /// is test code, so any non-blank role value is a test role. A blank value (defensive only) is not.
    /// </summary>
    public bool IsTest => !string.IsNullOrWhiteSpace(Value);
}
