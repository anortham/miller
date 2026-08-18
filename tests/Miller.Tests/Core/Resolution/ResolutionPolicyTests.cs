using Miller.Core.Resolution;
using Xunit;

namespace Miller.Tests.Core.Resolution;

public sealed class ResolutionPolicyTests
{
    [Theory]
    [InlineData(FactSymbolKind.Class)]
    [InlineData(FactSymbolKind.Interface)]
    [InlineData(FactSymbolKind.Struct)]
    [InlineData(FactSymbolKind.Enum)]
    [InlineData(FactSymbolKind.Type)]
    [InlineData(FactSymbolKind.Trait)]
    [InlineData(FactSymbolKind.Union)]
    [InlineData(FactSymbolKind.Delegate)]
    public void TypeLike_ContainsTheContractSet(FactSymbolKind kind)
    {
        Assert.True(ResolutionPolicy.IsTypeLike(kind));
    }

    [Theory]
    [InlineData(FactSymbolKind.Method)]
    [InlineData(FactSymbolKind.Namespace)]
    [InlineData(FactSymbolKind.Module)]
    public void TypeLike_ExcludesNonTypes(FactSymbolKind kind)
    {
        Assert.False(ResolutionPolicy.IsTypeLike(kind));
    }

    [Theory]
    [InlineData(ResolutionRefKind.Call, FactSymbolKind.Function, FactSymbolKind.Method, FactSymbolKind.Constructor)]
    [InlineData(ResolutionRefKind.Instantiates, FactSymbolKind.Class, FactSymbolKind.Struct, FactSymbolKind.Constructor)]
    [InlineData(ResolutionRefKind.MemberAccess, FactSymbolKind.Property, FactSymbolKind.Field, FactSymbolKind.Method, FactSymbolKind.Constant, FactSymbolKind.EnumMember)]
    [InlineData(ResolutionRefKind.VariableRef, FactSymbolKind.Variable, FactSymbolKind.Constant, FactSymbolKind.Field, FactSymbolKind.Property)]
    public void CompatibleKinds_Tier123_MatchTheContract(ResolutionRefKind refKind, params FactSymbolKind[] expected)
    {
        Assert.Equal(expected.OrderBy(k => k), ResolutionPolicy.CompatibleKinds(refKind, tier4: false).OrderBy(k => k));
    }

    [Fact]
    public void CompatibleKinds_TypeUsage_IsTypeLike()
    {
        Assert.Equal(
            ResolutionPolicy.TypeLike.OrderBy(k => k),
            ResolutionPolicy.CompatibleKinds(ResolutionRefKind.TypeUsage, tier4: false).OrderBy(k => k));
    }

    [Theory]
    [InlineData(ResolutionRefKind.Call, FactSymbolKind.Function, FactSymbolKind.Constructor)]
    [InlineData(ResolutionRefKind.Instantiates, FactSymbolKind.Class, FactSymbolKind.Struct, FactSymbolKind.Constructor)]
    public void CompatibleKinds_Tier4_MatchTheContract(ResolutionRefKind refKind, params FactSymbolKind[] expected)
    {
        Assert.Equal(expected.OrderBy(k => k), ResolutionPolicy.CompatibleKinds(refKind, tier4: true).OrderBy(k => k));
    }

    [Theory]
    [InlineData(ResolutionRefKind.MemberAccess)]
    [InlineData(ResolutionRefKind.VariableRef)]
    public void CompatibleKinds_Tier4_DisablesMemberAndVariable(ResolutionRefKind refKind)
    {
        Assert.Empty(ResolutionPolicy.CompatibleKinds(refKind, tier4: true));
    }

    [Fact]
    public void CompatibleKinds_Tier4_TypeUsage_IsTypeLike()
    {
        Assert.Equal(
            ResolutionPolicy.TypeLike.OrderBy(k => k),
            ResolutionPolicy.CompatibleKinds(ResolutionRefKind.TypeUsage, tier4: true).OrderBy(k => k));
    }

    [Theory]
    [InlineData("javascript", true)]
    [InlineData("jsx", true)]
    [InlineData("typescript", true)]
    [InlineData("tsx", true)]
    [InlineData("csharp", false)]
    [InlineData("python", false)]
    public void IsEsModuleLanguage_MatchesTheContractSet(string language, bool expected)
    {
        Assert.Equal(expected, ResolutionPolicy.IsEsModuleLanguage(language));
    }

    [Theory]
    [InlineData("typescript", true)]
    [InlineData("javascript", true)]
    [InlineData("tsx", false)]
    [InlineData("jsx", false)]
    [InlineData("csharp", false)]
    public void IsTier2Language_IsExactlyTypescriptOrJavascript(string language, bool expected)
    {
        Assert.Equal(expected, ResolutionPolicy.IsTier2Language(language));
    }

