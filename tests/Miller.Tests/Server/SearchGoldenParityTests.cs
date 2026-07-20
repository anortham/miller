using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Byte-for-byte golden corpus for the symbol search route. Every case pins the EXACT rendered string the
/// current implementation produces for a representative query shape, in both compact and JSON form, against
/// the synthesized julie artifact fixture. This is the hard gate for the P2/P3 semantic work: the typed
/// candidate seam and any later fusion arm must leave lexical-only output byte-identical.
/// </summary>
public sealed class SearchGoldenParityTests
{
    public sealed record GoldenCase(
        string Name,
        string Query,
        SearchToolMode Mode,
        int Limit,
        bool Json,
        string Expected,
        int ExpectedCount,
        bool? ExcludeTests = null,
        string? FilePattern = null,
        string? Language = null,
        string? CompactBanner = null,
        bool WithDocLookup = false);

    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (GoldenCase c in Cases)
                data.Add(c.Name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SymbolRoute_RendersGoldenOutput(string caseName)
    {
        GoldenCase golden = Cases.Single(c => c.Name == caseName);
        using var fixture = JulieDbFixture.CreateDefault();
        MillerRepositoryIndex index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fixture.DbPath));

        SearchRouteExecutionResult result = SearchRouteExecutor.RunSymbols(
            index,
            RouteFor(golden.Mode),
            new SearchRouteExecutionRequest(
                Query: golden.Query,
                Limit: golden.Limit,
                Json: golden.Json,
                ExcludeTests: golden.ExcludeTests,
                CompactBanner: golden.CompactBanner,
                FilePattern: golden.FilePattern,
                Language: golden.Language,
                HasDocLookup: golden.WithDocLookup
                    ? ids => ids.ToHashSet(StringComparer.Ordinal)
                    : null));

