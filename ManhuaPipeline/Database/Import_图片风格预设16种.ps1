# Import 16 image style presets into ImageStyles
# Source: F:\80 Agent Skill\...\*.md  (each file: "# title", "## positive" ```text block, "## negative" block)
# Target: StyleName = title, StyleDesc = positive prompt, StyleNegative = negative prompt
# Idempotent: updates by StyleName when it already exists.
# NOTE: this file is ASCII-only on purpose - PowerShell 5.1 reads .ps1 as ANSI unless it has a BOM.
$dir = 'F:\80 Agent Skill\图片风格预设_16种'
$cs  = 'Server=.;Database=ManhuaPipeline;User Id=ManhuaApp1;Password=!QAZ2wsxE;TrustServerCertificate=True;'

$files = Get-ChildItem -Path $dir -Filter '*.md' |
         Where-Object { $_.Name -ne 'README.md' } |
         Sort-Object Name

$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$ok = 0
foreach ($f in $files) {
    $t = Get-Content -Path $f.FullName -Raw -Encoding UTF8

    $name = ''
    if ($t -match '(?m)^#\s+(.+?)\s*$') { $name = $Matches[1].Trim() }

    $pos = ''
    if ($t -match '(?s)##\s*正向提示词.*?```[a-zA-Z]*\r?\n(.*?)```') { $pos = $Matches[1].Trim() }

    $neg = ''
    if ($t -match '(?s)##\s*反向提示词.*?```[a-zA-Z]*\r?\n(.*?)```') { $neg = $Matches[1].Trim() }

    if (-not $name -or -not $pos) { Write-Host ('SKIP (no title or positive): ' + $f.Name); continue }

    $sel = $conn.CreateCommand()
    $sel.CommandText = 'SELECT StyleId FROM ImageStyles WHERE StyleName=@n'
    [void]$sel.Parameters.AddWithValue('@n', $name)
    $id = $sel.ExecuteScalar()

    if ($id) {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'UPDATE ImageStyles SET StyleDesc=@d, StyleNegative=@g, UpdatedAt=SYSDATETIME() WHERE StyleId=@id'
        [void]$cmd.Parameters.AddWithValue('@d', $pos)
        [void]$cmd.Parameters.AddWithValue('@g', $neg)
        [void]$cmd.Parameters.AddWithValue('@id', [int]$id)
        [void]$cmd.ExecuteNonQuery()
        Write-Host ('UPDATE #' + $id + ' pos=' + $pos.Length + ' neg=' + $neg.Length)
    } else {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'INSERT INTO ImageStyles(StyleName, StyleDesc, StyleNegative) VALUES(@n,@d,@g)'
        [void]$cmd.Parameters.AddWithValue('@n', $name)
        [void]$cmd.Parameters.AddWithValue('@d', $pos)
        [void]$cmd.Parameters.AddWithValue('@g', $neg)
        [void]$cmd.ExecuteNonQuery()
        Write-Host ('INSERT pos=' + $pos.Length + ' neg=' + $neg.Length)
    }
    $ok++
}
$conn.Close()
Write-Host ('DONE: ' + $ok)
