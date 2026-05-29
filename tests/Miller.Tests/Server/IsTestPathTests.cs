using Miller.Server.Resolution;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the centralized <see cref="IsTestPath"/> classifier (M2 §4 exclude_tests) — the <b>language-agnostic
/// fallback</b> half of the cross-language test predicate (the primary is julie's persisted
/// <c>is_test</c>; decision-4). The rules are filename/segment conventions that hold across go/python/csharp/
/// java/kotlin/ts/js/ruby/rust/…, NOT a per-language switch. Deliberately lossy (a <c>fixtures/</c> dir
/// counts), so the tests cover the lossy edges AND the substring traps (<c>fastest</c>/<c>contest</c>/
/// <c>latest</c>/<c>attestation</c>) that must NOT match. Parameterized to avoid copy-paste tests.
/// </summary>
public sealed class IsTestPathTests
{
    [Theory]
    // directory-segment markers
    [InlineData("src/auth/test/AuthHelper.cs")]
    [InlineData("tests/Foo.cs")]
    [InlineData("app/__tests__/widget.ts")]
    [InlineData("pkg/spec/parser_spec.rb")]
    [InlineData("module/specs/thing.js")]
    [InlineData("internal/testdata/sample.go")]
    [InlineData("project/fixtures/data.py")]
    // filename markers
    [InlineData("src/widget.test.ts")]
    [InlineData("src/widget.spec.tsx")]
    [InlineData("src/widget.tests.js")]
    [InlineData("src/AuthServiceTest.cs")]
    [InlineData("src/AuthServiceTests.cs")]
    [InlineData("pkg/server_test.go")]
    [InlineData("pkg/parser_test.py")]
    [InlineData("pkg/test_parser.py")]
    [InlineData("src/AuthTest.java")]
    [InlineData("src/AuthTest.kt")]
    [InlineData("lib/calculator_spec.rb")]   // _spec boundary (ruby/rspec)
    [InlineData("src/ParserSpec.scala")]     // PascalCase Spec suffix (scalatest)
    [InlineData("test/widget_test.dart")]    // _test boundary (dart)
    // Windows-style separators must classify identically (path is normalized).
    [InlineData(@"src\auth\tests\AuthHelper.cs")]
    public void IsTestPath_True_ForTestPaths(string path)
    {
        Assert.True(IsTestPath.Check(path), $"expected '{path}' to be classified as a test path");
    }

    [Theory]
    [InlineData("src/auth/UserService.cs")]
    [InlineData("auth/token.ts")]
    [InlineData("core/math.rs")]
    [InlineData("http/Server.go")]
    // "latest" contains "test" as a substring but is NOT a test segment — must not false-positive.
    [InlineData("src/latest/Build.cs")]
    // "contest" / "attestation" substrings must not trip the segment match.
    [InlineData("src/contest/Entry.cs")]
    [InlineData("src/attestation/Verify.cs")]
    // a file literally named "manifest.json" ends in "est" — must not match.
    [InlineData("config/manifest.json")]
    // filename STEMS that contain "test" as a substring but at no word boundary — must not match.
    [InlineData("src/fastest.cs")]
    [InlineData("lib/contest.py")]
    [InlineData("app/latest.ts")]
    [InlineData("pkg/attestation.go")]
    [InlineData("core/greatest.rs")]
    public void IsTestPath_False_ForProductionPaths(string path)
    {
        Assert.False(IsTestPath.Check(path), $"expected '{path}' to be classified as production code");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTestPath_NullOrEmpty_IsFalse(string? path)
    {
        Assert.False(IsTestPath.Check(path));
    }
}
