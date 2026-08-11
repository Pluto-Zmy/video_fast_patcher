# VideoPatch Cross-Platform Avalonia Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Windows-only WinUI application with one Avalonia desktop application that ships as a self-contained Windows x64 folder and an Apple Silicon macOS DMG with bundled FFmpeg tools.

**Architecture:** Move configuration, validation, process execution, progress calculation, and video patching into `VideoPatch.Core`; expose platform differences through explicit tool-layout and encoder-profile types. Build `VideoPatch.Desktop` as an MVVM Avalonia shell over that core, then package the same desktop assembly separately for `win-x64` and `osx-arm64`.

**Tech Stack:** .NET 8, C# 12, Avalonia 12.1.0, CommunityToolkit.Mvvm 8.4.2, xUnit 3.2.2, FFmpeg 9.0, PowerShell 7, POSIX shell and macOS `hdiutil`/`codesign`/`xcrun`.

## Global Constraints

- Support Windows x64 and Apple Silicon macOS only; do not add Intel Mac support.
- Keep the existing `config.json` schema and Chinese `时:分:秒` format backward compatible.
- Both distributions must be self-contained and include FFmpeg plus FFprobe; users install neither .NET nor Homebrew.
- Windows remains a folder distribution; macOS is an `.app` inside a standard `.dmg`.
- The first macOS release has no Developer ID signature or notarization; keep optional signing/notarization hooks for later use.
- Use argument-list process APIs; never construct FFmpeg commands for shell execution.
- Preserve quick concat, re-encode, progress, remaining time, cancellation, per-launch logs, and safe temporary-directory cleanup.
- Write each production behavior test first, run it and observe the expected failure, then add only enough production code to pass.
- Pin NuGet packages centrally: Avalonia packages `12.1.0`, CommunityToolkit.Mvvm `8.4.2`, and xUnit `3.2.2`.

---

## Planned File Structure

```text
VideoPatch.sln
Directory.Packages.props
src/
  VideoPatch.Core/
    VideoPatch.Core.csproj
    Configuration/PatchConfig.cs
    Platform/PlatformKind.cs
    Platform/ToolLayout.cs
    Platform/PlatformToolResolver.cs
    Encoding/VideoEncoderProfile.cs
    Encoding/EncoderProfiles.cs
    Processing/IVideoPatchService.cs
    Processing/VideoPatchProgress.cs
    Processing/VideoPatcher.cs
    Processing/ProgressPlan.cs
    Processing/FfmpegProgressParser.cs
    Processing/IProcessRunner.cs
    Processing/ProcessRunner.cs
    Encoding/EncoderSelector.cs
    Storage/PathSafety.cs
  VideoPatch.Desktop/
    VideoPatch.Desktop.csproj
    Program.cs
    App.axaml
    App.axaml.cs
    Views/MainWindow.axaml
    Views/MainWindow.axaml.cs
    ViewModels/MainWindowViewModel.cs
    Services/AvaloniaFileDialogService.cs
    Services/DialogService.cs
    Services/LogService.cs
    Assets/AppIcon.svg
    Assets/AppIcon.png
    Packaging/macos/Info.plist
tests/
  VideoPatch.Tests/
    VideoPatch.Tests.csproj
    Configuration/PatchConfigTests.cs
    Processing/TimelineTests.cs
    Processing/ProgressParserTests.cs
    Storage/PathSafetyTests.cs
    Platform/PlatformToolResolverTests.cs
    Platform/EncoderProfilesTests.cs
    ViewModels/MainWindowViewModelTests.cs
    Integration/VideoPatcherIntegrationTests.cs
tools/ffmpeg/
  windows-x64/
  macos-arm64/
scripts/fetch-ffmpeg-macos.sh
build-windows.ps1
build-macos.sh
README.md
```

The old `VideoPatchGui` directory remains available until the Avalonia application passes parity checks; Task 8 removes it in the same commit that updates the final documentation.

---

### Task 1: Create the solution and migrate configuration/validation

**Files:**
- Create: `VideoPatch.sln`
- Create: `Directory.Packages.props`
- Create: `src/VideoPatch.Core/VideoPatch.Core.csproj`
- Create: `src/VideoPatch.Core/Configuration/PatchConfig.cs`
- Create: `src/VideoPatch.Core/Processing/Timeline.cs`
- Create: `tests/VideoPatch.Tests/VideoPatch.Tests.csproj`
- Create: `tests/VideoPatch.Tests/Configuration/PatchConfigTests.cs`
- Create: `tests/VideoPatch.Tests/Processing/TimelineTests.cs`

