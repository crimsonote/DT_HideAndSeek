<#
.SYNOPSIS
  把"这一次测试"的日志与产物归档到 .tmps/logs/<时间戳>-<标签>/，避免下次测试覆盖掉证据。

.DESCRIPTION
  为什么需要它：BepInEx 的 `LogOutput.log` 与客户端的 `Player.log` 都只保留"当前 + 上一份"，
  一次联机测试之后如果又启动过游戏，那次的证据就没了 —— 本项目的「后两段坏」「只播第一幕」
  两次排查都因此只能靠回忆与推测。

  归档内容：
    · 服务端日志   `<GameDir>\BepInEx\LogOutput.log`
    · 客户端日志   `%USERPROFILE%\AppData\LocalLow\FinalBlow\DeadlyTrick\Player.log` 与 `Player-prev.log`
    · 磁带 dump    `<GameDir>\BepInEx\plugins\tapedump\` 下最近 N 个目录（默认 3）
    · 配置快照     `<GameDir>\BepInEx\config\HideAndSeek.cfg`
    · 版本信息     `_meta.txt`（DLL 的 SHA256 + 插件版本 + 提交号 + 时间）

.PARAMETER Tag
  给这批归档起个标签（例如 `联机A`），会出现在目录名里。

.PARAMETER KeepDumps
  归档最近几个 tapedump 目录，默认 3。

.PARAMETER Clear
  归档完成后**清空**当前日志（`LogOutput.log` / `Player.log` / `Player-prev.log`），
  这样下一轮测试的日志边界很干净。⚠ 归档是清空**之前**做的，所以不会丢东西。

.EXAMPLE
  pwsh -File archive-logs.ps1 -Tag 联机A
  pwsh -File archive-logs.ps1 -Tag 联机A -Clear
#>
[CmdletBinding()]
param(
    [string]$Tag = "",
    [int]$KeepDumps = 3,
    [switch]$Clear,
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick"
)

$ErrorActionPreference = "Stop"

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$stamp   = Get-Date -Format "yyyyMMdd-HHmmss"
$name    = if ($Tag) { "$stamp-$Tag" } else { $stamp }
$root    = Join-Path (Split-Path -Parent $PSScriptRoot) "logs"
$out     = Join-Path $root $name
New-Item -ItemType Directory -Path $out -Force | Out-Null

$copied = New-Object System.Collections.Generic.List[string]
function Copy-One([string]$src, [string]$dstName) {
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $out $dstName) -Force
        $script:copied.Add("$dstName  <-  $src")
    } else {
        $script:copied.Add("$dstName  <-  （不存在）$src")
    }
}

# ── ① 日志 ────────────────────────────────────────────────────────────
Copy-One (Join-Path $GameDir "BepInEx\LogOutput.log") "LogOutput.log"

$clientDir = Join-Path $env:USERPROFILE "AppData\LocalLow\FinalBlow\DeadlyTrick"
Copy-One (Join-Path $clientDir "Player.log")      "Player.log"
Copy-One (Join-Path $clientDir "Player-prev.log") "Player-prev.log"

# ── ② 配置快照 ────────────────────────────────────────────────────────
Copy-One (Join-Path $GameDir "BepInEx\config\HideAndSeek.cfg") "HideAndSeek.cfg"

# ── ③ 最近 N 个 tapedump 目录 ─────────────────────────────────────────
$dumpRoot = Join-Path $GameDir "BepInEx\plugins\tapedump"
$dumpCount = 0
if (Test-Path $dumpRoot) {
    $dirs = Get-ChildItem $dumpRoot -Directory | Sort-Object Name | Select-Object -Last $KeepDumps
    foreach ($d in $dirs) {
        $dst = Join-Path $out ("tapedump\" + $d.Name)
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Get-ChildItem $d.FullName -File | ForEach-Object { Copy-Item $_.FullName $dst -Force }
        $dumpCount += @(Get-ChildItem $d.FullName -File).Count
    }
}

# ── ④ 版本信息（用它确认"归档的到底是哪一版"，比看时间戳可靠）─────────
$dll = Join-Path $GameDir "BepInEx\plugins\HideAndSeek.dll"
$meta = New-Object System.Collections.Generic.List[string]
$meta.Add("归档时间   : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$meta.Add("标签       : $(if ($Tag) { $Tag } else { '(无)' })")
$meta.Add("插件 DLL   : $(if (Test-Path $dll) { "$((Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0,16))  $((Get-Item $dll).LastWriteTime)" } else { '（不存在）' })")
$meta.Add("tapedump   : 归档 $($dirs.Count) 个目录 / $dumpCount 个文件")
$meta.Add("")

# 源码侧的版本与提交（可能不是同一个 worktree，所以失败不致命）
try {
    $meta.Add("git HEAD   : " + (git -C $PSScriptRoot rev-parse --short HEAD 2>$null))
    $meta.Add("git branch : " + (git -C $PSScriptRoot rev-parse --abbrev-ref HEAD 2>$null))
} catch { $meta.Add("git        : （取不到）") }
$plugin = Join-Path $PSScriptRoot "Plugin.cs"
if (Test-Path $plugin) {
    $v = [regex]::Match((Get-Content $plugin -Raw), 'Version\s*=\s*"([^"]+)"').Groups[1].Value
    $meta.Add("源码版本   : $v")
}
$meta.Add("")
$meta.Add("── 复制清单 ──")
$meta.AddRange([string[]]$copied)

[System.IO.File]::WriteAllLines((Join-Path $out "_meta.txt"), $meta, $utf8Bom)

Write-Host "已归档到: $out"
$meta | ForEach-Object { Write-Host "  $_" }

# ── ⑤ 可选：清空当前日志（为下一轮测试划边界）────────────────────────
if ($Clear) {
    foreach ($f in @(
        (Join-Path $GameDir "BepInEx\LogOutput.log"),
        (Join-Path $clientDir "Player.log"),
        (Join-Path $clientDir "Player-prev.log")
    )) {
        if (Test-Path $f) {
            # 有的进程会持有句柄；失败只提示、不中断
            try { [System.IO.File]::WriteAllText($f, "", $utf8Bom); Write-Host "  已清空: $f" }
            catch { Write-Host "  ⚠ 清空失败（文件被占用？）: $f" }
        }
    }
}
