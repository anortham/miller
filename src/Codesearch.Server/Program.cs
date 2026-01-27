using uniffi.codesearch_ffi;

namespace Codesearch.Server;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Codesearch Server");

        var tempPath = Path.Combine(Path.GetTempPath(), "codesearch_demo.lance");

        try
        {
            using var engine = new CodeSearchEngine(tempPath);
            Console.WriteLine($"Engine created at: {engine.DbPath()}");
            Console.WriteLine($"Health check: {engine.HealthCheck()}");
        }
        catch (CodeSearchException ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }
}
