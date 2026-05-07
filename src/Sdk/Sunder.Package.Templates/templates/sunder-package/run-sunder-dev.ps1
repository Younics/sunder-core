param(
    [string]$AppPath = $env:SUNDER_APP_PATH
)

if ([string]::IsNullOrWhiteSpace($AppPath)) {
    throw "Set SUNDER_APP_PATH or pass -AppPath to point at installed Sunder.App.exe."
}

$projectDir = Join-Path $PSScriptRoot "Sunder.Package.Template"
$devPackagePath = Join-Path $projectDir "bin\Debug\net10.0\sunder-dev"

if (-not (Test-Path $devPackagePath)) {
    throw "Dev package output not found at '$devPackagePath'. Build the package first."
}

Start-Process -FilePath $AppPath -ArgumentList @("--dev-package", $devPackagePath)
