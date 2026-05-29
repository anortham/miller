using Codesearch.Spike;

// Usage:
//   dotnet run -c Release                      -> contract check + benchmarks (default db /tmp/cs-spike.sqlite)
//   dotnet run -c Release -- contract <db>     -> contract check only
//   dotnet run -c Release -- bench <db>        -> benchmarks only
string mode = args.Length > 0 ? args[0] : "all";
string db = args.Length > 1 ? args[1] : "/tmp/cs-spike.sqlite";

switch (mode)
{
    case "contract":
        return ContractCheck.Run(db) ? 0 : 1;
    case "bench":
        Bench.Run(db);
        return 0;
    case "embed":
        EmbedBench.Run(db, args.Length > 2 ? int.Parse(args[2]) : 1500);
        return 0;
    case "search":
        SearchBench.Run(db);
        return 0;
    default:
        bool ok = ContractCheck.Run(db);
        Console.WriteLine();
        Bench.Run(db);
        return ok ? 0 : 1;
}
