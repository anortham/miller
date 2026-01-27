namespace Codesearch.Server;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Codesearch Server");
        Console.WriteLine($"Interop status: {Interop.Placeholder.Message}");
    }
}
