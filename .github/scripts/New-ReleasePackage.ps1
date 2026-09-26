<#
.SYNOPSIS
    Checks the plugin package DalamudPackager built and stages it for a GitHub Release.

.DESCRIPTION
    Used by .github/workflows/release.yml after a Release build and a passing test run, and safe to
    run locally after `dotnet build AetherFrame.slnx -c Release`. It never builds, tags, uploads or
    publishes anything. It fails, and stages nothing, unless:

      - latest.zip holds exactly the files a Dalamud plugin needs (AetherFrame.dll, AetherFrame.json
        and AetherFrame.deps.json) and nothing else;
      - the manifest names AetherFrame, carries the required installer fields and reports
        <Version>.0, as does the DLL itself;
      - CHANGELOG.md has a non-empty section for <Version>.

    Output in -Destination:
      AetherFrame-<Version>.zip   the package, byte for byte as DalamudPackager wrote it
      SHA256SUMS.txt              its SHA-256, in `sha256sum -c` format
      release-notes.md            the CHANGELOG section plus install and checksum notes
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $PluginOutput = 'AetherFrame/bin/x64/Release',
    [string] $Changelog = 'CHANGELOG.md',
    [string] $Destination = 'dist'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Fail([string] $message) { throw "Release package check failed: $message" }

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "'$Version' is not a MAJOR.MINOR.PATCH version." }
$assemblyVersion = "$Version.0"

# The package: exactly the three files, nothing more (no pdbs, no stray outputs, no folders).
$zipPath = Join-Path $PluginOutput 'AetherFrame/latest.zip'
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { Fail "no package at $zipPath. Build in Release first." }
$zipPath = (Resolve-Path -LiteralPath $zipPath).Path

$expectedEntries = @('AetherFrame.deps.json', 'AetherFrame.dll', 'AetherFrame.json')
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("aetherframe-release-" + [guid]::NewGuid().ToString('N'))
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $unexpected = @($entries | Where-Object { $_ -notin $expectedEntries })
    $missing = @($expectedEntries | Where-Object { $_ -notin $entries })
    if ($unexpected.Count -gt 0) { Fail "unexpected files in the package: $($unexpected -join ', ')" }
    if ($missing.Count -gt 0) { Fail "files missing from the package: $($missing -join ', ')" }

    New-Item -ItemType Directory -Path $scratch | Out-Null
    foreach ($entry in $zip.Entries) {
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $scratch $entry.FullName))
    }
}
finally {
    $zip.Dispose()
}

try {
    # The manifest Dalamud reads, as packaged.
    $manifest = Get-Content -LiteralPath (Join-Path $scratch 'AetherFrame.json') -Raw | ConvertFrom-Json
    if ($manifest.InternalName -ne 'AetherFrame') { Fail "manifest InternalName is '$($manifest.InternalName)', expected 'AetherFrame'." }
    if ($manifest.AssemblyVersion -ne $assemblyVersion) { Fail "manifest AssemblyVersion is '$($manifest.AssemblyVersion)', expected '$assemblyVersion'." }
    foreach ($field in 'Name', 'Author', 'Punchline', 'Description', 'RepoUrl') {
        if ([string]::IsNullOrWhiteSpace([string]$manifest.$field)) { Fail "manifest field $field is empty." }
    }
    if ([int]$manifest.DalamudApiLevel -le 0) { Fail 'manifest has no DalamudApiLevel.' }

    # The DLL itself, as packaged.
    $dllVersion = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $scratch 'AetherFrame.dll')).Version.ToString()
    if ($dllVersion -ne $assemblyVersion) { Fail "AetherFrame.dll is version $dllVersion, expected $assemblyVersion." }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

# This version's CHANGELOG section: from its heading up to the next version heading or the link list.
$lines = Get-Content -LiteralPath $Changelog
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match ('^## \[' + [regex]::Escape($Version) + '\]')) { $start = $i; break }
}
if ($start -lt 0) { Fail "$Changelog has no '## [$Version]' section." }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## \[' -or $lines[$i] -match '^\[[^\]]+\]:\s') { $end = $i; break }
}
# (A PowerShell range counts down when its end is below its start, so an empty section is caught first.)
if ($end -le $start + 1) { Fail "the $Version section of $Changelog is empty." }
$section = ($lines[($start + 1)..($end - 1)] -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($section)) { Fail "the $Version section of $Changelog is empty." }

# Stage.
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$packageName = "AetherFrame-$Version.zip"
$packagePath = Join-Path $Destination $packageName
Copy-Item -LiteralPath $zipPath -Destination $packagePath -Force
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $Destination 'SHA256SUMS.txt'), "$hash  $packageName`n", $utf8)

$notes = @"
$section

---

**Installing:** this is a test build for the Dalamud dev plugin loader. Follow the [tester guide](https://github.com/richhiiee/AetherFrame/blob/master/docs/Testing.md).

``$packageName`` SHA-256: ``$hash``
"@
[System.IO.File]::WriteAllText((Join-Path $Destination 'release-notes.md'), ($notes -replace "`r`n", "`n") + "`n", $utf8)

Write-Host "Package OK: $packageName (AetherFrame $assemblyVersion, Dalamud API $($manifest.DalamudApiLevel))"
Write-Host "SHA-256: $hash"
