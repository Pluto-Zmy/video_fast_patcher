$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "VideoPatchGui\VideoPatchGui.csproj"
$publishDir = Join-Path $PSScriptRoot "publish\win-x64"

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=embedded",
    "-o", $publishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Published to: $publishDir"
Write-Host "Run: $(Join-Path $publishDir 'VideoPatch.exe')"
