param([Parameter(Mandatory)][string]$RequestPath, [Parameter(Mandatory)][string]$ResultPath)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'WindowsTestProcessJob.cs')
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
$result = [Sunder.TestTools.WindowsTestProcessJob]::Run(
    $request.command, [string[]]$request.args, $request.cwd, [long]$request.timeoutMilliseconds, $request.cancelPath)
$result | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding utf8
