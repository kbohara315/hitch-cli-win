using System.Text.Json.Serialization;

namespace Hitch.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(PipeResponse))]
internal partial class HitchJsonContext : JsonSerializerContext;
