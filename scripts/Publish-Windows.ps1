[CmdletBinding()]
param()
& (Join-Path $PSScriptRoot 'Publish-Package.ps1') -Component Server -Runtime win-x64
& (Join-Path $PSScriptRoot 'Publish-Package.ps1') -Component Client -Runtime win-x64
