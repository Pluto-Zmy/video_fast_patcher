$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "VideoPatchGui\VideoPatchGui.csproj"
$publishDir = Join-Path $PSScriptRoot "publish\win-x64"

if (Test-Path $publishDir) {
    $workspaceRoot = (Resolve-Path $PSScriptRoot).Path
    $resolvedPublishDir = (Resolve-Path $publishDir).Path
    if (-not $resolvedPublishDir.StartsWith($workspaceRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean publish directory outside workspace: $resolvedPublishDir"
    }

    Remove-Item -LiteralPath $resolvedPublishDir -Recurse -Force
}

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:DebugType=embedded",
    "-o", $publishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Published to: $publishDir"
Write-Host "Run: $(Join-Path $publishDir 'VideoPatch.exe')"
