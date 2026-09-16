# 一键配置 claude-vision-skill（识图）
$ErrorActionPreference = 'Stop'

$skillDir = Join-Path $env:USERPROFILE ".codex\skills\claude-vision-skill"
if (-not (Test-Path -LiteralPath $skillDir)) {
    Write-Host "未找到技能目录: $skillDir" -ForegroundColor Red
    exit 1
}

$visionJs = Join-Path $skillDir "vision.js"
$skillMd  = Join-Path $skillDir "SKILL.md"
$envFile  = Join-Path $skillDir ".env"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# [1/3] 修正 SKILL.md 里的脚本路径（原仓库写死了作者电脑的路径）
$md = [System.IO.File]::ReadAllText($skillMd)
$skillPathFwd = ($skillDir -replace "\\", "/")
$mdNew = $md.Replace("/Users/wwu/.codex/skills/claude-vision-skill/vision.js", "$skillPathFwd/vision.js")
if ($mdNew -ne $md) {
    [System.IO.File]::WriteAllText($skillMd, $mdNew, $utf8NoBom)
    Write-Host "[1/3] SKILL.md 路径已修复" -ForegroundColor Green
} else {
    Write-Host "[1/3] SKILL.md 无需修改" -ForegroundColor Green
}

# [2/3] vision.js 增加 .env 手动加载（本机没装 dotenv，不补丁则 .env 不生效）
$js = [System.IO.File]::ReadAllText($visionJs)
if ($js.Contains("手动 .env 加载")) {
    Write-Host "[2/3] vision.js 已支持 .env，跳过" -ForegroundColor Green
} else {
    $anchor = 'try { require("dotenv").config({ path: path.resolve(__dirname, ".env") }); } catch {}'
    if (-not $js.Contains($anchor)) {
        Write-Host "vision.js 结构不符，未打补丁" -ForegroundColor Red
        exit 1
    }
    $loader = @'

// dotenv 未安装时的手动 .env 加载（KEY=VALUE 单行，已存在的环境变量优先）
const __envFile = path.resolve(__dirname, ".env");
if (fs.existsSync(__envFile)) {
  for (const __line of fs.readFileSync(__envFile, "utf8").split(/\r?\n/)) {
    const __m = __line.match(/^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$/);
    if (__m && !(__m[1] in process.env)) {
      process.env[__m[1]] = __m[2].replace(/^["']|["']$/g, "");
    }
  }
}
'@
    $js = $js.Replace($anchor, $anchor + $loader)
    [System.IO.File]::WriteAllText($visionJs, $js, $utf8NoBom)
    Write-Host "[2/3] vision.js 已加入 .env 加载" -ForegroundColor Green
}

# [3/3] 生成 .env 配置模板（已存在则不动）
if (-not (Test-Path -LiteralPath $envFile)) {
    $envText = @'
# 识图配置 —— 用记事本打开本文件，只改下面标注的几处，保存即可
# ① 必填：API Key（去火山方舟或百炼控制台复制，填在 = 后面）
DASHSCOPE_API_KEY=在这里粘贴你的APIKey

# ② 接口地址：默认阿里云百炼；如用火山方舟，把下面这行注释掉，并启用后面那行
DASHSCOPE_BASE_URL=https://dashscope.aliyuncs.com/compatible-mode/v1
# DASHSCOPE_BASE_URL=https://ark.cn-beijing.volces.com/api/v3

# ③ 模型 ID：默认百炼 qwen3-vl-flash；如用火山方舟，改成 Doubao-Seed-2.0-lite
VISION_MODEL=qwen3-vl-flash
# VISION_MODEL=Doubao-Seed-2.0-lite
'@
    [System.IO.File]::WriteAllText($envFile, $envText, $utf8NoBom)
    Write-Host "[3/3] 已生成配置模板: $envFile" -ForegroundColor Green
} else {
    Write-Host "[3/3] .env 已存在，跳过" -ForegroundColor Green
}

Write-Host ""
Write-Host "配置完成。下一步：用记事本打开 .env，粘贴你的 API Key（需要换火山方舟就按注释切换），保存后告诉我。" -ForegroundColor Yellow