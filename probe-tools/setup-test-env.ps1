# probe-tools 配下の各ツール(ToolA〜D、ToolI等)の動作確認用に、決まった構成のテストフォルダ・
# ファイルを用意する(既存があれば削除して作り直す)。新規作成/書き込み(上書き保存)/コピー/
# ムーブ/リネーム/通常削除/完全削除の各操作を、ファイル単位・フォルダ単位の両方でひととおり
# 試せる構成にしてある。
#
# 使い方:
#   .\probe-tools\setup-test-env.ps1 [-TestRoot D:\tmp\ProbeTest]
#   実行後、各ツールを起動してから、表示された Baseline time 以降にエクスプローラ上で操作を行い、
#   各ツールのログと突き合わせる。

param(
    [string]$TestRoot = "D:\tmp\ProbeTest"
)

$SourceDir = Join-Path $TestRoot "Source"
# フォルダそのものをコピー/移動/リネームするテスト用(ファイル単位のSourceと対になる構成)。
$SourceFoldersDir = Join-Path $TestRoot "SourceFolders"
$CopyFolderSource = Join-Path $SourceFoldersDir "CopyFolderSource"
$MoveFolderSource = Join-Path $SourceFoldersDir "MoveFolderSource"
$RenameFolderSource = Join-Path $SourceFoldersDir "RenameFolderSource"
# フォルダそのものを削除するテスト用(delete_normal.txt/delete_complete.txtと対になる構成)。
$DeleteFolderNormal = Join-Path $TestRoot "delete_folder_normal"
$DeleteFolderComplete = Join-Path $TestRoot "delete_folder_complete"

if (Test-Path $TestRoot) {
    Remove-Item -Recurse -Force $TestRoot
}
New-Item -ItemType Directory -Path $SourceDir -Force | Out-Null
New-Item -ItemType Directory -Path $CopyFolderSource -Force | Out-Null
New-Item -ItemType Directory -Path $MoveFolderSource -Force | Out-Null
New-Item -ItemType Directory -Path $RenameFolderSource -Force | Out-Null
New-Item -ItemType Directory -Path $DeleteFolderNormal -Force | Out-Null
New-Item -ItemType Directory -Path $DeleteFolderComplete -Force | Out-Null

"write test - initial content"  | Set-Content -Encoding UTF8 (Join-Path $TestRoot "write_target.txt")
"delete me (normal)"             | Set-Content -Encoding UTF8 (Join-Path $TestRoot "delete_normal.txt")
"delete me (complete)"           | Set-Content -Encoding UTF8 (Join-Path $TestRoot "delete_complete.txt")

"copy source file"    | Set-Content -Encoding UTF8 (Join-Path $SourceDir "copy_source.txt")
"move source file"    | Set-Content -Encoding UTF8 (Join-Path $SourceDir "move_source.txt")
"rename source file"  | Set-Content -Encoding UTF8 (Join-Path $SourceDir "rename_source.txt")

# フォルダ単位テスト用。空フォルダだと操作の痕跡が分かりにくいため、中に1ファイルずつ置く。
"inside copy folder"   | Set-Content -Encoding UTF8 (Join-Path $CopyFolderSource "inner.txt")
"inside move folder"   | Set-Content -Encoding UTF8 (Join-Path $MoveFolderSource "inner.txt")
"inside rename folder" | Set-Content -Encoding UTF8 (Join-Path $RenameFolderSource "inner.txt")
"inside delete folder (normal)"   | Set-Content -Encoding UTF8 (Join-Path $DeleteFolderNormal "inner.txt")
"inside delete folder (complete)" | Set-Content -Encoding UTF8 (Join-Path $DeleteFolderComplete "inner.txt")

Write-Host "Test environment reset complete: $TestRoot"
Write-Host "Baseline time (JST): $(Get-Date)"
