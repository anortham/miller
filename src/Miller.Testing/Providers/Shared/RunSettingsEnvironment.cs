using System.Xml;
using System.Xml.Linq;

namespace Miller.Testing;

/// <summary>
/// The environment variables a .NET test project declares in its VSTest run settings.
///
/// <para><b>The defect this exists for.</b> CT does not run <c>dotnet test</c> for an xunit v3 project. It
/// runs the built test EXECUTABLE directly, which is faster and gives the JSON reporter, but it also means
/// nothing reads the project's <c>RunSettingsFilePath</c> — that property is honored by MSBuild and VSTest,
/// not by the test binary. A project that declares an environment block therefore ran under CT with those
/// variables MISSING, and every test that depends on one failed.</para>
///
/// <para>Measured on Miller's own suite: four test classes run from the built executable failed 203 of 341
/// with the block missing and 0 of 341 with its single variable set. Roughly 292 of the 366 tests CT called
/// red were this one cause. It is not specific to Miller — it breaks ANY repo whose test project declares a
/// run-settings environment block.</para>
///
/// <para><b>Scope.</b> Only <c>RunSettings/RunConfiguration/EnvironmentVariables</c> is read. The other run
/// settings knobs (parallelism, result directory, timeouts) are VSTest host concerns that CT already decides
/// for itself, and honoring half of them would be worse than honoring none.</para>
///
/// <para><b>Failure is silent and empty</b>, matching
/// <see cref="ContinuousTestProjectInventory.ParseDefaultFilterExclusions"/>: a missing, unreadable, or
/// malformed settings file yields no variables rather than failing the run. A run settings problem must not
/// stop CT from testing the project.</para>
/// </summary>
public static class RunSettingsEnvironment
{
    private const string PropertyOpenTag = "<RunSettingsFilePath>";
    private const string PropertyCloseTag = "</RunSettingsFilePath>";

    /// <summary>The MSBuild property a project uses to name its own directory.</summary>
    private const string ProjectDirectoryMacro = "$(MSBuildProjectDirectory)";

    /// <summary>
    /// The variables declared by <paramref name="projectPath"/>'s run settings, or an empty map when the
    /// project declares none. Never throws for a bad project or a bad settings file.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return EmptyMap;

        string? settingsPath = TryResolveSettingsPath(projectPath);
        return settingsPath is null ? EmptyMap : Read(settingsPath);
    }

    /// <summary>
    /// The absolute run-settings path a project declares, or <c>null</c> when it declares none or the file
    /// does not exist. A relative value resolves against the project's own directory, which is also what
    /// <c>$(MSBuildProjectDirectory)</c> expands to.
    /// </summary>
    internal static string? TryResolveSettingsPath(string projectPath)
    {
        string projectDirectory;
        string projectText;
        try
        {
            projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty;
            if (!File.Exists(projectPath))
                return null;
            projectText = File.ReadAllText(projectPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        string? declared = ExtractProperty(projectText);
        if (declared is null)
            return null;

        string expanded = declared
            .Replace(ProjectDirectoryMacro, projectDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .Trim();
        if (expanded.Length == 0 || expanded.Contains("$(", StringComparison.Ordinal))
            return null; // An unexpanded MSBuild property cannot be resolved without evaluating the project.

        try
        {
            string full = Path.IsPathRooted(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(projectDirectory, expanded));
            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The raw <c>RunSettingsFilePath</c> value in a project's text, or <c>null</c>.</summary>
    internal static string? ExtractProperty(string projectText)
    {
        ArgumentNullException.ThrowIfNull(projectText);
        int start = projectText.IndexOf(PropertyOpenTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;
        start += PropertyOpenTag.Length;

        int end = projectText.IndexOf(PropertyCloseTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return null;

        string value = projectText[start..end].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// The environment block inside a run-settings file. An element's LOCAL name is the variable name and
    /// its text is the value, which is the shape VSTest reads.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Read(string settingsPath)
    {
        try
        {
            using FileStream stream = File.OpenRead(settingsPath);
            using XmlReader reader = XmlReader.Create(stream, SafeXmlReaderSettings());
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            XElement? root = document.Root;
            if (root is null)
                return EmptyMap;

            XElement? block = root
                .Elements()
                .FirstOrDefault(static element =>
                    string.Equals(element.Name.LocalName, "RunConfiguration", StringComparison.OrdinalIgnoreCase))
                ?.Elements()
                .FirstOrDefault(static element =>
                    string.Equals(element.Name.LocalName, "EnvironmentVariables", StringComparison.OrdinalIgnoreCase));
            if (block is null)
                return EmptyMap;

            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XElement variable in block.Elements())
            {
                string name = variable.Name.LocalName;
                if (name.Length == 0)
                    continue;

                // Last one wins, the same way a repeated key in an environment block does.
                variables[name] = variable.Value;
            }

            return variables;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return EmptyMap;
        }
    }

    private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.Ordinal);

    private static XmlReaderSettings SafeXmlReaderSettings() =>
        new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
}
