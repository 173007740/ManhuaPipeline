# 设置识图代理开机自启(一次性运行)
$ErrorActionPreference = 'Stop'
$startScript = 'D:\CodexProject\ManhuaPipeline\start_vision_proxy.ps1'
if (-not (Test-Path -LiteralPath $startScript)) { Write-Host "找不到启动脚本: $startScript" -ForegroundColor Red; exit 1 }
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $startScript + '"')
$trigger = New-ScheduledTaskTrigger -AtLogOn
Register-ScheduledTask -TaskName 'VisionProxy' -Action $action -Trigger $trigger -Description '登录时自动启动本地识图代理(57322)' -Force | Out-Null
Start-ScheduledTask -TaskName 'VisionProxy'
Start-Sleep -Seconds 3
$task = Get-ScheduledTask -TaskName 'VisionProxy' -ErrorAction SilentlyContinue
if ($task) {
    Write-Host '已设置开机自启(任务名 VisionProxy),并已立即启动一次' -ForegroundColor Green
    Write-Host '以后开机/登录后代理自动运行,无需手动操作' -ForegroundColor Green
} else {
    Write-Host '设置失败,请检查是否以管理员运行' -ForegroundColor Red
}
