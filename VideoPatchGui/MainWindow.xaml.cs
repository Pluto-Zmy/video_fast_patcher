using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VideoPatchGui;

public sealed partial class MainWindow : Window
{
    private const string TimeFormatHint = "时:分:秒，例如 00:03:12";
    private const string SettingsFileName = "settings.json";
    private const string LogDirectoryName = "logs";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ObservableCollection<SegmentConfig> _segments = new();
    private readonly IntPtr _windowHandle;
    private readonly object _logLock = new();
    private readonly string _logFilePath;

    private string? _currentConfigPath;
    private CancellationTokenSource? _runCts;
    private bool _startupNoticeChecked;

    public MainWindow()
    {
        InitializeComponent();

        Title = "视频修补工具";
        SystemBackdrop = new MicaBackdrop();
        _windowHandle = WindowNative.GetWindowHandle(this);
        _logFilePath = CreateLogFilePath();

        ConfigureWindow();
        InitializeControls();
        RefreshFfmpegStatus();
        LoadDefaultConfigIfPresent();
        AppendLog("应用启动。");
        RootGrid.Loaded += RootGrid_Loaded;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupNoticeChecked)
        {
            return;
        }

        _startupNoticeChecked = true;
        if (!LoadAppSettings().HideStartupNotice)
        {
            await ShowStartupNoticeAsync();
        }
    }

    private void ConfigureWindow()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = "视频修补工具";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        appWindow.Resize(new SizeInt32(1080, 1220));
    }

    private void InitializeControls()
    {
        ConcatModeComboBox.ItemsSource = ConcatModes.DisplayNames;
        ConcatModeComboBox.SelectedItem = ConcatModes.Fast;

        SegmentsList.ItemsSource = _segments;
    }

    private async Task ShowStartupNoticeAsync()
    {
        var dontShowAgainCheckBox = new CheckBox
        {
            Content = "下次不再提示",
            Margin = new Thickness(0, 8, 0, 0),
        };

        var contentPanel = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 620,
        };
        contentPanel.Children.Add(MakeNoticeSection("功能简介", "本工具用于将原视频中的一个或多个指定时间段替换为补丁视频，并输出修补后的新视频。支持直接复制编码参数的快速拼接，以及兼容性更强但速度更慢的重新编码。"));
        contentPanel.Children.Add(MakeNoticeSection("适用场景", "适合处理局部画面或声音错误、替换短片段、修正片头片尾、重新拼接少量补丁片段等场景。"));
        contentPanel.Children.Add(MakeNoticeSection("磁盘空间要求", "处理过程会在工作目录中生成临时切片和拼接文件。建议预留至少为原视频、补丁视频和输出文件合计大小 2 到 3 倍的可用空间。"));
        contentPanel.Children.Add(MakeNoticeSection("视频文件要求", "补丁片段时间必须有效且不能重叠；快速拼接要求原视频与补丁视频编码参数兼容，不兼容时请改用重新编码（更慢）。"));
        contentPanel.Children.Add(MakeNoticeSection("重要提醒！", "修补完成后，请务必自行检查输出视频的画面、声音、时长、同步情况和关键片段内容，确认无误后再用于正式场景！"));
        contentPanel.Children.Add(dontShowAgainCheckBox);

        var dialog = new ContentDialog
        {
            Title = "使用前提示",
            Content = new ScrollViewer
            {
                Content = contentPanel,
                MaxHeight = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "我知道了",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        await dialog.ShowAsync();

        if (dontShowAgainCheckBox.IsChecked == true)
        {
            SaveAppSettings(new AppSettings { HideStartupNotice = true });
        }
    }

    private static StackPanel MakeNoticeSection(string title, string body)
    {
        var panel = new StackPanel
        {
            Spacing = 4,
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        return panel;
    }

    private static AppSettings LoadAppSettings()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static void SaveAppSettings(AppSettings settings)
    {
        try
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // A failed preference write should not block the app from opening.
        }
    }

    private static string GetSettingsPath()
    {
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDirectory, "VideoPatch", SettingsFileName);
    }

    private static string CreateLogFilePath()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"VideoPatch_{timestamp}.log";

        foreach (var directory in GetCandidateLogDirectories())
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = GetUniqueLogPath(directory, fileName);
                File.WriteAllText(path, $"视频修补工具日志{Environment.NewLine}");
                return path;
            }
            catch
            {
                // Try the next writable location.
            }
        }

        var fallbackPath = Path.Combine(Path.GetTempPath(), GetUniqueLogFileName(fileName));
        File.WriteAllText(fallbackPath, $"视频修补工具日志{Environment.NewLine}");
        return fallbackPath;
    }

    private static IEnumerable<string> GetCandidateLogDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, LogDirectoryName);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "VideoPatch", LogDirectoryName);
        }
    }

    private static string GetUniqueLogPath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            path = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }

    private static string GetUniqueLogFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return $"{name}_{Guid.NewGuid():N}{extension}";
    }

    private async void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickOpenFilePathAsync("打开配置", [".json"]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            LoadConfig(path);
        }
    }

    private async void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        await SaveConfigAsync(saveAs: false);
    }

    private async void SaveConfigAs_Click(object sender, RoutedEventArgs e)
    {
        await SaveConfigAsync(saveAs: true);
    }

    private async void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickOpenFilePathAsync("选择输入视频", [".mp4", ".mov", ".mkv", ".avi", ".webm"]);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        InputTextBox.Text = path;
        if (string.IsNullOrWhiteSpace(OutputTextBox.Text))
        {
            var folder = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            var name = Path.GetFileNameWithoutExtension(path);
            OutputTextBox.Text = Path.Combine(folder, name + "_patched.mp4");
        }
    }

    private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var suggestedFileName = string.IsNullOrWhiteSpace(OutputTextBox.Text)
            ? "final.mp4"
            : Path.GetFileName(OutputTextBox.Text);
        var path = await PickSaveFilePathAsync("选择输出文件", "MP4 视频", [".mp4"], suggestedFileName);
        if (!string.IsNullOrWhiteSpace(path))
        {
            OutputTextBox.Text = path;
        }
    }

    private async void BrowseWorkdir_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderPathAsync("选择临时工作目录");
        if (!string.IsNullOrWhiteSpace(path))
        {
            WorkdirTextBox.Text = path;
        }
    }

    private void RefreshFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        RefreshFfmpegStatus();
    }

    private void AddSegment_Click(object sender, RoutedEventArgs e)
    {
        var segment = new SegmentConfig();
        _segments.Add(segment);
        SegmentsList.SelectedItem = segment;
    }

    private void RemoveSelectedSegment_Click(object sender, RoutedEventArgs e)
    {
        if (SegmentsList.SelectedItem is SegmentConfig segment)
        {
            _segments.Remove(segment);
        }
    }

    private async void BrowsePatchForSelectedRow_Click(object sender, RoutedEventArgs e)
    {
        if (SegmentsList.SelectedItem is SegmentConfig segment)
        {
            await BrowsePatchForSegmentAsync(segment);
            return;
        }

        var path = await PickOpenFilePathAsync("选择补丁视频", [".mp4", ".mov", ".mkv", ".avi", ".webm"]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            _segments.Add(new SegmentConfig { Patch = path });
        }
    }

    private async void BrowsePatchForSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SegmentConfig segment })
        {
            await BrowsePatchForSegmentAsync(segment);
        }
    }

    private async void RunPatch_Click(object sender, RoutedEventArgs e)
    {
        await RunPatchAsync();
    }

    private void CancelPatch_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
    }

    private void LoadDefaultConfigIfPresent()
    {
        var path = new[]
            {
                Path.Combine(Environment.CurrentDirectory, "config.json"),
                Path.Combine(AppContext.BaseDirectory, "config.json"),
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);

        if (File.Exists(path))
        {
            LoadConfig(path);
        }
        else
        {
            WorkdirTextBox.Text = "_patch_tmp";
        }
    }

    private void LoadConfig(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<PatchConfig>(json, JsonOptions)
                ?? throw new InvalidOperationException("配置文件为空。");

            _currentConfigPath = path;
            ApplyConfig(config);
            AppendLog($"已加载配置: {path}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ApplyConfig(PatchConfig config)
    {
        InputTextBox.Text = config.Input;
        OutputTextBox.Text = config.Output;
        WorkdirTextBox.Text = string.IsNullOrWhiteSpace(config.Workdir) ? "_patch_tmp" : config.Workdir;
        OverwriteCheckBox.IsChecked = config.Ffmpeg.Overwrite;

        ConcatModeComboBox.SelectedItem = ConcatModes.Normalize(config.Concat.Mode);

        _segments.Clear();
        foreach (var segment in config.Segments)
        {
            _segments.Add(segment);
        }
    }

    private async Task<bool> SaveConfigAsync(bool saveAs)
    {
        if (saveAs || string.IsNullOrWhiteSpace(_currentConfigPath))
        {
            var path = await PickSaveFilePathAsync("保存配置", "JSON 配置", [".json"], "config.json");
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            _currentConfigPath = path;
        }

        try
        {
            var config = CollectConfig();
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_currentConfigPath, json);
            AppendLog($"已保存配置: {_currentConfigPath}");
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return false;
        }
    }

    private PatchConfig CollectConfig()
    {
        ValidateSegmentTimes();

        return new PatchConfig
        {
            Input = InputTextBox.Text.Trim(),
            Output = OutputTextBox.Text.Trim(),
            Workdir = string.IsNullOrWhiteSpace(WorkdirTextBox.Text) ? "_patch_tmp" : WorkdirTextBox.Text.Trim(),
            Ffmpeg = new FfmpegConfig
            {
                Overwrite = OverwriteCheckBox.IsChecked == true,
                Loglevel = "warning",
            },
            Concat = new ConcatConfig
            {
                Mode = ConcatModes.Normalize(ConcatModeComboBox.SelectedItem as string),
                Container = "mp4",
            },
            Segments = _segments
                .Where(segment =>
                    !string.IsNullOrWhiteSpace(segment.Start) ||
                    !string.IsNullOrWhiteSpace(segment.End) ||
                    !string.IsNullOrWhiteSpace(segment.Patch))
                .Select(segment => new SegmentConfig
                {
                    Start = segment.Start.Trim(),
                    End = segment.End.Trim(),
                    Patch = segment.Patch.Trim(),
                })
                .ToList(),
        };
    }

    private void ValidateSegmentTimes()
    {
        for (var index = 0; index < _segments.Count; index++)
        {
            var segment = _segments[index];
            if (IsEmptySegment(segment))
            {
                continue;
            }

            if (!VideoPatcher.TryParseTimestamp(segment.Start, out var start))
            {
                throw new InvalidOperationException($"第 {index + 1} 行开始时间格式无效，必须是 {TimeFormatHint}。");
            }

            if (!VideoPatcher.TryParseTimestamp(segment.End, out var end))
            {
                throw new InvalidOperationException($"第 {index + 1} 行结束时间格式无效，必须是 {TimeFormatHint}。");
            }

            if (start >= end)
            {
                throw new InvalidOperationException($"第 {index + 1} 行开始时间必须早于结束时间。");
            }
        }
    }

    private static bool IsEmptySegment(SegmentConfig segment)
    {
        return string.IsNullOrWhiteSpace(segment.Start) &&
            string.IsNullOrWhiteSpace(segment.End) &&
            string.IsNullOrWhiteSpace(segment.Patch);
    }

    private async Task RunPatchAsync()
    {
        try
        {
            var ffmpegPath = VideoPatcher.FindFfmpeg();
            var config = CollectConfig();
            var baseDirectory = GetConfigBaseDirectory();

            SetRunning(true);
            AppendLog("开始执行。");

            _runCts = new CancellationTokenSource();
            var patcher = new VideoPatcher(ffmpegPath, AppendLog, UpdateProgress);
            await patcher.RunAsync(config, baseDirectory, _runCts.Token);
            await ShowInfoAsync($"视频修补完成。\n\n日志文件：{_logFilePath}", "完成");
        }
        catch (OperationCanceledException)
        {
            AppendLog("已取消。");
        }
        catch (Exception ex)
        {
            AppendLog("失败: " + ex.Message);
            ShowError($"{ex.Message}\n\n日志文件：{_logFilePath}");
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            SetRunning(false);
        }
    }

    private string GetConfigBaseDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_currentConfigPath))
        {
            return Path.GetDirectoryName(_currentConfigPath) ?? Environment.CurrentDirectory;
        }

        return Environment.CurrentDirectory;
    }

    private void SetRunning(bool running)
    {
        var enabled = !running;
        Toolbar.IsEnabled = enabled;
        SettingsGrid.IsHitTestVisible = enabled;
        SettingsGrid.Opacity = enabled ? 1 : 0.55;
        SegmentsGrid.IsHitTestVisible = enabled;
        SegmentsGrid.Opacity = enabled ? 1 : 0.55;
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;

        if (running)
        {
            UpdateProgress(0);
        }
    }

    private void UpdateProgress(int value)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(() => UpdateProgress(value));
            return;
        }

        PatchProgressBar.Value = Math.Clamp(value, (int)PatchProgressBar.Minimum, (int)PatchProgressBar.Maximum);
    }

    private async Task BrowsePatchForSegmentAsync(SegmentConfig segment)
    {
        var path = await PickOpenFilePathAsync("选择补丁视频", [".mp4", ".mov", ".mkv", ".avi", ".webm"]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            segment.Patch = path;
        }
    }

    private void RefreshFfmpegStatus()
    {
        var bundled = VideoPatcher.FindBundledFfmpeg();
        FfmpegTextBox.Text = bundled ?? "未找到内置 ffmpeg，将尝试使用系统 PATH 中的 ffmpeg";
    }

    private void AppendLog(string message)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never interrupt video processing.
        }
    }

    private async Task<string?> PickOpenFilePathAsync(string title, IReadOnlyList<string> extensions)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            ViewMode = PickerViewMode.List,
        };
        InitializeWithWindow.Initialize(picker, _windowHandle);

        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task<string?> PickSaveFilePathAsync(string title, string typeName, IReadOnlyList<string> extensions, string suggestedFileName)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedFileName,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        InitializeWithWindow.Initialize(picker, _windowHandle);
        picker.FileTypeChoices.Add(typeName, extensions.ToList());

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private async Task<string?> PickFolderPathAsync(string title)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        InitializeWithWindow.Initialize(picker, _windowHandle);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void ShowError(string message)
    {
        await ShowDialogAsync("错误", message);
    }

    private async Task ShowInfoAsync(string message, string title)
    {
        await ShowDialogAsync(title, message);
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        await dialog.ShowAsync();
    }
}

internal sealed class AppSettings
{
    public bool HideStartupNotice { get; set; }
}
