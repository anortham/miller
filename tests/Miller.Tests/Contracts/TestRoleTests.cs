using Miller.Core.Contracts;
using Xunit;

namespace Miller.Tests.Contracts;

/// <summary>
/// Pins <see cref="TestRole.IsTest"/> — the single predicate M4 uses to exclude test-role HttpClient url literals from
/// the route bridge. Verified against julie's <c>TestRole</c> enum (<c>julie-extractors/src/base/kinds.rs</c>): the
/// variants are exactly <c>test_case</c>, <c>parameterized_test</c>, <c>fixture_setup</c>, <c>fixture_teardown</c>,
/// <c>test_container</c>, and julie writes a role ONLY for test symbols. So every present role is a test role; the
/// predicate is "has a non-blank role", NOT a "contains test" substring (which would wrongly drop the fixture roles).
/// </summary>
public sealed class TestRoleTests
{
    [Theory]
    // The full verified julie role vocabulary (kinds.rs as_str) — all are test code.
    [InlineData("test_case")]
    [InlineData("parameterized_test")]
    [InlineData("fixture_setup")]       // would be missed by a naive "contains test" substring
    [InlineData("fixture_teardown")]    // would be missed by a naive "contains test" substring
    [InlineData("test_container")]
    public void IsTest_TrueForEveryJulieRole(string role)
    {
        Assert.True(new TestRole(role).IsTest);
    }

    [Theory]
    // A future role julie might add is still a test role (julie only writes the field for test symbols).
    [InlineData("some_future_role")]
    [InlineData("mock_setup")]
    public void IsTest_TrueForAnyPresentRole(string role)
    {
        Assert.True(new TestRole(role).IsTest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTest_FalseForBlankValue(string role)
    {
        // Defensive: a blank role is not a test signal (julie never writes one; a null TestRole models absence).
        Assert.False(new TestRole(role).IsTest);
    }

    [Fact]
    public void Value_IsCarriedVerbatim()
    {
        // The contract preserves julie's raw role string (not mapped to a C# enum), so unknown future roles survive.
        Assert.Equal("fixture_setup", new TestRole("fixture_setup").Value);
    }
}