**Interfaces:**
- Produces: `PatchConfig`, `FfmpegConfig`, `ConcatConfig`, `SegmentConfig`, `ConcatModes.Normalize(string?)`, `Timeline.ParseTimestamp(string, string)`, and `Timeline.Validate(IReadOnlyList<TimedSegment>)`.
- Consumes: Existing JSON schema and validation behavior from `VideoPatchGui/PatchConfig.cs` and `VideoPatchGui/VideoPatcher.cs`.

- [ ] **Step 1: Install or expose a .NET 8 SDK and record the version**

Run:

```bash
dotnet --version
```

Expected: an `8.0.x` SDK. If `dotnet` is unavailable, install the official .NET 8 SDK after obtaining permission for a machine-wide install, or use a workspace-local SDK; do not proceed with unverified source edits.

- [ ] **Step 2: Scaffold the solution, core project, and test project**

Create a solution referencing both projects. Use `net8.0`, nullable reference types, implicit usings, and C# 12. Put package versions in `Directory.Packages.props`; the test project references `xunit.v3` and the core project.

Run:

```bash
dotnet restore VideoPatch.sln
```

Expected: restore succeeds with no package downgrade warnings.

- [ ] **Step 3: Write failing compatibility and timeline tests**

Add literal expectations, including:

```csharp
[Fact]
public void Deserialize_LegacyConfig_PreservesSchemaAndNormalizesLegacyMode()
{
    const string json = """
        {"input":"in.mp4","output":"out.mp4","workdir":"_patch_tmp",
         "ffmpeg":{"overwrite":true,"loglevel":"warning"},
         "concat":{"mode":"encode","container":"mp4"},
         "segments":[{"start":"00:00:01","end":"00:00:02","patch":"p.mp4"}]}
        """;

    var config = JsonSerializer.Deserialize<PatchConfig>(json)!;

    Assert.Equal("in.mp4", config.Input);
    Assert.Equal("重新编码（更慢）", ConcatModes.Normalize(config.Concat.Mode));
    Assert.Equal("00:00:01", config.Segments.Single().Start);
}

[Theory]
[InlineData("00:03:12", 192)]
[InlineData("25:00:00", 90000)]
public void ParseTimestamp_ValidText_ReturnsExpectedSeconds(string text, int seconds)
{
    Assert.Equal(TimeSpan.FromSeconds(seconds), Timeline.ParseTimestamp(text, "开始时间"));
}

[Fact]
public void Validate_OverlappingSegments_Throws()
{
    var segments = new[]
    {
        new TimedSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "a.mp4"),
        new TimedSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "b.mp4")
    };

    var error = Assert.Throws<InvalidOperationException>(() => Timeline.Validate(segments));
    Assert.Contains("重叠", error.Message);
}
```

- [ ] **Step 4: Run the tests and verify RED**

Run:

```bash
dotnet test tests/VideoPatch.Tests/VideoPatch.Tests.csproj
```

Expected: compilation fails only because the new configuration and timeline types do not exist.

- [ ] **Step 5: Move the config types and implement minimal timeline APIs**

Copy the JSON property names/defaults without WinUI references. Implement:

```csharp
public sealed record TimedSegment(TimeSpan Start, TimeSpan End, string PatchPath);

public static class Timeline
{
    public static TimeSpan ParseTimestamp(string value, string fieldName)
    {
        var parts = value.Trim().Split(':');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length != 2 || parts[2].Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            minutes is < 0 or > 59 || seconds is < 0 or > 59)
        {
            throw new InvalidOperationException($"{fieldName}格式无效，必须是 时:分:秒，例如 00:03:12。当前值: {value}");
        }

        try { return new TimeSpan(hours, minutes, seconds); }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException($"{fieldName}超出支持范围。当前值: {value}");
        }
    }

    public static void Validate(IReadOnlyList<TimedSegment> segments)
    {
        var cursor = TimeSpan.Zero;
        foreach (var segment in segments)
        {
            if (segment.Start >= segment.End)
                throw new InvalidOperationException($"片段时间无效: {segment.Start} - {segment.End}");
            if (segment.Start < cursor)
                throw new InvalidOperationException($"片段时间重叠或倒序: {segment.Start} - {segment.End}");
            cursor = segment.End;
        }
    }
}
```

Make `ConcatModes` public because both the desktop project and tests consume it.

- [ ] **Step 6: Run all tests and commit**

Run:

```bash
dotnet test VideoPatch.sln
git add VideoPatch.sln Directory.Packages.props src/VideoPatch.Core tests/VideoPatch.Tests
git commit -m "refactor: extract cross-platform configuration core"
```

Expected: all tests pass with zero warnings.

---

