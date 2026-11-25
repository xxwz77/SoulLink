cd 'E:\Documents\deepseek-harness\SoulLink'
'=== 文档结构检查 ==='
$f = 'docs\HANDOFF-2.17.45-20261121.md'
Get-Content $f -Encoding UTF8 | Select-String -Pattern '^#' | ForEach-Object { '  ' + $_.Line }
'=== 文件信息 ==='
$i = Get-Item $f
'  大小: ' + [math]::Round($i.Length/1KB,1) + ' KB'
'  行数: ' + (Get-Content $f -Encoding UTF8).Count