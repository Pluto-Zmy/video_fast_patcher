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
3. 在表格中填写每段补丁的开始时间和视频文件。开始时间格式为 `时:分:秒`，例如 `00:03:12`；结束时间由补丁视频流时长自动计算。
4. 点击“开始修补”。

也可以直接打开或保存 `config.json` 配置文件。每段只需 `start` 和 `patch`；旧配置中的 `end` 会被忽略。补丁时长可以包含小数秒，程序会检查各段是否重叠或超出原视频。
填写的开始时间应对应补丁第一帧；补丁前后若含冗余画面，其完整时长也计入替换区间。

修补成功后，程序会自动删除临时工作目录；如果输入、输出或补丁文件位于该目录内，会跳过删除并记录到日志文件中。

执行过程中，底部进度条会通过内置 `ffprobe.exe` 获取视频时长，并解析 ffmpeg 的 `-progress` 输出显示实际处理进度。

程序每次启动都会创建独立日志文件，不会覆盖旧日志。默认日志目录为发布目录下的 `logs`；如果该目录不可写，会改用 `%LOCALAPPDATA%\VideoPatch\logs`。

## H.265 / HEVC

支持 H.265 原视频和补丁。快速拼接保留原编码，要求原视频与补丁的编码参数兼容。
需要输出 H.265 时，选择“重新编码（更慢）”，将“输出编码”设为“H.265 (HEVC)”。
程序自动尝试 NVIDIA、Intel、AMD 和 Windows Media Foundation 的 HEVC 硬件编码器；
不可用或编码失败时回退 CPU libx265。MP4 重新编码输出使用 hvc1 标记。

配置文件使用 concat.video_codec：h264（默认）或 hevc（也接受 h265）。
旧配置未填写该字段时继续使用 H.264。快速拼接不使用此选项。
原视频和补丁应使用相同的视频编码；此选项不支持将 H.264 与 H.265 混合片段直接拼接。
H.265 的播放需要播放器支持 HEVC。

验证命令（在仓库根目录运行，使用内置 FFmpeg）：

    dotnet run --project tests/VideoPatch.Smoke -- .