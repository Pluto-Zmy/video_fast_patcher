using System.ComponentModel;
using System.Text.Json;

namespace VideoPatchGui;

public sealed class MainForm : Form
{
    private const string TimeFormatHint = "时:分:秒，例如 00:03:12";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly TextBox _inputText = new();
    private readonly TextBox _outputText = new();
    private readonly TextBox _workdirText = new();
    private readonly TextBox _logText = new();
    private readonly TextBox _ffmpegText = new();
    private readonly CheckBox _overwriteCheck = new()
    {
        Checked = true,
        Text = "覆盖输出",
        AutoSize = true,
        MinimumSize = new Size(96, 24),
        Margin = new Padding(0, 4, 12, 4),
    };
    private readonly ComboBox _loglevelCombo = new();
    private readonly ComboBox _concatModeCombo = new();
    private readonly DataGridView _segmentsGrid = new();
    private readonly ProgressBar _progressBar = new();
    private Control? _toolbarPanel;
    private Control? _settingsPanel;
    private Control? _segmentsPanel;
    private readonly Button _runButton = new()
    {
        Text = "开始修补",
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(96, 30),
        Padding = new Padding(10, 3, 10, 3),
    };
    private readonly Button _cancelButton = new()
    {
        Text = "取消",
        Enabled = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(76, 30),
        Padding = new Padding(10, 3, 10, 3),
    };
    private readonly BindingList<SegmentConfig> _segments = new();

    private string? _currentConfigPath;
    private CancellationTokenSource? _runCts;

    public MainForm()
    {
        Text = "视频修补工具";
        Width = 980;
        Height = 912;
        MinimumSize = new Size(840, 660);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        RefreshFfmpegStatus();
        LoadDefaultConfigIfPresent();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(BuildToolbar(), 0, 0);
        root.Controls.Add(BuildSettingsPanel(), 0, 1);
        root.Controls.Add(BuildSegmentsPanel(), 0, 2);
        root.Controls.Add(BuildLogPanel(), 0, 3);
        root.Controls.Add(BuildBottomBar(), 0, 4);
    }

