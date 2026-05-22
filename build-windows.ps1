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

$keptLanguages = @("en", "zh")
$removedCultureDirectories = @()

Get-ChildItem -LiteralPath $publishDir -Directory | ForEach-Object {
    $directory = $_
    $language = $null

    try {
        $culture = [System.Globalization.CultureInfo]::GetCultureInfo($directory.Name)
        $language = $culture.TwoLetterISOLanguageName.ToLowerInvariant()
    }
    catch {
        if ($directory.Name -match "^(?<language>[a-z]{2,3})(?:-[A-Za-z0-9]+)+$") {
            $language = $Matches.language.ToLowerInvariant()
        }
    }

    if ($language -and ($keptLanguages -notcontains $language)) {
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
        $removedCultureDirectories += $directory.Name
    }
}

Write-Host "Published to: $publishDir"
Write-Host "Trimmed language resource directories: $($removedCultureDirectories.Count)"
Write-Host "Run: $(Join-Path $publishDir 'VideoPatch.exe')"
