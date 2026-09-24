using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    [JsonPropertyName("video_codec")]
    public string VideoCodec { get; set; } = VideoCodecs.H264;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = ConcatModes.Fast;

    [JsonPropertyName("container")]
    public string Container { get; set; } = "mp4";
}

internal static class VideoCodecs
{
    public const string H264 = "h264";
    public const string H265 = "hevc";
    public static readonly string[] DisplayNames = ["H.264", "H.265 (HEVC)"];

    public static string Normalize(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" or "h264" or "h.264" => H264,
        "hevc" or "h265" or "h.265" or "h.265 (hevc)" => H265,
        _ => throw new InvalidOperationException($"不支持的视频编码: {codec}"),
    };
}

internal static class ConcatModes
{
    public const string Fast = "快速拼接";
    public const string ReEncode = "重新编码（更慢）";

    private const string LegacyCopy = "copy";
    private const string LegacyEncode = "encode";

    public static readonly string[] DisplayNames = [Fast, ReEncode];

    public static string Normalize(string? mode)
    {
        if (string.Equals(mode, ReEncode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, LegacyEncode, StringComparison.OrdinalIgnoreCase))
        {
            return ReEncode;
        }

        return Fast;
    }

    public static bool IsFast(string? mode)
    {
        var normalized = Normalize(mode);
        return string.Equals(normalized, Fast, StringComparison.Ordinal);
    }
}

public sealed class SegmentConfig : INotifyPropertyChanged
{
    private string _start = "";
    private string _patch = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("start")]
    public string Start
    {
        get => _start;
        set => SetField(ref _start, value);
    }

    [JsonPropertyName("patch")]
    public string Patch
    {
        get => _patch;
        set => SetField(ref _patch, value);
    }

    private void SetField(ref string field, string? value, [CallerMemberName] string? propertyName = null)
    {
        var normalized = value ?? "";
        if (string.Equals(field, normalized, StringComparison.Ordinal))
        {
            return;
        }

        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
