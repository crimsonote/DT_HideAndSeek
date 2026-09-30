# check-replay-equivalence.ps1 —— 重构期的"没改坏"机械证据
#
# 为什么需要它（而不是"编译过了"）：
#   AGENTS.md 记过一笔：补丁类嵌到第二层时**永远不会挂载**，而日志里的「失败 0」
#   完全不反映（FailedCount 只统计扫到的那一层）。所以"编译通过 + 失败 0"不等于"补丁还在"。
#
#   回放模块正在做同功能重构。第一阶段的"拆文件（partial）"在 IL 层面**应当逐字节等价**，
#   因此可以从**编译产物**里提取事实做快照，前后比对 —— 这样"我没改坏"就不需要进游戏验证。
#
# 提取四类事实（全部排序去重，纯文本，便于 git diff / Compare-Object）：
#   ① patch     —— 每个 [HarmonyPatch] 的目标（类级与方法级）：漏挂/改目标会显形
#   ② attr      —— [PatchFeature] / [ConfigField] 的参数：段名、默认值、描述变了会显形
#   ③ strings   —— 全部字符串字面量：丢一条诊断文案也会显形
#   ④ members   —— 全量类型与方法签名：漏搬/改名/改签名会显形
#
# 用法：
#   pwsh -File check-replay-equivalence.ps1 -Save      # 重构前：存基线
#   pwsh -File check-replay-equivalence.ps1 -Compare   # 重构后：与基线比对（有差异则退出码 2）
#
# 快照位置默认写到仓库外的 .tmps/（那里被 .git/info/exclude 忽略，属于过程材料）。

[CmdletBinding()]
param(
    [switch]$Save,
    [switch]$Compare,
    [string]$Dll,
    [string]$Snapshot
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Save -and -not $Compare) {
    Write-Host "用法：pwsh -File check-replay-equivalence.ps1 -Save | -Compare" -ForegroundColor Yellow
    exit 1
}

# ── 定位 dll ────────────────────────────────────────────────────────────
if (-not $Dll) {
    $binDir = Join-Path $root 'bin'
    $cand = @()
    if (Test-Path $binDir) {
        $cand = @(Get-ChildItem $binDir -Recurse -Filter 'HideAndSeek.dll' -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending)
    }
    if ($cand.Count -eq 0) {
        Write-Host "[FAIL] 找不到 HideAndSeek.dll —— 先 dotnet build -c Release" -ForegroundColor Red
        exit 2
    }
    $Dll = $cand[0].FullName
}
if (-not (Test-Path $Dll)) { Write-Host ("[FAIL] dll 不存在：" + $Dll) -ForegroundColor Red; exit 2 }

# ── 定位 Mono.Cecil ─────────────────────────────────────────────────────
$cecilCandidates = @(
    (Join-Path $root '..\libs\Mono.Cecil.dll'),
    (Join-Path $env:ProgramFiles '..\..\Program Files (x86)\Steam\steamapps\common\Deadly Trick\BepInEx\core\Mono.Cecil.dll'),
    'C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick\BepInEx\core\Mono.Cecil.dll'
)
$cecil = $cecilCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cecil) {
    Write-Host "[FAIL] 找不到 Mono.Cecil.dll（用 -p:GameDir=... 构建时需要对应的 BepInEx\core）" -ForegroundColor Red
    exit 2
}
[void][System.Reflection.Assembly]::LoadFrom($cecil)

# ── 默认快照路径 ────────────────────────────────────────────────────────
if (-not $Snapshot) {
    $Snapshot = Join-Path $root '..\.tmps\replay-equivalence.txt'
}

# ── 收集 ────────────────────────────────────────────────────────────────
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Dll)
$ver = $asm.Name.Version.ToString()

