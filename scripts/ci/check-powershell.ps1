$ErrorActionPreference = 'Stop'
foreach ($file in Get-ChildItem "$PSScriptRoot/.." -Filter '*.ps1' -Recurse) {
    $tokens = $null; $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw ($errors | Out-String) }
}
