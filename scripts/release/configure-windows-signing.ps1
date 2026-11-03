$ErrorActionPreference = 'Stop'
foreach ($name in @('AZURE_TRUSTED_SIGNING_ENDPOINT', 'AZURE_TRUSTED_SIGNING_ACCOUNT_NAME', 'AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Missing signing setting: $name" }
}
$policy = Get-Content "$PSScriptRoot/../../eng/release/tooling.json" -Raw | ConvertFrom-Json
$client = $policy.windowsSigningClient
$archive = Join-Path $env:RUNNER_TEMP 'artifact-signing-client.zip'
$root = Join-Path $env:RUNNER_TEMP 'artifact-signing-client'
Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$($client.packageId)/$($client.version)/$($client.packageId).$($client.version).nupkg" -OutFile $archive
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ine $client.sha256) { throw 'Signing client checksum mismatch' }
Expand-Archive -LiteralPath $archive -DestinationPath $root
$dlibs = @(Get-ChildItem $root -Filter Azure.CodeSigning.Dlib.dll -Recurse | Where-Object { $_.FullName -match '[\\/]x64[\\/]' })
if ($dlibs.Count -ne 1) { throw 'Expected exactly one x64 signing client' }
$metadataPath = Join-Path $env:RUNNER_TEMP 'trusted-signing-metadata.json'
[ordered]@{
    Endpoint = $env:AZURE_TRUSTED_SIGNING_ENDPOINT
    CodeSigningAccountName = $env:AZURE_TRUSTED_SIGNING_ACCOUNT_NAME
    CertificateProfileName = $env:AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME
    ExcludeCredentials = @('EnvironmentCredential', 'ManagedIdentityCredential', 'WorkloadIdentityCredential', 'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential', 'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
} | ConvertTo-Json -Depth 4 | Out-File $metadataPath -Encoding utf8
"dlib_path=$($dlibs[0].FullName)" | Out-File $env:GITHUB_OUTPUT -Encoding utf8 -Append
"metadata_path=$metadataPath" | Out-File $env:GITHUB_OUTPUT -Encoding utf8 -Append
