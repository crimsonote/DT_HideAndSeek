# redeploy.ps1 —— 一键：构建（含 BOM 检查）→ 关游戏 → 部署 → 重新启动
#
# 用途：改完代码后不必手动退游戏、复制、再启动。
# 注意：.NET 程序集无法在进程内替换，所以"改代码"终究需要重启游戏；
#       本脚本只是把这一串手工操作收敛成一条命令。
#
# 用法：
#   pwsh -File redeploy.ps1
#   pwsh -File ...\redeploy.ps1 -NoLaunch        # 只构建+部署，不自动启动
#   pwsh -File ...\redeploy.ps1 -FixBom          # 发现缺 BOM 时自动补齐（默认只报错）

param(
    [switch]$NoLaunch,          # 部署后不启动游戏
    [switch]$FixBom,            # 缺 BOM 时自动补齐；默认只报错退出
    [int]$WaitSeconds = 25      # 等待游戏优雅退出的上限
)

$ErrorActionPreference = 'Stop'

$gameDir = $(if ($env:DEADLYTRICK_DIR) { $env:DEADLYTRICK_DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick" })
$projectDir = $PSScriptRoot
$srcDll = Join-Path $projectDir 'bin\Release\netstandard2.1\HideAndSeek.dll'
$dstDll = Join-Path $gameDir 'BepInEx\plugins\HideAndSeek.dll'
$exe = Join-Path $gameDir 'DeadlyTrick.exe'

# ── 1a) BOM 检查 ─────────────────────────────────────────────────────
# 无 BOM 时 Roslyn 会按系统 ANSI(GBK) 读源码，中文字面量在**编译期**就已损坏且无警告。
# 这里默认只检测并报错 —— 自动改写源文件会把未经审阅的改动塞进工作区，
# 与"改完先 review 再提交"相冲。需要自动补时显式加 -FixBom。
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$noBom = @()
Get-ChildItem $projectDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\' } |
    ForEach-Object {
        $b = [System.IO.File]::ReadAllBytes($_.FullName)
        $hasBom = $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
        if (-not $hasBom) { $noBom += $_ }
    }

if ($noBom.Count -gt 0) {
    if ($FixBom) {
        foreach ($f in $noBom) {
            $b = [System.IO.File]::ReadAllBytes($f.FullName)
            [System.IO.File]::WriteAllText($f.FullName, [System.Text.Encoding]::UTF8.GetString($b), $utf8Bom)
            Write-Host "[补BOM] $($f.Name)"
        }
    } else {
        Write-Host "以下文件缺 UTF-8 BOM（中文会在编译期损坏），先修再构建：" -ForegroundColor Red
        $noBom | ForEach-Object { Write-Host ("  " + $_.FullName.Replace($projectDir + '\', '')) }
        Write-Host "  加 -FixBom 可让本脚本自动补齐。" -ForegroundColor Yellow
        exit 1
    }
}

# ── 1b) 构建 ─────────────────────────────────────────────────────────
Push-Location $projectDir
$buildOutput = dotnet build -c Release --nologo
$buildExit = $LASTEXITCODE
$buildOutput | Select-Object -Last 3
Pop-Location

# 编译失败时旧的 bin 产物仍在 —— 不检查退出码就会把上一次的 DLL 装上去，
# 还打印"哈希一致=True"，看起来完全成功（这正是本脚本以前会骗人的地方）。
if ($buildExit -ne 0) {
    Write-Host "构建失败（exit $buildExit），已中止部署。" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $srcDll)) { throw "构建产物不存在: $srcDll" }

# 产物比源码旧 = 这次没真正重新编译
$newer = Get-ChildItem $projectDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.LastWriteTimeUtc -gt (Get-Item $srcDll).LastWriteTimeUtc }
if ($newer) {
    Write-Host "构建产物比源文件旧，拒绝部署（编译可能被跳过）：" -ForegroundColor Red
    $newer | Select-Object -First 10 | ForEach-Object {
        Write-Host ("  " + $_.FullName.Replace($projectDir + '\', ''))
    }
    exit 1
}

# ── 2) 关闭游戏（先请它自己退，超时再强杀）──
$procs = Get-Process -Name 'DeadlyTrick*' -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "正在关闭游戏（$($procs.Count) 个进程）..."
    $procs | ForEach-Object { [void]$_.CloseMainWindow() }

    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Process -Name 'DeadlyTrick*' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }

    $left = Get-Process -Name 'DeadlyTrick*' -ErrorAction SilentlyContinue
    if ($left) {
        Write-Host "  超时未退出，强制结束（进程内未保存的进度可能丢失）"
        $left | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
    Write-Host "  已退出"
}

# ── 3) 部署（复制后必须校验哈希，失败即中止）──
Copy-Item $srcDll $dstDll -Force
$hashSrc = (Get-FileHash $srcDll -Algorithm SHA256).Hash
$hashDst = (Get-FileHash $dstDll -Algorithm SHA256).Hash
if ($hashSrc -ne $hashDst) {
    Write-Host "部署后哈希不一致，复制可能失败。" -ForegroundColor Red
    exit 1
}
Write-Host ("已部署: {0} 字节  {1}  SHA256前16位={2}" -f (Get-Item $dstDll).Length, (Get-Item $dstDll).LastWriteTime, $hashDst.Substring(0, 16)) -ForegroundColor Green

# ── 4) 启动 ──
if ($NoLaunch) {
    Write-Host "已跳过启动（-NoLaunch）"
} elseif (Test-Path $exe) {
    Start-Process $exe
    Write-Host "已启动游戏"
} else {
    Write-Host "未找到 $exe —— 请手动启动"
}
