[CmdletBinding()]
param(
    [string]$Repository = "Younics/sunder-core",
    [string]$Version = "latest",
    [string]$ExpectedWindowsPublisherSubject = $env:SUNDER_WINDOWS_PUBLISHER_SUBJECT
)

$ErrorActionPreference = "Stop"

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "install.ps1 currently supports Windows. Use install.sh on macOS or Linux."
}

$nativeArchitecture = if ([string]::IsNullOrWhiteSpace($env:PROCESSOR_ARCHITEW6432)) {
    $env:PROCESSOR_ARCHITECTURE
} else {
    $env:PROCESSOR_ARCHITEW6432
}
$runtime = switch ($nativeArchitecture.ToLowerInvariant()) {
    "amd64" { "win-x64" }
    "arm64" { "win-arm64" }
    default { throw "Unsupported Windows architecture '$nativeArchitecture'." }
}

$releaseApiUrl = if ($Version -eq "latest") {
    "https://api.github.com/repos/$Repository/releases/latest"
} else {
    if ($Version -match '^v?[0-9]') {
        $Version = "app/v$($Version.TrimStart('v'))"
    }
    "https://api.github.com/repos/$Repository/releases/tags/$Version"
}

$headers = @{ "User-Agent" = "sunder-install-script" }
$release = Invoke-RestMethod -Uri $releaseApiUrl -Headers $headers
if ([string]$release.tag_name -notmatch '^app/v') {
    throw "Release '$($release.tag_name)' is not a Sunder App release. Specify an app/v* tag."
}
$assetName = "Sunder-app-$runtime-stable-Setup.exe"
$matchingAssets = @($release.assets | Where-Object { [string]$_.name -ceq $assetName })
if ($matchingAssets.Count -ne 1) {
    throw "Expected exactly one release asset named '$assetName' in '$($release.tag_name)', but found $($matchingAssets.Count)."
}
$asset = $matchingAssets[0]

function Get-AssetSha256 {
    param([Parameter(Mandatory = $true)]$Asset)

    if ([string]$Asset.digest -notmatch '^sha256:([0-9a-fA-F]{64})$') {
        throw "Release asset '$($Asset.name)' does not have a valid GitHub SHA-256 digest."
    }
    return $Matches[1].ToUpperInvariant()
}

function Assert-AssetDigest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $ExpectedSha256) {
        throw "Release asset '$(Split-Path $Path -Leaf)' does not match its GitHub SHA-256 digest."
    }
}

$downloadDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "sunder-install"
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$installerPath = Join-Path $downloadDirectory $asset.name
$appSha256 = Get-AssetSha256 $asset

"Downloading $($asset.name) from $Repository release $($release.tag_name)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installerPath -Headers $headers

$appLock = $null
try {
    $appLock = [IO.File]::Open($installerPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Assert-AssetDigest $installerPath $appSha256

    $appSignature = Get-AuthenticodeSignature -LiteralPath $installerPath
    if ($appSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Sunder Setup signature validation failed with status '$($appSignature.Status)'."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedWindowsPublisherSubject) -and
        $appSignature.SignerCertificate.Subject -cne $ExpectedWindowsPublisherSubject) {
        throw "Sunder Setup is not signed by the expected Windows publisher."
    }
    "Verified Authenticode publisher: $($appSignature.SignerCertificate.Subject)"
    if ([string]::IsNullOrWhiteSpace($ExpectedWindowsPublisherSubject)) {
        $confirmation = Read-Host "No publisher pin was supplied. Run this trusted publisher's installer? [y/N]"
        if ($confirmation -notin @("y", "Y", "yes", "YES", "Yes")) {
            throw "Sunder installation was cancelled before executing the installer."
        }
    }

    "Starting Sunder installer..."
    $appInstaller = Start-Process -FilePath $installerPath -Wait -PassThru
    if ($appInstaller.ExitCode -notin @(0, 1641, 3010)) {
        throw "Sunder App installation failed with exit code $($appInstaller.ExitCode)."
    }
} finally {
    if ($null -ne $appLock) { $appLock.Dispose() }
}
