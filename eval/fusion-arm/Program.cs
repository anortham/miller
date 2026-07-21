using System.Globalization;

namespace FusionArm;

public static class Program
{
    const string Usage = """
        fusion-arm — offline fused retrieval arm for Miller semantic evaluation (production routing + RRF, no index).

          fuse --queries <queries.jsonl> --lexical <dir> --semantic <dir>
               --k-const <int> --conceptual-ratio <r> --out <results.jsonl> [--forced-hybrid]

        Per-query input files are <query_id>.json under --lexical and --semantic.
        --forced-hybrid bypasses routing and fuses every query under Conceptual weights (identifier diagnostic arm).
        Exit codes: 0 ok, 1 usage/IO error.
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine(Usage);
                return 1;
            }

            return args[0] switch
            {
                "fuse" => RunFuse(Cli.Parse(args.Skip(1))),
                "--help" or "-h" or "help" => Ok(Usage),
                _ => Fail($"unknown verb '{args[0]}'\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException)
        {
            return Fail(ex.Message);
        }
    }

    static int RunFuse(Cli cli)
    {
        var config = new FusionConfig(
            ConceptualRatio: cli.Double("conceptual-ratio"),
            RankConstant: cli.Int("k-const"),
            ForcedHybrid: cli.Flag("forced-hybrid"));

        var options = new FusionRunOptions(
            QueriesPath: cli.Require("queries"),
            LexicalDir: cli.Require("lexical"),
            SemanticDir: cli.Require("semantic"),
            OutPath: cli.Require("out"),
            Config: config);

        FusionRunSummary summary = FusionRunner.Run(options);

        Console.Error.WriteLine(
            $"fusion-arm: {summary.EmittedCount}/{summary.QueryCount} queries emitted, {summary.MissingCount} missing input");
        if (summary.MissingCount > 0)
            Console.Error.WriteLine($"  missing: {string.Join(", ", summary.MissingQueryIds)}");
        Console.Error.WriteLine($"results written to {Path.GetFullPath(options.OutPath)}");

        return 0;
    }

    static int Ok(string message)
    {
        Console.WriteLine(message);
        return 0;
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    sealed class Cli
    {
        static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal) { "forced-hybrid" };

        readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public static Cli Parse(IEnumerable<string> args)
        {
            var cli = new Cli();
            string? pending = null;
            foreach (string arg in args)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (pending is not null)
                        throw new ArgumentException($"--{pending} requires a value");

                    string name = arg[2..];
                    if (KnownFlags.Contains(name))
                        cli._flags.Add(name);
                    else
                        pending = name;
                    continue;
                }

                if (pending is null)
                    throw new ArgumentException($"unexpected argument '{arg}'");
                cli._values[pending] = arg;
                pending = null;
            }

            if (pending is not null)
                throw new ArgumentException($"--{pending} requires a value");
            return cli;
        }

        public string Require(string name) =>
            _values.TryGetValue(name, out string? value)
                ? value
                : throw new ArgumentException($"--{name} is required\n\n{Usage}");

        public int Int(string name) => int.Parse(Require(name), CultureInfo.InvariantCulture);

        public double Double(string name) => double.Parse(Require(name), CultureInfo.InvariantCulture);

        public bool Flag(string name) => _flags.Contains(name);
    }
}