    private Control BuildToolbar()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 8),
        };

        panel.Controls.Add(MakeButton("打开配置", (_, _) => OpenConfig()));
        panel.Controls.Add(MakeButton("保存配置", (_, _) => SaveConfig(false)));
        panel.Controls.Add(MakeButton("另存为", (_, _) => SaveConfig(true)));
        panel.Controls.Add(MakeButton("清空日志", (_, _) => _logText.Clear()));

        _toolbarPanel = panel;
        return panel;
    }

    private Control BuildSettingsPanel()
    {
        var group = new GroupBox
        {
            Text = "文件与选项",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 5,
            AutoSize = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));

        AddPathRow(grid, 0, "输入视频", _inputText, "浏览", BrowseInput);
        AddPathRow(grid, 1, "输出文件", _outputText, "选择", BrowseOutput);
        AddPathRow(grid, 2, "工作目录", _workdirText, "选择", BrowseWorkdir);

        _ffmpegText.ReadOnly = true;
        AddPathRow(grid, 3, "ffmpeg", _ffmpegText, "刷新", (_, _) => RefreshFfmpegStatus());

        var optionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
        };

        _loglevelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _loglevelCombo.Items.AddRange(new object[] { "", "quiet", "panic", "fatal", "error", "warning", "info", "verbose", "debug" });
        _loglevelCombo.SelectedItem = "warning";

        _concatModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _concatModeCombo.Items.AddRange(new object[] { "copy", "encode" });
        _concatModeCombo.SelectedItem = "copy";

        optionsPanel.Controls.Add(_overwriteCheck);
        optionsPanel.Controls.Add(MakeLabel("日志级别"));
        optionsPanel.Controls.Add(_loglevelCombo);
        optionsPanel.Controls.Add(MakeLabel("拼接模式"));
        optionsPanel.Controls.Add(_concatModeCombo);

        grid.Controls.Add(MakeFormLabel("选项"), 0, 4);
        grid.Controls.Add(optionsPanel, 1, 4);
        grid.SetColumnSpan(optionsPanel, 2);

        group.Controls.Add(grid);
        _settingsPanel = group;
        return group;
    }

    private Control BuildSegmentsPanel()
    {
        var group = new GroupBox
        {
            Text = "补丁片段",
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 190),
            Padding = new Padding(10),
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 6),
        };
        buttons.Controls.Add(MakeButton("添加片段", (_, _) => _segments.Add(new SegmentConfig())));
        buttons.Controls.Add(MakeButton("删除片段", (_, _) => RemoveSelectedSegment()));
        buttons.Controls.Add(MakeButton("选择补丁文件", (_, _) => BrowsePatchForSelectedRow()));
        root.Controls.Add(buttons, 0, 0);

        var timeHintLabel = new Label
        {
            Text = "开始时间和结束时间格式：" + TimeFormatHint,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 6),
        };
        root.Controls.Add(timeHintLabel, 0, 1);

        _segmentsGrid.Dock = DockStyle.Fill;
        _segmentsGrid.AutoGenerateColumns = false;
        _segmentsGrid.AllowUserToAddRows = true;
        _segmentsGrid.AllowUserToDeleteRows = true;
        _segmentsGrid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _segmentsGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _segmentsGrid.RowHeadersWidth = 42;
        _segmentsGrid.DataSource = _segments;
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "开始时间\r\n时:分:秒",
            DataPropertyName = nameof(SegmentConfig.Start),
            ToolTipText = TimeFormatHint,
            Width = 150,
        });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "结束时间\r\n时:分:秒",
            DataPropertyName = nameof(SegmentConfig.End),
            ToolTipText = TimeFormatHint,
            Width = 150,
        });
        _segmentsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "补丁视频",
            DataPropertyName = nameof(SegmentConfig.Patch),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
        });
        _segmentsGrid.DataError += (_, _) => { };
        _segmentsGrid.CellValidating += SegmentsGrid_CellValidating;
        _segmentsGrid.CellEndEdit += (_, e) =>
        {
            _segmentsGrid.Rows[e.RowIndex].ErrorText = "";
            _segmentsGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].ErrorText = "";
        };
        root.Controls.Add(_segmentsGrid, 0, 2);

        group.Controls.Add(root);
        _segmentsPanel = group;
        return group;
    }

    private Control BuildLogPanel()
    {
        var group = new GroupBox
        {
            Text = "执行日志",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
        };

        _logText.Dock = DockStyle.Fill;
        _logText.Multiline = true;
        _logText.ReadOnly = true;
        _logText.ScrollBars = ScrollBars.Vertical;
        _logText.Font = new Font(FontFamily.GenericMonospace, 9);

        group.Controls.Add(_logText);
        return group;
    }

    private Control BuildBottomBar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1000;
        _progressBar.Value = 0;
        _progressBar.Style = ProgressBarStyle.Blocks;
        panel.Controls.Add(_progressBar, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        _runButton.Click += async (_, _) => await RunPatchAsync();
        _cancelButton.Click += (_, _) => _runCts?.Cancel();
        buttons.Controls.Add(_runButton);
        buttons.Controls.Add(_cancelButton);
        panel.Controls.Add(buttons, 1, 0);

        return panel;
    }

    private static void AddPathRow(TableLayoutPanel grid, int row, string label, TextBox textBox, string buttonText, EventHandler click)
    {
        textBox.Dock = DockStyle.Fill;

        var button = new Button
        {
            Text = buttonText,
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 2, 0, 2),
        };
        button.Click += click;

        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(MakeFormLabel(label), 0, row);
        grid.Controls.Add(textBox, 1, row);
        grid.Controls.Add(button, 2, row);
    }

    private static Label MakeFormLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 8, 2),
        };
    }

    private static Label MakeLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6),
        };
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0),
        };
        button.Click += click;
        return button;
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
            _workdirText.Text = "_patch_tmp";
        }
    }

    private void OpenConfig()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "打开配置",
            Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = "config.json",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            LoadConfig(dialog.FileName);
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
        _inputText.Text = config.Input;
        _outputText.Text = config.Output;
        _workdirText.Text = string.IsNullOrWhiteSpace(config.Workdir) ? "_patch_tmp" : config.Workdir;
        _overwriteCheck.Checked = config.Ffmpeg.Overwrite;
        _loglevelCombo.SelectedItem = config.Ffmpeg.Loglevel ?? "";
        if (_loglevelCombo.SelectedIndex < 0)
        {
            _loglevelCombo.SelectedItem = "warning";
        }

        _concatModeCombo.SelectedItem = config.Concat.Mode;
        if (_concatModeCombo.SelectedIndex < 0)
        {
            _concatModeCombo.SelectedItem = "copy";
        }

        _segments.Clear();
        foreach (var segment in config.Segments)
        {
            _segments.Add(segment);
        }
    }

    private bool SaveConfig(bool saveAs)
    {
        if (saveAs || string.IsNullOrWhiteSpace(_currentConfigPath))
        {
            using var dialog = new SaveFileDialog
            {
                Title = "保存配置",
                Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                FileName = "config.json",
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return false;
            }

            _currentConfigPath = dialog.FileName;
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
        if (!_segmentsGrid.EndEdit())
        {
            throw new InvalidOperationException("请先修正表格中的时间格式。");
        }

        ValidateSegmentTimes();

        return new PatchConfig
        {
            Input = _inputText.Text.Trim(),
            Output = _outputText.Text.Trim(),
            Workdir = string.IsNullOrWhiteSpace(_workdirText.Text) ? "_patch_tmp" : _workdirText.Text.Trim(),
            Ffmpeg = new FfmpegConfig
            {
                Overwrite = _overwriteCheck.Checked,
                Loglevel = (_loglevelCombo.SelectedItem as string)?.Trim(),
            },
            Concat = new ConcatConfig
            {
                Mode = (_concatModeCombo.SelectedItem as string) ?? "copy",
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

    private void SegmentsGrid_CellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        var propertyName = _segmentsGrid.Columns[e.ColumnIndex].DataPropertyName;
        if (propertyName is not (nameof(SegmentConfig.Start) or nameof(SegmentConfig.End)))
        {
            return;
        }

        var value = Convert.ToString(e.FormattedValue)?.Trim() ?? "";
        if (value.Length == 0 || VideoPatcher.TryParseTimestamp(value, out _))
        {
            _segmentsGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].ErrorText = "";
            return;
        }

        e.Cancel = true;
        var message = "时间格式必须是 " + TimeFormatHint;
        _segmentsGrid.Rows[e.RowIndex].ErrorText = message;
        _segmentsGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].ErrorText = message;
    }

    private async Task RunPatchAsync()
    {
        var ffmpegPath = VideoPatcher.FindFfmpeg();
        var config = CollectConfig();
        var baseDirectory = GetConfigBaseDirectory();

        SetRunning(true);
        AppendLog("开始执行。");

        _runCts = new CancellationTokenSource();
        try
        {
            var patcher = new VideoPatcher(ffmpegPath, AppendLog, UpdateProgress);
            await patcher.RunAsync(config, baseDirectory, _runCts.Token);
            MessageBox.Show(this, "视频修补完成。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            _runCts.Dispose();
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
        if (_toolbarPanel != null)
        {
            _toolbarPanel.Enabled = enabled;
        }

        if (_settingsPanel != null)
        {
            _settingsPanel.Enabled = enabled;
        }

        if (_segmentsPanel != null)
        {
            _segmentsPanel.Enabled = enabled;
        }

        _runButton.Enabled = !running;
        _cancelButton.Enabled = running;
        _progressBar.Style = ProgressBarStyle.Blocks;
        if (running)
        {
            UpdateProgress(0);
        }
    }

    private void UpdateProgress(int value)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<int>(UpdateProgress), value);
            return;
        }

        _progressBar.Value = Math.Clamp(value, _progressBar.Minimum, _progressBar.Maximum);
    }

    private void BrowseInput(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择输入视频",
            Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _inputText.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(_outputText.Text))
            {
                var folder = Path.GetDirectoryName(dialog.FileName) ?? Environment.CurrentDirectory;
                var name = Path.GetFileNameWithoutExtension(dialog.FileName);
                _outputText.Text = Path.Combine(folder, name + "_patched.mp4");
            }
        }
    }

    private void BrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "选择输出文件",
            Filter = "MP4 视频 (*.mp4)|*.mp4|所有文件 (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(_outputText.Text) ? "final.mp4" : Path.GetFileName(_outputText.Text),
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputText.Text = dialog.FileName;
        }
    }

    private void BrowseWorkdir(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择临时工作目录",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _workdirText.Text = dialog.SelectedPath;
        }
    }

    private void BrowsePatchForSelectedRow()
    {
        if (_segmentsGrid.CurrentRow == null)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择补丁视频",
            Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (_segmentsGrid.CurrentRow.DataBoundItem is SegmentConfig segment)
        {
            segment.Patch = dialog.FileName;
            _segmentsGrid.Refresh();
        }
        else
        {
            _segments.Add(new SegmentConfig { Patch = dialog.FileName });
        }
    }

    private void RemoveSelectedSegment()
    {
        if (_segmentsGrid.CurrentRow?.DataBoundItem is SegmentConfig segment)
        {
            _segments.Remove(segment);
        }
    }

    private void RefreshFfmpegStatus()
    {
        var bundled = VideoPatcher.FindBundledFfmpeg();
        _ffmpegText.Text = bundled ?? "未找到内置 ffmpeg，将尝试使用系统 PATH 中的 ffmpeg";
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLog), message);
            return;
        }

        _logText.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void ShowError(string message)
    {
        MessageBox.Show(this, message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
