# redeploy.ps1 —— 一键：关游戏 → 部署最新构建 → 重新启动
#
# 用途：改完代码后不必手动退游戏、复制、再启动。
# 注意：.NET 程序集无法在进程内替换，所以"改代码"终究需要重启游戏；
#       本脚本只是把这一串手工操作收敛成一条命令。
#
# 用法：
#   pwsh -File D:\git\DT_Tools\HideAndSeek\redeploy.ps1
#   pwsh -File ...\redeploy.ps1 -NoLaunch      # 只关+部署，不自动启动

param(
    [switch]$NoLaunch,          # 部署后不启动游戏
    [int]$WaitSeconds = 25      # 等待游戏优雅退出的上限
)

$ErrorActionPreference = 'Stop'

$gameDir = "C:\Program Files (x86)\Steam\steamapps\common\Deadly Trick"
$projectDir = $PSScriptRoot
$srcDll = Join-Path $projectDir 'bin\Release\netstandard2.1\HideAndSeek.dll'
$dstDll = Join-Path $gameDir 'BepInEx\plugins\HideAndSeek.dll'
$exe = Join-Path $gameDir 'DeadlyTrick.exe'

# ── 1) 构建（顺带补 BOM：write/edit 之后常见丢失，无 BOM 会让中文在编译期损坏）──
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
Get-ChildItem $projectDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\' } |
    ForEach-Object {
        $b = [System.IO.File]::ReadAllBytes($_.FullName)
        $hasBom = $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
        if (-not $hasBom) {
            [System.IO.File]::WriteAllText(
                $_.FullName,
                [System.Text.Encoding]::UTF8.GetString($b),
                $utf8Bom)
            Write-Host "[补BOM] $($_.Name)"
        }
    }

Push-Location $projectDir
dotnet build -c Release --nologo | Select-Object -Last 3
Pop-Location

if (-not (Test-Path $srcDll)) { throw "构建产物不存在: $srcDll" }

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

# ── 3) 部署 ──
Copy-Item $srcDll $dstDll -Force
$s = Get-Item $srcDll
$d = Get-Item $dstDll
$same = (Get-FileHash $srcDll).Hash -eq (Get-FileHash $dstDll).Hash
Write-Host ("已部署: {0} 字节  {1}  哈希一致={2}" -f $d.Length, $d.LastWriteTime, $same)

# ── 4) 启动 ──
if ($NoLaunch) {
    Write-Host "已跳过启动（-NoLaunch）"
} elseif (Test-Path $exe) {
    Start-Process $exe
    Write-Host "已启动游戏"
} else {
    Write-Host "未找到 $exe —— 请手动启动"
}