### Task 2: Add platform tool and encoder policies

**Files:**
- Create: `src/VideoPatch.Core/Platform/PlatformKind.cs`
- Create: `src/VideoPatch.Core/Platform/ToolLayout.cs`
- Create: `src/VideoPatch.Core/Platform/PlatformToolResolver.cs`
- Create: `src/VideoPatch.Core/Encoding/VideoEncoderProfile.cs`
- Create: `src/VideoPatch.Core/Encoding/EncoderProfiles.cs`
- Create: `tests/VideoPatch.Tests/Platform/PlatformToolResolverTests.cs`
- Create: `tests/VideoPatch.Tests/Platform/EncoderProfilesTests.cs`

**Interfaces:**
- Produces: `PlatformKind`, `ToolLayout`, `PlatformToolResolver.Resolve(PlatformKind, string)`, and `EncoderProfiles.For(PlatformKind)`.
- Consumes: an explicit platform and base directory so tests never depend on the host OS.

- [ ] **Step 1: Write failing tool-layout tests**

```csharp
[Theory]
[InlineData(PlatformKind.Windows, "ffmpeg.exe", "ffprobe.exe")]
[InlineData(PlatformKind.MacOSArm64, "ffmpeg", "ffprobe")]
public void Resolve_ReturnsPlatformExecutableNames(
    PlatformKind platform, string ffmpegName, string ffprobeName)
{
    var layout = PlatformToolResolver.Resolve(platform, "/app/Contents/MacOS");

    Assert.Equal(ffmpegName, Path.GetFileName(layout.FfmpegPath));
    Assert.Equal(ffprobeName, Path.GetFileName(layout.FfprobePath));
}

[Fact]
public void Resolve_MacOS_UsesBundleResourcesDirectory()
{
    var layout = PlatformToolResolver.Resolve(PlatformKind.MacOSArm64, "/app/Contents/MacOS");

    Assert.Equal(
        Path.GetFullPath("/app/Contents/Resources/tools/ffmpeg/macos-arm64/ffmpeg"),
        layout.FfmpegPath);
}
```

- [ ] **Step 2: Write failing encoder-policy tests**

```csharp
[Fact]
public void For_MacOS_PrefersVideoToolboxThenCpu()
{
    Assert.Equal(
        new[] { "h264_videotoolbox", "libx264" },
        EncoderProfiles.For(PlatformKind.MacOSArm64).Select(x => x.Name));
}

[Fact]
public void For_Windows_DoesNotOfferMacOrMediaFoundationEncoder()
{
    var names = EncoderProfiles.For(PlatformKind.Windows).Select(x => x.Name).ToArray();

    Assert.Equal(new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" }, names);
}
```

- [ ] **Step 3: Run tests and verify RED**

Run:

```bash
dotnet test tests/VideoPatch.Tests/VideoPatch.Tests.csproj
```

Expected: fails because platform policies are missing.

- [ ] **Step 4: Implement explicit immutable policies**

```csharp
public enum PlatformKind { Windows, MacOSArm64 }

public sealed record ToolLayout(string FfmpegPath, string FfprobePath, string ResourceDirectory);

public sealed record VideoEncoderProfile(
    string Name,
    string DisplayName,
    IReadOnlyList<string> Arguments,
    bool HardwareAccelerated);
```

Use `OperatingSystem.IsWindows()` and `RuntimeInformation.ProcessArchitecture` only in a small `CurrentPlatform.Detect()` boundary. Throw a Chinese `PlatformNotSupportedException` on macOS x64 or any unsupported OS.

- [ ] **Step 5: Run tests and commit**

```bash
dotnet test VideoPatch.sln
git add src/VideoPatch.Core/Platform src/VideoPatch.Core/Encoding tests/VideoPatch.Tests/Platform
git commit -m "feat: add cross-platform ffmpeg policies"
```

Expected: all tests pass.

---

### Task 3: Port the video engine into the core

**Files:**
- Create: `src/VideoPatch.Core/Processing/IVideoPatchService.cs`
- Create: `src/VideoPatch.Core/Processing/VideoPatchProgress.cs`
- Create: `src/VideoPatch.Core/Processing/ProgressPlan.cs`
- Create: `src/VideoPatch.Core/Processing/FfmpegProgressParser.cs`
- Create: `src/VideoPatch.Core/Processing/IProcessRunner.cs`
- Create: `src/VideoPatch.Core/Processing/ProcessRunner.cs`
- Create: `src/VideoPatch.Core/Processing/VideoPatcher.cs`
- Create: `src/VideoPatch.Core/Encoding/EncoderSelector.cs`
- Create: `src/VideoPatch.Core/Storage/PathSafety.cs`
- Create: `tests/VideoPatch.Tests/Processing/ProgressParserTests.cs`
- Create: `tests/VideoPatch.Tests/Storage/PathSafetyTests.cs`
- Create: `tests/VideoPatch.Tests/Processing/EncoderFallbackTests.cs`

