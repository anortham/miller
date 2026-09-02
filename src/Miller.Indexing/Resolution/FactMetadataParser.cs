using System.Text.Json;
using Miller.Core.Resolution;

namespace Miller.Indexing.Resolution;

internal static class FactMetadataParser
{
    internal static ImportMetadata? ParseImport(string? json)
    {
        if (string.IsNullOrEmpty(json) || !TryParseObject(json, out JsonElement root))
            return null;

        return new ImportMetadata(
            Alias: ReadString(root, "alias"),
            LocalName: ReadString(root, "local_name"),
            ImportedName: ReadString(root, "imported_name"),
            Imported: ReadString(root, "imported"),
            ImportedNameCamel: ReadString(root, "importedName"),
            Source: ReadString(root, "source"),
            IsTypeOnly: ReadBool(root, "isTypeOnly"),
            IsTypeOnlySnake: ReadBool(root, "is_type_only"),
            IsDefault: ReadBool(root, "isDefault"),
            IsDefaultSnake: ReadBool(root, "is_default"),
            IsNamespace: ReadBool(root, "isNamespace"),
            IsNamespaceSnake: ReadBool(root, "is_namespace"));
    }

    internal static string? IsStaticRaw(string? json)
    {
        if (string.IsNullOrEmpty(json) || !TryParseObject(json, out JsonElement root))
            return null;
        if (!root.TryGetProperty("isStatic", out JsonElement property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => property.GetString(),
            _ => null,
        };
    }

    internal static (string? Receiver, string? Qualifier, string? ReceiverType) IdentifierReceivers(string? json)
    {
        if (string.IsNullOrEmpty(json) || !TryParseObject(json, out JsonElement root))
            return (null, null, null);
        return (ReadString(root, "receiver"), ReadString(root, "receiver_qualifier"), ReadString(root, "receiver_type"));
    }

    internal static string? ReceiverType(string? json)
    {
        if (string.IsNullOrEmpty(json) || !TryParseObject(json, out JsonElement root))
            return null;
        return ReadString(root, "receiver_type");
    }

    private static bool TryParseObject(string json, out JsonElement root)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                root = default;
                return false;
            }

            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            return null;
        string? value = property.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement property))
            return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(property.GetString(), "true", StringComparison.Ordinal),
            _ => false,
        };
    }
}
