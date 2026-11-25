$exe = 'D:\AAA挂机专区\A1\BetterGI\BetterGI.exe'
$fs = [System.IO.File]::OpenRead($exe)
$buf = New-Object byte[] (31457280)
$n = $fs.Read($buf, 0, $buf.Length)
$fs.Close()
$text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $n)

foreach ($kw in @('startOneDragon', 'startGroups', 'startSoloTask', 'OneDragonName', 'ConfigGroup')) {
  Write-Output ('===== ' + $kw + ' =====')
  $idx = 0
  $hits = 0
  while ($hits -lt 3) {
    $i = $text.IndexOf($kw, $idx)
    if ($i -lt 0) { break }
    $s = [Math]::Max(0, $i - 90)
    $len = [Math]::Min(200, $text.Length - $s)
    $frag = $text.Substring($s, $len)
    $clean = ''
    foreach ($ch in $frag.ToCharArray()) {
      if ([int]$ch -ge 32 -and [int]$ch -le 126) { $clean += $ch } else { $clean += ' ' }
    }
    Write-Output ('  >> ' + ($clean -replace '\s+', ' ').Trim())
    $idx = $i + 1
    $hits++
  }
}
