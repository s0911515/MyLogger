# ToolA-FsWatcherProbe

`System.IO.FileSystemWatcher` の生の挙動を事実確認するための最小プローブ。MyLogger 本体のフィルタ・
重複排除・移動統合ロジックは一切介さず、`FileSystemWatcher` が実際に発火したイベントをそのまま記録する。

## 仕組み

.NET 標準の `System.IO.FileSystemWatcher` をそのまま利用する。OSカーネルのファイルシステムフィルタ
ドライバーが発生させる変更通知をアプリ側で受け取る仕組みで、**ETWやセキュリティ監査ログとは別系統**。
`IncludeSubdirectories = true`、`NotifyFilter` は全フラグ有効、`InternalBufferSize` は既定値(8192)の
まま(バッファオーバーフローの再現性を保つため意図的にデフォルトを使用)。

- 誰が操作したか(ユーザー名・プロセス)は一切分からない。パスと変更種別のみ。
- 管理者権限は不要。

## 使い方

```powershell
# ソースから実行
dotnet run --project probe-tools\ToolA-FsWatcherProbe -- <監視パス> [ログファイル]

# 同梱のビルド済みexe(.NETランタイム不要)
.\dist\FsWatcherProbe.exe <監視パス> [ログファイル]
```

- `<監視パス>` 省略時は `D:\FsWatcherProbe\testarea`(無ければ自動作成)
- `[ログファイル]` 省略時は実行ファイルと同じフォルダの `fswatcherprobe.log`
- 起動後、対象パス配下でファイル操作(作成/変更/削除/リネーム/移動)を行い、**Ctrl+C** で停止するとログが確定する

## 記録されるログの内容

1行1イベント、`[HH:mm:ss.ffffff]`(マイクロ秒精度)のタイムスタンプ付き。

```
[14:32:01.123456] Created   ChangeType=Created FullPath=D:\testarea\a.txt IsDir=False
[14:32:01.123789] Changed   ChangeType=Changed FullPath=D:\testarea\a.txt IsDir=False
[14:32:05.001111] Renamed   ChangeType=Renamed OldFullPath=D:\testarea\a.txt FullPath=D:\testarea\b.txt IsDir=False
[14:32:08.500000] Deleted   ChangeType=Deleted FullPath=D:\testarea\b.txt
[14:32:10.000000] Error     InternalBufferOverflowException: ...
```

既知の事実(詳細は [doc/FileSystemWatcher調査.md](../../doc/FileSystemWatcher調査.md) 参照):

- 1回のファイル作成で `Created` 1件 + `Changed` 2件がほぼ同時に発火することがある
- コピー操作ではコピー元にも `Changed`(アクセス時刻更新)が発火することがある
- 大量操作時にバッファオーバーフロー(`Error` イベント、以降のイベントを一部取りこぼす)が発生しうる
