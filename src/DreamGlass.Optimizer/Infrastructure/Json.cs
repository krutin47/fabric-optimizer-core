using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DreamGlass.Optimizer.Infrastructure;

/// <summary>Shared JSON settings for contracts (camelCase, string enums) plus canonical hashing.</summary>
public static class OptimizerJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Document is empty.");

    /// <summary>SHA-256 (lower-case hex) of the compact canonical JSON of <paramref name="value"/>.</summary>
    public static string Hash<T>(T value) => Sha256Hex(Serialize(value, indented: false));

    public static string Sha256Hex(string text)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(digest);
    }
}
