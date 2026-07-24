using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageTracker.Contracts;

/// <summary>
/// Source-generated serialization metadata for the CLI↔daemon contract types, so the CLI can be
/// published trimmed/single-file (and AOT-ready) without reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CommandEnvelope))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(DaemonConfig))]
[JsonSerializable(typeof(CompressRequest))]
[JsonSerializable(typeof(CompressResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ContractsJsonContext : JsonSerializerContext
{
}
