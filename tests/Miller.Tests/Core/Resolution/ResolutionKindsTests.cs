using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class ResolutionKindsTests
{
    [Theory]
    [InlineData("call", ResolutionRefKind.Call)]
    [InlineData("type_usage", ResolutionRefKind.TypeUsage)]
    [InlineData("member_access", ResolutionRefKind.MemberAccess)]
    [InlineData("variable_ref", ResolutionRefKind.VariableRef)]
    public void FromIdentifierKind_MapsKnownKinds(string raw, ResolutionRefKind expected)
    {
        Assert.Equal(expected, ResolutionKinds.FromIdentifierKind(raw));
    }

    [Theory]
    [InlineData("import")]
    [InlineData("calls")]
    [InlineData("")]
    [InlineData("CALL")]
    public void FromIdentifierKind_Unknown_ReturnsNull(string raw)
    {
        Assert.Null(ResolutionKinds.FromIdentifierKind(raw));
    }

    [Theory]
    [InlineData("calls", ResolutionRefKind.Call)]
    [InlineData("instantiates", ResolutionRefKind.Instantiates)]
    [InlineData("uses", ResolutionRefKind.TypeUsage)]
    [InlineData("extends", ResolutionRefKind.TypeUsage)]
    [InlineData("implements", ResolutionRefKind.TypeUsage)]
    public void FromPendingKind_MapsKnownKinds(string raw, ResolutionRefKind expected)
    {
        Assert.Equal(expected, ResolutionKinds.FromPendingKind(raw));
    }

    [Theory]
    [InlineData("call")]
    [InlineData("variable_ref")]
    [InlineData("member_access")]
    [InlineData("")]
    [InlineData("USES")]
    public void FromPendingKind_Unknown_ReturnsNull(string raw)
    {
        Assert.Null(ResolutionKinds.FromPendingKind(raw));
    }
}
