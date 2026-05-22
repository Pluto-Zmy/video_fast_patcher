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
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "copy";

    [JsonPropertyName("container")]
    public string Container { get; set; } = "mp4";
}

public sealed class SegmentConfig : INotifyPropertyChanged
{
    private string _start = "";
    private string _end = "";
    private string _patch = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("start")]
    public string Start
    {
        get => _start;
        set => SetField(ref _start, value);
    }

    [JsonPropertyName("end")]
    public string End
    {
        get => _end;
        set => SetField(ref _end, value);
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
