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

    /// <summary>
    /// Canonical form used only for hashing: compact, and decimals written without trailing zeros so that 600 and
    /// 600.0 (the same measurement) hash alike.
    /// </summary>
    private static JsonSerializerOptions Canonical { get; } = Create(indented: false, canonicalDecimals: true);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> SnapshotHashes = [];

    private sealed class CanonicalDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDecimal();

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value / 1.0000000000000000000000000000m);
    }

    private static JsonSerializerOptions Create(bool indented, bool canonicalDecimals = false)
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
        if (canonicalDecimals) o.Converters.Add(new CanonicalDecimalConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Document is empty.");

    /// <summary>SHA-256 (lower-case hex) of the compact canonical JSON of <paramref name="value"/>.</summary>
    public static string Hash<T>(T value) => Sha256Hex(JsonSerializer.Serialize(value, Canonical));

    /// <summary>
    /// Hash of a snapshot's canonical form: the identity a plan is bound to (handbook §97, A5). Snapshots are
    /// immutable records, so the hash is computed once per snapshot object.
    /// </summary>
    public static string SnapshotHash(Contracts.OptimizationSnapshot snapshot) =>
        SnapshotHashes.GetValue(snapshot, static s => Hash((Contracts.OptimizationSnapshot)s));

    public static string Sha256Hex(string text)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(digest);
    }
}
