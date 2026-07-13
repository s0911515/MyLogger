# probe-tools 配下の各ツール(ToolA〜D)の動作確認用に、決まった構成のテストフォルダ・ファイルを
# 用意する(既存があれば削除して作り直す)。新規作成/書き込み(上書き保存)/コピー/ムーブ/リネーム/
# 通常削除/完全削除の各操作をひととおり試せる構成にしてある。
#
# 使い方:
#   .\probe-tools\setup-test-env.ps1 [-TestRoot D:\tmp\ProbeTest]
#   実行後、各ツールを起動してから、表示された Baseline time 以降にエクスプローラ上で操作を行い、
#   各ツールのログと突き合わせる。

param(
    [string]$TestRoot = "D:\tmp\ProbeTest"
)

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