**Interfaces:**
- Consumes: `PatchConfig`, `ToolLayout`, `EncoderProfiles.For(PlatformKind)`.
- Produces: `IVideoPatchService.RunAsync(PatchConfig, string, IProgress<VideoPatchProgress>, CancellationToken)` and reusable path/progress helpers.

- [ ] **Step 1: Write failing path-safety tests**

Use real temporary directories, not mocks:

```csharp
[Fact]
public void CanDeleteWorkDirectory_SourceInsideWorkDirectory_ReturnsFalse()
{
    using var fixture = TempDirectory.Create();
    var source = Path.Combine(fixture.Path, "input.mp4");
    File.WriteAllText(source, "fixture");

    Assert.False(PathSafety.CanDeleteWorkDirectory(fixture.Path, new[] { source }));
}

[Fact]
public void CanDeleteWorkDirectory_SiblingPrefix_ReturnsTrue()
{
    using var root = TempDirectory.Create();
    var work = Directory.CreateDirectory(Path.Combine(root.Path, "work")).FullName;
    var source = Path.Combine(root.Path, "work-old", "input.mp4");

    Assert.True(PathSafety.CanDeleteWorkDirectory(work, new[] { source }));
}
```

This second test prevents prefix bugs such as treating `/tmp/work-old` as a child of `/tmp/work`.

- [ ] **Step 2: Write failing progress parser tests**

```csharp
[Theory]
[InlineData("out_time_us=1500000", 1.5)]
[InlineData("out_time_ms=1500000", 1.5)]
[InlineData("out_time=00:00:01.500000", 1.5)]
public void ParseTime_ValidProgressLine_ReturnsSeconds(string line, double seconds)
{
    Assert.Equal(seconds, FfmpegProgressParser.ParseTime(line)!.Value.TotalSeconds, 3);
}
```

- [ ] **Step 3: Run focused tests and verify RED**

```bash
dotnet test tests/VideoPatch.Tests/VideoPatch.Tests.csproj
```

Expected: fails because the extracted helpers do not exist.

- [ ] **Step 4: Implement the helpers and make focused tests GREEN**

Implement filesystem comparisons with `StringComparison.OrdinalIgnoreCase` on Windows and `StringComparison.Ordinal` on macOS. Reject filesystem roots. Parse FFmpeg progress with invariant culture.

Run the focused test command again; expected: pass.

- [ ] **Step 5: Write a failing encoder fallback test around the real selection service**

Use a deterministic fake process runner that returns full `ProcessResult` values for the external-process boundary. Assert the selected profile returned by the real selector, not the fake's invocation count:

```csharp
[Fact]
public async Task SelectAsync_WhenHardwareProbesFail_ReturnsLibx264()
{
    var runner = new ScriptedProcessRunner(exitCodes: new[] { 1, 1, 1 });
    var selector = new EncoderSelector(runner, EncoderProfiles.For(PlatformKind.Windows));

    var selected = await selector.SelectAsync("ffmpeg.exe", CancellationToken.None);

    Assert.Equal("libx264", selected.Name);
}
```

Define the process boundary once and use it for encoder probes, duration probes, and processing:

