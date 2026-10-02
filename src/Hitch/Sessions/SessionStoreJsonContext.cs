using System.Text.Json.Serialization;

namespace Hitch.Sessions;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionRecord))]
[JsonSerializable(typeof(SessionState))]
internal partial class SessionStoreJsonContext : JsonSerializerContext;
