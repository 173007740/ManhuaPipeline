# 一键重启 ManhuaPipeline 服务：先停旧实例再启动新实例，避免 5133 端口被占用
# 用法：右键"使用 PowerShell 运行"，或在终端执行  .\restart_manhua.ps1
$ErrorActionPreference = 'Stop'
$exe     = 'D:\CodexProject\ManhuaPipeline\ManhuaPipeline\bin\Debug\net9.0\ManhuaPipeline.exe'
$workDir = 'D:\CodexProject\ManhuaPipeline\ManhuaPipeline'
$outLog  = 'D:\CodexProject\ManhuaPipeline\logs\service_restart.out.log'
$errLog  = 'D:\CodexProject\ManhuaPipeline\logs\service_restart.err.log'

if (-not (Test-Path -LiteralPath $exe)) { Write-Host "找不到服务程序: $exe" -ForegroundColor Red; exit 1 }

# 1. 停掉所有旧实例
$old = Get-Process ManhuaPipeline -ErrorAction SilentlyContinue
if ($old) {
    $old | Stop-Process -Force
    Write-Host "已停止旧实例: $($old.Id -join ', ')" -ForegroundColor Yellow
    Start-Sleep -Seconds 2
} else {
    Write-Host '没有正在运行的服务实例' -ForegroundColor Gray
}

# 2. 启动新实例
Start-Process -FilePath $exe -WorkingDirectory $workDir `
    -RedirectStandardOutput $outLog -RedirectStandardError $errLog -WindowStyle Hidden

# 3. 等待端口就绪
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 1
    try {
        $r = Invoke-WebRequest -Uri 'http://localhost:5133' -UseBasicParsing -TimeoutSec 2
        if ($r.StatusCode -eq 200) {
            $p = Get-Process ManhuaPipeline -ErrorAction SilentlyContinue | Select-Object -First 1
            Write-Host "服务已启动: http://localhost:5133 (PID $($p.Id))" -ForegroundColor Green
            exit 0
        }
    } catch {}
}
Write-Host '启动超时，请查看 logs\service_restart.err.log' -ForegroundColor Red
exit 1
