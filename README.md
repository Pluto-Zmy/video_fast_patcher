# 视频修补工具 Windows GUI

这是一个 .NET 8 + WinUI 3 桌面程序。程序优先使用随发布目录携带的 `tools\ffmpeg\ffmpeg.exe`。

## 构建

```powershell
.\build-windows.ps1
```

发布产物在 `publish\win-x64`，入口是 `VideoPatch.exe`。发布命令使用 self-contained 模式，目标机器不需要单独安装 .NET 运行时。

WinUI 3 版本使用文件夹式发布。分发时请完整复制 `publish\win-x64` 目录，不要只复制 `VideoPatch.exe`。发布目录中的 `tools\ffmpeg` 是程序运行所需的内置 ffmpeg，也需要一起分发。

## 使用

1. 打开 `VideoPatch.exe`。
2. 选择输入视频、输出文件和工作目录。
3. 在表格中填写每个需要替换的时间段和补丁视频。开始时间和结束时间格式为 `时:分:秒`，例如 `00:03:12`。
4. 点击“开始修补”。

也可以直接打开或保存 `config.json` 配置文件。

修补成功后，程序会自动删除临时工作目录；如果输入、输出或补丁文件位于该目录内，会跳过删除并在日志中提示。

执行过程中，底部进度条会通过内置 `ffprobe.exe` 获取视频时长，并解析 ffmpeg 的 `-progress` 输出显示实际处理进度。