    [Theory]
    [InlineData("class", FactSymbolKind.Class)]
    [InlineData("enum_member", FactSymbolKind.EnumMember)]
    [InlineData("import", FactSymbolKind.Import)]
    [InlineData("delegate", FactSymbolKind.Delegate)]
    public void ParseSymbolKind_MapsKnownStrings(string raw, FactSymbolKind expected)
    {
        Assert.Equal(expected, ResolutionPolicy.ParseSymbolKind(raw));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Class")]
    [InlineData("")]
    public void ParseSymbolKind_Unknown_ReturnsNull(string raw)
    {
        Assert.Null(ResolutionPolicy.ParseSymbolKind(raw));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ParseIsStatic_KnownStrings(string raw, bool expected)
    {
        Assert.Equal(expected, ResolutionPolicy.ParseIsStatic(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("")]
    public void ParseIsStatic_AnythingElse_IsUnknown(string? raw)
    {
        Assert.Null(ResolutionPolicy.ParseIsStatic(raw));
    }

    [Fact]
    public void ConfidenceConstants_MatchTheContract()
    {
        Assert.Equal(0.95, ResolutionPolicy.LocalConfidence);
        Assert.Equal(0.85, ResolutionPolicy.ImportConfidence);
        Assert.Equal(0.75, ResolutionPolicy.ReceiverDeclaredConfidence);
        Assert.Equal(0.65, ResolutionPolicy.ReceiverInferredConfidence);
        Assert.Equal(0.70, ResolutionPolicy.StaticTypeConfidence);
        Assert.Equal(0.55, ResolutionPolicy.GlobalConfidence);
        Assert.Equal("tier1_local", ResolutionPolicy.LocalMethod);
        Assert.Equal("tier2_import", ResolutionPolicy.ImportMethod);
        Assert.Equal("tier3_receiver", ResolutionPolicy.ReceiverMethod);
        Assert.Equal("tier3_static_type", ResolutionPolicy.StaticTypeMethod);
        Assert.Equal("tier4_global", ResolutionPolicy.GlobalMethod);
        Assert.Equal(6, ResolutionPolicy.Version);
    }

    [Fact]
    public void Chain_PendingCallWithReceiver_IsReceiverThenStaticType()
    {
        Assert.Equal(
            [ResolutionTier.Receiver, ResolutionTier.StaticType],
            ResolutionPolicy.Chain(ResolutionOrigin.Pending, ResolutionRefKind.Call, hasReceiver: true));
    }

    [Theory]
    [InlineData(ResolutionRefKind.Call)]
    [InlineData(ResolutionRefKind.Instantiates)]
    [InlineData(ResolutionRefKind.TypeUsage)]
    public void Chain_PendingCallLikeWithoutReceiver_RunsImportThroughGlobal(ResolutionRefKind kind)
    {
        Assert.Equal(
            [ResolutionTier.Import, ResolutionTier.Receiver, ResolutionTier.StaticType, ResolutionTier.Global],
            ResolutionPolicy.Chain(ResolutionOrigin.Pending, kind, hasReceiver: false));
    }

    [Fact]
    public void Chain_PendingMemberAccessWithReceiver_IsReceiverThenStaticType()
    {
        Assert.Equal(
            [ResolutionTier.Receiver, ResolutionTier.StaticType],
            ResolutionPolicy.Chain(ResolutionOrigin.Pending, ResolutionRefKind.MemberAccess, hasReceiver: true));
    }

    [Fact]
    public void Chain_PendingMemberAccessWithoutReceiver_OmitsGlobal()
    {
        Assert.Equal(
            [ResolutionTier.Import, ResolutionTier.Receiver, ResolutionTier.StaticType],
            ResolutionPolicy.Chain(ResolutionOrigin.Pending, ResolutionRefKind.MemberAccess, hasReceiver: false));
    }

    [Fact]
    public void Chain_PendingVariableRef_IsEmpty()
    {
        Assert.Empty(ResolutionPolicy.Chain(ResolutionOrigin.Pending, ResolutionRefKind.VariableRef, hasReceiver: false));
    }

    [Fact]
    public void Chain_IdentifierCallWithReceiver_IsReceiverThenStaticType()
    {
        Assert.Equal(
            [ResolutionTier.Receiver, ResolutionTier.StaticType],
            ResolutionPolicy.Chain(ResolutionOrigin.Identifier, ResolutionRefKind.Call, hasReceiver: true));
    }

    [Theory]
    [InlineData(ResolutionRefKind.Call)]
    [InlineData(ResolutionRefKind.TypeUsage)]
    public void Chain_IdentifierCallOrTypeUsageWithoutReceiver_IsImportStaticGlobal(ResolutionRefKind kind)
    {
        Assert.Equal(
            [ResolutionTier.Import, ResolutionTier.StaticType, ResolutionTier.Global],
            ResolutionPolicy.Chain(ResolutionOrigin.Identifier, kind, hasReceiver: false));
    }

    [Fact]
    public void Chain_IdentifierTypeUsageWithReceiver_StillRunsImportStaticGlobal()
    {
        Assert.Equal(
            [ResolutionTier.Import, ResolutionTier.StaticType, ResolutionTier.Global],
            ResolutionPolicy.Chain(ResolutionOrigin.Identifier, ResolutionRefKind.TypeUsage, hasReceiver: true));
    }

    [Fact]
    public void Chain_IdentifierMemberAccessWithReceiver_IsReceiverThenStaticType()
    {
        Assert.Equal(
            [ResolutionTier.Receiver, ResolutionTier.StaticType],
            ResolutionPolicy.Chain(ResolutionOrigin.Identifier, ResolutionRefKind.MemberAccess, hasReceiver: true));
    }

    [Fact]
    public void Chain_IdentifierMemberAccessWithoutReceiver_IsEmpty()
    {
        Assert.Empty(ResolutionPolicy.Chain(ResolutionOrigin.Identifier, ResolutionRefKind.MemberAccess, hasReceiver: false));
    }

    [Fact]
    public void Chain_IdentifierVariableRef_IsLocalOnly()
    {
        Assert.Equal(
            [ResolutionTier.Local],
            ResolutionPolicy.Chain(ResolutionOrigin.Identifier, ResolutionRefKind.VariableRef, hasReceiver: false));
    }

    [Fact]
    public void Chain_IdentifierInstantiates_IsEmpty()
    {
        Assert.Empty(ResolutionPolicy.Chain(ResolutionOrigin.Identifier, ResolutionRefKind.Instantiates, hasReceiver: false));
        Assert.Empty(ResolutionPolicy.Chain(ResolutionOrigin.Identifier, ResolutionRefKind.Instantiates, hasReceiver: true));
    }
}
