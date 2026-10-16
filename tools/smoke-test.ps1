<#
    smoke-test.ps1 —— LanMouseShare 自动验收（第七阶段 · 测试代理的核心产出）

    两段式验收：
      第 1 段  自检（--selftest=N）：权限 / DPI / 目标框架 / 配置目录 / 日志 / 窗口 / 托盘 /
                                   进程架构 / 显示器 / JSON 配置 / 局域网地址 / 屏幕布局 / 输入注入
      第 2 段  回环联调（--nettest=M）：坐标映射 / 协议编解码与粘包 / TCP 连接 / 密钥认证 /
                                   各类输入帧送达 / 发送泵合并与计数 / 心跳保活 / 断线检测 / 错误密钥被拒

    第 2 段全程只在本机回环地址上跑，不装钩子、不注入输入、不动真实鼠标，所以随时可跑、可进 CI。

    退出码：0 = 全部通过，1 = 有失败项，2 = 脚本自身出错。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [int]$SelfTestSeconds = 4,
    [int]$NetTestMs = 5000,
    [int]$TimeoutSeconds = 90,
    [switch]$SkipBuild,
    [switch]$SkipNetTest
)

$ErrorActionPreference = 'Stop'

$toolsDir = $PSScriptRoot
$root = Split-Path -Parent $toolsDir
$exe = Join-Path $root ("src\LanMouseShare.App\bin\{0}\LanMouseShare.exe" -f $Configuration)

if (-not $SkipBuild) {
    & (Join-Path $toolsDir 'build.ps1') -Configuration $Configuration
}

if (-not (Test-Path $exe)) {
    throw "找不到可执行文件：$exe"
}

# ---------- 校验 0：可执行文件里确实嵌入了 requireAdministrator 清单 ----------
$bytes = [System.IO.File]::ReadAllBytes($exe)
$exeText = [System.Text.Encoding]::ASCII.GetString($bytes)
$hasAdminManifest = $exeText.Contains('requireAdministrator')
Write-Host ("[smoke] 清单内嵌 requireAdministrator : {0}" -f $hasAdminManifest)

$logDir = Join-Path $env:LOCALAPPDATA 'LanMouseShare\logs'
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

# 取当前最新的日志文件，并记住它此刻的长度 —— 之后只读新增的那一段。
# 同一天里两段验收写的是同一个文件，必须靠偏移量切分，否则第一段的结论会串到第二段。
function Get-CurrentLog {
    $f = Get-ChildItem $logDir -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $len = 0
    if ($f) { $len = $f.Length }
    return [pscustomobject]@{ File = $f; Length = $len }
}

# 从指定字节偏移开始读 UTF-8 文本。
# 为什么不用 Get-Content -Encoding UTF8：它读的是整个文件，没法从中间开始。
# 日志是 UTF-8 无 BOM，用 StreamReader 必须显式传 UTF8，否则中文全是乱码。
function Read-LogFrom {
    param([string]$Path, [long]$Offset)
    if (-not (Test-Path $Path)) { return @() }
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        if ($Offset -gt 0) { $fs.Seek($Offset, [System.IO.SeekOrigin]::Begin) | Out-Null }
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        $text = $sr.ReadToEnd()
        $sr.Dispose()
    } finally {
        $fs.Dispose()
    }
    return @($text -split "`r`n|`n")
}

<#
    跑一轮：启动 exe（带给定参数）-> 等它自己退出 -> 返回退出码与这段期间新增的日志行。
    程序在 --selftest / --nettest 模式下会自己退出，不需要界面交互。
#>
function Invoke-Round {
    param([string]$Arguments, [string]$Title)

    Write-Host ''
    Write-Host ("========== {0} ==========" -f $Title)

    $snapshot = Get-CurrentLog
    Write-Host ("[smoke] 启动：{0} {1}" -f $exe, $Arguments)
    $process = Start-Process -FilePath $exe -ArgumentList $Arguments -PassThru

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }

    $forcedKill = $false
    if (-not $process.HasExited) {
        $forcedKill = $true
        Write-Host '[smoke] 等待超时，强制结束进程'
        try { $process.Kill() } catch { }
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if (-not $process.HasExited) {
            # 程序带 requireAdministrator 清单，非提权进程 Stop-Process / Kill 只会得到 Access is denied。
            # 可靠做法是让 taskkill 自己提权来收尸，否则下一次编译会撞 MSB3021 文件占用。
            Write-Host '[smoke] 进程未退出（自提权进程常见），改用提权 taskkill 收尾'
            Start-Process -FilePath taskkill -ArgumentList '/F','/IM','LanMouseShare.exe' -Verb RunAs -Wait -ErrorAction SilentlyContinue | Out-Null
            Start-Sleep -Milliseconds 800
            $process.Refresh()
        }
        Start-Sleep -Milliseconds 300
    }
    $exitCode = $null
    try { $exitCode = $process.ExitCode } catch { $exitCode = $null }

    Start-Sleep -Milliseconds 300
    $logFile = Get-ChildItem $logDir -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $offset = 0
    if ($snapshot.File -and $logFile -and $snapshot.File.FullName -eq $logFile.FullName) {
        $offset = $snapshot.Length
    }
    $lines = @()
    if ($logFile) { $lines = Read-LogFrom -Path $logFile.FullName -Offset $offset }
    if ($logFile) { Write-Host ("[smoke] 日志文件   : {0}" -f $logFile.FullName) }
    Write-Host ("[smoke] 进程退出码 : {0}" -f $exitCode)

    return [pscustomobject]@{
        ExitCode   = $exitCode
        ForcedKill = $forcedKill
        Lines      = $lines
    }
}

