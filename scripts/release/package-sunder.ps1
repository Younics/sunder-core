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

    [string]$WindowsSignParams = "",

    [string]$WindowsPublisherSubject = $env:SUNDER_WINDOWS_PUBLISHER_SUBJECT
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    throw "Version '$Version' must be strict SemVer without build metadata. Use a value like 0.1.0 or 0.1.0-beta.1."
}
if ($Version.Contains("-")) {
    foreach ($identifier in $Version.Substring($Version.IndexOf("-") + 1).Split(".")) {
        if ($identifier -match '^[0-9]+$' -and $identifier.Length -gt 1 -and $identifier.StartsWith("0", [StringComparison]::Ordinal)) {
            throw "Numeric SemVer prerelease identifiers must not contain leading zeroes."
        }
    }
}
if ($Runtime -notin @("win-x64", "win-arm64")) {
    throw "Runtime must be win-x64 or win-arm64. Use package-sunder.sh for Linux and macOS runtimes."
}

$vpk = Get-Command "vpk" -ErrorAction SilentlyContinue
if ($null -eq $vpk) {
    throw "The Velopack CLI 'vpk' was not found. Install it with: dotnet tool install --global vpk --version 0.0.1298"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$projectPath = Join-Path $repoRoot "languages\csharp\dotnet\host\Sunder.App\Sunder.App.csproj"
$artifactRoot = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repoRoot $OutputRoot }
$publishDir = Join-Path $artifactRoot "publish\sunder\$Runtime"
$velopackChannel = "app-$Runtime-$Channel"
$releaseDir = Join-Path $artifactRoot "velopack\$Channel\$Runtime"
$mainExe = "Sunder.App.exe"
$imageDir = Join-Path $repoRoot "languages\csharp\dotnet\host\Sunder.App\Assets\Images"
$iconPath = Join-Path $imageDir "app.ico"
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "Windows packaging icon is missing: $iconPath"
}

function Import-VelopackHistory {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryUrl,
        [Parameter(Mandatory = $true)][string]$VelopackChannel,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string]$AccessToken = "",
        [switch]$IncludePrerelease
    )

    [Uri]$repository = $null
    if (-not [Uri]::TryCreate($RepositoryUrl, [UriKind]::Absolute, [ref]$repository) -or
        $repository.Scheme -ne "https" -or
        $repository.Host -ne "github.com" -or
        -not [string]::IsNullOrEmpty($repository.Query) -or
        -not [string]::IsNullOrEmpty($repository.Fragment)) {
        throw "GitHubRepositoryUrl must be an https://github.com/owner/repository URL."
    }
    $segments = @($repository.AbsolutePath.Trim('/').Split('/', [StringSplitOptions]::RemoveEmptyEntries))
    if ($segments.Count -ne 2) {
        throw "GitHubRepositoryUrl must identify exactly one GitHub owner and repository."
    }
    $repositoryName = $segments[1]
    if ($repositoryName.EndsWith(".git", [StringComparison]::OrdinalIgnoreCase)) {
        $repositoryName = $repositoryName.Substring(0, $repositoryName.Length - 4)
    }

    $headers = @{
        Accept = "application/vnd.github+json"
        "User-Agent" = "sunder-release-packager"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $headers.Authorization = "Bearer $AccessToken"
    }

    $manifestName = "releases.$VelopackChannel.json"
    $release = $null
    $releaseVersion = $null
    $page = 1
    while ($true) {
        $apiUrl = "https://api.github.com/repos/$($segments[0])/$repositoryName/releases?per_page=100&page=$page"
        $pageReleases = @(Invoke-RestMethod -Uri $apiUrl -Headers $headers)
        foreach ($candidate in $pageReleases) {
            if (-not (
                -not $candidate.draft -and
                ($IncludePrerelease -or -not $candidate.prerelease) -and
                @($candidate.assets | Where-Object { [string]$_.name -ceq $manifestName }).Count -eq 1)) {
                continue
            }
            if ([string]$candidate.tag_name -notmatch '^app/v(.+)$') {
                continue
            }
            try {
                $candidateVersion = [System.Management.Automation.SemanticVersion]$Matches[1]
            }
            catch {
                continue
            }
            if ($null -eq $releaseVersion -or $candidateVersion -gt $releaseVersion) {
                $release = $candidate
                $releaseVersion = $candidateVersion
            }
        }
        if ($pageReleases.Count -lt 100) {
            break
        }
        $page++
    }
    if ($null -eq $release) {
        Write-Verbose "No previous Velopack release was found for channel '$VelopackChannel'."
        return
    }

    $historyDirectory = Join-Path $Destination ".history-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $historyDirectory -Force | Out-Null
    try {
        $releaseAssets = @($release.assets)
        $manifestAsset = @($releaseAssets | Where-Object { [string]$_.name -ceq $manifestName })[0]
        $manifestPath = Join-Path $historyDirectory $manifestName
        Invoke-WebRequest -Uri $manifestAsset.browser_download_url -Headers $headers -OutFile $manifestPath
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $packageEntries = @($manifest.Assets)
        if ($packageEntries.Count -eq 0) {
            throw "Velopack history manifest '$manifestName' contains no packages."
        }

        foreach ($packageEntry in $packageEntries) {
            $fileName = [string]$packageEntry.FileName
            if ([string]::IsNullOrWhiteSpace($fileName) -or
                [IO.Path]::GetFileName($fileName) -cne $fileName -or
                -not $fileName.EndsWith(".nupkg", [StringComparison]::OrdinalIgnoreCase)) {
                throw "Velopack history manifest '$manifestName' contains an unsafe package name."
            }
            $packageAssets = @($releaseAssets | Where-Object { [string]$_.name -ceq $fileName })
            if ($packageAssets.Count -ne 1) {
                throw "Release '$($release.tag_name)' does not contain exactly one '$fileName' asset."
            }
            if ([string]$packageEntry.SHA256 -notmatch '^[0-9a-fA-F]{64}$') {
                throw "Velopack history manifest '$manifestName' contains an invalid SHA-256 for '$fileName'."
            }

            $packagePath = Join-Path $historyDirectory $fileName
            Invoke-WebRequest -Uri $packageAssets[0].browser_download_url -Headers $headers -OutFile $packagePath
            $actualSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
            if ($actualSha256 -cne ([string]$packageEntry.SHA256).ToUpperInvariant()) {
                throw "Velopack history package '$fileName' does not match its release manifest SHA-256."
            }
        }

        Get-ChildItem -LiteralPath $historyDirectory -File | Move-Item -Destination $Destination
    }
    finally {
        if (Test-Path -LiteralPath $historyDirectory) {
            Remove-Item -LiteralPath $historyDirectory -Recurse -Force
        }
    }
}

