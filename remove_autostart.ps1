# 取消识图代理开机自启
$task = Get-ScheduledTask -TaskName 'VisionProxy' -ErrorAction SilentlyContinue
if ($task) {
    Unregister-ScheduledTask -TaskName 'VisionProxy' -Confirm:$false
    Write-Host '已取消开机自启' -ForegroundColor Yellow
} else {
    Write-Host '没有找到自启任务(可能已取消)' -ForegroundColor Green
}
