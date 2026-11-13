<#
.SYNOPSIS
    UI 验证：只跑「构建 + 截图」，不跑单元测试。

.DESCRIPTION
    改样式 / 布局 / XAML 之后的唯一验证方式。单元测试留给逻辑改动，
    界面改动跑测试纯属浪费时间。

    判定规则：15 秒内没有产出 PNG 就是失败——不要加大超时重试，先看 error.txt。

.PARAMETER OutDir
    截图输出目录（相对仓库根）。默认 artifacts\shot-verify，看完可以整个删掉。

.PARAMETER AiOnly
    只出 AI 页一张图，迭代聊天界面时最快。

.PARAMETER Solution
    连整个解决方案一起构建。默认只构建表现层项目（更快，XAML 错误一样能挡住）。

.PARAMETER TimeoutSeconds
    截图超时，默认 15 秒。出不来图就是失败，不要调大。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\ui-verify.ps1 -AiOnly
#>
[CmdletBinding()]
param(
    [string]$OutDir = 'artifacts\shot-verify',
    [switch]$AiOnly,
    [switch]$Solution,
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ① 先确认没有正在运行的实例：开着会干扰截图、也会占住输出文件
$running = @(Get-Process -Name CameraControl -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "[失败] 检测到 $($running.Count) 个 CameraControl 正在运行（PID: $(($running.Id) -join ', ')）。" -ForegroundColor Red
    Write-Host "       开着界面截图会失败或出旧图，请先关掉它再跑。" -ForegroundColor Red
    exit 4
}

# ② 构建（默认只构建表现层）
$target = if ($Solution) { 'CameraControl.slnx' } else { 'src\CameraControl.Presentation\CameraControl.Presentation.csproj' }
$buildTimer = [Diagnostics.Stopwatch]::StartNew()
dotnet build $target -v q --nologo
$buildTimer.Stop()

if ($LASTEXITCODE -ne 0) {
    Write-Host "[失败] 构建没过，先把编译错误修掉（这一步能挡住绝大多数 XAML 错误）。" -ForegroundColor Red
    exit 1
}

# ③ 截图：超时即失败
$exe = Join-Path $root 'src\CameraControl.Presentation\bin\Debug\net10.0-windows10.0.19041.0\CameraControl.exe'
if (-not (Test-Path $exe)) {
    Write-Host "[失败] 找不到可执行文件：$exe" -ForegroundColor Red
    exit 3
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$shotArgs = @('--shot', $OutDir, '--demo-chat')
if ($AiOnly) { $shotArgs += '--ai-only' }

# 用 CreateProcess 直接起进程，不走 ShellExecute：
# 受限环境（沙箱 / 无交互桌面）里 Start-Process 会被取消，报 "operation was canceled by the user"。
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.WorkingDirectory = $root
$psi.UseShellExecute = $false
$psi.Arguments = (($shotArgs | ForEach-Object { if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ } }) -join ' ')

$shotTimer = [Diagnostics.Stopwatch]::StartNew()
$proc = [System.Diagnostics.Process]::Start($psi)
$exited = $proc.WaitForExit($TimeoutSeconds * 1000)
if (-not $exited -and -not $proc.HasExited) { $proc.Kill() }
$shotTimer.Stop()

$images = @(Get-ChildItem $OutDir -Filter *.png -ErrorAction SilentlyContinue)
$buildSec = [int]$buildTimer.Elapsed.TotalSeconds
$shotSec = [math]::Round($shotTimer.Elapsed.TotalSeconds, 1)

# ④ 判定
if ($images.Count -eq 0) {
    Write-Host "[失败] ${TimeoutSeconds}s 内没有产出任何截图（实际等待 ${shotSec}s）——判定失败，不要加大超时重试。" -ForegroundColor Red

    $errorFile = Join-Path $OutDir 'error.txt'
    if (Test-Path $errorFile) {
        Write-Host "--- error.txt（通常是 XAML 解析或绑定异常）---" -ForegroundColor Red
        Get-Content $errorFile -Raw
    }

    exit 2
}

Write-Host "[通过] 构建 ${buildSec}s + 截图 ${shotSec}s，共 $($images.Count) 张：" -ForegroundColor Green
$images | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "目录：$OutDir（逐张看图核对，确认后可删）" -ForegroundColor DarkGray

exit 0
