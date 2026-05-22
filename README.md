# 视频修补工具 Windows GUI

这个版本把原来的 `video_patch.py` 迁移成 .NET 8 WinForms 桌面程序，不再依赖 Python 环境。程序优先使用随发布目录携带的 `tools\ffmpeg\ffmpeg.exe`。

## 构建

```powershell
.\build-windows.ps1
```

发布产物在 `publish\win-x64`，入口是 `VideoPatch.exe`。发布命令使用 self-contained 模式，目标机器不需要单独安装 .NET 运行时。

发布目录中的 `tools\ffmpeg` 是程序运行所需的内置 ffmpeg，请和 `VideoPatch.exe` 一起分发。

## 使用

1. 打开 `VideoPatch.exe`。
2. 选择输入视频、输出文件和工作目录。
3. 在表格中填写每个需要替换的时间段和补丁视频。开始时间和结束时间格式为 `时:分:秒`，例如 `00:03:12`。
4. 点击“开始修补”。

也可以直接打开或保存与旧脚本兼容的 `config.json`。

修补成功后，程序会自动删除临时工作目录；如果输入、输出或补丁文件位于该目录内，会跳过删除并在日志中提示。

执行过程中，底部进度条会通过内置 `ffprobe.exe` 获取视频时长，并解析 ffmpeg 的 `-progress` 输出显示实际处理进度。
