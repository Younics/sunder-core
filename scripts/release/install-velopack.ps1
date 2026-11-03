$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$versions = python3 -B "$root/scripts/ci/metadata.py" toolchains | ConvertFrom-Json
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$temporary = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$sdk = Join-Path $temporary 'sunder-velopack-sdk'
$tools = Join-Path $temporary 'sunder-velopack'
dotnet new globaljson --sdk-version $versions.velopack_sdk --roll-forward disable --output $sdk --force
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Push-Location $sdk
try {
    dotnet tool install --tool-path $tools vpk --version $versions.velopack
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
} finally { Pop-Location }
if ($env:GITHUB_PATH) { $tools | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append }
Write-Output "Velopack installed at $tools"
