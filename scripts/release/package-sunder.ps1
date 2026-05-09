[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Runtime = "win-x64",

    [ValidateSet("stable", "beta", "nightly")]
    [string]$Channel = "stable",

    [string]$Configuration = "Release",

    [string]$OutputRoot = "artifacts",

    [string]$GitHubRepositoryUrl = "",

    [string]$GitHubToken = "",

    [switch]$IncludePrereleaseUpdates,

    [string]$WindowsSignParams = ""
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a valid SemVer value for Velopack. Use a value like 0.1.0 or 0.1.0-beta.1."
}

$vpk = Get-Command "vpk" -ErrorAction SilentlyContinue
if ($null -eq $vpk) {
    throw "The Velopack CLI 'vpk' was not found. Install it with: dotnet tool install --global vpk --version 0.0.1298"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$projectPath = Join-Path $repoRoot "src\Host\Sunder.App\Sunder.App.csproj"
$runtimeHostProjectPath = Join-Path $repoRoot "src\Host\Sunder.Runtime.Host\Sunder.Runtime.Host.csproj"
$cliProjectPath = Join-Path $repoRoot "src\Host\Sunder.Cli\Sunder.Cli.csproj"
$artifactRoot = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repoRoot $OutputRoot }
$publishDir = Join-Path $artifactRoot "publish\sunder\$Runtime"
$velopackChannel = "app-$Runtime-$Channel"
$releaseDir = Join-Path $artifactRoot "velopack\$Channel\$Runtime"
$mainExe = if ($Runtime.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) { "Sunder.App.exe" } else { "Sunder.App" }
$imageDir = Join-Path $repoRoot "src\Host\Sunder.App\Assets\Images"
$iconPath = if ($Runtime.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) {
    Join-Path $imageDir "app.ico"
} elseif ($Runtime.StartsWith("linux-", [StringComparison]::OrdinalIgnoreCase)) {
    Join-Path $imageDir "logo.png"
} else {
    Join-Path $imageDir "app.icns"
}

foreach ($restoreProject in @($projectPath, $runtimeHostProjectPath, $cliProjectPath)) {
    & dotnet restore $restoreProject -r $Runtime -p:Configuration=$Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for '$restoreProject' and runtime $Runtime."
    }
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

if (Test-Path -LiteralPath $releaseDir) {
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

if (-not [string]::IsNullOrWhiteSpace($GitHubRepositoryUrl)) {
    $effectiveGitHubToken = if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) { $GitHubToken } else { $env:GITHUB_TOKEN }
    $downloadArgs = @(
        "download",
        "github",
        "--repoUrl", $GitHubRepositoryUrl.Trim(),
        "--channel", $velopackChannel,
        "--outputDir", $releaseDir
    )

    if (-not [string]::IsNullOrWhiteSpace($effectiveGitHubToken)) {
        $downloadArgs += @("--token", $effectiveGitHubToken)
    }

    if ($IncludePrereleaseUpdates) {
        $downloadArgs += "--pre"
    }

    & $vpk.Source @downloadArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Existing Velopack assets for channel '$velopackChannel' could not be downloaded. Continuing without delta history."
    }
}

$publishArgs = @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--no-restore",
    "--self-contained", "true",
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    "-p:ContinuousIntegrationBuild=true",
    "-p:PublishSingleFile=false",
    "-o", $publishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for runtime $Runtime."
}

$packArgs = @(
    "pack",
    "--packId", "Sunder",
    "--packTitle", "Sunder",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", $mainExe,
    "--runtime", $Runtime,
    "--channel", $velopackChannel,
    "--outputDir", $releaseDir
)

if (Test-Path -LiteralPath $iconPath) {
    $packArgs += @("--icon", $iconPath)
}

if ($Runtime.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)
    -and -not [string]::IsNullOrWhiteSpace($WindowsSignParams)) {
    $packArgs += @("--signParams", $WindowsSignParams)
}

& $vpk.Source @packArgs
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed for runtime $Runtime."
}

"Sunder Velopack release created: $releaseDir ($velopackChannel)"
