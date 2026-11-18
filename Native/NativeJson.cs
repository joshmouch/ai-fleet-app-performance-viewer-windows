using System.Text.Json.Serialization;
using PerformanceViewer.Components;

namespace PerformanceViewer.Native;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourceWindow[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(StoredFileWindow))]
[JsonSerializable(typeof(MetricTimeline))]
internal partial class NativeJson : JsonSerializerContext { }

