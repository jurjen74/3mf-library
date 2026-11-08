<#
.SYNOPSIS
    Opens a model from the local copy of the library in a slicer.

.DESCRIPTION
    Called (through launch.vbs) for threemflib://open?path=<library path> links from the library site, e.g.
    threemflib://open?path=Stuff%2FLamp.3mf&slicer=orca (slicer: bambu, orca, prusa or cura; default bambu). Any web
    page can fire such a link, so this only opens model files that really sit inside the library folder from
    config.json, in a slicer that install.ps1 found.

    Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Url
    The threemflib:// link. Default: the THREEMFLIB_URL environment variable, which launch.vbs sets.

.PARAMETER DryRun
    Print "<slicer> <file>" instead of opening it, and report problems on stderr instead of in a message box.

.PARAMETER ConfigPath
    Settings file (default: config.json next to this script).
#>
param(
    [Parameter(Position = 0)] [string] $Url = $env:THREEMFLIB_URL,
    [switch] $DryRun,
    [string] $ConfigPath = (Join-Path $PSScriptRoot 'config.json')
)

$ErrorActionPreference = 'Stop'
$modelExtensions = @('.3mf', '.stl', '.obj', '.step', '.stp')
$slicerNames = [ordered]@{ bambu = 'Bambu Studio'; orca = 'OrcaSlicer'; prusa = 'PrusaSlicer'; cura = 'UltiMaker Cura' }

function Fail([string] $message, [int] $code) {
    if ($DryRun) {
        [Console]::Error.WriteLine($message)
    }
    else {
        Add-Type -AssemblyName PresentationFramework
        [void][System.Windows.MessageBox]::Show($message, '3MF Library Opener', 'OK', 'Warning')
    }
    exit $code
}

try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    Fail "The helper's settings are missing or broken ($ConfigPath). Run install.ps1 again." 6
}

# Browsers sometimes add a slash after the host: accept threemflib://open?... and threemflib://open/?...
$match = [regex]::Match($Url, '^threemflib://open/?\?(.*)$', 'IgnoreCase')
if (-not $match.Success) { Fail "Not a library link: $Url" 2 }

$relative = ''
$slicer = 'bambu'
foreach ($pair in $match.Groups[1].Value -split '&') {
    # Percent-decode (UTF-8): %E6%B0%B4 becomes 水.
    if ($pair.StartsWith('path=')) { $relative = [System.Uri]::UnescapeDataString($pair.Substring(5)) }
    elseif ($pair.StartsWith('slicer=')) { $slicer = $pair.Substring(7) }
}

if (-not $slicerNames.Contains($slicer)) { Fail "Unknown slicer: $slicer" 6 }
$slicerName = $slicerNames[$slicer]

# Library paths look like Folder/Sub/File.3mf: no drive, no root, no "." or ".." segments.
$segments = $relative -split '[\\/]'
if ([string]::IsNullOrWhiteSpace($relative) -or
    $relative.StartsWith('/') -or $relative.StartsWith('\') -or
    $relative.Contains(':') -or
    @($segments | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_ -eq '' }).Count -gt 0) {
    Fail "Refused path: $relative" 3
}

if ($modelExtensions -notcontains [System.IO.Path]::GetExtension($relative).ToLowerInvariant()) {
    Fail "Only model files can be opened: $relative" 4
}

$separator = [System.IO.Path]::DirectorySeparatorChar
$root = [System.IO.Path]::GetFullPath([string]$config.libraryFolder).TrimEnd('\', '/')
# [string[]]: an untyped array would reach Combine() as one space-joined string.
$file = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine([string[]](@($root) + $segments)))

# Belt and braces: the resolved file must still be inside the library folder.
if (-not $file.StartsWith($root + $separator, [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail "Refused path outside the library: $relative" 3
}

# OneDrive "online-only" files exist as placeholders; opening one downloads it.
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    Fail "Not in your local library: $relative" 5
}

if ($DryRun) {
    Write-Output "$slicer $file"
    exit 0
}

# config.json: { "slicers": { "bambu": "C:\\...\\bambu-studio.exe", ... } } (older installs: "bambuStudio").
$exe = $null
if ($config.slicers) { $exe = [string]$config.slicers.$slicer }
if (-not $exe -and $slicer -eq 'bambu') { $exe = [string]$config.bambuStudio }
if (-not $exe -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Fail "$slicerName isn't set up for the helper. Install it, then run install.ps1 again." 7
}

Start-Process -FilePath $exe -ArgumentList ('"' + $file + '"')
