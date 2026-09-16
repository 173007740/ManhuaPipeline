# 停止本地识图代理(端口 57322)
$conn = Get-NetTCPConnection -LocalPort 57322 -State Listen -ErrorAction SilentlyContinue
if ($conn) {
    $p = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
    if ($p) { Stop-Process -Id $p.Id -Force; Write-Host "已停止代理 (PID $($p.Id))" -ForegroundColor Yellow }
} else {
    Write-Host '代理未在运行' -ForegroundColor Green
}
