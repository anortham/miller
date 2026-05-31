using Miller.Core.Contracts;
using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Resolver;

/// <summary>
/// Pins <see cref="FieldSetExtractor"/> — the field-set source the scorer's Jaccard corroborator reads (design §5).
/// Four cases the design calls out: (1) class/interface properties via <c>parent_id</c> children; (2) C# <c>record</c>
/// positional params parsed from the <c>signature</c> (records have NO property children — a naive child query returns
/// empty and misfires the corroborator); (3) <c>[JsonProperty("x")]</c> rename from <c>raw_text</c>; (4) the
/// balanced-bracket return-type unwrap for transitive returns. Asserts exact ordered fields / unwrapped type names.
/// </summary>
public sealed class FieldSetExtractorTests
{
    private static SymbolDetail Sym(string id, string name, string kind, string signature = "")
        => new(
            Id: id, Name: name, Kind: kind, FilePath: "x.cs", Signature: signature,
            Namespace: null, TestRole: null, ParentClassName: null);

    [Fact]
    public void ExtractFields_ClassProperties_FromChildren_InDeclarationOrder()
    {
        var owner = Sym("U", "UserDto", "class");
        var children = new[]
        {
            Sym("U.Id", "Id", "property", "int Id"),
            Sym("U.Name", "Name", "property", "string Name"),
            Sym("U.Email", "Email", "property", "string Email"),
        };

        var fieldSet = FieldSetExtractor.ExtractFields(owner, children, annotations: []);

        Assert.Equal("U", fieldSet.OwnerId);
        Assert.Equal(
            [new FieldMember("Id", "int"), new FieldMember("Name", "string"), new FieldMember("Email", "string")],
            fieldSet.Fields);
        Assert.Equal(3, fieldSet.Count);
    }

    [Fact]
    public void ExtractFields_NonPropertyChildren_AreExcluded()
    {
        // A nested method/class child is not a field; only properties and fields contribute to the shape.
        var owner = Sym("U", "UserDto", "class");
        var children = new[]
        {
            Sym("U.Id", "Id", "property", "int Id"),
            Sym("U.ToString", "ToString", "method", "string ToString()"),
            Sym("U.Nested", "Nested", "class", "class Nested"),
            Sym("U.Tag", "Tag", "field", "string Tag"),
        };

        var fieldSet = FieldSetExtractor.ExtractFields(owner, children, annotations: []);

        Assert.Equal(
            [new FieldMember("Id", "int"), new FieldMember("Tag", "string")],
            fieldSet.Fields);
    }

    [Fact]
    public void ExtractFields_CSharpRecord_ParsesPositionalParams_FromSignature()
    {
        // Records have NO property children. The fields come from the positional params in the signature.
        var owner = Sym("D", "DocumentRevisionDto", "record",
            "public record DocumentRevisionDto(int Id, string Title, DateTime CreatedAt)");

        var fieldSet = FieldSetExtractor.ExtractFields(owner, children: [], annotations: []);

        Assert.Equal(
            [
                new FieldMember("Id", "int"),
                new FieldMember("Title", "string"),
                new FieldMember("CreatedAt", "DateTime"),
            ],
            fieldSet.Fields);
    }

    [Fact]
    public void ExtractFields_Record_WithGenericParam_KeepsGenericTypeIntact()
    {
        // A positional param whose type is generic (commas inside <>) must not be split on the inner comma.
        var owner = Sym("P", "PagedResult", "record",
            "public record PagedResult(List<int> Items, IDictionary<string, int> Counts, int Total)");

        var fieldSet = FieldSetExtractor.ExtractFields(owner, children: [], annotations: []);

        Assert.Equal(
            [
                new FieldMember("Items", "List<int>"),
                new FieldMember("Counts", "IDictionary<string, int>"),
                new FieldMember("Total", "int"),
            ],
            fieldSet.Fields);
    }

    [Fact]
    public void ExtractFields_JsonPropertyRename_UsesWireName()
    {
        // [JsonProperty("user_id")] on the Id property => the field-set wire name is "user_id", not "Id".
        var owner = Sym("U", "UserDto", "class");
        var children = new[]
        {
            Sym("U.Id", "Id", "property", "int Id"),
            Sym("U.Name", "Name", "property", "string Name"),
        };
        var annotations = new[]
        {
            new SymbolAnnotation("U.Id", 0, "JsonProperty", "jsonproperty", "JsonProperty(\"user_id\")", "JsonProperty"),
        };

        var fieldSet = FieldSetExtractor.ExtractFields(owner, children, annotations);

        Assert.Equal(
            [new FieldMember("user_id", "int"), new FieldMember("Name", "string")],
            fieldSet.Fields);
    }

    [Fact]
    public void ExtractFields_PropertyTypes_HandleAccessorsModifiersAndNameSubstrings()
    {
        // Member type extraction must tokenize from the END, not locate the name by substring: "Order" is a substring
        // of its own type "OrderRef", and modifiers/accessors/initializers must be dropped so the declared type is clean.
        var owner = Sym("U", "OrderDto", "class");
        var children = new[]
        {
            Sym("U.Order", "Order", "property", "OrderRef Order { get; set; }"),
            Sym("U.Name", "Name", "property", "public required string Name"),
            Sym("U.Id", "Id", "property", "int Id { get; init; } = 0"),
            Sym("U.Tag", "Tag", "field", "string Tag = \"x\""),
        };

        var fieldSet = FieldSetExtractor.ExtractFields(owner, children, annotations: []);

        Assert.Equal(
            [
                new FieldMember("Order", "OrderRef"),
                new FieldMember("Name", "string"),
                new FieldMember("Id", "int"),
                new FieldMember("Tag", "string"),
            ],
            fieldSet.Fields);
    }

    // ---- balanced-bracket return-type unwrap -------------------------------------------------------------------

    public static TheoryData<string, string?> UnwrapTable() => new()
    {
        // signature/return, expected unwrapped named type (null = no named user type / bare)
        { "AppSetting", "AppSetting" },
        { "Task<AppSetting>", "AppSetting" },
        { "ActionResult<AppSetting>", "AppSetting" },
        { "Task<ActionResult<AppSetting>>", "AppSetting" },
        { "Task<IEnumerable<IProject>>", "IProject" },
        { "ActionResult<List<UserDto>>", "UserDto" },
        // Bare ActionResult / IActionResult unwrap to nothing (no named user type) => null, NO edge.
        { "ActionResult", null },
        { "IActionResult", null },
        { "Task<ActionResult>", null },
        { "Task<IActionResult>", null },
        // A primitive inner type is not a named user type for the responds-> edge => null (the Task<bool> case).
        { "Task<bool>", null },
        { "Task<int>", null },
        { "Task<string>", null },
        // void / Task with no generic => null.
        { "Task", null },
        { "void", null },
    };

    [Theory]
    [MemberData(nameof(UnwrapTable))]
    public void UnwrapReturnType_PeelsWrappers_ToNamedUserType(string returnType, string? expected)
    {
        Assert.Equal(expected, FieldSetExtractor.UnwrapReturnType(returnType));
    }
}
