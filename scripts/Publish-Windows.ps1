[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts/win-x64'
$zip = Join-Path $repo 'artifacts/Windows-Powershell-RC-win-x64.zip'
& dotnet publish (Join-Path $repo 'src/WindowsPowerShellRc') -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:RestoreLockedMode=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
foreach ($required in @('WindowsPowerShellRc.exe', 'coreclr.dll', 'System.Management.Automation.dll', 'Microsoft.PowerShell.Commands.Utility.dll')) {
    if (-not (Test-Path (Join-Path $output $required))) { throw "Missing packaged dependency: $required" }
}
# Project-owned binaries and symbols must not disclose the build checkout path.
foreach ($name in @('WindowsPowerShellRc.dll', 'WindowsPowerShellRc.pdb')) {
    $path = Join-Path $output $name
    if (-not (Test-Path $path)) { continue }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    foreach ($encoding in @([System.Text.Encoding]::UTF8, [System.Text.Encoding]::Unicode)) {
        $text = $encoding.GetString($bytes)
        if ($text.Contains($repo) -or $text.Contains($repo.Replace('\', '/'))) {
            throw "Local checkout path detected in $name; distribution aborted."
        }
    }
}
Copy-Item (Join-Path $repo 'README.md') $output -Force
Copy-Item (Join-Path $repo 'LICENSE') $output -Force
$docs = Join-Path $output 'docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null
Copy-Item (Join-Path $repo 'docs/windows-acceptance.md') $docs -Force
Copy-Item (Join-Path $repo 'docs/platform-support.md') $docs -Force
Copy-Item (Join-Path $repo 'docs/development.md') $docs -Force
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force
Write-Host "Built: $zip"
Write-Host 'Windows execution/acceptance must still be performed; packaging is not runtime validation.'
