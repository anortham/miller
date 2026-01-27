using uniffi.codesearch_ffi;

Console.WriteLine("Codesearch Server Demo");
Console.WriteLine("======================");

var tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_demo_{Guid.NewGuid():N}");
var dbPath = Path.Combine(tempDir, "demo.lance");

try
{
    using var engine = new CodeSearchEngine(dbPath);
    Console.WriteLine($"Created engine at: {engine.DbPath()}");
    Console.WriteLine($"Health check: {engine.HealthCheck()}");

    // Add sample symbols
    var symbols = new List<SymbolInput>
    {
        new("fn_1", "authenticate_user", "function", "rust", "src/auth.rs",
            "pub fn authenticate_user(token: &str) -> Result<User>", "Authenticates a user",
            10, 25, null),
        new("fn_2", "validate_token", "function", "rust", "src/auth.rs",
            "fn validate_token(token: &str) -> bool", "Validates JWT token",
            30, 40, null),
        new("fn_3", "hash_password", "function", "rust", "src/crypto.rs",
            "pub fn hash_password(password: &str) -> String", null,
            5, 15, null),
        new("imp_1", "bcrypt", "import", "rust", "src/crypto.rs",
            null, null, 1, 1, null),
    };

    var vectors = symbols.Select((_, i) => CreateMockVector(i * 0.1f)).ToList();
    Console.WriteLine($"Added {engine.AddSymbols(symbols, vectors)} symbols");

    engine.CreateFtsIndex();
    Console.WriteLine("Created FTS index");

    // Demo searches
    Console.WriteLine("\n--- Text Search: 'authenticate' ---");
    foreach (var r in engine.SearchTextBoosted("authenticate", 5))
        Console.WriteLine($"  [{r.score:F3}] {r.kind}: {r.name}");

    Console.WriteLine("\n--- Hybrid Search: 'password' ---");
    foreach (var r in engine.SearchHybridBoosted("password", CreateMockVector(0.2f), 5))
        Console.WriteLine($"  [{r.score:F3}] {r.kind}: {r.name}");

    Console.WriteLine($"\nTotal symbols: {engine.SymbolCount()}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Environment.Exit(1);
}
finally
{
    if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, recursive: true);
}

static List<float> CreateMockVector(float seed)
{
    var v = Enumerable.Range(0, 768).Select(i => (float)Math.Sin(seed + i * 0.001f)).ToList();
    var norm = (float)Math.Sqrt(v.Sum(x => x * x));
    return v.Select(x => x / norm).ToList();
}
