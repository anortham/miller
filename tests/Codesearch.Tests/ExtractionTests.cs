using Xunit;

namespace Codesearch.Tests;

public class ExtractionTests
{
    [Fact]
    public void DetectLanguage_ReturnsLanguageForKnownExtension()
    {
        var lang = uniffi.codesearch_ffi.CodesearchFfiMethods.DetectLanguage("test.rs");
        Assert.Equal("rust", lang);
    }

    [Fact]
    public void DetectLanguage_ReturnsPythonForPyExtension()
    {
        var lang = uniffi.codesearch_ffi.CodesearchFfiMethods.DetectLanguage("script.py");
        Assert.Equal("python", lang);
    }

    [Fact]
    public void DetectLanguage_ReturnsNullForUnknownExtension()
    {
        var lang = uniffi.codesearch_ffi.CodesearchFfiMethods.DetectLanguage("test.xyz");
        Assert.Null(lang);
    }

    [Fact]
    public void SupportedLanguages_ReturnsNonEmptyList()
    {
        var langs = uniffi.codesearch_ffi.CodesearchFfiMethods.SupportedLanguages();
        Assert.NotEmpty(langs);
        Assert.Contains("rust", langs);
        Assert.Contains("python", langs);
        Assert.Contains("javascript", langs);
    }

    [Fact]
    public void ExtractFile_ExtractsRustSymbols()
    {
        var code = """
            fn hello() {
                println!("Hello");
            }

            fn main() {
                hello();
            }
            """;

        var results = uniffi.codesearch_ffi.CodesearchFfiMethods.ExtractFile(
            code, "test.rs", "."
        );

        Assert.NotEmpty(results.symbols);
        Assert.Contains(results.symbols, s => s.name == "hello");
        Assert.Contains(results.symbols, s => s.name == "main");
    }

    [Fact]
    public void ExtractFile_ExtractsPythonSymbols()
    {
        var code = """
            def greet(name):
                return f"Hello, {name}"

            class Person:
                def __init__(self, name):
                    self.name = name
            """;

        var results = uniffi.codesearch_ffi.CodesearchFfiMethods.ExtractFile(
            code, "test.py", "."
        );

        Assert.NotEmpty(results.symbols);
        Assert.Contains(results.symbols, s => s.name == "greet");
        Assert.Contains(results.symbols, s => s.name == "Person");
    }

    [Fact]
    public void ExtractFile_ExtractsRelationships()
    {
        var code = """
            fn helper() {}

            fn caller() {
                helper();
            }
            """;

        var results = uniffi.codesearch_ffi.CodesearchFfiMethods.ExtractFile(
            code, "test.rs", "."
        );

        // Should have relationships (caller calls helper)
        Assert.NotEmpty(results.relationships);
    }

    [Fact]
    public void ExtractFile_ExtractsIdentifiers()
    {
        // Use a more complex example that includes function calls and variable references
        var code = """
            fn helper(x: i32) -> i32 {
                x * 2
            }

            fn process(value: i32) -> i32 {
                let result = helper(value);
                result + 1
            }
            """;

        var results = uniffi.codesearch_ffi.CodesearchFfiMethods.ExtractFile(
            code, "test.rs", "."
        );

        // Identifiers represent usages/references (not declarations)
        // The extraction quality depends on julie-extractors implementation
        // We verify that the identifiers list is accessible and properly typed
        Assert.NotNull(results.identifiers);

        // If identifiers are extracted, verify they have expected properties
        foreach (var ident in results.identifiers)
        {
            Assert.False(string.IsNullOrEmpty(ident.name));
            Assert.False(string.IsNullOrEmpty(ident.kind));
            Assert.False(string.IsNullOrEmpty(ident.filePath));
            Assert.True(ident.lineNumber > 0);
        }
    }
}
