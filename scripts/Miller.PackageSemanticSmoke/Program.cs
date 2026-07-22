using Miller.Indexing.Semantic;
using Miller.PackageSemanticSmoke;

if (args.Length == 1 && args[0] == "--print-model-id")
{
    Console.WriteLine(SemanticEncoderSelection.Active.ModelId);
    return 0;
}

if (args.Length != 2 || args[0] != "--package-root")
{
    Console.Error.WriteLine(
        "usage: miller-package-semantic-smoke --package-root <artifacts/publish/target>");
    return 64;
}

SemanticEncoderPin pin = SemanticEncoderSelection.Active;
PackageSemanticPayloadPaths paths = PackageSemanticPayloadPaths.FromPackageRoot(args[1]);
var runner = new PackageSemanticSmokeRunner(
    (executable, selected) => new ProcessPackageSemanticSession(executable, selected),
    new SqliteVecSelfQuery());
PackageSemanticSmokeResult result = await runner.RunAsync(paths, pin);
TextWriter output = result.Succeeded ? Console.Out : Console.Error;
output.WriteLine($"[{(result.Succeeded ? "PASS" : "FAIL")}] {result.Stage}: {result.Message}");
return result.Succeeded ? 0 : 1;
