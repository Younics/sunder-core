[CmdletBinding()]
param(
    [string]$Repository = "Younics/sunder-core",
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"

if (-not $IsWindows) {
    throw "install.ps1 currently supports Windows. Use install.sh on macOS or Linux."
}

$runtime = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    "X64" { "win-x64" }
    "Arm64" { "win-arm64" }
    default { throw "Unsupported Windows architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
}

$releaseApiUrl = if ($Version -eq "latest") {
    "https://api.github.com/repos/$Repository/releases/latest"
} else {
    "https://api.github.com/repos/$Repository/releases/tags/$Version"
}

$headers = @{ "User-Agent" = "sunder-install-script" }
$release = Invoke-RestMethod -Uri $releaseApiUrl -Headers $headers
$asset = $release.assets |
    Where-Object { $_.name -match "Setup\.exe$" -and $_.name -match [Regex]::Escape($runtime) } |
    Select-Object -First 1

if ($null -eq $asset) {
    $asset = $release.assets |
        Where-Object { $_.name -match "Setup\.exe$" } |
        Select-Object -First 1
}

if ($null -eq $asset) {
    throw "No Sunder Windows setup asset was found in release '$($release.tag_name)'."
}

$downloadDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "sunder-install"
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$installerPath = Join-Path $downloadDirectory $asset.name

"Downloading $($asset.name) from $Repository release $($release.tag_name)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installerPath -Headers $headers

"Starting Sunder installer..."
Start-Process -FilePath $installerPath -Wait
