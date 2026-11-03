param(
    [Parameter(Mandatory)][ValidateSet('cli', 'host')][string]$Component,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Rid
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$projects = @{ cli = 'Sunder.Cli'; host = 'Sunder.Host.Supervisor' }
$project = Join-Path $root "languages/csharp/dotnet/host/$($projects[$Component])/$($projects[$Component]).csproj"
$output = Join-Path $root "artifacts/publish/$Component/$Rid"
$properties = @('-p:RestoreLockedMode=false', "-p:NuGetLockFilePath=obj/packages.$Rid.lock.json")
dotnet restore $project -r $Rid -p:Configuration=Release @properties
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$singleFile = if ($Component -eq 'cli') { 'true' } else { 'false' }
dotnet publish $project -c Release -r $Rid --no-restore --self-contained true `
    "-p:Version=$Version" "-p:InformationalVersion=$Version" `
    -p:IncludeSourceRevisionInInformationalVersion=false -p:ContinuousIntegrationBuild=true `
    "-p:PublishSingleFile=$singleFile" -p:PublishTrimmed=false @properties -o $output
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$suffix = if ($IsWindows) { '.exe' } else { '' }
if ($Component -eq 'host') {
    foreach ($file in @("Sunder.Host.Supervisor$suffix", "RuntimeHost/Sunder.Runtime.Host$suffix")) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) { throw "Missing Host payload: $file" }
    }
    python3 "$root/scripts/test/release/smoke-host.py" $output
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
} else {
    $binary = Join-Path $output "sunder$suffix"
    $result = (& $binary version --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -or $result.exitCode -ne 0 -or $result.data.version -ne $Version) { throw 'CLI version smoke failed' }
    $help = (& $binary --help | Out-String)
    if ($LASTEXITCODE -or [string]::IsNullOrWhiteSpace($help)) { throw 'CLI help smoke failed' }
    $helpJson = (& $binary --help --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -or $helpJson.schemaVersion -ne 1 -or -not $helpJson.success) { throw 'CLI JSON help smoke failed' }
}
$prefix = if ($Component -eq 'cli') { 'sunder-cli' } else { 'sunder-runtime-host' }
$release = Join-Path $root 'artifacts/release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
if ($IsWindows) {
    Compress-Archive -Path "$output/*" -DestinationPath "$release/$prefix-$Version-$Rid.zip"
} else {
    tar -czf "$release/$prefix-$Version-$Rid.tar.gz" -C $output .
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
