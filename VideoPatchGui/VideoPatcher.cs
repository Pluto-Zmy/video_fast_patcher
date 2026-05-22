using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoPatchGui;

public sealed class VideoPatcher
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly Action<string> _log;
    private readonly Action<int>? _progress;

    public VideoPatcher(string ffmpegPath, Action<string> log, Action<int>? progress = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = FindFfprobe(ffmpegPath);
        _log = log;
        _progress = progress;
    }

    public static string? FindBundledFfmpeg()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string FindFfmpeg()
    {
        return FindBundledFfmpeg() ?? "ffmpeg.exe";
    }

    private static string FindFfprobe(string ffmpegPath)
    {
        if (!Path.IsPathFullyQualified(ffmpegPath))
        {
            return "ffprobe.exe";
        }

        var ffmpegDirectory = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrWhiteSpace(ffmpegDirectory))
        {
            return "ffprobe.exe";
        }

        var ffprobePath = Path.Combine(ffmpegDirectory, "ffprobe.exe");
        return File.Exists(ffprobePath) ? ffprobePath : "ffprobe.exe";
    }

    public async Task RunAsync(PatchConfig config, string baseDirectory, CancellationToken cancellationToken)
    {
        ValidateConfig(config);

        var inputVideo = ResolvePath(baseDirectory, config.Input);
        var outputVideo = ResolvePath(baseDirectory, config.Output);
        var workdir = ResolvePath(baseDirectory, config.Workdir);

        if (!File.Exists(inputVideo))
        {
            throw new FileNotFoundException("输入视频不存在。", inputVideo);
        }

        Directory.CreateDirectory(workdir);

        var segments = config.Segments
            .Select(segment => new SegmentWithTime(
                segment,
                ParseTimestamp(segment.Start, "开始时间"),
                ParseTimestamp(segment.End, "结束时间"),
                ResolvePath(baseDirectory, segment.Patch)))
            .OrderBy(segment => segment.StartTime)
            .ToList();

        ValidateTimeline(segments);

        foreach (var segment in segments)
        {
            if (!File.Exists(segment.PatchPath))
            {
                throw new FileNotFoundException("补丁视频不存在。", segment.PatchPath);
            }
        }

        ReportProgress(0);
        var progressPlan = await BuildProgressPlanAsync(inputVideo, segments, cancellationToken);

        var parts = new List<string>();
        var sourceFiles = new List<string> { inputVideo, outputVideo };
        var cursorText = "00:00:00";
        var cursor = TimeSpan.Zero;
        var partIndex = 0;
        var progressIndex = 0;

        foreach (var segment in segments)
        {
            if (segment.StartTime > cursor)
            {
                var partFile = Path.Combine(workdir, SafeName(partIndex));
                var progressStep = progressPlan.Steps[progressIndex++];
                await RunFfmpegAsync(BuildBaseArgs(config.Ffmpeg)
                    .Concat(new[]
                    {
                        "-i", inputVideo,
                        "-ss", cursorText,
                        "-to", segment.Segment.Start,
                        "-c", "copy",
                        partFile,
                    }), progressStep, cancellationToken);

                parts.Add(partFile);
                partIndex++;
            }

            parts.Add(segment.PatchPath);
            sourceFiles.Add(segment.PatchPath);
            cursorText = segment.Segment.End;
            cursor = segment.EndTime;
        }

        var tailFile = Path.Combine(workdir, SafeName(partIndex));
        var tailProgressStep = progressPlan.Steps[progressIndex++];
        await RunFfmpegAsync(BuildBaseArgs(config.Ffmpeg)
            .Concat(new[]
            {
                "-i", inputVideo,
                "-ss", cursorText,
                "-c", "copy",
                tailFile,
            }), tailProgressStep, cancellationToken);

        parts.Add(tailFile);

        var concatList = Path.Combine(workdir, "concat_list.txt");
        await File.WriteAllLinesAsync(
            concatList,
            parts.Select(part => $"file '{EscapeConcatPath(Path.GetFullPath(part))}'"),
            new UTF8Encoding(false),
            cancellationToken);

        var concatArgs = BuildBaseArgs(config.Ffmpeg)
            .Concat(new[] { "-f", "concat", "-safe", "0", "-i", concatList })
            .ToList();

        if (ConcatModes.IsFast(config.Concat.Mode))
        {
            concatArgs.AddRange(new[] { "-c", "copy" });
        }
        else
        {
            concatArgs.AddRange(new[] { "-c:v", "libx264", "-c:a", "aac" });
        }

        concatArgs.Add(outputVideo);
        var concatProgressStep = progressPlan.Steps[progressIndex];
        await RunFfmpegAsync(concatArgs, concatProgressStep, cancellationToken);

        _log($"完成: {outputVideo}");
        CleanupWorkdir(workdir, sourceFiles);
        ReportProgress(1000);
    }

    private static void ValidateConfig(PatchConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Input))
        {
            throw new InvalidOperationException("请选择输入视频。");
        }

        if (string.IsNullOrWhiteSpace(config.Output))
        {
            throw new InvalidOperationException("请选择输出文件。");
        }

        config.Segments = config.Segments
            .Where(segment =>
                !string.IsNullOrWhiteSpace(segment.Start) ||
                !string.IsNullOrWhiteSpace(segment.End) ||
                !string.IsNullOrWhiteSpace(segment.Patch))
            .ToList();

        if (config.Segments.Count == 0)
        {
            throw new InvalidOperationException("至少需要添加一个补丁片段。");
        }
    }

    private static void ValidateTimeline(IReadOnlyList<SegmentWithTime> segments)
    {
        var cursor = TimeSpan.Zero;

        foreach (var segment in segments)
        {
            if (segment.StartTime >= segment.EndTime)
            {
                throw new InvalidOperationException($"片段时间无效: {segment.Segment.Start} - {segment.Segment.End}");
            }

            if (segment.StartTime < cursor)
            {
                throw new InvalidOperationException($"片段时间重叠或倒序: {segment.Segment.Start} - {segment.Segment.End}");
            }

            cursor = segment.EndTime;
        }
    }

    private static TimeSpan ParseTimestamp(string value, string fieldName)
    {
        if (TryParseTimestamp(value, out var timestamp))
        {
            return timestamp;
        }

        throw new InvalidOperationException($"{fieldName}格式无效，必须是 时:分:秒，例如 00:03:12。当前值: {value}");
    }

    public static bool TryParseTimestamp(string value, out TimeSpan timestamp)
    {
        timestamp = default;

        var parts = value.Trim().Split(':');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length != 2 || parts[2].Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        if (minutes is < 0 or > 59 || seconds is < 0 or > 59)
        {
            return false;
        }

        try
        {
            timestamp = new TimeSpan(hours, minutes, seconds);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolvePath(string baseDirectory, string path)
    {
        return Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(path, baseDirectory);
    }

    private static string SafeName(int index)
    {
        return $"part_{index:0000}.mp4";
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private void CleanupWorkdir(string workdir, IReadOnlyList<string> sourceFiles)
    {
        try
        {
            var fullWorkdir = NormalizeDirectory(workdir);
            if (!Directory.Exists(fullWorkdir))
            {
                return;
            }

            var root = Path.GetPathRoot(fullWorkdir);
            if (string.IsNullOrWhiteSpace(root) ||
                string.Equals(fullWorkdir, NormalizeDirectory(root), StringComparison.OrdinalIgnoreCase))
            {
                _log($"已跳过删除临时目录，路径不安全: {fullWorkdir}");
                return;
            }

            var protectedFile = sourceFiles
                .Select(Path.GetFullPath)
                .FirstOrDefault(path => IsPathInsideDirectory(path, fullWorkdir));
            if (protectedFile != null)
            {
                _log($"已跳过删除临时目录，目录内包含输入、输出或补丁文件: {protectedFile}");
                return;
            }

            Directory.Delete(fullWorkdir, recursive: true);
            _log($"已删除临时目录: {fullWorkdir}");
        }
        catch (Exception ex)
        {
            _log($"删除临时目录失败: {ex.Message}");
        }
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = NormalizeDirectory(directory) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProgressPlan> BuildProgressPlanAsync(
        string inputVideo,
        IReadOnlyList<SegmentWithTime> segments,
        CancellationToken cancellationToken)
    {
        _log("正在读取视频时长，用于计算真实进度。");

        var inputDuration = await ProbeDurationAsync(inputVideo, cancellationToken);
        var patchDurations = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            if (!patchDurations.ContainsKey(segment.PatchPath))
            {
                patchDurations[segment.PatchPath] = await ProbeDurationAsync(segment.PatchPath, cancellationToken);
            }
        }

        var weightedSteps = new List<WeightedProgressStep>();
        var cursor = TimeSpan.Zero;
        var concatDuration = TimeSpan.Zero;

        foreach (var segment in segments)
        {
            if (segment.StartTime > cursor)
            {
                var duration = segment.StartTime - cursor;
                weightedSteps.Add(new WeightedProgressStep(duration));
                concatDuration += duration;
            }

            concatDuration += patchDurations[segment.PatchPath];
            cursor = segment.EndTime;
        }

        var tailDuration = inputDuration > cursor ? inputDuration - cursor : TimeSpan.Zero;
        weightedSteps.Add(new WeightedProgressStep(tailDuration));
        concatDuration += tailDuration;
        weightedSteps.Add(new WeightedProgressStep(concatDuration));

        return ProgressPlan.FromWeightedSteps(weightedSteps);
    }

    private async Task<TimeSpan> ProbeDurationAsync(string videoPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(videoPath);

        AddToolDirectoryToPath(startInfo, _ffprobePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffprobe。");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe 读取时长失败: {videoPath}\n{error}");
        }

        if (!double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0)
        {
            throw new InvalidOperationException($"ffprobe 返回了无效时长: {videoPath}");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private IEnumerable<string> BuildBaseArgs(FfmpegConfig ffmpegConfig)
    {
        yield return _ffmpegPath;

        if (ffmpegConfig.Overwrite)
        {
            yield return "-y";
        }

        if (!string.IsNullOrWhiteSpace(ffmpegConfig.Loglevel))
        {
            yield return "-loglevel";
            yield return ffmpegConfig.Loglevel!;
        }
    }

    private async Task RunFfmpegAsync(
        IEnumerable<string> command,
        ProgressStep progressStep,
        CancellationToken cancellationToken)
    {
        var args = command.ToList();
        var exe = args[0];
        var arguments = new[] { "-nostats", "-progress", "pipe:1" }
            .Concat(args.Skip(1))
            .ToList();

        _log(FormatCommand(new[] { exe }.Concat(arguments)));

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        AddToolDirectoryToPath(startInfo, exe);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => HandleProgressLine(e.Data, progressStep);
        process.ErrorDataReceived += (_, e) => LogLine(e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 ffmpeg。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg 执行失败，退出码: {process.ExitCode}");
        }

        ReportProgress(progressStep.End);
    }

    private static void AddToolDirectoryToPath(ProcessStartInfo startInfo, string toolPath)
    {
        if (!Path.IsPathFullyQualified(toolPath))
        {
            return;
        }

        var toolDirectory = Path.GetDirectoryName(Path.GetFullPath(toolPath));
        if (!string.IsNullOrWhiteSpace(toolDirectory))
        {
            startInfo.Environment["PATH"] = toolDirectory + Path.PathSeparator + startInfo.Environment["PATH"];
        }
    }

    private void HandleProgressLine(string? line, ProgressStep progressStep)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (TryReadProgressTime(line, out var elapsed))
        {
            ReportProgress(progressStep.GetValue(elapsed));
            return;
        }

        if (line.StartsWith("progress=", StringComparison.OrdinalIgnoreCase) ||
            line.Contains('=', StringComparison.Ordinal))
        {
            return;
        }

        LogLine(line);
    }

    private static bool TryReadProgressTime(string line, out TimeSpan elapsed)
    {
        elapsed = default;
        var separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        var key = line[..separatorIndex];
        var value = line[(separatorIndex + 1)..].Trim();

        if (string.Equals(key, "out_time", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out elapsed);
        }

        if (string.Equals(key, "out_time_us", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            elapsed = TimeSpan.FromMilliseconds(microseconds / 1000.0);
            return true;
        }

        return false;
    }

    private void ReportProgress(double value)
    {
        var rounded = (int)Math.Round(Math.Clamp(value, 0, 1000), MidpointRounding.AwayFromZero);
        _progress?.Invoke(rounded);
    }

    private void LogLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            _log(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore kill failures during cancellation.
        }
    }

    private static string FormatCommand(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(Quote));
    }

    private static string Quote(string value)
    {
        return value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
    }

    private sealed record SegmentWithTime(SegmentConfig Segment, TimeSpan StartTime, TimeSpan EndTime, string PatchPath);

    private sealed record WeightedProgressStep(TimeSpan Duration);

    private sealed record ProgressStep(double Start, double End, TimeSpan ExpectedDuration)
    {
        public double GetValue(TimeSpan elapsed)
        {
            if (ExpectedDuration <= TimeSpan.Zero)
            {
                return Start;
            }

            var ratio = elapsed.TotalSeconds / ExpectedDuration.TotalSeconds;
            return Start + ((End - Start) * Math.Clamp(ratio, 0, 1));
        }
    }

    private sealed class ProgressPlan
    {
        public required IReadOnlyList<ProgressStep> Steps { get; init; }

        public static ProgressPlan FromWeightedSteps(IReadOnlyList<WeightedProgressStep> weightedSteps)
        {
            if (weightedSteps.Count == 0)
            {
                return new ProgressPlan { Steps = Array.Empty<ProgressStep>() };
            }

            var totalSeconds = weightedSteps.Sum(step => Math.Max(0, step.Duration.TotalSeconds));
            var steps = new List<ProgressStep>(weightedSteps.Count);
            var cursor = 0.0;

            for (var index = 0; index < weightedSteps.Count; index++)
            {
                var duration = weightedSteps[index].Duration;
                var share = totalSeconds > 0
                    ? Math.Max(0, duration.TotalSeconds) / totalSeconds
                    : 1.0 / weightedSteps.Count;
                var end = index == weightedSteps.Count - 1 ? 1000 : cursor + (share * 1000);
                steps.Add(new ProgressStep(cursor, end, duration));
                cursor = end;
            }

            return new ProgressPlan { Steps = steps };
        }
    }
}
