using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcceptCrypto.Sdk.Json;

/// <summary>JSON settings of the AcceptCrypto.NET wire format: snake_case, amounts as strings, ISO 8601 timestamps.</summary>
public static class AcceptCryptoJson
{
    /// <summary>Serializer options used by the client and the webhook parser (read-only).</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>
/// snake_case enum converter that maps values it does not know to <c>default</c> (the <c>Unknown</c> member) instead of
/// failing, so a newer service version adding a status does not break an older SDK.
/// </summary>
internal sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<string, TEnum> ByName = Enum.GetValues<TEnum>()
        .Where(v => !EqualityComparer<TEnum>.Default.Equals(v, default))
        .ToFrozenDictionary(v => JsonNamingPolicy.SnakeCaseLower.ConvertName(v.ToString()), StringComparer.Ordinal);

    private static readonly FrozenDictionary<TEnum, string> ByValue = ByName.ToFrozenDictionary(p => p.Value, p => p.Key);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string for {typeof(TEnum).Name}.");
        }

        return ByName.GetValueOrDefault(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ByValue.TryGetValue(value, out var name)
            ? name
            : throw new JsonException($"{typeof(TEnum).Name}.{value} cannot be sent to the service."));

    /// <summary>The wire name of a value (e.g. <c>paid_late</c>).</summary>
    public static string NameOf(TEnum value) => ByValue.TryGetValue(value, out var name)
        ? name
        : throw new ArgumentOutOfRangeException(nameof(value), value, $"{typeof(TEnum).Name}.{value} has no wire name.");
}
