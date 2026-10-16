# ensure-bom.ps1
# Adds a UTF-8 BOM to every source file that contains non-ASCII characters but
# currently has no BOM.
#
# WHY THIS FILE IS PURE ASCII (do not add Chinese text here):
#   Windows PowerShell 5.1 and csc decode a BOM-less UTF-8 file as ANSI (code page
#   936).  When that happens, a GBK double-byte sequence swallows the following
#   quote character and PowerShell reports "string is missing the terminator".
#   The editor tool strips the BOM from every file it edits, so this repair script
#   must survive being edited.  ASCII-only source can never be mis-decoded.
#
# Usage:  powershell -ExecutionPolicy Bypass -File ensure-bom.ps1 [-Path <dir>]
[CmdletBinding()]
param(
    [string]$Path = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $Path = Split-Path -Parent $PSScriptRoot
}
if (-not (Test-Path $Path)) {
    throw "Path not found: $Path"
}

$extensions = @('.cs', '.xaml', '.ps1', '.manifest', '.csproj', '.sln', '.md', '.json', '.config')
$excludePattern = '\\(bin|obj|\.vs|_preview|packages)\\'
$utf8Bom = New-Object System.Text.UTF8Encoding($true)

$files = Get-ChildItem -Path $Path -Recurse -File | Where-Object {
    ($extensions -contains $_.Extension.ToLowerInvariant()) -and ($_.FullName -notmatch $excludePattern)
}

$scanned = 0
$fixed = 0
foreach ($file in $files) {
    $scanned++
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        continue
    }
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $hasNonAscii = $false
    for ($i = 0; $i -lt $text.Length; $i++) {
        if ([int]$text[$i] -gt 127) { $hasNonAscii = $true; break }
    }
    if (-not $hasNonAscii) {
        continue
    }
    [System.IO.File]::WriteAllText($file.FullName, $text, $utf8Bom)
    Write-Host ("[bom] added BOM: {0}" -f $file.FullName)
    $fixed++
}

Write-Host ("[bom] scanned {0} file(s), fixed {1}." -f $scanned, $fixed)
