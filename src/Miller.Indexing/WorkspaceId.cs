using System.Security.Cryptography;
using System.Text;

namespace Miller.Indexing;

public static class WorkspaceId
{
    private const int DisplayHashPrefixLength = 12;

    public static string FromCanonicalRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
    }

    public static string Display(string root, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length < DisplayHashPrefixLength)
            throw new ArgumentException(
                $"Workspace id must be at least {DisplayHashPrefixLength} characters long.", nameof(id));

        string trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmedRoot);
        string sanitizedLeaf = SanitizeLeaf(string.IsNullOrWhiteSpace(leaf) ? "workspace" : leaf);
        return $"{sanitizedLeaf}-{id[..DisplayHashPrefixLength]}";
    }

    private static string SanitizeLeaf(string leaf)
    {
        var sb = new StringBuilder(leaf.Length);
        bool previousWasSeparator = false;
        foreach (char ch in leaf)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')
            {
                sb.Append(ch);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                sb.Append('-');
                previousWasSeparator = true;
            }
        }

        string sanitized = sb.ToString().Trim('-');
        return sanitized.Length == 0 ? "workspace" : sanitized;
    }
}
