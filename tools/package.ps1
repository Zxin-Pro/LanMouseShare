<#
    package.ps1 —— 打包成可分发 zip

    产物：dist\LanMouseShare-<版本>-win.zip
    内容：LanMouseShare.exe、README.md、LICENSE、CHANGELOG.md、docs\ 全部文档、快速开始.txt

    退出码：0 = 成功，1 = 失败。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
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

# ---------- 版本号从可执行文件里读，保证和程序显示的一致 ----------
$version = (Get-Item $exe).VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.0.0.0' }
$version = $version.Trim()

$dist = Join-Path $root 'dist'
$stageName = "LanMouseShare-$version-win"
$stage = Join-Path $dist $stageName
$zip = Join-Path $dist "$stageName.zip"

Write-Host ("[pack] 版本   : {0}" -f $version)
Write-Host ("[pack] 暂存区 : {0}" -f $stage)

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# ---------- 主程序 ----------
Copy-Item $exe -Destination $stage

# 顺带把配置一起打进去，这样解压出来能直接跑（App.config 可能不存在，容错处理）
$appConfig = "$exe.config"
if (Test-Path $appConfig) { Copy-Item $appConfig -Destination $stage }

# ---------- 根目录文档 ----------
foreach ($name in @('README.md', 'LICENSE', 'CHANGELOG.md')) {
    $src = Join-Path $root $name
    if (Test-Path $src) { Copy-Item $src -Destination $stage }
}

# ---------- docs 目录 ----------
$docs = Join-Path $root 'docs'
if (Test-Path $docs) {
    Copy-Item $docs -Destination $stage -Recurse
}

# ---------- 快速开始.txt：解压之后第一眼要看的东西 ----------
# 用 UTF-8 带 BOM 写，否则 Windows 记事本打开是乱码。
$quick = @'
LanMouseShare 快速开始
======================

两台电脑在同一个局域网里，用一套鼠标键盘。

准备工作
--------
1. 解压到任意目录（路径里尽量不要有中文和空格）。
2. 两台电脑都双击 LanMouseShare.exe。
   程序需要管理员权限，每次启动都会弹 UAC，点「是」。

主机（插着鼠标键盘的那台）
--------------------------
1. 打开「共享」页，角色选「主机」。
2. 「连接密钥」随便填一串字符，记住它，两台电脑必须填一样的。
   可以点「生成密钥」让程序帮你生成一个。
3. 点「开始共享」。
4. 点「复制本机 IP」，这个 IP 要给副机用。

副机（没插键盘鼠标的那台）
--------------------------
1. 打开「共享」页，角色选「副机」。
2. 「连接密钥」填和主机一模一样的那串字符。
3. 「主机 IP」粘贴主机上复制来的那个地址。
4. 点「开始共享」。

设置屏幕位置
------------
两台都连上之后，打开「屏幕布局」页：
  - 副机在主机右边就选「右侧」，左边选「左侧」，上面选「上方」，下面选「下方」。
  - 如果两台屏幕的高度（或宽度）没对齐，用「偏移像素」微调。
    预览图会实时显示效果，对照实际摆放调就行。
  - 位置一定要和实际摆放一致，不然鼠标会往错的方向跑。

然后就可以用了
--------------
把鼠标往副机那个方向推出屏幕边缘，光标就直接过去了，不用按任何热键。
从副机屏幕边缘划回来，光标就回到主机。

想强制把光标拉回本机：Ctrl+Alt+Home（需要先打开「共享键盘」）。

主机点「开始共享」时会自动把防火墙的入站放行规则建好（域 / 专用 / 公用三档），
正常情况下你什么都不用做。如果 Windows 仍然弹窗问是否允许，选「允许」。
规则没建成功或者仍然连不上，见 docs\部署与安装.md 里手动放行 24800 端口的做法。

出问题了？
----------
日志在这里：%LOCALAPPDATA%\LanMouseShare\logs\
详细排查步骤见：docs\使用手册.md 的「常见问题」一节。

更多文档
--------
docs\使用手册.md      怎么用、出问题怎么办
docs\部署与安装.md    系统要求、Win7 装 .NET 4.8、防火墙放行
docs\技术架构.md      代码结构、线程模型、设计决策
'@
$quickPath = Join-Path $stage '快速开始.txt'
[System.IO.File]::WriteAllText($quickPath, $quick, (New-Object System.Text.UTF8Encoding($true)))

# ---------- 压缩 ----------
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$sizeKb = [math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host ''
Write-Host ("[pack] 打包完成：{0}" -f $zip)
Write-Host ("[pack] 体积    ：{0} KB" -f $sizeKb)
Write-Host '[pack] 包内文件：'
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($stage.Length + 1)
    Write-Host ("         {0}" -f $rel)
}
exit 0