# 渲染一个特性参数（可能是 TypeReference / string / 嵌套 CustomAttributeArgument / 数组）
function Render-Arg($a) {
    if ($null -eq $a) { return 'null' }
    if ($a -is [Mono.Cecil.CustomAttributeArgument]) { return (Render-Arg $a.Value) }
    if ($a -is [Mono.Cecil.TypeReference]) { return ('type:' + $a.FullName) }
    if ($a -is [string]) { return ('str:' + $a.Replace([string][char]13, '\r').Replace([string][char]10, '\n')) }
    if ($a -is [System.Array]) {
        $parts = @()
        foreach ($x in $a) { $parts += (Render-Arg $x) }
        return ('[' + ($parts -join ',') + ']')
    }
    return ('val:' + $a)
}

$patchLines  = New-Object System.Collections.Generic.List[string]
$attrLines   = New-Object System.Collections.Generic.List[string]
$stringLines = New-Object System.Collections.Generic.List[string]
$memberLines = New-Object System.Collections.Generic.List[string]

# 递归遍历类型（含嵌套）
$stack = New-Object System.Collections.Stack
foreach ($t in $asm.MainModule.Types) { $stack.Push($t) }

while ($stack.Count -gt 0) {
    $t = $stack.Pop()
    foreach ($n in $t.NestedTypes) { $stack.Push($n) }

    $tname = $t.FullName
    $memberLines.Add('TYPE ' + $tname)
    if ($t.IsNested) { $memberLines.Add('  NESTED-IN ' + $t.DeclaringType.FullName) }

    # 类型级特性
    foreach ($ca in $t.CustomAttributes) {
        $an = $ca.AttributeType.Name -replace 'Attribute$', ''
        if ($an -eq 'HarmonyPatch') {
            $args = @()
            foreach ($x in $ca.ConstructorArguments) { $args += (Render-Arg $x) }
            $patchLines.Add('CLASS  ' + $tname + '  <=  ' + ($args -join ' | '))
        }
        elseif ($an -eq 'PatchFeature') {
            $args = @()
            foreach ($x in $ca.ConstructorArguments) { $args += (Render-Arg $x) }
            foreach ($f in $ca.Fields) { $args += ($f.Name + '=' + (Render-Arg $f.Argument)) }
            foreach ($p in $ca.Properties) { $args += ($p.Name + '=' + (Render-Arg $p.Argument)) }
            $attrLines.Add('PATCHFEATURE ' + $tname + '  ' + ($args -join ' | '))
        }
    }

    # 字段级特性（[ConfigField] 挂在配置字段上）
    foreach ($fd in $t.Fields) {
        foreach ($ca in $fd.CustomAttributes) {
            if (($ca.AttributeType.Name -replace 'Attribute$', '') -eq 'ConfigField') {
                $args = @()
                foreach ($x in $ca.ConstructorArguments) { $args += (Render-Arg $x) }
                $fname = $fd.Name
                $ftype = ''
                if ($fd.FieldType -is [Mono.Cecil.GenericInstanceType]) {
                    $ftype = $fd.FieldType.ElementType.FullName + '<' +
                             (($fd.FieldType.GenericArguments | ForEach-Object { $_.FullName }) -join ',') + '>'
                } else { $ftype = $fd.FieldType.FullName }
                $attrLines.Add('CONFIGFIELD ' + $tname + '.' + $fname + ' : ' + $ftype + '  ' + ($args -join ' | '))
            }
        }
    }

    # 方法
    foreach ($m in $t.Methods) {
        $ps = @()
        foreach ($p in $m.Parameters) { $ps += ($p.ParameterType.FullName + ' ' + $p.Name) }
        $memberLines.Add('  METHOD ' + $tname + '::' + $m.Name + '(' + ($ps -join ', ') + ') : ' + $m.ReturnType.FullName +
                         ' [' + $m.Attributes.ToString() + ']')

        # 方法级 Harmony 标记
        foreach ($ca in $m.CustomAttributes) {
            $an = $ca.AttributeType.Name -replace 'Attribute$', ''
            if ($an -eq 'HarmonyPatch') {
                $args = @()
                foreach ($x in $ca.ConstructorArguments) { $args += (Render-Arg $x) }
                $patchLines.Add('METHOD ' + $tname + '::' + $m.Name + '  <=  ' + ($args -join ' | '))
            }
            elseif ($an -like 'Harmony*') {
                $patchLines.Add('HOOK   ' + $an + '  ' + $tname + '::' + $m.Name)
            }
        }

        # 字符串字面量（日志文案）—— 含全部 ldstr，最严格
        if ($m.HasBody) {
            foreach ($ins in $m.Body.Instructions) {
                if ($ins.OpCode.Name -eq 'ldstr' -and $ins.Operand -is [string]) {
                    $s = ([string]$ins.Operand).Replace([string][char]13, '\r').Replace([string][char]10, '\n')
                    if ($s.Length -gt 0) { $stringLines.Add($s) }
                }
            }
        }
    }

    # 属性（get_/set_ 已作为方法收录；这里只补类型签名，便于看出自动属性）
    foreach ($p in $t.Properties) {
        $memberLines.Add('  PROPERTY ' + $tname + '::' + $p.Name + ' : ' + $p.PropertyType.FullName)
    }
}

