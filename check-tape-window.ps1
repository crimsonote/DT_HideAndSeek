# check-tape-window.ps1 —— 用 tapedump 的真实数据离线检验窗口算法
#
# 目的：证明"锚点应该取**离事件时刻最近**的那一枚 NormalTimeEdit"是对的，
#       而现有实现取"最后一枚"会偏。
#
# 用法：pwsh -File check-tape-window.ps1 [-DumpDir <tapedump 目录>]

param(
    [string]$DumpDir = "C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick\BepInEx\plugins\tapedump"
)

if (-not (Test-Path $DumpDir)) { Write-Host "找不到 $DumpDir"; exit 1 }
$dir = Get-ChildItem $DumpDir -Directory | Sort-Object Name | Select-Object -Last 1
Write-Host ("局目录: " + $dir.Name + "`n")

$rows = @()
foreach ($f in (Get-ChildItem $dir.FullName -File | Sort-Object Name)) {
    $lines = Get-Content $f.FullName -Encoding UTF8

    $atM = [regex]::Match(($lines -join "`n"), 'Clip\.At = ([\d.]+)\s+Before = ([\d.]+)\s+After = ([\d.]+)')
    if (-not $atM.Success) { continue }
    $at = [double]$atM.Groups[1].Value
    $before = [double]$atM.Groups[2].Value
    $after = [double]$atM.Groups[3].Value

    # 原始段与重裁段的分界
    $sep = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -like '## 重裁后*' })
    $raw = if ($sep.Count) { $lines[0..($sep[0] - 2)] } else { $lines }
    $new = if ($sep.Count) { $lines[($sep[0] + 1)..($lines.Count - 1)] } else { @() }

    # 原始段里所有 NormalTimeEdit 的时间戳
    $anchors = @()
    foreach ($ln in $raw) {
        if ($ln -match 'EditShot' -and $ln -match 'NormalTimeEdit') {
            $m = [regex]::Match($ln, '^\[\s*\d+\] t=\s*([\d.]+)')
            if ($m.Success) { $anchors += [double]$m.Groups[1].Value }
        }
    }
    if ($anchors.Count -eq 0) { continue }

    # 实际输出区间
    $out = @($new | Where-Object { $_ -match '^\[\s*\d+\] t=' } |
             ForEach-Object { [double]([regex]::Match($_, 't=\s*([\d.]+)').Groups[1].Value) })
    if ($out.Count -eq 0) { continue }

    $oldAnchor = $anchors[-1]                                   # 现有实现：最后一枚
    $newAnchor = $anchors | Sort-Object { [Math]::Abs($_ - $at) } | Select-Object -First 1  # 新实现：离 At 最近

    $rows += [pscustomobject]@{
        文件     = $f.Name
        At       = $at
        规范窗口 = "[{0:F2},{1:F2}]" -f ($at - $before), ($at + $after)
        旧锚点   = $oldAnchor
        旧窗口   = "[{0:F2},{1:F2}]" -f ($oldAnchor - $before), ($oldAnchor + $after)
        新锚点   = $newAnchor
        新窗口   = "[{0:F2},{1:F2}]" -f ($newAnchor - $before), ($newAnchor + $after)
        实际输出 = "[{0:F2},{1:F2}]" -f $out[0], $out[-1]
        旧偏差   = [Math]::Round($oldAnchor - $at, 2)
        新偏差   = [Math]::Round($newAnchor - $at, 2)
    }
}

$rows | Format-Table -AutoSize

Write-Host ""
$bad = @($rows | Where-Object { [Math]::Abs($_.旧偏差) -gt 0.5 }).Count
$good = @($rows | Where-Object { [Math]::Abs($_.新偏差) -gt 0.5 }).Count
Write-Host ("现有实现（取最后一枚）偏差 > 0.5s 的幕: " + $bad + " / " + $rows.Count) -ForegroundColor Yellow
Write-Host ("新实现（取离 At 最近）偏差 > 0.5s 的幕: " + $good + " / " + $rows.Count) -ForegroundColor Green
