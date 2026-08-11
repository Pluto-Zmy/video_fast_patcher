# VideoPatch 跨平台 Avalonia 迁移设计

## 目标

将现有 Windows 专用的 .NET 8 + WinUI 3 视频修补工具迁移为一套 Avalonia 桌面应用，同时支持：

- Windows x64，自包含文件夹式发布；
- Apple Silicon macOS，自包含 `.app` 并封装为标准 `.dmg`；
- 两个平台共用界面、业务逻辑和 `config.json` 格式；
- 两个平台均内置 FFmpeg 和 FFprobe，不要求安装 .NET、Homebrew 或其他运行环境。

首个 macOS 版本为未签名、未公证的开发构建。构建流程预留 Developer ID 签名和 Apple 公证入口，以便开发者账号恢复有效会员资格后启用。

## 非目标

- 不支持 Intel Mac；
- 不制作 Mac App Store 包；
- 不改变补丁配置文件的字段或时间格式；
- 不增加现有视频修补流程之外的新编辑能力；
- 不在首版中实现自动更新。

## 技术方案

采用 Avalonia 单一跨平台应用，替换 WinUI 3 界面。项目拆分为三个程序集：

1. `VideoPatch.Core`：平台无关的配置模型、时间线校验、FFmpeg 参数生成、进度解析和修补工作流。
2. `VideoPatch.Desktop`：Avalonia UI、ViewModel、文件选择、对话框、日志位置和应用生命周期。
3. `VideoPatch.Tests`：核心行为、平台策略和 ViewModel 状态转换测试。

Windows 和 macOS 使用同一套 Avalonia XAML 与 ViewModel。平台差异通过窄接口隔离，业务流程不得直接判断窗口系统或拼写平台专属可执行文件名。

## 组件职责

### 核心模型与配置

保留 `PatchConfig`、`FfmpegConfig`、`ConcatConfig` 和 `SegmentConfig` 的 JSON 字段名称与默认值。已有 Windows 版生成的 `config.json` 必须能直接打开、编辑和保存。

时间输入继续采用 `时:分:秒`，例如 `00:03:12`。时间线必须拒绝空片段、无效格式、开始时间不早于结束时间、片段重叠和不存在的补丁文件。

### 视频修补服务

视频修补服务负责：

- 解析相对路径；
- 通过 FFprobe 获取视频时长；
- 生成原视频切片；
- 将补丁视频与原视频片段拼接；
- 支持快速复制流和重新编码两种模式；
- 解析 FFmpeg `-progress` 输出并上报总进度、状态和剩余时间；
- 响应取消并终止整个 FFmpeg 进程树；
- 在不影响任何输入、输出或补丁源文件的前提下清理临时目录。

进程启动必须使用参数列表 API，不通过 shell 拼接命令字符串。

### 平台工具解析

`PlatformToolResolver` 根据运行平台提供 FFmpeg、FFprobe 和编码器候选：

- Windows x64：解析内置 `tools/ffmpeg/windows-x64/ffmpeg.exe` 与 `ffprobe.exe`；硬件编码依次探测 NVIDIA NVENC、Intel Quick Sync 和 AMD AMF，最终回退 `libx264`。
- macOS ARM64：解析 `.app/Contents/Resources/tools/ffmpeg/macos-arm64/ffmpeg` 与 `ffprobe`；硬件编码优先探测 `h264_videotoolbox`，最终回退 `libx264`。

编码器必须通过实际的短探测命令确认可用，不能只根据平台或 FFmpeg 编码器列表判定。Windows Media Foundation 编码器不再作为跨平台公共候选。

工具缺失或不可执行时，应用启动后显示明确的中文错误，禁止开始修补，并在日志中记录解析过的工具路径。

### Avalonia 界面

界面延续现有信息架构：

- 顶部：打开配置、保存配置、另存为；
- 文件与选项：输入视频、输出文件、工作目录、当前 FFmpeg、覆盖输出、拼接模式；
- 补丁片段：添加、删除、选择补丁文件和逐行编辑开始时间、结束时间、补丁路径；
- 底部：运行状态、进度条、开始修补和取消。

Windows 与 macOS 使用相同内容布局，采用系统窗口装饰、系统字体和 Avalonia Storage Provider 文件选择器，不复制 WinUI 自定义标题栏。窗口应能缩放，在常见笔记本屏幕上不要求 1080×1220 的固定最小尺寸。

