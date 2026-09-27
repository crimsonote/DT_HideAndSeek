# deploy-version.ps1 —— 部署**指定版本**的构建快照（用于从实验版回退到正式版）
#
# 为什么需要它：
#   实验性改动（例如设置页折叠栏）会部署上去覆盖游戏里的正式版。
#   要回到某个已发布的正式版，就得有一份"那个版本当时的 DLL"——
#   光靠 git 回退源码再编译，既慢又依赖当时的构建环境。
#   所以每个正式版发布时把产物快照到 .tmps\releases\<tag>\，这里负责取回来。
#
# 用法：
#   pwsh -File deploy-version.ps1 -List                 # 看有哪些快照
#   pwsh -File deploy-version.ps1 -Version v1.2.0       # 部署那个版本
#   pwsh -File deploy-version.ps1 -Version v1.2.0 -GameDir "D:\SteamLibrary\..."
#
# 相关脚本：
#   deploy.ps1     —— 部署"当前构建"（日常开发用，带回退前的新鲜度检查）
#   redeploy.ps1   —— 游戏运行中也能换 DLL 的变体（若存在）

param(
    [string]$Version,
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick",
    [switch]$List
)

$ErrorActionPreference = "Stop"

# .tmps\releases 是刻意不进 git 的（二进制产物），放在仓库的兄弟目录下
$releases = Join-Path (Split-Path $PSScriptRoot -Parent) ".tmps\releases"

# ── -List：列出可用快照 ──────────────────────────────────────────────
if ($List -or [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "可用的版本快照（$releases）：" -ForegroundColor Cyan
    if (-not (Test-Path $releases)) {
        Write-Host "  （还没有任何快照。发布正式版时执行：`n" +
                   "     Copy-Item bin\Release\netstandard2.1\HideAndSeek.dll " +
                   "$releases\<tag>\HideAndSeek.dll）" -ForegroundColor Yellow
        exit 0
    }
    $any = $false
    Get-ChildItem $releases -Directory | Sort-Object Name | ForEach-Object {
        $dll = Join-Path $_.FullName "HideAndSeek.dll"
        if (Test-Path $dll) {
            $any = $true
            $f = Get-Item $dll
            Write-Host ("  {0,-10} {1,8} 字节  {2}  SHA256前16 {3}" -f `
                $_.Name, $f.Length, $f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), `
                (Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0, 16))
        }
    }
    if (-not $any) { Write-Host "  （没有任何含 HideAndSeek.dll 的快照目录）" -ForegroundColor Yellow }

    $state = Join-Path $releases "deployed.txt"
    if (Test-Path $state) {
        Write-Host ""
        Write-Host ("最近一次用本脚本部署的是：" + (Get-Content $state -Raw).Trim()) -ForegroundColor Green
    }
    exit 0
}

# ── 定位快照 ────────────────────────────────────────────────────────
$dir = Join-Path $releases $Version
$src = Join-Path $dir "HideAndSeek.dll"
if (-not (Test-Path $src)) {
    Write-Host "找不到快照: $src" -ForegroundColor Red
    Write-Host ""
    Write-Host "两条出路：" -ForegroundColor Yellow
    Write-Host "  1) 如果这是你已发布的版本，用 git 把它构建出来再快照："
    Write-Host "       git worktree add ..\..\.tmps\build-$Version $Version"
    Write-Host "       cd ..\..\.tmps\build-$Version ; dotnet build -c Release --nologo"
    Write-Host "       New-Item -ItemType Directory -Force '$dir'"
    Write-Host "       Copy-Item bin\Release\netstandard2.1\HideAndSeek.dll '$src'"
    Write-Host "  2) 或者用 deploy.ps1 部署当前构建"
    exit 1
}

$plugins = Join-Path $GameDir "BepInEx\plugins"
if (-not (Test-Path $plugins)) {
    Write-Host "找不到插件目录: $plugins（用 -GameDir 指定游戏安装目录）" -ForegroundColor Red
    exit 1
}

$running = Get-Process -Name 'DeadlyTrick*' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "游戏正在运行，HideAndSeek.dll 被占用。请先退出游戏。" -ForegroundColor Red
    exit 1
}

$dst = Join-Path $plugins "HideAndSeek.dll"

# 覆盖前先留一份"被替换掉的那份"，便于再退回去一步
if (Test-Path $dst) {
    $backup = Join-Path $releases ("_replaced-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".dll")
    Copy-Item $dst $backup -Force
    Write-Host ("被替换的那份已留档: " + $backup) -ForegroundColor DarkGray
}

Copy-Item $src $dst -Force

$hSrc = (Get-FileHash $src -Algorithm SHA256).Hash
$hDst = (Get-FileHash $dst -Algorithm SHA256).Hash
if ($hSrc -ne $hDst) {
    Write-Host "部署后哈希不一致，复制可能失败。" -ForegroundColor Red
    exit 1
}

Set-Content -Path (Join-Path $releases "deployed.txt") -Value $Version -NoNewline -Encoding ASCII

Write-Host ("已部署 {0}: {1} 字节  {2}" -f $Version, (Get-Item $dst).Length, (Get-Item $dst).LastWriteTime) -ForegroundColor Green
Write-Host ("SHA256 前 16 位: " + $hDst.Substring(0, 16))
Write-Host ""
Write-Host "接下来: 启动游戏，日志里应出现 '失败 0'。" -ForegroundColor Cyan
