# 启动本地识图代理(端口 57322)
$ErrorActionPreference = 'Stop'
$proxy = 'D:\CodexProject\ManhuaPipeline\vision_proxy.js'
if (-not (Test-Path -LiteralPath $proxy)) { Write-Host "找不到代理脚本: $proxy" -ForegroundColor Red; exit 1 }
$node = (Get-Command node -ErrorAction Stop).Source
if (Get-NetTCPConnection -LocalPort 57322 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host '代理已在运行 (端口 57322),无需重复启动' -ForegroundColor Green
    exit 0
}
$outLog = Join-Path (Split-Path $proxy) 'vision_proxy_out.log'
$errLog = Join-Path (Split-Path $proxy) 'vision_proxy_err.log'
$arg = '"' + $proxy + '"'
Start-Process -FilePath $node -ArgumentList $arg -WindowStyle Hidden -RedirectStandardOutput $outLog -RedirectStandardError $errLog
Start-Sleep -Seconds 2
if (Get-NetTCPConnection -LocalPort 57322 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host '代理已启动: http://127.0.0.1:57322/v1 (Codex 需重启后生效)' -ForegroundColor Green
} else {
    Write-Host "代理启动失败,请查看日志: $errLog" -ForegroundColor Red
    if (Test-Path -LiteralPath $errLog) { Get-Content -LiteralPath $errLog -Tail 20 }
}
