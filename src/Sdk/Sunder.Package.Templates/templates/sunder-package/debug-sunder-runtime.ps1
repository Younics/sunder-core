param(
    [string]$AppPath = $env:SUNDER_APP_PATH,
    [string]$RuntimeHostPath = $env:SUNDER_RUNTIME_HOST_PATH,
    [string]$RuntimeUrl = "http://127.0.0.1:5276"
)

if ([string]::IsNullOrWhiteSpace($AppPath)) {
    throw "Set SUNDER_APP_PATH or pass -AppPath to point at installed Sunder.App.exe."
}

if ([string]::IsNullOrWhiteSpace($RuntimeHostPath)) {
    throw "Set SUNDER_RUNTIME_HOST_PATH or pass -RuntimeHostPath to point at installed Sunder.Runtime.Host.exe."
}

$projectDir = Join-Path $PSScriptRoot "Sunder.Package.Template"
$devPackagePath = Join-Path $projectDir "bin\Debug\net10.0\sunder-dev"

if (-not (Test-Path $devPackagePath)) {
    throw "Dev package output not found at '$devPackagePath'. Build the package first."
}

Start-Process -FilePath $RuntimeHostPath -ArgumentList @("--wait-for-debugger", "--urls", $RuntimeUrl)
Start-Sleep -Seconds 2
Start-Process -FilePath $AppPath -ArgumentList @("--runtime-url", $RuntimeUrl, "--dev-package", $devPackagePath)
