# 切换 Codex 指向本地识图代理(57322)<->原中转(57321),可反复切换
$ErrorActionPreference = 'Stop'
$cfg = Join-Path $env:USERPROFILE '.codex\config.toml'
if (-not (Test-Path -LiteralPath $cfg)) { Write-Host "找不到 Codex 配置: $cfg" -ForegroundColor Red; exit 1 }
$text = [System.IO.File]::ReadAllText($cfg)
$old = 'http://127.0.0.1:57321/v1'
$new = 'http://127.0.0.1:57322/v1'
$utf8 = New-Object System.Text.UTF8Encoding($false)
if ($text.Contains($new)) {
    $text = $text.Replace($new, $old)
    [System.IO.File]::WriteAllText($cfg, $text, $utf8)
    Write-Host '已切回: Codex -> 原中转 57321 (重启 Codex 生效)' -ForegroundColor Yellow
} elseif ($text.Contains($old)) {
    $backup = $cfg + '.bak-' + (Get-Date -Format 'yyyyMMddHHmmss')
    [System.IO.File]::Copy($cfg, $backup)
    $text = $text.Replace($old, $new)
    [System.IO.File]::WriteAllText($cfg, $text, $utf8)
    Write-Host "已切换: Codex -> 识图代理 57322 (已备份原配置到 $backup)" -ForegroundColor Green
    Write-Host '下一步: 1) 先运行 start_vision_proxy.ps1  2) 完全退出并重启 Codex  3) 新对话直接贴图' -ForegroundColor Yellow
} else {
    Write-Host '配置里没找到 57321/57322 的 base_url,请检查 config.toml' -ForegroundColor Red
}
