using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Miller.Testing;

internal static class CtStableIds
{
    public static string StableId(string @namespace, params object?[] parts)
    {
        string normalized = string.Join("\x1f", parts.Select(PartToString));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string hex = Convert.ToHexString(digest).ToLowerInvariant()[..24];
        return $"{@namespace}:{hex}";
    }

    private static string PartToString(object? part) =>
        part switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture) ?? "",
            _ => part.ToString() ?? "",
        };
}