        Assert.Equal(golden.Expected, result.Output);
        Assert.Equal(golden.ExpectedCount, result.Count);
    }

    [Fact]
    public void GoldenCorpus_CoversCompactAndJsonAcrossQueryShapes()
    {
        Assert.True(Cases.Count >= 12, $"Golden corpus must cover at least 12 query shapes, has {Cases.Count}.");
        Assert.Contains(Cases, c => c.Json);
        Assert.Contains(Cases, c => !c.Json);
        Assert.Contains(Cases, c => c.ExpectedCount == 0);
        Assert.Contains(Cases, c => c.Mode == SearchToolMode.File);
        Assert.Contains(Cases, c => c.FilePattern is not null);
        Assert.Contains(Cases, c => c.Language is not null);
    }

    private static SearchRoute RouteFor(SearchToolMode mode) => mode switch
    {
        SearchToolMode.File => SearchRoutePlanner.Plan("file", regions: null),
        SearchToolMode.Symbol => SearchRoutePlanner.Plan("symbol", regions: null),
        SearchToolMode.Text => SearchRoutePlanner.Plan("text", regions: null),
        _ => SearchRoutePlanner.Plan("auto", regions: null),
    };

    public static IReadOnlyList<GoldenCase> Cases { get; } = BuildCases();

    private static IReadOnlyList<GoldenCase> BuildCases() =>
    [
        new(
            "symbol-exact-compact",
            "GetUser", SearchToolMode.Symbol, 6, false,
            "Definition found: GetUser\n  auth/UserService.cs:5 (method)\n  public User GetUser(int id)\n\nOther matches:\n\nhttp/Server.go:25 (method)\n  func (s *Server) getHTTPResponseCode() int\n\nauth/UserService.cs:\n  :1 (class)\n    public class UserService\n  :12 (method)\nnext: inspect target=\"GetUser\" depth=overview",
            4),
        new(
            "symbol-exact-json",
            "GetUser", SearchToolMode.Symbol, 6, true,
            "[{\"name\":\"GetUser\",\"kind\":\"method\",\"file\":\"auth/UserService.cs\",\"line\":5,\"signature\":\"public User GetUser(int id)\",\"score\":12.444753393701339,\"symbol_id\":\"b2c3d4e5f6001122334455667788990a\"},{\"name\":\"getHTTPResponseCode\",\"kind\":\"method\",\"file\":\"http/Server.go\",\"line\":25,\"signature\":\"func (s *Server) getHTTPResponseCode() int\",\"score\":1.8953700113641159,\"symbol_id\":\"4455667788990a1b2c3d4e5f60112233\"},{\"name\":\"UserService\",\"kind\":\"class\",\"file\":\"auth/UserService.cs\",\"line\":1,\"signature\":\"public class UserService\",\"score\":1.8197656511737126,\"symbol_id\":\"a1b2c3d4e5f600112233445566778899\"},{\"name\":\"DeleteUser\",\"kind\":\"method\",\"file\":\"auth/UserService.cs\",\"line\":12,\"signature\":null,\"score\":1.7740173526805187,\"symbol_id\":\"c3d4e5f6001122334455667788990a1b\"}]",
            4),
        new(
            "symbol-auto-compact",
            "ServeHTTP", SearchToolMode.Auto, 6, false,
            "Definition found: ServeHTTP\n  http/Server.go:40 (method)\n  func (s *Server) ServeHTTP(w ResponseWriter, r *Request)\n\nOther matches:\n\nhttp/Server.go:25 (method)\n  func (s *Server) getHTTPResponseCode() int\nnext: inspect target=\"ServeHTTP\" depth=overview",
            2),
        new(
            "phrase-text-compact",
            "get user by id", SearchToolMode.Text, 6, false,
            "auth/UserService.cs:\n  :5 GetUser method  public User GetUser(int id)\n  :1 UserService class  public class UserService\n  :12 DeleteUser method\nhttp/Server.go:\n  :25 getHTTPResponseCode method  func (s *Server) getHTTPResponseCode() int",
            4),
        new(
            "phrase-text-json",
            "get user by id", SearchToolMode.Text, 6, true,
            "[{\"name\":\"GetUser\",\"kind\":\"method\",\"file\":\"auth/UserService.cs\",\"line\":5,\"signature\":\"public User GetUser(int id)\",\"score\":6.098686861112545,\"symbol_id\":\"b2c3d4e5f6001122334455667788990a\"},{\"name\":\"getHTTPResponseCode\",\"kind\":\"method\",\"file\":\"http/Server.go\",\"line\":25,\"signature\":\"func (s *Server) getHTTPResponseCode() int\",\"score\":1.8953700113641159,\"symbol_id\":\"4455667788990a1b2c3d4e5f60112233\"},{\"name\":\"UserService\",\"kind\":\"class\",\"file\":\"auth/UserService.cs\",\"line\":1,\"signature\":\"public class UserService\",\"score\":1.8197656511737126,\"symbol_id\":\"a1b2c3d4e5f600112233445566778899\"},{\"name\":\"DeleteUser\",\"kind\":\"method\",\"file\":\"auth/UserService.cs\",\"line\":12,\"signature\":null,\"score\":1.7740173526805187,\"symbol_id\":\"c3d4e5f6001122334455667788990a1b\"}]",
            4),
        new(
            "file-mode-compact",
            "auth/UserService.cs", SearchToolMode.File, 6, false,
            "File match: auth/UserService.cs\n  :1 UserService class\n  :5 GetUser method\n  :12 DeleteUser method",
            3),
        new(
            "file-mode-json",
            "auth/UserService.cs", SearchToolMode.File, 6, true,
            "[{\"name\":\"UserService\",\"kind\":\"class\",\"file\":\"auth/UserService.cs\",\"line\":1,\"signature\":\"public class UserService\",\"score\":1,\"symbol_id\":\"a1b2c3d4e5f600112233445566778899\"},{\"name\":\"GetUser\",\"kind\":\"method\",\"file\":\"auth/UserService.cs\",\"line\":5,\"signature\":\"public User GetUser(int id)\",\"score\":1,\"symbol_id\":\"b2c3d4e5f6001122334455667788990a\"},{\"name\":\"DeleteUser\",\"kind\":\"method\",\"file\":\"auth/UserService.cs\",\"line\":12,\"signature\":null,\"score\":1,\"symbol_id\":\"c3d4e5f6001122334455667788990a1b\"}]",
            3),
        new(
            "filtered-file-pattern-compact",
            "user", SearchToolMode.Symbol, 6, false,
            "auth/UserService.cs:\n  :5 GetUser method  public User GetUser(int id)\n  :1 UserService class  public class UserService\n  :12 DeleteUser method\nnext: inspect target=\"GetUser\" depth=overview",
            3, FilePattern: "auth/**"),
        new(
            "filtered-language-compact",
            "dot", SearchToolMode.Symbol, 6, false,
            "Definition found: dot\n  core/math.rs:20 (method)\n  pub fn dot(&self, other: &Vector512) -> f32\nnext: inspect target=\"dot\" depth=overview",
            1, Language: "rust"),
        new(
            "filtered-miss-outside-scope",
            "GetUser", SearchToolMode.Symbol, 6, false,
            "No results within file_pattern=core/**.\nOutside scope:\nGetUser  method  auth/UserService.cs:5  public User GetUser(int id)\ngetHTTPResponseCode  method  http/Server.go:25  func (s *Server) getHTTPResponseCode() int\nUserService  class  auth/UserService.cs:1  public class UserService",
            0, FilePattern: "core/**"),
        new(
            "limit-edge-one-compact",
            "user", SearchToolMode.Symbol, 1, false,
            "GetUser  method  auth/UserService.cs:5  public User GetUser(int id)\n… 2 more (raise limit)\nnext: inspect target=\"GetUser\" depth=overview",
            1),
        new(
            "limit-edge-one-json",
            "user", SearchToolMode.Symbol, 1, true,
            "[{\"name\":\"GetUser\",\"kind\":\"method\",\"file\":\"auth/UserService.cs\",\"line\":5,\"signature\":\"public User GetUser(int id)\",\"score\":1.9723546964584648,\"symbol_id\":\"b2c3d4e5f6001122334455667788990a\"}]",
            1),
        new(
            "empty-result-compact",
            "zzzznosuchsymbol", SearchToolMode.Symbol, 6, false,
            "No results. No indexed symbol name matches 'zzzznosuchsymbol'.\nNext: search query=\"zzzznosuchsymbol\" mode=source — find it as source-body text",
            0),
        new(
            "empty-result-json",
            "zzzznosuchsymbol", SearchToolMode.Symbol, 6, true,
            "[]",
            0),
        new(
            "has-doc-annotation-compact",
            "GetUser", SearchToolMode.Symbol, 6, false,
            "Definition found: GetUser\n  auth/UserService.cs:5 (method) has_doc\n  public User GetUser(int id)\n\nOther matches:\n\nhttp/Server.go:25 (method) has_doc\n  func (s *Server) getHTTPResponseCode() int\n\nauth/UserService.cs:\n  :1 (class) has_doc\n    public class UserService\n  :12 (method) has_doc\nnext: inspect target=\"GetUser\" depth=overview",
            4, WithDocLookup: true),
        new(
            "banner-prefixed-compact",
            "ServeHTTP", SearchToolMode.Symbol, 6, false,
            "workspace: fixture\nDefinition found: ServeHTTP\n  http/Server.go:40 (method)\n  func (s *Server) ServeHTTP(w ResponseWriter, r *Request)\n\nOther matches:\n\nhttp/Server.go:25 (method)\n  func (s *Server) getHTTPResponseCode() int\nnext: inspect target=\"ServeHTTP\" depth=overview",
            2, CompactBanner: "workspace: fixture"),
        new(
            "exclude-tests-forced-compact",
            "user", SearchToolMode.Symbol, 6, false,
            "auth/UserService.cs:\n  :5 GetUser method  public User GetUser(int id)\n  :1 UserService class  public class UserService\n  :12 DeleteUser method\nnext: inspect target=\"GetUser\" depth=overview",
            3, ExcludeTests: true),
        new(
            "file-mode-empty-compact",
            "no/such/path.cs", SearchToolMode.File, 6, false,
            "No indexed file matches 'no/such/path.cs'. Indexed paths match on fragments.\nNext: search query=\"path.cs\" mode=file — retry with the basename",
            0),
    ];
}
