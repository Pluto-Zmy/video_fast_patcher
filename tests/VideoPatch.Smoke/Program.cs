using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using VideoPatchGui;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var ffmpeg = Path.Combine(root, "VideoPatchGui/tools/ffmpeg/ffmpeg.exe");
var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
var dir = Path.Combine(root, "tests/VideoPatch.Smoke/bin/media", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
async Task<string> Run(string exe, params string[] arguments)
{
    var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var error = await stderr;
    Check(process.ExitCode == 0, error);
    return await stdout;
}
Check(JsonSerializer.Deserialize<PatchConfig>("{}")!.Concat.VideoCodec == "h264", "Legacy config default");
foreach (var alias in new[] { "hevc", "h265", "H.265", "H.265 (HEVC)" })
    Check(VideoCodecs.Normalize(alias) == "hevc", "HEVC alias");
var roundtrip = JsonSerializer.Deserialize<PatchConfig>(JsonSerializer.Serialize(new PatchConfig { Concat = new() { VideoCodec = "hevc" } }));
Check(roundtrip!.Concat.VideoCodec == "hevc", "Config roundtrip");
var legacy = JsonSerializer.Deserialize<SegmentConfig>("""{"start":"00:00:01","end":"00:00:02","patch":"patch.mp4"}""");
Check(legacy!.Start == "00:00:01" && !JsonSerializer.Serialize(legacy).Contains("end"), "Legacy end ignored");


// Execute the actual CPU profile and output argument builder, even on machines with a working GPU.
var type = typeof(VideoPatcher);
var cpu = type.GetField("CpuHevcEncoder", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
var build = type.GetMethod("BuildReEncodeConcatArgs", BindingFlags.Static | BindingFlags.NonPublic)!;
var cpuOutput = Path.Combine(dir, "cpu.mp4");
var cpuArgs = (List<string>)build.Invoke(null, new object[] {
    new[] { "-y", "-v", "error", "-f", "lavfi", "-i", "color=c=red:s=320x240:r=24:d=4", "-g", "12", "-bf", "0" }, cpu, cpuOutput })!;
await Run(ffmpeg, cpuArgs.ToArray());
var patch = Path.Combine(dir, "patch.mp4");
await Run(ffmpeg, "-y", "-v", "error", "-i", cpuOutput, "-t", "1.5", "-c", "copy", patch);
var logs = new List<string>();
var patcher = new VideoPatcher(ffmpeg, line => { lock (logs) logs.Add(line); });
foreach (var (mode, codec) in new[] { ("copy", "hevc"), ("encode", "hevc"), ("encode", "h264") })
{
    var output = Path.Combine(dir, $"{mode}-{codec}.mp4");
    await patcher.RunAsync(new PatchConfig {
        Input = cpuOutput, Output = output, Workdir = Path.Combine(dir, $"work-{mode}-{codec}"),
        Concat = new() { Mode = mode, VideoCodec = codec },
        Segments = [new() { Start = "00:00:01", Patch = patch }]
    }, root, CancellationToken.None);
    Check(logs.Any(line => line.Contains("00:00:01.000000 - 00:00:02.500000")), "Fractional patch duration used for end time");
    var actual = (await Run(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", output)).Trim();
    Check(actual == codec, $"Expected {codec}, got {actual}");
    await Run(ffmpeg, "-v", "error", "-xerror", "-i", output, "-f", "null", "-");
    if (mode == "encode" && codec == "hevc")
        Check((await Run(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_tag_string", "-of", "csv=p=0", output)).Trim() == "hvc1", "HEVC MP4 tag");
    Console.WriteLine($"PASS {mode} {codec}");
}
foreach (var line in logs.Where(l => l.Contains("编码器:") || l.Contains("回退"))) Console.WriteLine(line);
Console.WriteLine("PASS CPU libx265, config compatibility and output decoding");

// Reject a second patch whose start falls inside the first patch's derived interval.
try
{
    await patcher.RunAsync(new PatchConfig {
        Input = cpuOutput, Output = Path.Combine(dir, "overlap.mp4"),
        Workdir = Path.Combine(dir, "work-overlap"),
        Segments = [
            new() { Start = "00:00:01", Patch = patch },
            new() { Start = "00:00:02", Patch = patch }
        ]
    }, root, CancellationToken.None);
    throw new Exception("Overlap should have failed");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("重叠")) { }

try
{
    await patcher.RunAsync(new PatchConfig {
        Input = cpuOutput, Output = Path.Combine(dir, "overshoot.mp4"),
        Workdir = Path.Combine(dir, "work-overshoot"),
        Segments = [new() { Start = "00:00:03", Patch = patch }]
    }, root, CancellationToken.None);
    throw new Exception("Overshoot should have failed");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("超过原视频时长")) { }

// Reaching the source end exactly should not attempt to create an empty tail.
var oneSecond = Path.Combine(dir, "one-second.mp4");
await Run(ffmpeg, "-y", "-v", "error", "-i", cpuOutput, "-t", "1", "-c", "copy", oneSecond);
await patcher.RunAsync(new PatchConfig {
    Input = cpuOutput, Output = Path.Combine(dir, "to-end.mp4"),
    Workdir = Path.Combine(dir, "work-to-end"),
    Segments = [new() { Start = "00:00:03", Patch = oneSecond }]
}, root, CancellationToken.None);
Console.WriteLine("PASS derived end, legacy config, overlap, source end");
