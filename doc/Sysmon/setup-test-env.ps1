$TestRoot  = "D:\tmp\SysmonTest"
$SourceDir = Join-Path $TestRoot "Source"

if (Test-Path $TestRoot) {
    Remove-Item -Recurse -Force $TestRoot
}
New-Item -ItemType Directory -Path $SourceDir -Force | Out-Null

"write test - initial content"  | Set-Content -Encoding UTF8 (Join-Path $TestRoot "write_target.txt")
"delete me (normal)"             | Set-Content -Encoding UTF8 (Join-Path $TestRoot "delete_normal.txt")
"delete me (complete)"           | Set-Content -Encoding UTF8 (Join-Path $TestRoot "delete_complete.txt")

"copy source file"    | Set-Content -Encoding UTF8 (Join-Path $SourceDir "copy_source.txt")
"move source file"    | Set-Content -Encoding UTF8 (Join-Path $SourceDir "move_source.txt")
"rename source file"  | Set-Content -Encoding UTF8 (Join-Path $SourceDir "rename_source.txt")

Write-Host "Test environment reset complete: $TestRoot"
Write-Host "Baseline time (JST): $(Get-Date)"
