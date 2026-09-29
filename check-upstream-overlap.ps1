# check-upstream-overlap.ps1 —— 与上游 DT_Tools 的 Harmony 补丁目标重叠检查
#
# 用法：pwsh -File check-upstream-overlap.ps1 [-Upstream D:\git\DT_Tools\DT_Tools]
# 退出码非 0 表示有 FAIL。
#
# 为什么需要它：两个插件 Prefix 同一个方法时，**Harmony 只让第一个 return false 的 Prefix 生效**；
# 两个都是 void、只改参数（ref）时，**谁后跑谁的值留下**。默认优先级相同时顺序取决于
# 补丁挂载次序，会随插件加载先后漂移 —— 实测不出来、也没法复现。因此本模块约定：
# 只要与上游改了同一个方法，就必须写 [HarmonyPriority(...)] 钉住顺序（见 AGENTS.md 第 9 条）。
#
# 判定规则（保守，宁可少报）：
#   某目标两侧**都**有 Prefix 补丁，而本模块在该目标上没有任何 [HarmonyPriority] ⇒ FAIL。
#   其余组合（Postfix / Transpiler / 只有一侧）只列出来供人看，不判失败 ——
#   Prefix 返回 false 时 Harmony 仍会执行 Postfix，Transpiler 与 Prefix 也不互相截断。
#
# 注意：hook 类型与优先级的探测是**启发式**的（在 [HarmonyPatch] 之后 40 行内找第一个
# [HarmonyPrefix]/[HarmonyPostfix]/[HarmonyTranspiler] 与 [HarmonyPriority]），
# 因为两者常被 [PatchFeature(...)]、字段声明隔开。判成 "?" 表示没找到，需要人工看一眼。

param(
    [string]$Upstream = (Join-Path (Split-Path -Parent $PSScriptRoot) 'DT_Tools'),
    [string]$HsRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$script:fail = 0
function Fail([string]$m) { Write-Host ("[FAIL] " + $m) -ForegroundColor Red;    $script:fail++ }
function Ok([string]$m)   { Write-Host ("[ ok ] " + $m) -ForegroundColor Green }

if (-not (Test-Path $Upstream)) {
    Write-Host ("[SKIP] 上游源码目录不存在：" + $Upstream) -ForegroundColor Yellow
    Write-Host "       本检查只在 DT_Tools 源码就在旁边时有意义（独立 clone 时可忽略）。"
    exit 0
}

# 把三种等价写法归一成同一个 key：
#   "get_X" / nameof(X)+MethodType.Getter  ⇒  T::X::getter
#   "set_X" / nameof(X)+MethodType.Setter  ⇒  T::X::setter
#   "M"     / nameof(M)                    ⇒  T::M::method
function Normalize([string]$type, [string]$member) {
    $t = $type -replace '^.*\.', ''
    $m = $member -replace '^.*\.', ''
    $kind = 'method'
    if ($m -match '^get_(.+)$') { $m = $Matches[1]; $kind = 'getter' }
    elseif ($m -match '^set_(.+)$') { $m = $Matches[1]; $kind = 'setter' }
    "$t::$m::$kind"
}

function Scan-Lines($lines, [string]$origin) {
    $out = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $type = $null; $member = $null
        if ($line -match '\[HarmonyPatch\(typeof\(([^)]+)\)\s*,\s*"([^"]+)"') {
            $type = $Matches[1]; $member = $Matches[2]
        } elseif ($line -match '\[HarmonyPatch\(typeof\(([^)]+)\)\s*,\s*nameof\(([^)]+)\)') {
            $type = $Matches[1]; $member = $Matches[2]
            if ($line -match 'MethodType\.Getter') { $member = 'get_' + ($member -replace '^.*\.', '') }
            elseif ($line -match 'MethodType\.Setter') { $member = 'set_' + ($member -replace '^.*\.', '') }
        } elseif ($line -match '\[HarmonyPatch\(typeof\(([^)]+)\)\s*,\s*MethodType\.Constructor') {
            $type = $Matches[1]; $member = '.ctor'
        }
        if (-not $type) { continue }

        $hook = '?'; $prio = $false
        for ($j = $i; $j -lt [Math]::Min($i + 40, $lines.Count); $j++) {
            if (-not $prio -and $lines[$j] -match '\[HarmonyPriority\(') { $prio = $true }
            if ($hook -eq '?') {
                if ($lines[$j] -match '\[HarmonyPrefix') { $hook = 'Prefix' }
                elseif ($lines[$j] -match '\[HarmonyPostfix') { $hook = 'Postfix' }
                elseif ($lines[$j] -match '\[HarmonyTranspiler') { $hook = 'Transpiler' }
            }
            if ($prio -and $hook -ne '?') { break }
        }

        $out += [pscustomobject]@{
            Target = (Normalize $type $member)
            Hook   = $hook
            Prio   = $prio
            Origin = $origin
        }
    }
    $out
}

