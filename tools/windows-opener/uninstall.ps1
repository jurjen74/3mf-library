<#
.SYNOPSIS
    Removes the 3MF Library Opener for the current Windows user.

.DESCRIPTION
    powershell -ExecutionPolicy Bypass -File tools\windows-opener\uninstall.ps1
#>
$ErrorActionPreference = 'Stop'

Remove-Item -Path 'HKCU:\Software\Classes\threemflib' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA '3MF Library Opener') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Removed the threemflib:// link registration and the helper files.'
