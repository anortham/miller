namespace Miller.SearchQuality;

internal static class Program
{
    public static int Main(string[] args) => SearchQualityCli.Run(args, Console.Out, Console.Error);
}