$hsFiles = Get-ChildItem $HsRoot -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$hs = @()
foreach ($f in $hsFiles) {
    $hs += Scan-Lines (Get-Content $f.FullName -Encoding UTF8) $f.Name
}

$dtFiles = Get-ChildItem $Upstream -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$dt = @()
foreach ($f in $dtFiles) {
    $dt += Scan-Lines (Get-Content $f.FullName -Encoding UTF8) $f.Name
}

Write-Host ("本模块补丁点 " + $hs.Count + " 个；上游补丁点 " + $dt.Count + " 个`n")

$overlap = @($hs | Group-Object Target | Where-Object { ($dt | ForEach-Object { $_.Target }) -contains $_.Name })

if ($overlap.Count -eq 0) {
    Ok "与上游没有补丁目标重叠"
    exit 0
}

Write-Host "重叠目标："
foreach ($g in $overlap) {
    $h = $g.Group
    $d = @($dt | Where-Object { $_.Target -eq $g.Name })
    $hsHooks = (($h.Hook | Sort-Object -Unique) -join '+')
    $dtHooks = (($d.Hook | Sort-Object -Unique) -join '+')
    $hasPrio = @($h | Where-Object { $_.Prio }).Count -gt 0
    $mark = if ($hasPrio) { 'priority' } else { 'no-priority' }
    Write-Host ("  {0,-42} HS[{1,-12}] DT[{2,-12}] {3}" -f $g.Name, $hsHooks, $dtHooks, $mark)
    Write-Host ("      本模块: " + (($h | ForEach-Object { $_.Origin + ":" + $_.Hook }) -join ', '))
    Write-Host ("      上游  : " + (($d | ForEach-Object { $_.Origin + ":" + $_.Hook }) -join ', '))
}
Write-Host ""

# ── 判定：两侧都有 Prefix 且本模块未钉优先级 ⇒ FAIL ──
foreach ($g in $overlap) {
    $h = $g.Group
    $d = @($dt | Where-Object { $_.Target -eq $g.Name })
    $hsHasPrefix = @($h | Where-Object { $_.Hook -eq 'Prefix' -or $_.Hook -eq '?' }).Count -gt 0
    $dtHasPrefix = @($d | Where-Object { $_.Hook -eq 'Prefix' -or $_.Hook -eq '?' }).Count -gt 0
    $hasPrio = @($h | Where-Object { $_.Prio }).Count -gt 0

    if ($hsHasPrefix -and $dtHasPrefix -and -not $hasPrio) {
        Fail ($g.Name + " 两侧都有 Prefix，但本模块没有 [HarmonyPriority] —— 谁生效取决于补丁挂载顺序（见 AGENTS.md 第 9 条）")
    }
}

Write-Host ""
if ($script:fail -eq 0) {
    Ok ("重叠 " + $overlap.Count + " 处，全部已钉住顺序或属于无冲突组合")
    exit 0
}
Write-Host ("失败：" + $script:fail + " 处重叠未钉顺序") -ForegroundColor Red
exit 1
