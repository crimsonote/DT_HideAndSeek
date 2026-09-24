# verify.ps1 —— 提交 / 构建前的静态检查（纯文本，不需要 Roslyn）
#
# 用法：pwsh -File verify.ps1
# 退出码非 0 表示有 FAIL。WARN 不影响退出码。
#
# 这些检查都是"实际踩过的坑"沉淀下来的：
#   BOM          —— 无 BOM 时 Roslyn 按 GBK 读源码，中文在编译期就已损坏且无警告
#   版本一致性   —— tag 与代码版本不符会让"回退到正式版本"失去意义
#   段名唯一     —— 重复段名会让 PatchLoader 抛异常、整个插件不加载
#   违规 Bind    —— AGENTS.md 要求配置一律走 [ConfigField] 自动绑定
#   迁移表对照   —— 段重命名时漏搬某个键，用户定制会静默回落到默认值（v16 踩过）
#   死配置       —— 声明了但代码从不读的 [ConfigField] 会出现在 DT CONFIG 里骗人

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$script:fail = 0
$script:warn = 0
function Fail([string]$m) { Write-Host ("[FAIL] " + $m) -ForegroundColor Red;    $script:fail++ }
function Warn([string]$m) { Write-Host ("[WARN] " + $m) -ForegroundColor Yellow; $script:warn++ }
function Ok([string]$m)   { Write-Host ("[ ok ] " + $m) -ForegroundColor Green }

function ReadText([string]$path) { return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) }

$cs = @(Get-ChildItem $root -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\obj\\' })
Write-Host ("检查 " + $cs.Count + " 个 .cs 文件`n")

# ── 1) BOM ───────────────────────────────────────────────────────────
$noBom = @()
foreach ($f in $cs) {
    $b = [System.IO.File]::ReadAllBytes($f.FullName)
    if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
        $noBom += $f.FullName.Replace($root + '\', '')
    }
}
if ($noBom.Count -eq 0) { Ok ("BOM：全部 " + $cs.Count + " 个 .cs 都带 UTF-8 BOM") }
else { Fail ("BOM 缺失：" + ($noBom -join ', ')) }

# ── 2) 版本一致性（Plugin.cs / csproj / 最近 tag）─────────────────────
$pluginTxt = ReadText (Join-Path $root 'Plugin.cs')
$csprojTxt = ReadText (Join-Path $root 'HideAndSeek.csproj')
$pluginVer = ([regex]::Match($pluginTxt, 'Version\s*=\s*"([^"]+)"')).Groups[1].Value
$csprojVer = ([regex]::Match($csprojTxt, '<Version>([^<]+)</Version>')).Groups[1].Value

if ($pluginVer -and $pluginVer -eq $csprojVer) { Ok ("版本一致：Plugin.cs 与 csproj 都是 " + $pluginVer) }
else { Fail ("版本不一致：Plugin.cs=" + $pluginVer + "  csproj=" + $csprojVer) }

$tag = (& git -C $root describe --tags --exact-match 2>$null)
if ($LASTEXITCODE -eq 0 -and $tag) {
    $tagVer = $tag -replace '^v', ''
    if ($tagVer -eq $pluginVer) { Ok ("当前提交已被 " + $tag + " 标记，与代码版本相符") }
    else { Fail ("tag " + $tag + " 与代码版本 " + $pluginVer + " 不符") }
} else {
    Warn "当前提交没有 exact-match tag（工作树处于两个 tag 之间，产物自称的版本号不可追溯）"
}

# ── 3) 段名唯一 ──────────────────────────────────────────────────────
$sections = @()
foreach ($f in $cs) {
    foreach ($m in [regex]::Matches((ReadText $f.FullName), 'section:\s*"([^"]+)"')) {
        $sections += $m.Groups[1].Value
    }
}
$dup = @($sections | Group-Object | Where-Object { $_.Count -gt 1 })
if ($dup.Count -eq 0) { Ok ("段名唯一：" + $sections.Count + " 个 [PatchFeature] 段名无重复") }
else { Fail ("段名重复：" + (($dup | ForEach-Object { $_.Name }) -join ', ') + "（PatchLoader 会抛异常，整个插件不加载）") }

# ── 4) 违规 Config.Bind（AGENTS.md：Features/ 与 Console/ 下禁止手写）──
$badBind = @()
foreach ($f in $cs) {
    if ($f.FullName -match '\\Core\\') { continue }
    if ((ReadText $f.FullName) -match 'Config\s*\.\s*Bind') { $badBind += $f.FullName.Replace($root + '\', '') }
}
if ($badBind.Count -eq 0) { Ok "无手写 Config.Bind（Features/ 与 Console/ 下）" }
else { Fail ("手写 Config.Bind：" + ($badBind -join ', ')) }

# ── 5) 迁移表对照：段重命名的功能，每个 [ConfigField] 都要有搬迁条目 ──
$cmdFeature = Join-Path $root 'Features\Rule\CommandFeature.cs'
$migration = Join-Path $root 'Core\ConfigMigration.cs'
if ((Test-Path $cmdFeature) -and (Test-Path $migration)) {
    $fields = @()
    foreach ($m in [regex]::Matches((ReadText $cmdFeature), 'public\s+static\s+ConfigEntry<[^>]+>\s+(\w+)\s*;')) {
        $fields += $m.Groups[1].Value
    }
    $moved = @()
    foreach ($m in [regex]::Matches((ReadText $migration), 'CommandFeature\.(\w+)')) {
        $moved += $m.Groups[1].Value
    }
    $missing = @($fields | Where-Object { $moved -notcontains $_ })
    if ($missing.Count -eq 0) { Ok ("迁移表对照：[Command] 的 " + $fields.Count + " 个配置项都有搬迁条目") }
    else { Fail ("迁移表漏搬：" + ($missing -join ', ') + "（用户在这些键上的定制会静默回落到默认值）") }

    $extra = @($moved | Where-Object { $fields -notcontains $_ })
    if ($extra.Count -gt 0) { Fail ("迁移表指向了不存在的配置项：" + ($extra -join ', ')) }
}

# ── 6) 死配置：声明了但全仓没有第二处引用 ────────────────────────────
function StripComments([string]$t) {
    $t = [regex]::Replace($t, '(?m)^\s*///?.*$', '')
    $t = [regex]::Replace($t, '//.*$', '')
    return $t
}

$dead = @()
foreach ($f in $cs) {
    $txt = ReadText $f.FullName
    foreach ($m in [regex]::Matches($txt, '\[ConfigField\([^\]]*\)\]\s*\r?\n\s*public\s+static\s+ConfigEntry<[^>]+>\s+(\w+)\s*;')) {
        $name = $m.Groups[1].Value
        $count = 0
        foreach ($g in $cs) {
            $t = StripComments (ReadText $g.FullName)
            $count += ([regex]::Matches($t, '\b' + [regex]::Escape($name) + '\b')).Count
        }
        if ($count -le 1) { $dead += ($f.Name + "::" + $name) }
    }
}
if ($dead.Count -eq 0) { Ok "无死配置（每个 [ConfigField] 都至少被读一次）" }
else { Warn ("疑似死配置（声明了但代码从不读）：" + ($dead -join ', ')) }

Write-Host ""
if ($script:fail -eq 0) {
    Write-Host ("通过：" + $script:warn + " 个 WARN，0 个 FAIL") -ForegroundColor Green
    exit 0
}
Write-Host ("失败：" + $script:fail + " 个 FAIL，" + $script:warn + " 个 WARN") -ForegroundColor Red
exit 1
