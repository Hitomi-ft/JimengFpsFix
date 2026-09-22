# 重新构建 帧率修复工具 (需要 .NET SDK 9+)
#
#   .\build.ps1              仅构建并输出到 dist\
#   .\build.ps1 -Clean       先删除 bin/obj/dist 再构建
#
# 构建产物：
#   dist\JimengFpsFix.exe    图形界面版（双击运行，可多选/拖拽批量修复）
#   dist\fpsfix.exe          命令行版（批量脚本用）

param([switch]$Clean)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Push-Location $root
try {
    # 把 dotnet 的首次运行标记放到项目目录，避免写入用户配置目录
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    if ($Clean) {
        foreach ($p in 'dist', 'src\bin', 'src\obj', 'src\cli\bin', 'src\cli\obj') {
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }
    }

    Write-Host '构建 GUI 版 ...' -ForegroundColor Cyan
    dotnet publish 'src\JimengFpsFix.csproj' -c Release -o dist -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'GUI 版构建失败' }

    Write-Host '构建命令行版 ...' -ForegroundColor Cyan
    dotnet publish 'src\cli\FpsFix.Cli.csproj' -c Release -o dist -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw '命令行版构建失败' }

    # 把使用说明放到 exe 旁边
    if (Test-Path '使用说明.txt') { Copy-Item '使用说明.txt' 'dist\使用说明.txt' -Force }

    Write-Host ''
    Write-Host '完成，输出在 dist\ ：' -ForegroundColor Green
    Get-ChildItem 'dist' -File | Sort-Object Name | ForEach-Object { '  {0,-34} {1,8:N0} 字节' -f $_.Name, $_.Length }
}
finally { Pop-Location }
