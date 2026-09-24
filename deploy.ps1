# deploy.ps1 —— 把最新构建复制到游戏插件目录
#
# 用法：
#   pwsh -File deploy.ps1
#   pwsh -File deploy.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Deadly Trick"

param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick",
    [switch]$SkipFreshnessCheck
)

$ErrorActionPreference = "Stop"

$src = Join-Path $PSScriptRoot "bin\Release\netstandard2.1\HideAndSeek.dll"
if (-not (Test-Path $src)) {
    Write-Host "找不到构建产物: $src" -ForegroundColor Red
    Write-Host "请先执行: dotnet build -c Release" -ForegroundColor Yellow
    exit 1
}

$plugins = Join-Path $GameDir "BepInEx\plugins"
if (-not (Test-Path $plugins)) {
    Write-Host "找不到插件目录: $plugins" -ForegroundColor Red
    Write-Host "请用 -GameDir 指定游戏安装目录" -ForegroundColor Yellow
    exit 1
}

# ── 新鲜度检查 ────────────────────────────────────────────────────────
# 只检查"文件存在"是不够的：编译失败时旧 DLL 仍在，于是照样复制上去、
# 还打印"已部署"，看起来一切正常 —— 实际装的是上一次的产物。
# 这里把 DLL 的时间戳与全部源文件比，旧了就停下。
if (-not $SkipFreshnessCheck) {
    $dllTime = (Get-Item $src).LastWriteTimeUtc
    $newer = Get-ChildItem -Path $PSScriptRoot -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.LastWriteTimeUtc -gt $dllTime }
    if ($newer) {
        Write-Host "构建产物比源文件旧，拒绝部署（请先 dotnet build -c Release）：" -ForegroundColor Red
        $newer | Select-Object -First 10 | ForEach-Object {
            Write-Host ("  " + $_.FullName.Replace($PSScriptRoot + '\', '') + "  " + $_.LastWriteTime)
        }
        exit 1
    }
}

$dst = Join-Path $plugins "HideAndSeek.dll"

# 游戏在跑时 DLL 被锁，Copy-Item 会失败；先给出明确提示而不是抛栈
$running = Get-Process -Name 'DeadlyTrick*' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "游戏正在运行，HideAndSeek.dll 被占用。请先退出游戏（或改用 redeploy.ps1）。" -ForegroundColor Red
    exit 1
}

Copy-Item $src $dst -Force

$hashSrc = (Get-FileHash $src -Algorithm SHA256).Hash
$hashDst = (Get-FileHash $dst -Algorithm SHA256).Hash
if ($hashSrc -ne $hashDst) {
    Write-Host "部署后哈希不一致，复制可能失败。" -ForegroundColor Red
    exit 1
}

Write-Host ("已部署: {0} 字节  {1}" -f (Get-Item $dst).Length, (Get-Item $dst).LastWriteTime) -ForegroundColor Green
Write-Host ("SHA256 前 16 位: " + $hashDst.Substring(0, 16))

# doorstop 检查：winhttp.dll 若被改名为 .disable，BepInEx 不会加载任何插件
$winhttp = Join-Path $GameDir "winhttp.dll"
$disabled = Join-Path $GameDir "winhttp.dll.disable"
if (-not (Test-Path $winhttp) -and (Test-Path $disabled)) {
    Write-Host ""
    Write-Host "警告: winhttp.dll 当前是 .disable 状态，BepInEx 不会加载任何插件。" -ForegroundColor Yellow
    Write-Host "      需要把 winhttp.dll.disable 改回 winhttp.dll 后才会生效。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "接下来:" -ForegroundColor Cyan
Write-Host "  1. 启动游戏，日志里应出现 '失败 0'（hs_check 可看各功能挂载状态）"
Write-Host "  2. 把 [HS_Mode] 的 Enabled 改为 true（或在 DT CONSOLE 执行 hs_mode on）"
