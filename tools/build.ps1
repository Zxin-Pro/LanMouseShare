<#
    build.ps1 —— 编译 LanMouseShare 解决方案
    本机没有 Visual Studio 时，会自动回退到 .NET Framework 自带的 MSBuild
    （C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe）。
    该 MSBuild 完全能够编译 net48 的经典 WPF 项目（含 XAML -> BAML）。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$MSBuildPath = '',
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

$toolsDir = $PSScriptRoot
$root = Split-Path -Parent $toolsDir
$solution = Join-Path $root 'LanMouseShare.sln'
if (-not (Test-Path $solution)) {
    throw "找不到解决方案文件：$solution"
}

function Find-MSBuild {
    $candidates = New-Object System.Collections.ArrayList
    $winDir = $env:WINDIR
    if (-not $winDir) { $winDir = 'C:\Windows' }
    [void]$candidates.Add((Join-Path $winDir 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'))
    [void]$candidates.Add((Join-Path $winDir 'Microsoft.NET\Framework\v4.0.30319\MSBuild.exe'))

    $programFiles = $env:ProgramFiles
    if ($programFiles) {
        foreach ($edition in @('Enterprise', 'Professional', 'Community', 'BuildTools')) {
            [void]$candidates.Add((Join-Path $programFiles ("Microsoft Visual Studio\2022\{0}\MSBuild\Current\Bin\MSBuild.exe" -f $edition)))
            [void]$candidates.Add((Join-Path $programFiles ("Microsoft Visual Studio\2019\{0}\MSBuild\Current\Bin\MSBuild.exe" -f $edition)))
        }
    }
    $programFilesX86 = $env:ProgramFiles -replace ' \(x86\)$', ''
    [void]$candidates.Add((Join-Path $programFilesX86 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'))

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    return ''
}

if (-not $MSBuildPath) { $MSBuildPath = Find-MSBuild }
if (-not $MSBuildPath -or -not (Test-Path $MSBuildPath)) {
    throw '找不到 MSBuild.exe。请用 -MSBuildPath 显式指定，或安装 Visual Studio / Build Tools。'
}

Write-Host ("[build] MSBuild      : {0}" -f $MSBuildPath)
Write-Host ("[build] 解决方案     : {0}" -f $solution)
Write-Host ("[build] 配置         : {0}" -f $Configuration)

$target = if ($Rebuild) { 'Rebuild' } else { 'Build' }
$msbuildArgs = @($solution, "/t:$target", "/p:Configuration=$Configuration", '/p:Platform=Any CPU', '/v:minimal', '/nologo')

& $MSBuildPath @msbuildArgs
$code = $LASTEXITCODE
if ($code -ne 0) {
    throw ("编译失败，MSBuild 退出码 = {0}" -f $code)
}

$exe = Join-Path $root ("src\LanMouseShare.App\bin\{0}\LanMouseShare.exe" -f $Configuration)
Write-Host ("[build] 成功：{0}" -f $exe)
