[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Server','Client')][string]$Component,
    [Parameter(Mandatory)][ValidateSet('win-x64','osx-arm64','osx-x64')][string]$Runtime
)
$ErrorActionPreference = 'Stop'
if ($Component -eq 'Server' -and $Runtime -ne 'win-x64') { throw 'The server distribution targets Windows x64.' }
$repo = Split-Path -Parent $PSScriptRoot
$project = if ($Component -eq 'Server') { 'src/WindowsPowerShellRc' } else { 'src/WindowsPowerShellRc.Client' }
$output = Join-Path $repo "artifacts/$($Component.ToLowerInvariant())/$Runtime"
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
& dotnet publish (Join-Path $repo $project) -c Release -r $Runtime --self-contained true -p:PublishTrimmed=false -p:RestoreLockedMode=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
$binary = if ($Component -eq 'Server') { 'WindowsPowerShellRc.Server' } else { 'WindowsPowerShellRc.Client' }
$executable = $binary + $(if ($Runtime.StartsWith('win')) { '.exe' } else { '' })
foreach ($required in @($executable, "$binary.dll", 'WindowsPowerShellRc.Transport.dll')) {
    if (-not (Test-Path (Join-Path $output $required))) { throw "Missing packaged dependency: $required" }
}
if ($Component -eq 'Server') {
    foreach ($required in @('coreclr.dll', 'System.Management.Automation.dll', 'Microsoft.PowerShell.Commands.Utility.dll', 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1')) {
        if (-not (Test-Path (Join-Path $output $required))) { throw "Missing bundled engine dependency: $required" }
    }
} elseif (Test-Path (Join-Path $output 'System.Management.Automation.dll')) {
    throw 'The client must not contain the server PowerShell engine.'
}
foreach ($file in Get-ChildItem $output -File | Where-Object { $_.Name -like 'WindowsPowerShellRc*.dll' -or $_.Name -like 'WindowsPowerShellRc*.pdb' }) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $text = $encoding.GetString($bytes)
        if ($text.Contains($repo) -or $text.Contains($repo.Replace('\', '/'))) { throw "Build checkout path detected in $($file.Name)." }
    }
}
Copy-Item (Join-Path $repo 'README.md') $output -Force
Copy-Item (Join-Path $repo 'LICENSE') $output -Force
Copy-Item (Join-Path $repo 'docs') $output -Recurse -Force
$name = "WindowsPowerShellRc-$Component-$Runtime"
if ($Runtime.StartsWith('win')) {
    $archive = Join-Path $repo "artifacts/$name.zip"
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
} else {
    $bundle = Join-Path $output 'WindowsPowerShellRc.Client.app'
    $macos = Join-Path $bundle 'Contents/MacOS'
    New-Item -ItemType Directory -Path $macos -Force | Out-Null
    Get-ChildItem $output | Where-Object { $_.FullName -ne $bundle } | Move-Item -Destination $macos
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $macos "$binary.dll")).ProductVersion.Split('+')[0]
    @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>io.github.rkrx.PowerShellRemoteDiagnostics.Client</string>
<key>CFBundleName</key><string>PowerShell RC Client</string>
<key>CFBundleExecutable</key><string>WindowsPowerShellRc.Client</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>$version</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@ | Set-Content (Join-Path $bundle 'Contents/Info.plist') -Encoding utf8NoBOM
    $archive = Join-Path $repo "artifacts/$name.tar.gz"
    & tar -czf $archive -C $output .
    if ($LASTEXITCODE -ne 0) { throw 'Client archive creation failed.' }
}
Write-Host "Built: $archive"
