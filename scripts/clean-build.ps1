# 清理构建产物（bin/obj）
#
# 为什么需要：WPF 工程每次构建都会在 bin/obj 里落一份完整输出，
# 目标框架改过一次（net10.0-windows → net10.0-windows10.0.19041.0）之后，
# 旧框架目录不会被自动清掉，同一份 DLL 在磁盘上存了两份。
# 实测仓库里因此白占 100+ MB，VS 扫描构建产物也会变慢。
#
# 用法：
#   pwsh -File scripts\clean-build.ps1            # 只清 bin/obj（安全）
#   pwsh -File scripts\clean-build.ps1 -StaleTfm  # 只清"过期目标框架"目录
#   pwsh -File scripts\clean-build.ps1 -All       # 两者都清
#
# 注意：清完第一次构建会慢一些（要重新还原/编译），之后恢复正常。

[CmdletBinding()]
param(
    [switch]$StaleTfm,
    [switch]$All
)

$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

# 当前目标框架（见 Directory.Build.props）；这些开头的目录都要保留
$currentFrameworks = @('net10.0-windows10.0.19041.0', 'net10.0')
$staleFrameworks = @('net10.0-windows')

function Get-DirSizeMb($path) {
    if (-not (Test-Path $path)) { return 0 }
    $sum = (Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum
    if (-not $sum) { return 0 }
    return [math]::Round($sum / 1MB, 1)
}

$freed = 0.0
$targets = @()

if ($All -or $StaleTfm) {
    foreach ($name in $staleFrameworks) {
        $targets += Get-ChildItem -Path $root -Recurse -Directory -Filter $name -ErrorAction SilentlyContinue |
                    Where-Object { $_.Parent.Name -in @('Debug', 'Release') }
    }
}

if ($All -or -not $StaleTfm) {
    $targets += Get-ChildItem -Path $root -Recurse -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -in @('bin', 'obj') -and $_.Parent.Name -ne 'artifacts' }
}

foreach ($dir in $targets | Sort-Object FullName -Unique) {
    if (-not $dir.FullName.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }

    $mb = Get-DirSizeMb $dir.FullName
    Remove-Item $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $dir.FullName)) {
        $freed += $mb
        Write-Host ("  删除 {0,8:N1} MB  {1}" -f $mb, $dir.FullName.Replace($root + '\', ''))
    }
    else {
        Write-Host ("  跳过（被占用）      {0}" -f $dir.FullName.Replace($root + '\', ''))
    }
}

$total = [math]::Round(((Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
          Measure-Object Length -Sum).Sum) / 1MB, 1)

Write-Host ""
Write-Host ("本次回收 {0:N1} MB；当前仓库占用 {1:N1} MB" -f $freed, $total)
Write-Host "提示：数据与日志在 data\（已被 .gitignore 忽略），日志单文件上限 10 MB、最多 10 个。"
