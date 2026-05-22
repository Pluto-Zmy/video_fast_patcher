using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI;
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

    private static readonly string[] Loglevels = ["", "quiet", "panic", "fatal", "error", "warning", "info", "verbose", "debug"];
    private static readonly string[] ConcatModes = ["copy", "encode"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ObservableCollection<SegmentConfig> _segments = new();
    private readonly IntPtr _windowHandle;

    private string? _currentConfigPath;
    private CancellationTokenSource? _runCts;

    public MainWindow()
    {
        InitializeComponent();

        Title = "视频修补工具";
        SystemBackdrop = new MicaBackdrop();
        _windowHandle = WindowNative.GetWindowHandle(this);

        ConfigureWindow();
        InitializeControls();
        RefreshFfmpegStatus();
        LoadDefaultConfigIfPresent();
    }

    private void ConfigureWindow()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = "视频修补工具";
        appWindow.Resize(new SizeInt32(1080, 1720));
    }

    private void InitializeControls()
    {
        LoglevelComboBox.ItemsSource = Loglevels;
        LoglevelComboBox.SelectedItem = "warning";

        ConcatModeComboBox.ItemsSource = ConcatModes;
        ConcatModeComboBox.SelectedItem = "copy";

        SegmentsList.ItemsSource = _segments;
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

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Text = "";
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

        var loglevel = config.Ffmpeg.Loglevel ?? "";
        LoglevelComboBox.SelectedItem = Loglevels.FirstOrDefault(item => string.Equals(item, loglevel, StringComparison.OrdinalIgnoreCase)) ?? "warning";

        ConcatModeComboBox.SelectedItem = ConcatModes.FirstOrDefault(item => string.Equals(item, config.Concat.Mode, StringComparison.OrdinalIgnoreCase)) ?? "copy";

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
                Loglevel = (LoglevelComboBox.SelectedItem as string)?.Trim(),
            },
            Concat = new ConcatConfig
            {
                Mode = (ConcatModeComboBox.SelectedItem as string) ?? "copy",
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
            await ShowInfoAsync("视频修补完成。", "完成");
        }
        catch (OperationCanceledException)
        {
            AppendLog("已取消。");
        }
        catch (Exception ex)
        {
            AppendLog("失败: " + ex.Message);
            ShowError(ex.Message);
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
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(() => AppendLog(message));
            return;
        }

        LogTextBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogTextBox.SelectionStart = LogTextBox.Text.Length;
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
