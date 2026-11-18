using System.Text.Json.Serialization;
using PerformanceViewer.Components;

namespace PerformanceViewer.Native;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceWindow[]))]
internal partial class NativeJson : JsonSerializerContext { }