& dotnet restore $projectPath -r $Runtime -p:Configuration=$Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for '$projectPath' and runtime $Runtime."
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
    try {
        Import-VelopackHistory `
            -RepositoryUrl $GitHubRepositoryUrl.Trim() `
            -VelopackChannel $velopackChannel `
            -Destination $releaseDir `
            -AccessToken $effectiveGitHubToken `
            -IncludePrerelease:$IncludePrereleaseUpdates
    }
    catch {
        Write-Warning "Existing Velopack assets for channel '$velopackChannel' could not be downloaded. Continuing without delta history: $($_.Exception.Message)"
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
    "-p:IncludeSourceRevisionInInformationalVersion=false",
    "-p:ContinuousIntegrationBuild=true",
    "-p:PublishSingleFile=false",
    "-o", $publishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for runtime $Runtime."
}

$publishedSettingsPath = Join-Path $publishDir "appsettings.json"
$publishedSettings = Get-Content -LiteralPath $publishedSettingsPath -Raw | ConvertFrom-Json
$publishedSettings.Updates.IncludePrerelease = [bool]$IncludePrereleaseUpdates
if (-not [string]::IsNullOrWhiteSpace($GitHubRepositoryUrl)) {
    $publishedSettings.Updates.GitHubRepositoryUrl = $GitHubRepositoryUrl.Trim()
}
$publishedSettings |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $publishedSettingsPath -Encoding utf8NoBOM

$requiredBundledFiles = @(
    (Join-Path $publishDir "RuntimeHost\Sunder.Host.Supervisor.exe"),
    (Join-Path $publishDir "RuntimeHost\RuntimeHost\Sunder.Runtime.Host.exe"),
    (Join-Path $publishDir "Cli\sunder.exe")
)
foreach ($requiredBundledFile in $requiredBundledFiles) {
    if (-not (Test-Path -LiteralPath $requiredBundledFile -PathType Leaf)) {
        throw "The App publish is missing bundled payload '$requiredBundledFile'."
    }
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

$packArgs += @("--icon", $iconPath)
$hasWindowsSignParams = -not [string]::IsNullOrWhiteSpace($WindowsSignParams)

if ($hasWindowsSignParams) {
    $packArgs += @("--signParams", $WindowsSignParams)
}
$packArgs += "--noPortable"

& $vpk.Source @packArgs
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed for runtime $Runtime."
}

$currentPackagePrefix = "Sunder-$Version-$velopackChannel-"
Get-ChildItem -LiteralPath $releaseDir -File -Filter "*.nupkg" |
    Where-Object { -not $_.Name.StartsWith($currentPackagePrefix, [StringComparison]::Ordinal) } |
    Remove-Item -Force
$historyManifestPath = Join-Path $releaseDir "releases.$velopackChannel.json"
if (Test-Path -LiteralPath $historyManifestPath) {
    Remove-Item -LiteralPath $historyManifestPath -Force
}
Get-ChildItem -LiteralPath $releaseDir -File -Filter "RELEASES-*" | Remove-Item -Force

$setupName = "Sunder-$velopackChannel-Setup.exe"
$setupPath = Join-Path $releaseDir $setupName
$setupFiles = @(Get-ChildItem -LiteralPath $releaseDir -File -Filter "*-Setup.exe")
if ($setupFiles.Count -ne 1 -or $setupFiles[0].Name -cne $setupName) {
    throw "Expected exactly one Windows installer named '$setupName' in '$releaseDir'."
}
if (Test-Path -LiteralPath (Join-Path $releaseDir "Sunder-$velopackChannel-Portable.zip")) {
    throw "The Windows release unexpectedly contains a portable ZIP."
}

if ($hasWindowsSignParams) {
    $signature = Get-AuthenticodeSignature -LiteralPath $setupPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "The Velopack Setup signature is not valid: $($signature.Status)."
    }
    if (-not [string]::IsNullOrWhiteSpace($WindowsPublisherSubject) -and
        $signature.SignerCertificate.Subject -cne $WindowsPublisherSubject) {
        throw "The Velopack Setup is not signed by the expected Windows publisher."
    }
}

"Sunder Velopack release created: $releaseDir ($velopackChannel)"
