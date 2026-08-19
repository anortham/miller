using Miller.Testing;
using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Parsing;

public sealed class ContinuousTestClassifierTests
{
    [Fact]
    public void Classifier_promotes_extractor_is_test_to_test_case()
    {
        var symbol = Symbol(
            name: "test_charge",
            qualifiedName: "tests.payments.test_service.test_charge",
            metadata: new Dictionary<string, object?> { ["is_test"] = true, ["test_role"] = "test_case" },
            isTest: true,
            testRole: ContinuousTestRole.TestCase);
        var fileRecord = File("tests/payments/test_service.py");

        var fileRole = ContinuousTestClassifier.ClassifyFileRole(fileRecord.Path, fileRecord.Language, [symbol]);
        var classification = ContinuousTestClassifier.ClassifySymbol(symbol, fileRole);
        var testCase = ContinuousTestClassifier.TestCaseFromSymbol(symbol, fileRecord, classification);

        Assert.Equal(ContinuousTestPathRole.Test, fileRole.Role);
        Assert.Equal(ContinuousTestRole.TestCase, classification.TestRole);
        Assert.Equal("tests/payments/test_service.py::test_charge", classification.Selector);
        Assert.Equal(1.0, classification.Confidence);
        Assert.Equal("extractor_metadata", classification.EvidenceSource);
        Assert.NotNull(testCase);
        Assert.Equal("tests/payments/test_service.py::test_charge", testCase!.Selector);
        Assert.Equal("tests/payments/test_service.py", testCase.FilePath);
        Assert.Equal("blake3:file", testCase.ContentHash);
        Assert.Equal("test_charge", testCase.SymbolName);
        Assert.Equal("tests/payments/test_service.py", testCase.SymbolPath);
    }

    [Fact]
    public void Classifier_marks_fixture_setup_not_scorable()
    {
        var symbol = Symbol(
            name: "fake_card",
            qualifiedName: "tests.payments.test_service.fake_card",
            metadata: new Dictionary<string, object?> { ["test_role"] = "fixture_setup" },
            isTest: true,
            testRole: ContinuousTestRole.FixtureSetup);
        var fileRecord = File("tests/payments/test_service.py");

        var fileRole = ContinuousTestClassifier.ClassifyFileRole(fileRecord.Path, fileRecord.Language, [symbol]);
        var classification = ContinuousTestClassifier.ClassifySymbol(symbol, fileRole);

        Assert.Equal(ContinuousTestRole.FixtureSetup, classification.TestRole);
        Assert.False(classification.Scorable);
        Assert.Null(ContinuousTestClassifier.TestCaseFromSymbol(symbol, fileRecord, classification));
    }

    [Fact]
    public void Classifier_uses_path_fallback_with_heuristic_evidence()
    {
        var symbol = Symbol(name: "test_charge", qualifiedName: "test_charge");

        var fileRole = ContinuousTestClassifier.ClassifyFileRole("tests/test_service.py", "python", [symbol]);
        var classification = ContinuousTestClassifier.ClassifySymbol(symbol, fileRole);

        Assert.Equal(ContinuousTestPathRole.Test, fileRole.Role);
        Assert.Equal("path_heuristic", fileRole.EvidenceSource);
        Assert.Equal(ContinuousTestRole.TestCase, classification.TestRole);
        Assert.Equal("tests/test_service.py::test_charge", classification.Selector);
        Assert.Equal(0.7, classification.Confidence);
        Assert.Equal("path_heuristic", classification.EvidenceSource);
    }

