using System.Text.Json.Serialization;

namespace WGL2Bridge.Config;

/// <summary>
/// Source-generated JSON context for all configuration deserialization. WGL2Bridge is published
/// with NativeAOT, so the reflection-based serializer is never used; everything flows through this
/// compile-time-generated context.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BridgeConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
