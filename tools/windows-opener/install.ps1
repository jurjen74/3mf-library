<#
.SYNOPSIS
    Installs the 3MF Library Opener for the current Windows user (no admin rights needed).

.DESCRIPTION
    Lets the library site open the local (synced) copy of a model in your slicer instead of downloading it:
      - copies open-model.ps1 and launch.vbs to %LOCALAPPDATA%\3MF Library Opener
      - writes config.json with the library folder and the slicers it found
        (Bambu Studio, OrcaSlicer, PrusaSlicer, UltiMaker Cura; at least one is needed)
      - registers threemflib:// links for this user (HKCU\Software\Classes\threemflib)

    Run from the repository:
      powershell -ExecutionPolicy Bypass -File tools\windows-opener\install.ps1
    Then on the site (in this browser): Account -> This computer -> pick your slicer and tick
    "Open models from my local copy". Installed another slicer later? Run this again.

.PARAMETER LibraryFolder
    The local copy of the library. Default: "3D Print" in your OneDrive folder.

.PARAMETER BambuStudio
    Path to bambu-studio.exe, if it isn't found automatically. Same for -OrcaSlicer, -PrusaSlicer and -Cura.
#>
param(
    [string] $LibraryFolder,
    [string] $BambuStudio,
    [string] $OrcaSlicer,
    [string] $PrusaSlicer,
    [string] $Cura
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA '3MF Library Opener'
$classKey = 'HKCU:\Software\Classes\threemflib'

# --- Library folder -------------------------------------------------------------------------------------------
if (-not $LibraryFolder) {
    $oneDrive = @($env:OneDriveConsumer, $env:OneDrive) | Where-Object { $_ } | Select-Object -First 1
    if (-not $oneDrive) { throw 'OneDrive folder not found. Pass -LibraryFolder "C:\path\to\3D Print".' }
    $LibraryFolder = Join-Path $oneDrive '3D Print'
}
if (-not (Test-Path -LiteralPath $LibraryFolder -PathType Container)) {
    throw "Library folder not found: $LibraryFolder. Pass -LibraryFolder with the right path."
}
$LibraryFolder = (Resolve-Path -LiteralPath $LibraryFolder).ProviderPath

# --- Slicers --------------------------------------------------------------------------------------------------
$uninstallEntries = @(Get-ItemProperty -ErrorAction SilentlyContinue -Path @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'))

# Explicit path, or the usual install folders, or what the slicer's installer registered for uninstalling.
function Find-Slicer([string] $explicit, [string[]] $folders, [string] $exe, [string] $displayName) {
    if ($explicit) {
        if (-not (Test-Path -LiteralPath $explicit -PathType Leaf)) { throw "Not found: $explicit" }
        return (Resolve-Path -LiteralPath $explicit).ProviderPath
    }

    $candidates = @()
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if (-not $base) { continue }
        foreach ($folder in $folders) { $candidates += (Join-Path (Join-Path $base $folder) $exe) }
    }
    foreach ($entry in $uninstallEntries | Where-Object { $_.DisplayName -like $displayName }) {
        if ($entry.InstallLocation) { $candidates += (Join-Path $entry.InstallLocation $exe) }
        if ($entry.DisplayIcon) { $candidates += ($entry.DisplayIcon -split ',')[0].Trim('"') }
    }

    return $candidates |
        Where-Object { $_ -and (Split-Path $_ -Leaf) -eq $exe -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1
}

$slicers = [ordered]@{}
$found = @{
    bambu = Find-Slicer $BambuStudio @('Bambu Studio') 'bambu-studio.exe' 'Bambu Studio*'
    orca  = Find-Slicer $OrcaSlicer @('OrcaSlicer') 'orca-slicer.exe' 'OrcaSlicer*'
    prusa = Find-Slicer $PrusaSlicer @('Prusa3D\PrusaSlicer', 'PrusaSlicer') 'prusa-slicer.exe' 'PrusaSlicer*'
    cura  = Find-Slicer $Cura @() 'UltiMaker-Cura.exe' 'UltiMaker Cura*'
}
foreach ($id in 'bambu', 'orca', 'prusa', 'cura') {
    if ($found[$id]) { $slicers[$id] = $found[$id] }
}
if ($slicers.Count -eq 0) {
    throw 'No slicer found (Bambu Studio, OrcaSlicer, PrusaSlicer or UltiMaker Cura). Pass its path, e.g. -BambuStudio "C:\...\bambu-studio.exe".'
}

# --- Files ----------------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $target | Out-Null
foreach ($name in 'open-model.ps1', 'launch.vbs') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $target -Force
}
Get-ChildItem -LiteralPath $target | Unblock-File   # scripts from a download would otherwise be blocked

[ordered]@{ libraryFolder = $LibraryFolder; slicers = $slicers } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $target 'config.json') -Encoding UTF8

# --- Link registration (current user only) --------------------------------------------------------------------
$wscript = Join-Path $env:SystemRoot 'System32\wscript.exe'
$command = '"{0}" "{1}" "%1"' -f $wscript, (Join-Path $target 'launch.vbs')
$icon = @($slicers.Values)[0]

New-Item -Path $classKey -Force | Out-Null
Set-ItemProperty -Path $classKey -Name '(default)' -Value 'URL:3MF Library link'
Set-ItemProperty -Path $classKey -Name 'URL Protocol' -Value ''
New-Item -Path "$classKey\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$classKey\DefaultIcon" -Name '(default)' -Value ('"{0}",0' -f $icon)
New-Item -Path "$classKey\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$classKey\shell\open\command" -Name '(default)' -Value $command

Write-Host "Installed:  $target"
Write-Host "Library:    $LibraryFolder"
foreach ($id in $slicers.Keys) { Write-Host ('Slicer:     {0,-6} {1}' -f $id, $slicers[$id]) }
Write-Host ''
Write-Host 'Check without opening anything:'
Write-Host ('  powershell -ExecutionPolicy Bypass -File "{0}" -DryRun "threemflib://open?path=<Folder>%2F<File>.3mf&slicer=bambu"' -f (Join-Path $target 'open-model.ps1'))
Write-Host 'Then on the site: Account -> This computer -> pick your slicer and tick "Open models from my local copy".'