运行期间禁用所有会改变任务配置的控件，只启用取消操作；结束、取消或失败后恢复控件状态。所有用户提示使用中文，详细异常和 FFmpeg 标准错误写入日志。

## 数据流

1. 用户新建、打开或编辑配置。
2. ViewModel 将界面状态转换为 `PatchConfig` 并执行轻量输入校验。
3. 视频修补服务解析路径并完成时间线与源文件校验。
4. 平台工具解析器返回已验证的 FFmpeg、FFprobe 和编码器候选。
5. FFprobe 生成进度计划，FFmpeg 依次执行切片与拼接。
6. 服务通过回调或进度接口向 ViewModel 报告状态、千分比进度和剩余时间。
7. ViewModel 更新界面；完成后服务执行安全清理，失败或取消时记录诊断信息并恢复界面。

## 日志与错误处理

默认日志位置为：

- Windows：`%LOCALAPPDATA%\VideoPatch\logs`；
- macOS：`~/Library/Logs/VideoPatch`。

每次启动创建独立日志文件。用户可见错误包含失败阶段和可执行的解决建议，但不展示冗长命令输出。日志记录完整异常、工具路径、选定编码器、FFmpeg 退出码和标准错误摘要。

硬件编码器失败时依次尝试剩余候选，全部失败后使用 `libx264`。输入或配置错误不得触发编码器回退。取消使用 `OperationCanceledException` 路径处理，不显示为普通失败。

## 内置 FFmpeg

仓库中的平台二进制按运行时标识分目录保存，并附各自的许可证与来源说明。发布过程只复制目标平台目录，避免 Windows 包包含 macOS 二进制或反之。

macOS 的 FFmpeg 与 FFprobe 必须是 ARM64 二进制，包含 `libx264` 和 VideoToolbox 支持，并在 `.app` 中具有可执行权限。打包验证使用 `file`、`test -x` 和 FFmpeg 自检命令确认这些条件。

## 构建与分发

### Windows

`build-windows.ps1` 发布 `win-x64` 自包含应用到 `publish/win-x64`。分发物是完整目录，不承诺单文件运行。

### macOS

`build-macos.sh` 发布 `osx-arm64` 自包含应用，生成标准 bundle 结构：

```text
VideoPatch.app/
  Contents/
    Info.plist
    MacOS/VideoPatch
    Resources/Assets/
    Resources/tools/ffmpeg/macos-arm64/
```

脚本校验 bundle 后使用系统工具生成 `publish/macos-arm64/VideoPatch.dmg`。首版不签名、不公证，使用说明必须解释首次启动时通过 Finder 右键选择“打开”。

脚本接受可选签名身份、Team ID 和公证配置；只有显式提供这些参数时才执行 `codesign`、`notarytool` 和 stapling。未提供时不得假装产物已签名。

## 测试策略

### 单元测试

- 旧版配置 JSON 的加载与保存兼容性；
- 时间格式和时间线校验；
- Windows 与 macOS 工具路径解析；
- 各平台编码器候选顺序和 CPU 回退；
- FFmpeg 参数列表生成与特殊路径处理；
- FFmpeg 进度解析和总进度映射；
- 临时目录安全清理判定；
- ViewModel 在运行、成功、取消和失败时的控件状态。

### 集成与发布验证

使用短测试视频验证快速拼接、重新编码、取消和进度输出。Windows 发布验证检查 `win-x64` 应用启动及内置工具；macOS 发布验证检查 `.app` 目录、ARM64 架构、工具执行权限、DMG 挂载以及从挂载卷启动。

由于当前开发环境没有 `dotnet`，实施阶段必须先安装 .NET 8 SDK 或在具有 .NET 8 SDK 的构建环境中执行测试。没有实际构建与测试输出时，不得宣称迁移完成。

## 验收标准

- 同一个源码树可分别构建 Windows x64 和 Apple Silicon macOS 版本；
- 两个平台启动后均无需外部安装 FFmpeg 或 .NET；
- 现有 `config.json` 能直接使用；
- 快速拼接、重新编码、进度、剩余时间、取消和安全清理在两端可用；
- Windows 产物为完整自包含目录；
- macOS 产物为可挂载的未签名 `.dmg`，其中包含可启动的 ARM64 `.app`；
- 自动化测试通过，发布验证无缺失文件或错误架构。