function Show-Interesting {
    param($Lines)
    foreach ($line in $Lines) {
        if ($line -match '\[(PASS|FAIL)\]|SELFTEST RESULT|NETTEST RESULT|DPI|管理员权限|操作系统|目标框架|运行架构|托盘|配置：|局域网地址') {
            Write-Host $line
        }
    }
}

# ==================================================================
# 第 1 段：自检
# ==================================================================
$stage1 = Invoke-Round -Arguments ("--selftest={0}" -f $SelfTestSeconds) -Title '第 1 段 · 自检'

Write-Host ''
Write-Host '---------------- 自检明细 ----------------'
Show-Interesting -Lines $stage1.Lines
Write-Host '------------------------------------------'

$selfTestLine = $stage1.Lines | Where-Object { $_ -match 'SELFTEST RESULT' } | Select-Object -Last 1
$selfPass = @($stage1.Lines | Where-Object { $_ -match '\[PASS\]' })
$selfFail = @($stage1.Lines | Where-Object { $_ -match '\[FAIL\]' })
$selfOk = ($selfTestLine -and $selfTestLine -match 'SELFTEST RESULT: PASS' -and $stage1.ExitCode -eq 0 -and $selfFail.Count -eq 0 -and -not $stage1.ForcedKill)

Write-Host ("[smoke] 自检结论 : {0}" -f $selfTestLine)
Write-Host ("[smoke] PASS 项  : {0}    FAIL 项 : {1}" -f $selfPass.Count, $selfFail.Count)

# ==================================================================
# 第 2 段：回环联调
# ==================================================================
$netOk = $true
$netTestLine = '(已跳过)'
$netPassCount = 0
$netFailCount = 0

if (-not $SkipNetTest) {
    $stage2 = Invoke-Round -Arguments ("--nettest={0}" -f $NetTestMs) -Title '第 2 段 · 回环联调'

    Write-Host ''
    Write-Host '---------------- 联调明细 ----------------'
    Show-Interesting -Lines $stage2.Lines
    Write-Host '------------------------------------------'

    $netTestLine = $stage2.Lines | Where-Object { $_ -match 'NETTEST RESULT' } | Select-Object -Last 1
    $netPass = @($stage2.Lines | Where-Object { $_ -match '\[PASS\]' })
    $netFail = @($stage2.Lines | Where-Object { $_ -match '\[FAIL\]' })
    $netPassCount = $netPass.Count
    $netFailCount = $netFail.Count
    $netOk = ($netTestLine -and $netTestLine -match 'NETTEST RESULT: PASS' -and $stage2.ExitCode -eq 0 -and $netFail.Count -eq 0 -and -not $stage2.ForcedKill)

    Write-Host ("[smoke] 联调结论 : {0}" -f $netTestLine)
    Write-Host ("[smoke] PASS 项  : {0}    FAIL 项 : {1}" -f $netPassCount, $netFailCount)
}

# ==================================================================
# 总结
# ==================================================================
# PowerShell 5.1 里 if 不能直接当表达式写进括号里求值，先取成变量再用。
$selfVerdict = '失败'
if ($selfOk) { $selfVerdict = '通过' }
$netVerdict = '失败'
if ($netOk) { $netVerdict = '通过' }

$allOk = $selfOk -and $netOk -and $hasAdminManifest

Write-Host ''
Write-Host '================== 验收总结 =================='
Write-Host ("  清单内嵌 requireAdministrator : {0}" -f $hasAdminManifest)
Write-Host ("  第 1 段 自检                  : {0}  ({1})" -f $selfVerdict, $selfTestLine)
Write-Host ("  第 2 段 回环联调              : {0}  ({1})" -f $netVerdict, $netTestLine)
Write-Host '=============================================='
Write-Host ''
if ($allOk) {
    Write-Host '[smoke] 验收通过。'
    exit 0
}
Write-Host '[smoke] 验收失败。'
exit 1
