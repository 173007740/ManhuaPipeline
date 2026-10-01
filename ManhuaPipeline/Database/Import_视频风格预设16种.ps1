# Import 16 video style presets into VideoStyles
# Source: F:\80 Agent Skill\视频风格预设_16种\*.md (title + positive block + negative block)
# Target: StyleName = title, StylePrompt = positive, StyleNegative = negative, Category = by file number
# Category mapping (same four buckets as the image presets):
#   01-06 2D animation / 07-09 3D animation / 10-13 live action / 14-16 comic & illustration
# Idempotent: updates by StyleName when it already exists.
# NOTE: this file needs a UTF-8 BOM - PowerShell 5.1 reads BOM-less .ps1 as ANSI and mangles Chinese.
$dir = 'F:\80 Agent Skill\视频风格预设_16种'
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

    $num = 0
    if ($f.Name -match '^(\d+)') { $num = [int]$Matches[1] }
    $cat = switch ($num) {
        { $_ -ge 1  -and $_ -le 6  } { '2D动画'; break }
        { $_ -ge 7  -and $_ -le 9  } { '3D动画'; break }
        { $_ -ge 10 -and $_ -le 13 } { '真人影视'; break }
        { $_ -ge 14 -and $_ -le 16 } { '漫画与插画'; break }
        default { '' }
    }

    $sel = $conn.CreateCommand()
    $sel.CommandText = 'SELECT StyleId FROM VideoStyles WHERE StyleName=@n'
    [void]$sel.Parameters.AddWithValue('@n', $name)
    $id = $sel.ExecuteScalar()

    if ($id) {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'UPDATE VideoStyles SET StylePrompt=@p, StyleNegative=@g, Category=@c, UpdatedAt=GETDATE() WHERE StyleId=@id'
        [void]$cmd.Parameters.AddWithValue('@p', $pos)
        [void]$cmd.Parameters.AddWithValue('@g', $neg)
        [void]$cmd.Parameters.AddWithValue('@c', $cat)
        [void]$cmd.Parameters.AddWithValue('@id', [int]$id)
        [void]$cmd.ExecuteNonQuery()
        Write-Host ('UPDATE #' + $id + ' cat=' + $cat + ' pos=' + $pos.Length + ' neg=' + $neg.Length)
    } else {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'INSERT INTO VideoStyles(StyleName,StylePrompt,StyleNegative,Category,IsDefault) VALUES(@n,@p,@g,@c,0)'
        [void]$cmd.Parameters.AddWithValue('@n', $name)
        [void]$cmd.Parameters.AddWithValue('@p', $pos)
        [void]$cmd.Parameters.AddWithValue('@g', $neg)
        [void]$cmd.Parameters.AddWithValue('@c', $cat)
        [void]$cmd.ExecuteNonQuery()
        Write-Host ('INSERT cat=' + $cat + ' pos=' + $pos.Length + ' neg=' + $neg.Length)
    }
    $ok++
}
$conn.Close()
Write-Host ('DONE: ' + $ok)
