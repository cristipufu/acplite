using System.Text.Json;

namespace Acplite.Sdk.Protocol;

/// <summary>
/// Provides allocation-conscious, reflection-free construction and safe property extraction
/// for <see cref="JsonElement"/> payloads across ACP and provider protocols.
/// </summary>
public static class JsonElementBuilder
{
    public static JsonElement Build(Action<Utf8JsonWriter> writeAction)
    {
        ArgumentNullException.ThrowIfNull(writeAction);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writeAction(writer);
        }

        using var document = JsonDocument.Parse(stream.GetBuffer().AsMemory(0, (int)stream.Length));
        return document.RootElement.Clone();
    }

    public static JsonElement EmptyObject { get; } = Build(static writer =>
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    });

    public static string? GetOptionalString(this JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    public static string GetStringOrDefault(this JsonElement element, string propertyName, string defaultValue = "")
        => element.GetOptionalString(propertyName) ?? defaultValue;

    public static bool GetBooleanOrDefault(this JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    public static int? GetOptionalInt32(this JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt32(out var value))
        {
            return value;
        }
        return null;
    }

    public static long? GetOptionalInt64(this JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt64(out var value))
        {
            return value;
        }
        return null;
    }

    public static double? GetOptionalDouble(this JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetDouble(out var value))
        {
            return value;
        }
        return null;
    }

    public static decimal? GetOptionalDecimal(this JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetDecimal(out var value))
        {
            return value;
        }
        return null;
    }

    public static bool TryGetObject(this JsonElement element, string propertyName, out JsonElement objectElement)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Object)
        {
            objectElement = prop;
            return true;
        }

        objectElement = default;
        return false;
    }

    public static bool TryGetArray(this JsonElement element, string propertyName, out JsonElement arrayElement)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Array)
        {
            arrayElement = prop;
            return true;
        }

        arrayElement = default;
        return false;
    }
}