$patchLines  = @($patchLines  | Sort-Object -Unique)
$attrLines   = @($attrLines   | Sort-Object -Unique)
$stringLines = @($stringLines | Sort-Object -Unique)
$memberLines = @($memberLines | Sort-Object -Unique)

$out = New-Object System.Collections.Generic.List[string]
$out.Add('# replay-equivalence snapshot')
$out.Add('# assembly  : ' + $asm.Name.Name + ' ' + $ver)
$out.Add('# dll       : ' + $Dll)
$out.Add('# generated : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$out.Add('# counts    : patch=' + $patchLines.Count + ' attr=' + $attrLines.Count +
         ' strings=' + $stringLines.Count + ' members=' + $memberLines.Count)
$out.Add('')
$out.Add('## PATCH TARGETS (' + $patchLines.Count + ')')
foreach ($x in $patchLines) { $out.Add($x) }
$out.Add('')
$out.Add('## ATTRIBUTES (' + $attrLines.Count + ')')
foreach ($x in $attrLines) { $out.Add($x) }
$out.Add('')
$out.Add('## MEMBERS (' + $memberLines.Count + ')')
foreach ($x in $memberLines) { $out.Add($x) }
$out.Add('')
$out.Add('## STRINGS (' + $stringLines.Count + ')')
foreach ($x in $stringLines) { $out.Add($x) }

# ── 保存 或 比对 ────────────────────────────────────────────────────────
if ($Save) {
    $dir = Split-Path -Parent $Snapshot
    if (-not (Test-Path $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
    [System.IO.File]::WriteAllLines($Snapshot, $out, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("[ ok ] 基线已保存：" + $Snapshot)
    Write-Host ("       补丁目标 " + $patchLines.Count + " / 特性 " + $attrLines.Count +
                " / 成员 " + $memberLines.Count + " / 字符串 " + $stringLines.Count)
    exit 0
}

if (-not (Test-Path $Snapshot)) {
    Write-Host ("[FAIL] 没有基线：" + $Snapshot + " —— 先跑 -Save") -ForegroundColor Red
    exit 2
}

$base = @(Get-Content $Snapshot -Encoding UTF8)
# 基线里的时间戳行必然不同，比对时忽略
$baseBody = @($base | Where-Object { $_ -notmatch '^# (generated|dll)' })
$curBody  = @($out  | Where-Object { $_ -notmatch '^# (generated|dll)' })

$diff = @(Compare-Object -ReferenceObject $baseBody -DifferenceObject $curBody)
if ($diff.Count -eq 0) {
    Write-Host "[ ok ] 与基线完全一致 —— 这次改动在 IL 层面零差异" -ForegroundColor Green
    exit 0
}

Write-Host ("[FAIL] 与基线有 " + $diff.Count + " 处差异：") -ForegroundColor Red
foreach ($d in $diff) {
    $mark = if ($d.SideIndicator -eq '=>') { '  + 新增/改后' } else { '  - 丢失/改前' }
    Write-Host ($mark + '  ' + $d.InputObject)
}
exit 2