```csharp
public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? ToolDirectory = null);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<string>? onStandardOutputLine,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 6: Port the existing engine and replace Windows assumptions**

Move the processing behavior from `VideoPatchGui/VideoPatcher.cs`; do not rewrite working algorithms. Make these exact changes:

- accept a `ToolLayout` instead of locating `.exe` files internally;
- accept platform encoder candidates instead of a static Windows list;
- use `IProcessRunner` for FFmpeg and FFprobe process execution;
- report one immutable `VideoPatchProgress(Status, Value, Remaining)` object;
- delegate time parsing to `Timeline` and deletion decisions to `PathSafety`;
- preserve `ProcessStartInfo.ArgumentList`, redirected output/error, PATH augmentation, cancellation and whole-tree kill;
- retain GPU failure cleanup and CPU retry without catching validation or cancellation errors.

The service boundary is:

```csharp
public interface IVideoPatchService
{
    Task RunAsync(
        PatchConfig config,
        string baseDirectory,
        IProgress<VideoPatchProgress> progress,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 7: Run core tests, then all tests, and commit**

```bash
dotnet test tests/VideoPatch.Tests/VideoPatch.Tests.csproj
dotnet test VideoPatch.sln
git add src/VideoPatch.Core/Processing src/VideoPatch.Core/Storage tests/VideoPatch.Tests/Processing tests/VideoPatch.Tests/Storage
git commit -m "refactor: port video engine to cross-platform core"
```

Expected: all tests pass and no `Microsoft.UI`, `Windows.*`, `user32`, or literal `ffmpeg.exe` reference exists under `src/VideoPatch.Core` except Windows test expectations.

---

### Task 4: Build and test the desktop ViewModel

**Files:**
- Create: `src/VideoPatch.Desktop/VideoPatch.Desktop.csproj`
- Create: `src/VideoPatch.Desktop/ViewModels/MainWindowViewModel.cs`
- Create: `src/VideoPatch.Desktop/Services/IFileDialogService.cs`
- Create: `src/VideoPatch.Desktop/Services/IDialogService.cs`
- Create: `src/VideoPatch.Desktop/Services/ILogService.cs`
- Create: `src/VideoPatch.Desktop/Services/LogService.cs`
- Modify: `tests/VideoPatch.Tests/VideoPatch.Tests.csproj`
- Create: `tests/VideoPatch.Tests/ViewModels/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `IVideoPatchService`, `PlatformToolResolver`, and the configuration models.
- Produces: bindable properties and commands used by `MainWindow.axaml`.

- [ ] **Step 1: Add the desktop reference and write failing state tests**

```csharp
[Fact]
public async Task RunCommand_WhileServiceRuns_DisablesEditingAndEnablesCancel()
{
    var service = new PausingVideoPatchService();
    var vm = ViewModelFixture.Create(service);
    vm.Input = "input.mp4";
    vm.Output = "output.mp4";
    vm.Segments.Add(new SegmentConfig { Start = "00:00:01", End = "00:00:02", Patch = "patch.mp4" });

    var run = vm.RunCommand.ExecuteAsync(null);
    await service.Started;

    Assert.True(vm.IsRunning);
    Assert.False(vm.CanEdit);
    Assert.True(vm.CancelCommand.CanExecute(null));

    service.Complete();
    await run;
}

[Fact]
public async Task CancelCommand_CancelledRun_ShowsCancelledStateWithoutErrorDialog()
{
    var service = new PausingVideoPatchService();
    var dialogs = new RecordingDialogService();
    var vm = ViewModelFixture.Create(service, dialogs);

    var run = vm.RunCommand.ExecuteAsync(null);
    await service.Started;
    vm.CancelCommand.Execute(null);
    await run;

    Assert.Equal("已取消", vm.Status);
    Assert.Empty(dialogs.Errors);
}
```

- [ ] **Step 2: Run and verify RED**

```bash
dotnet test tests/VideoPatch.Tests/VideoPatch.Tests.csproj
```

Expected: fails because the ViewModel and service contracts are missing.

- [ ] **Step 3: Implement the minimal MVVM state and commands**

Use `ObservableObject`, `ObservableProperty`, `RelayCommand`, and `AsyncRelayCommand`. Expose:

```csharp
public ObservableCollection<SegmentConfig> Segments { get; }
public bool IsRunning { get; }
public bool CanEdit => !IsRunning;
public int ProgressValue { get; }
public string Status { get; }
public string RemainingTimeText { get; }
public IAsyncRelayCommand RunCommand { get; }
public IRelayCommand CancelCommand { get; }
```

Also expose open/save/save-as, browse input/output/workdir/patch, add-segment, and remove-segment commands. Keep all picker and message behavior behind services. Keep the current config path so relative paths resolve from the opened JSON file.

- [ ] **Step 4: Add log-location tests and implement `LogService`**

Test candidate calculation with explicit platform and home/local-app-data inputs. Windows returns `%LOCALAPPDATA%/VideoPatch/logs`; macOS returns `$HOME/Library/Logs/VideoPatch`. Test unique filenames and temp fallback using real temporary directories.

- [ ] **Step 5: Run tests and commit**

```bash
dotnet test VideoPatch.sln
git add src/VideoPatch.Desktop tests/VideoPatch.Tests
git commit -m "feat: add cross-platform desktop view model"
```

Expected: all tests pass; tests assert ViewModel outcomes rather than fake call counts.

---

### Task 5: Build the shared Avalonia UI

**Files:**
- Create: `src/VideoPatch.Desktop/Program.cs`
- Create: `src/VideoPatch.Desktop/App.axaml`
- Create: `src/VideoPatch.Desktop/App.axaml.cs`
- Create: `src/VideoPatch.Desktop/Views/MainWindow.axaml`
- Create: `src/VideoPatch.Desktop/Views/MainWindow.axaml.cs`
- Create: `src/VideoPatch.Desktop/Services/AvaloniaFileDialogService.cs`
- Create: `src/VideoPatch.Desktop/Services/DialogService.cs`
- Copy: `VideoPatchGui/Assets/AppIcon.svg` to `src/VideoPatch.Desktop/Assets/AppIcon.svg`
- Copy: `VideoPatchGui/Assets/AppIcon.png` to `src/VideoPatch.Desktop/Assets/AppIcon.png`

**Interfaces:**
- Consumes: every bindable property/command from `MainWindowViewModel`.
- Produces: one resizable native-decorated desktop window for Windows and macOS.

- [ ] **Step 1: Add Avalonia package references and minimal startup files**

Reference `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, and `CommunityToolkit.Mvvm` using central versions. Configure startup:

```csharp
[STAThread]
public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

public static AppBuilder BuildAvaloniaApp() =>
    AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
```

- [ ] **Step 2: Build and verify the empty shell before adding layout**

```bash
dotnet build src/VideoPatch.Desktop/VideoPatch.Desktop.csproj
dotnet run --project src/VideoPatch.Desktop/VideoPatch.Desktop.csproj
```

Expected: a native-decorated empty window opens on the current platform and closes cleanly.

- [ ] **Step 3: Translate the existing information architecture to Avalonia XAML**

Use a `Grid` with toolbar, settings card, segment editor, and status/footer rows. Use `ScrollViewer` for the full page and a flexible segment list so the minimum window can be approximately `820x650`. Bind all actions to commands; code-behind may only construct picker/dialog services that require a `TopLevel` and assign `DataContext`.

Avoid WinUI-only elements (`CommandBar`, `AppBarButton`, `SymbolIcon`, theme-resource names, custom titlebar). Use Avalonia `Button`, `TextBox`, `ComboBox`, `ListBox`, `ProgressBar`, `Border`, and `DataTemplate`.

- [ ] **Step 4: Implement native file dialogs and modal messages**

Use `TopLevel.StorageProvider.OpenFilePickerAsync`, `SaveFilePickerAsync`, and `OpenFolderPickerAsync`. Return local filesystem paths only; show a Chinese error if a provider returns a non-file URI. Use an Avalonia owner window for modal completion/error dialogs.

- [ ] **Step 5: Build, run ViewModel tests, and manually smoke-test bindings**

```bash
dotnet build VideoPatch.sln
dotnet test VideoPatch.sln
dotnet run --project src/VideoPatch.Desktop/VideoPatch.Desktop.csproj
```

Expected manual checks: add/delete segment works; pickers populate fields; legacy config opens; run disables editing; cancel remains enabled; window remains usable at `820x650`.

- [ ] **Step 6: Commit**

```bash
git add src/VideoPatch.Desktop tests/VideoPatch.Tests
git commit -m "feat: replace WinUI shell with Avalonia"
```

---

### Task 6: Restructure bundled tools and publish Windows x64

**Files:**
- Move: `VideoPatchGui/tools/ffmpeg/*` to `tools/ffmpeg/windows-x64/`
- Modify: `src/VideoPatch.Desktop/VideoPatch.Desktop.csproj`
- Rewrite: `build-windows.ps1`
- Create: `scripts/verify-windows-publish.ps1`

**Interfaces:**
- Consumes: Windows `ToolLayout` from Task 2.
- Produces: `publish/win-x64/VideoPatch.exe` plus `tools/ffmpeg/windows-x64`.

- [ ] **Step 1: Move the existing Windows FFmpeg distribution intact**

Preserve executables, DLLs, `LICENSE`, `README.txt`, and `FFMPEG-NOTICE.txt`. Update the desktop project to copy only `tools/ffmpeg/windows-x64/**` when `RuntimeIdentifier == win-x64`.

- [ ] **Step 2: Write the Windows publish verifier before changing the build**

The script accepts one publish-directory argument, returns nonzero if any required path is missing, runs:

```powershell
& "$PublishDir\tools\ffmpeg\windows-x64\ffmpeg.exe" -hide_banner -version
& "$PublishDir\tools\ffmpeg\windows-x64\ffprobe.exe" -hide_banner -version
```

and requires both exit codes to be zero.

- [ ] **Step 3: Run the verifier against no output and observe RED**

```powershell
pwsh ./scripts/verify-windows-publish.ps1 ./publish/win-x64
```

Expected: nonzero exit with a missing `VideoPatch.exe` or FFmpeg path message.

- [ ] **Step 4: Rewrite the Windows build and publish**

Publish `src/VideoPatch.Desktop/VideoPatch.Desktop.csproj` with `-c Release -r win-x64 --self-contained true -o publish/win-x64`. Preserve the existing workspace-bound cleanup guard and language-resource trimming.

- [ ] **Step 5: Verify on Windows and commit**

```powershell
pwsh ./build-windows.ps1
pwsh ./scripts/verify-windows-publish.ps1 ./publish/win-x64
```

Expected: both succeed on a Windows x64 build host and the app opens without an installed .NET runtime.

```bash
git add tools/ffmpeg/windows-x64 src/VideoPatch.Desktop/VideoPatch.Desktop.csproj build-windows.ps1 scripts/verify-windows-publish.ps1
git commit -m "build: publish self-contained Windows Avalonia app"
```

---

### Task 7: Bundle ARM64 FFmpeg and create the macOS DMG

**Files:**
- Create: `scripts/fetch-ffmpeg-macos.sh`
- Create: `tools/ffmpeg/macos-arm64/README.txt`
- Create: `tools/ffmpeg/macos-arm64/FFMPEG-NOTICE.txt`
- Add: `tools/ffmpeg/macos-arm64/ffmpeg`
- Add: `tools/ffmpeg/macos-arm64/ffprobe`
- Create: `src/VideoPatch.Desktop/Packaging/macos/Info.plist`
- Create: `build-macos.sh`
- Create: `scripts/verify-macos-publish.sh`
- Modify: `src/VideoPatch.Desktop/VideoPatch.Desktop.csproj`

**Interfaces:**
- Consumes: macOS ARM64 `ToolLayout` from Task 2.
- Produces: `publish/macos-arm64/VideoPatch.app` and `publish/macos-arm64/VideoPatch.dmg`.

- [ ] **Step 1: Write a pinned FFmpeg acquisition script**

Download the pinned FFmpeg 9.0 ARM64 release ZIPs from:

```text
https://ffmpeg.martin-riedl.de/download/macos/arm64/1785863997_9.0/ffmpeg.zip
https://ffmpeg.martin-riedl.de/download/macos/arm64/1785863997_9.0/ffprobe.zip
```

Verify the archives against these literal hashes before extracting:

```text
ffmpeg.zip  5267ef149ee0d208057a1b316aac079b661b0476574dee5da7d225769773c603
ffprobe.zip 7778fbb533fb60d3336cbd9a9e51eced71658f020b570c7203590c1c41d42f50
```

Record release `9.0`, the source URLs, and the same SHA-256 values in `README.txt`. Never follow a “latest” redirect or silently replace committed binaries. Copy the applicable GPL license/source-offer notice.

- [ ] **Step 2: Fetch and inspect the binaries**

```bash
./scripts/fetch-ffmpeg-macos.sh
file tools/ffmpeg/macos-arm64/ffmpeg tools/ffmpeg/macos-arm64/ffprobe
tools/ffmpeg/macos-arm64/ffmpeg -hide_banner -encoders
```

Expected: both files report Mach-O `arm64`; encoder output contains `libx264` and `h264_videotoolbox`.

- [ ] **Step 3: Write the macOS publish verifier and observe RED**

The verifier must behaviorally check:

```bash
test -x publish/macos-arm64/VideoPatch.app/Contents/MacOS/VideoPatch
test -x publish/macos-arm64/VideoPatch.app/Contents/Resources/tools/ffmpeg/macos-arm64/ffmpeg
file publish/macos-arm64/VideoPatch.app/Contents/MacOS/VideoPatch | grep -q arm64
publish/macos-arm64/VideoPatch.app/Contents/Resources/tools/ffmpeg/macos-arm64/ffmpeg -hide_banner -version
hdiutil verify publish/macos-arm64/VideoPatch.dmg
```

Run it before building; expected: nonzero exit because the app bundle is absent.

- [ ] **Step 4: Create `Info.plist` and the `.app` bundle script**

Use bundle identifier `com.videopatch.desktop`, executable `VideoPatch`, category `public.app-category.video`, version `1.0.1`, high-resolution support, and `NSPrincipalClass=NSApplication`. Publish `osx-arm64` self-contained files into a staging directory, copy them into `Contents/MacOS`, copy assets and FFmpeg into `Contents/Resources`, and apply executable permissions.

On Apple Silicon, apply an ad-hoc signature when no Developer ID identity is supplied so all Mach-O code has a valid local signature; label the result clearly as “未使用 Developer ID 签名，未公证”. If `MACOS_SIGN_IDENTITY` is supplied, sign nested FFmpeg binaries first and the `.app` last with hardened runtime.

- [ ] **Step 5: Create and optionally notarize the DMG**

Build a read-only compressed DMG with `hdiutil create`, containing `VideoPatch.app` and an `Applications` symlink. If and only if all of `MACOS_SIGN_IDENTITY`, `APPLE_TEAM_ID`, and `APPLE_NOTARY_PROFILE` are set, submit the app archive, staple the app, rebuild/sign the DMG, submit the DMG, and staple it. Otherwise print the unsigned/unnotarized first-launch instructions.

- [ ] **Step 6: Build, verify, mount, launch, and commit**

```bash
./build-macos.sh
./scripts/verify-macos-publish.sh publish/macos-arm64
hdiutil attach publish/macos-arm64/VideoPatch.dmg -nobrowse
```

Expected: verification succeeds, DMG mounts, the app opens via Finder “右键 → 打开”, and the UI reports the bundled ARM64 FFmpeg path.

```bash
git add scripts/fetch-ffmpeg-macos.sh scripts/verify-macos-publish.sh build-macos.sh tools/ffmpeg/macos-arm64 src/VideoPatch.Desktop/Packaging src/VideoPatch.Desktop/VideoPatch.Desktop.csproj
git commit -m "build: add Apple Silicon DMG distribution"
```

---

### Task 8: Verify feature parity, update documentation, and remove WinUI

**Files:**
- Create: `tests/VideoPatch.Tests/Integration/VideoPatcherIntegrationTests.cs`
- Create: `tests/fixtures/create-fixtures.sh`
- Modify: `README.md`
- Modify: `.gitignore`
- Delete: `VideoPatchGui/`

**Interfaces:**
- Consumes: the completed core, desktop app, bundled tools, and both build scripts.
- Produces: a single documented Avalonia codebase with automated processing smoke tests.

- [ ] **Step 1: Generate deterministic media fixtures**

Use the bundled FFmpeg to create a two-second color/test-tone input and a one-second patch in a temporary test-output directory. Do not commit generated MP4 files.

- [ ] **Step 2: Write failing end-to-end core tests**

Run the real `VideoPatcher` with real FFmpeg against the fixtures. Test:

- quick concat creates a probeable non-empty output;
- re-encode creates H.264/AAC output and reaches progress `1000`;
- cancellation throws `OperationCanceledException` and leaves no child process;
- a protected source inside the work directory prevents recursive cleanup.

Gate these tests with a clear skip reason only when the platform's committed FFmpeg binary is unavailable; CI/release jobs must provide the binary and may not skip them.

- [ ] **Step 3: Run integration tests and verify RED for any remaining portability defect**

```bash
dotnet test tests/VideoPatch.Tests/VideoPatch.Tests.csproj
```

Expected: the tests execute the real engine and tools. Preserve any portability failure as the RED evidence for Step 4; if all pass because the extracted behavior is already correct, record that result and do not manufacture a failure.

- [ ] **Step 4: Fix only defects reproduced by integration tests**

For each defect, keep the failing test, apply the smallest core or packaging correction, and rerun the focused test before proceeding.

- [ ] **Step 5: Replace the README with cross-platform instructions**

Document prerequisites for developers, Windows build/use, Apple Silicon build/use, the Finder right-click flow for the unnotarized DMG, bundled FFmpeg license/source, config compatibility, logs, and optional future signing environment variables. State explicitly that Intel Mac is unsupported.

- [ ] **Step 6: Remove the old WinUI project after parity is demonstrated**

Delete `VideoPatchGui/` only after the Avalonia UI, unit tests, integration tests, Windows publish verifier, and macOS publish verifier all pass. Confirm no solution, script, or documentation path still references WinUI artifacts.

- [ ] **Step 7: Run final verification**

On Apple Silicon macOS:

```bash
dotnet test VideoPatch.sln
dotnet build VideoPatch.sln -c Release
./build-macos.sh
./scripts/verify-macos-publish.sh publish/macos-arm64
git diff --check
git status --short
```

On Windows x64:

```powershell
dotnet test VideoPatch.sln
dotnet build VideoPatch.sln -c Release
pwsh ./build-windows.ps1
pwsh ./scripts/verify-windows-publish.ps1 ./publish/win-x64
git diff --check
git status --short
```

Expected: all tests/builds/verifiers succeed; the only status entries are the intended migration changes.

- [ ] **Step 8: Commit the completed migration**

```bash
git add README.md .gitignore VideoPatch.sln Directory.Packages.props src tests tools scripts build-windows.ps1 build-macos.sh
git add -u VideoPatchGui
git commit -m "feat: ship VideoPatch for Windows and Apple Silicon"
```

Before claiming completion, retain the complete macOS and Windows command output as verification evidence.
