# HideAndSeek 部署脚本
# 用法：
#   pwsh -File deploy.ps1
#   pwsh -File deploy.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Deadly Trick"

param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick"
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

$dst = Join-Path $plugins "HideAndSeek.dll"
Copy-Item $src $dst -Force
Write-Host "已部署: $dst" -ForegroundColor Green

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
Write-Host "  1. 启动游戏，配置自动生成"
Write-Host "     - 装了 DT_Tools -> 写入其 DT_Tools.cfg（DT CONFIG 页面可改）"
Write-Host "     - 没装         -> BepInEx\config\HideAndSeek.cfg"
Write-Host "  2. 把 [HS_Mode] 的 Enabled 改为 true（或在 DT CONSOLE 执行 hs_mode on）"
