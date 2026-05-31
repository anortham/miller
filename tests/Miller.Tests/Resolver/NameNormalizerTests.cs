using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Resolver;

/// <summary>
/// Pins <see cref="NameNormalizer.Stem"/> — the "safe finisher" that folds a type name to a canonical stem so an
/// entity and its DTO collapse to the same key. Asserts the exact stem, never just "non-empty". Covers: <c>I</c>/<c>_</c>
/// prefix strip, the DTO/Model/Request/Response/View/VM/Entity suffix strip, singular↔plural folding, and the
/// combined case. A row that proves <c>Equal(a) == Equal(b)</c> for an entity/DTO pair is the load-bearing assertion.
/// </summary>
public sealed class NameNormalizerTests
{
    public static TheoryData<string, string> StemTable() => new()
    {
        // Interface "I" prefix stripped (but not a real leading-I word like "Identity" — only when CamelCase follows).
        { "IUser", "user" },
        { "IUserService", "userservice" },
        // Underscore prefix stripped.
        { "_internalThing", "internalthing" },
        // DTO / Model / Request / Response / View / VM / Entity suffixes stripped.
        { "UserDto", "user" },
        { "UserModel", "user" },
        { "CreateOrderRequest", "createorder" },
        { "OrderResponse", "order" },
        { "UserView", "user" },
        { "UserVM", "user" },
        { "ApplicationUserEntity", "applicationuser" },
        // Plural → singular (the canonical stem is singular).
        { "Users", "user" },
        { "Categories", "category" },
        { "Boxes", "box" },
        // Combined: interface + plural + nothing else.
        { "IUsers", "user" },
        // Combined: prefix + suffix.
        { "IUserDto", "user" },
        // A bare already-canonical name is unchanged (lowercased).
        { "Order", "order" },
        // Suffix is only stripped as a whole trailing token, not mid-word: "Modeller" must NOT lose "Model".
        { "Modeller", "modeller" },
        // DELIBERATE aggressive fold (design §4 — the name leg is corroborator-only, NEVER a sole High signal): a
        // control/domain type whose name ends in a role word collapses just like a ViewModel/Model would. There is no
        // syntactic way to tell "UserView" (a DTO) from "WebView" (a control) apart, and the limited blast radius
        // (spurious Medium pairings only) is the accepted tradeoff. Pinned so the behavior is explicit, not incidental.
        { "WebView", "web" },
        { "TreeView", "tree" },
        { "DataModel", "data" },
    };

    [Theory]
    [MemberData(nameof(StemTable))]
    public void Stem_FoldsToCanonical(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Stem(input));
    }

    [Fact]
    public void Stem_EntityAndDto_CollapseToSameStem()
    {
        // The whole point: an entity and its DTO must produce the same stem so the name leg can pair them.
        Assert.Equal(NameNormalizer.Stem("ApplicationUser"), NameNormalizer.Stem("ApplicationUserDto"));
        Assert.Equal(NameNormalizer.Stem("User"), NameNormalizer.Stem("IUser"));
        Assert.Equal(NameNormalizer.Stem("Account"), NameNormalizer.Stem("Accounts"));
    }

    [Fact]
    public void Stem_DistinctTypes_DoNotCollide()
    {
        // Two genuinely different types must NOT collapse — guards against an over-eager strip.
        Assert.NotEqual(NameNormalizer.Stem("UserDto"), NameNormalizer.Stem("OrderDto"));
        Assert.NotEqual(NameNormalizer.Stem("Preference"), NameNormalizer.Stem("AppSetting"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Stem_BlankInput_ReturnsEmpty(string input)
    {
        Assert.Equal(string.Empty, NameNormalizer.Stem(input));
    }
}
