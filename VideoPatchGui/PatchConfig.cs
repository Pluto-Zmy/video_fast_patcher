using System.Text.Json.Serialization;

namespace VideoPatchGui;

public sealed class PatchConfig
{
    [JsonPropertyName("input")]
    public string Input { get; set; } = "";

    [JsonPropertyName("output")]
    public string Output { get; set; } = "";

    [JsonPropertyName("workdir")]
    public string Workdir { get; set; } = "_patch_tmp";

    [JsonPropertyName("ffmpeg")]
    public FfmpegConfig Ffmpeg { get; set; } = new();

    [JsonPropertyName("concat")]
    public ConcatConfig Concat { get; set; } = new();

    [JsonPropertyName("segments")]
    public List<SegmentConfig> Segments { get; set; } = new();
}

public sealed class FfmpegConfig
{
    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; set; } = true;

    [JsonPropertyName("loglevel")]
    public string? Loglevel { get; set; } = "warning";
}

public sealed class ConcatConfig
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "copy";

    [JsonPropertyName("container")]
    public string Container { get; set; } = "mp4";
}

public sealed class SegmentConfig
{
    [JsonPropertyName("start")]
    public string Start { get; set; } = "";

    [JsonPropertyName("end")]
    public string End { get; set; } = "";

    [JsonPropertyName("patch")]
    public string Patch { get; set; } = "";
}
