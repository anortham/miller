using System.Reflection;

namespace Miller.Server;

/// <summary>
/// Loads the MCP server-level agent instructions (<c>MILLER_AGENT_INSTRUCTIONS.md</c>) embedded in this
/// assembly and exposes them as the string set on <c>McpServerOptions.ServerInstructions</c> (Program.cs). The
/// markdown is the behavioral-adoption guidance the client surfaces to the agent — "search before reading",
/// the per-tool one-liners, and the workflows — mirroring julie's <c>JULIE_AGENT_INSTRUCTIONS.md</c>. Embedding
/// (not a file read) means the instructions ship inside the binary and are available regardless of cwd.
/// </summary>
public static class AgentInstructions
{
    private const string ResourceSuffix = "MILLER_AGENT_INSTRUCTIONS.md";

    /// <summary>
    /// The embedded agent instructions as a single string. Throws <see cref="InvalidOperationException"/> if the
    /// resource is missing — a packaging error (the EmbeddedResource was dropped from the csproj) that should
    /// fail loudly at startup rather than silently shipping a server with no guidance.
    /// </summary>
    public static string Load()
    {
        Assembly assembly = typeof(AgentInstructions).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceSuffix}' not found in {assembly.GetName().Name}. " +
                "It must be declared as <EmbeddedResource> in Miller.Server.csproj.");

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not open embedded resource stream '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