    [Fact]
    public void Classifier_prefers_extractor_metadata_over_path_conflict()
    {
        var symbol = Symbol(
            name: "helper",
            qualifiedName: "src.test_helpers.helper",
            metadata: new Dictionary<string, object?> { ["test_role"] = "test_case" },
            isTest: true,
            testRole: ContinuousTestRole.TestCase);

        var fileRole = ContinuousTestClassifier.ClassifyFileRole("src/test_helpers.py", "python", [symbol]);
        var classification = ContinuousTestClassifier.ClassifySymbol(symbol, fileRole);

        Assert.Equal(ContinuousTestPathRole.Test, fileRole.Role);
        Assert.Equal("extractor_metadata", fileRole.EvidenceSource);
        Assert.Equal(ContinuousTestRole.TestCase, classification.TestRole);
        Assert.Equal("extractor_metadata", classification.EvidenceSource);
        Assert.Equal(1.0, classification.Confidence);
    }

    [Fact]
    public void Classifier_suppresses_ambiguous_source_test_prefix_metadata()
    {
        var symbol = Symbol(
            name: "test_result_histories",
            qualifiedName: "CanonicalStore.test_result_histories",
            kind: "method",
            metadata: new Dictionary<string, object?> { ["is_test"] = true },
            isTest: true,
            testRole: ContinuousTestRole.TestCase);
        var fileRecord = File("python/eros/store/sqlite.py");

        var fileRole = ContinuousTestClassifier.ClassifyFileRole(fileRecord.Path, fileRecord.Language, [symbol]);
        var classification = ContinuousTestClassifier.ClassifySymbol(symbol, fileRole);

        Assert.Equal(ContinuousTestPathRole.Source, fileRole.Role);
        Assert.False(classification.IsTest);
        Assert.Null(classification.TestRole);
        Assert.Null(ContinuousTestClassifier.TestCaseFromSymbol(symbol, fileRecord, classification));
    }

    [Fact]
    public void Classifier_suppresses_source_test_prefix_metadata_role()
    {
        var symbol = Symbol(
            name: "test_result_histories",
            qualifiedName: "CanonicalStore.test_result_histories",
            kind: "method",
            metadata: new Dictionary<string, object?> { ["test_role"] = "test_case" },
            testRole: ContinuousTestRole.TestCase);
        var fileRecord = File("python/eros/store/sqlite.py");

        var fileRole = ContinuousTestClassifier.ClassifyFileRole(fileRecord.Path, fileRecord.Language, [symbol]);
        var classification = ContinuousTestClassifier.ClassifySymbol(symbol, fileRole);

        Assert.Equal(ContinuousTestPathRole.Source, fileRole.Role);
        Assert.False(classification.IsTest);
        Assert.Null(classification.TestRole);
        Assert.Null(ContinuousTestClassifier.TestCaseFromSymbol(symbol, fileRecord, classification));
    }

    [Fact]
    public void Classifier_allows_explicit_test_role_outside_standard_test_path()
    {
        var symbol = Symbol(
            name: "contract_handles_payment",
            qualifiedName: "contracts.payment.contract_handles_payment",
            metadata: new Dictionary<string, object?> { ["test_role"] = "test_case" },
            testRole: ContinuousTestRole.TestCase);

        var fileRole = ContinuousTestClassifier.ClassifyFileRole("contracts/payment_contract.py", "python", [symbol]);
        var classification = ContinuousTestClassifier.ClassifySymbol(symbol, fileRole);

        Assert.Equal(ContinuousTestPathRole.Test, fileRole.Role);
        Assert.True(classification.IsTest);
        Assert.Equal("extractor_metadata", classification.EvidenceSource);
    }

    private static CtClassifierSymbol Symbol(
        string name = "test_charge",
        string qualifiedName = "tests.payments.test_service.test_charge",
        string kind = "function",
        IReadOnlyDictionary<string, object?>? metadata = null,
        IReadOnlyList<object?>? annotations = null,
        bool isTest = false,
        ContinuousTestRole? testRole = null) =>
        new(
            WorkspaceId: "ws:1",
            Name: name,
            QualifiedName: qualifiedName,
            Kind: kind,
            Metadata: metadata ?? new Dictionary<string, object?>(),
            Annotations: annotations ?? [],
            IsTest: isTest,
            TestRole: testRole);

    private static CtClassifierFile File(string path) =>
        new(Path: path, Language: "python", ContentHash: "blake3:file");
}
