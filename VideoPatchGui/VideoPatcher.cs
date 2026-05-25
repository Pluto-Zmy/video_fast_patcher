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
    private readonly Action<string>? _status;
    private readonly Action<TimeSpan?>? _remainingTime;
    private VideoEncoderProfile? _selectedReEncodeVideoEncoder;

    private static readonly VideoEncoderProfile CpuVideoEncoder = new(
        "libx264",
        "CPU libx264",
        ["-c:v", "libx264", "-preset", "medium", "-crf", "23"],
        HardwareAccelerated: false);

    private static readonly IReadOnlyList<VideoEncoderProfile> HardwareVideoEncoders =
    [
        new("h264_nvenc", "NVIDIA NVENC", ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-b:v", "0"], HardwareAccelerated: true),
        new("h264_qsv", "Intel Quick Sync", ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "23"], HardwareAccelerated: true),
        new("h264_amf", "AMD AMF", ["-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "23", "-qp_p", "23", "-qp_b", "23"], HardwareAccelerated: true),
        new("h264_mf", "Windows Media Foundation", ["-c:v", "h264_mf", "-hw_encoding", "1", "-rate_control", "quality", "-quality", "75"], HardwareAccelerated: true),
    ];

    public VideoPatcher(
        string ffmpegPath,
        Action<string> log,
        Action<int>? progress = null,
        Action<string>? status = null,
        Action<TimeSpan?>? remainingTime = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = FindFfprobe(ffmpegPath);
        _log = log;
        _progress = progress;
        _status = status;
        _remainingTime = remainingTime;
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
        ReportStatus("正在读取视频时长");
        var progressPlan = await BuildProgressPlanAsync(inputVideo, segments, cancellationToken);

        var parts = new List<string>();
        var sourceFiles = new List<string> { inputVideo, outputVideo };
        var cursorText = "00:00:00";
        var cursor = TimeSpan.Zero;
        var partIndex = 0;
        var progressIndex = 0;
        var sourcePartIndex = 0;
        var sourcePartTotal = CountSourceParts(segments);

        foreach (var segment in segments)
        {
            if (segment.StartTime > cursor)
            {
                var partFile = Path.Combine(workdir, SafeName(partIndex));
                var progressStep = progressPlan.Steps[progressIndex++];
                ReportStatus($"正在生成原视频片段 {++sourcePartIndex}/{sourcePartTotal}");
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
        ReportStatus($"正在生成原视频片段 {++sourcePartIndex}/{sourcePartTotal}");
        await RunFfmpegAsync(BuildBaseArgs(config.Ffmpeg)
            .Concat(new[]
            {
                "-i", inputVideo,
                "-ss", cursorText,
                "-c", "copy",
                tailFile,
            }), tailProgressStep, cancellationToken);

        parts.Add(tailFile);

        ReportStatus("正在准备拼接清单");
        var concatList = Path.Combine(workdir, "concat_list.txt");
        await File.WriteAllLinesAsync(
            concatList,
            parts.Select(part => $"file '{EscapeConcatPath(Path.GetFullPath(part))}'"),
            new UTF8Encoding(false),
            cancellationToken);

        var concatInputArgs = BuildBaseArgs(config.Ffmpeg)
            .Concat(new[] { "-f", "concat", "-safe", "0", "-i", concatList })
            .ToList();

        var concatProgressStep = progressPlan.Steps[progressIndex];
        if (ConcatModes.IsFast(config.Concat.Mode))
        {
            ReportStatus("正在拼接输出（快速拼接）");
            var concatArgs = concatInputArgs.ToList();
            concatArgs.AddRange(new[] { "-c", "copy" });
            concatArgs.Add(outputVideo);
            await RunFfmpegAsync(concatArgs, concatProgressStep, cancellationToken);
        }
        else
        {
            await RunReEncodeConcatAsync(
                concatInputArgs,
                outputVideo,
                config.Ffmpeg,
                concatProgressStep,
                cancellationToken);
        }

        _log($"完成: {outputVideo}");
        ReportStatus("正在清理临时文件");
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

    private static int CountSourceParts(IReadOnlyList<SegmentWithTime> segments)
    {
        var cursor = TimeSpan.Zero;
        var count = 1;

        foreach (var segment in segments)
        {
            if (segment.StartTime > cursor)
            {
                count++;
            }

            cursor = segment.EndTime;
        }

        return count;
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

    private async Task RunReEncodeConcatAsync(
        IReadOnlyList<string> concatInputArgs,
        string outputVideo,
        FfmpegConfig ffmpegConfig,
        ProgressStep progressStep,
        CancellationToken cancellationToken)
    {
        var encoder = await SelectReEncodeVideoEncoderAsync(cancellationToken);
        _log($"重新编码使用视频编码器: {encoder.DisplayName} ({encoder.Name})");
        ReportStatus(BuildReEncodeStatus(encoder));

        var outputExistedBeforeAttempt = File.Exists(outputVideo);
        try
        {
            await RunFfmpegAsync(
                BuildReEncodeConcatArgs(concatInputArgs, encoder, outputVideo),
                progressStep,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
            when (encoder.HardwareAccelerated &&
                !cancellationToken.IsCancellationRequested &&
                (ffmpegConfig.Overwrite || !outputExistedBeforeAttempt))
        {
            ReportStatus("GPU 编码失败，正在回退 CPU 重新编码");
            _log($"GPU 编码失败，回退到 CPU libx264。原因: {ex.Message}");
            _selectedReEncodeVideoEncoder = CpuVideoEncoder;
            DeletePartialOutputIfCreated(outputVideo, outputExistedBeforeAttempt);
            ReportProgress(progressStep.Start);
            ReportStatus("正在重新编码输出（GPU 失败，已回退 CPU libx264）");

            await RunFfmpegAsync(
                BuildReEncodeConcatArgs(concatInputArgs, CpuVideoEncoder, outputVideo),
                progressStep,
                cancellationToken);
        }
    }

    private async Task<VideoEncoderProfile> SelectReEncodeVideoEncoderAsync(CancellationToken cancellationToken)
    {
        if (_selectedReEncodeVideoEncoder != null)
        {
            return _selectedReEncodeVideoEncoder;
        }

        _log("正在检测可用 GPU H.264 编码器。");
        ReportStatus("正在检测 GPU 硬件编码器");
        foreach (var encoder in HardwareVideoEncoders)
        {
            if (await CanUseVideoEncoderAsync(encoder, cancellationToken))
            {
                _selectedReEncodeVideoEncoder = encoder;
                _log($"已启用 GPU 编码器: {encoder.DisplayName} ({encoder.Name})");
                return encoder;
            }
        }

        _selectedReEncodeVideoEncoder = CpuVideoEncoder;
        _log("未检测到可用 GPU 编码器，使用 CPU libx264。");
        ReportStatus("未检测到可用 GPU，准备使用 CPU 重新编码");
        return CpuVideoEncoder;
    }

    private async Task<bool> CanUseVideoEncoderAsync(
        VideoEncoderProfile encoder,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=640x360:r=1:d=1",
            "-frames:v",
            "1",
            "-an",
        };
        arguments.AddRange(encoder.Arguments);
        arguments.AddRange(["-f", "null", "-"]);

        var result = await RunFfmpegProbeAsync(arguments, cancellationToken);
        if (result.ExitCode == 0)
        {
            return true;
        }

        _log($"GPU 编码器不可用: {encoder.DisplayName} ({encoder.Name}) - {SummarizeProcessOutput(result.StandardError)}");
        return false;
    }

    private async Task<ProcessResult> RunFfmpegProbeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        AddToolDirectoryToPath(startInfo, _ffmpegPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffmpeg。");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static List<string> BuildReEncodeConcatArgs(
        IReadOnlyList<string> concatInputArgs,
        VideoEncoderProfile encoder,
        string outputVideo)
    {
        var args = concatInputArgs.ToList();
        args.AddRange(encoder.Arguments);
        args.AddRange(["-c:a", "aac", outputVideo]);
        return args;
    }

    private static string BuildReEncodeStatus(VideoEncoderProfile encoder)
    {
        return encoder.HardwareAccelerated
            ? $"正在重新编码输出（GPU 硬件加速已启用：{encoder.DisplayName}）"
            : "正在重新编码输出（未启用 GPU，使用 CPU libx264）";
    }

    private void DeletePartialOutputIfCreated(string outputVideo, bool outputExistedBeforeAttempt)
    {
        if (outputExistedBeforeAttempt || !File.Exists(outputVideo))
        {
            return;
        }

        try
        {
            File.Delete(outputVideo);
            _log($"已删除失败的 GPU 编码输出文件: {outputVideo}");
        }
        catch (Exception ex)
        {
            _log($"删除失败的 GPU 编码输出文件失败: {ex.Message}");
        }
    }

    private static string SummarizeProcessOutput(string output)
    {
        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "未返回错误信息";
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
        var progressEstimator = new FfmpegProgressEstimator(progressStep);

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
        ReportRemainingTime(null);
        process.OutputDataReceived += (_, e) => HandleProgressLine(e.Data, progressStep, progressEstimator);
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
        ReportRemainingTime(TimeSpan.Zero);
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

    private void HandleProgressLine(
        string? line,
        ProgressStep progressStep,
        FfmpegProgressEstimator progressEstimator)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (TryReadProgressTime(line, out var elapsed))
        {
            ReportProgress(progressStep.GetValue(elapsed));
            ReportRemainingTime(progressEstimator.EstimateRemaining(elapsed));
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

    private void ReportStatus(string status)
    {
        ReportRemainingTime(null);
        _status?.Invoke(status);
    }

    private void ReportRemainingTime(TimeSpan? remaining)
    {
        _remainingTime?.Invoke(remaining);
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

    private sealed record VideoEncoderProfile(
        string Name,
        string DisplayName,
        IReadOnlyList<string> Arguments,
        bool HardwareAccelerated);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

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

    private sealed class FfmpegProgressEstimator
    {
        private const double MinimumWallSeconds = 5;
        private const double MinimumProgressUnits = 2;

        private readonly ProgressStep _progressStep;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

        public FfmpegProgressEstimator(ProgressStep progressStep)
        {
            _progressStep = progressStep;
        }

        public TimeSpan? EstimateRemaining(TimeSpan elapsed)
        {
            if (_progressStep.ExpectedDuration <= TimeSpan.Zero)
            {
                return null;
            }

            var now = DateTimeOffset.Now;
            var wallSeconds = (now - _startedAt).TotalSeconds;
            if (wallSeconds < MinimumWallSeconds)
            {
                return null;
            }

            var currentValue = _progressStep.GetValue(elapsed);
            var completedProgressUnits = Math.Max(0, currentValue - _progressStep.Start);
            if (completedProgressUnits < MinimumProgressUnits)
            {
                return null;
            }

            var progressUnitsPerSecond = completedProgressUnits / wallSeconds;
            if (progressUnitsPerSecond <= 0)
            {
                return null;
            }

            var remainingProgressUnits = Math.Max(0, 1000 - currentValue);
            return TimeSpan.FromSeconds(remainingProgressUnits / progressUnitsPerSecond);
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
