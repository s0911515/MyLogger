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

## ログサンプル(実測、2026-07-14)

`setup-test-env.ps1` で用意した `D:\tmp\ProbeTest` に対し、エクスプローラーで基本操作を1つずつ
実施して記録した実際のログ。加工していない生ログをそのまま掲載する。

```
[23:52:30.131743] === FsWatcherProbe 開始 監視パス=D:\tmp\ProbeTest ログ=D:\tmp\toolA-sample.log ===
[23:52:30.155101] 監視開始。プロセス終了(Ctrl+C / kill)まで待機します。
[23:53:06.444936] Created   ChangeType=Created FullPath=D:\tmp\ProbeTest\新規 テキスト ドキュメント.txt IsDir=false
[23:53:14.322974] Changed   ChangeType=Changed FullPath=D:\tmp\ProbeTest\write_target.txt IsDir=false
[23:53:23.760306] Changed   ChangeType=Changed FullPath=D:\tmp\ProbeTest\Source IsDir=true
[23:53:27.631037] Created   ChangeType=Created FullPath=D:\tmp\ProbeTest\copy_source.txt IsDir=false
[23:53:27.631316] Changed   ChangeType=Changed FullPath=D:\tmp\ProbeTest\copy_source.txt IsDir=false
[23:53:27.631736] Changed   ChangeType=Changed FullPath=D:\tmp\ProbeTest\copy_source.txt IsDir=false
[23:53:35.189256] Deleted   ChangeType=Deleted FullPath=D:\tmp\ProbeTest\Source\move_source.txt
[23:53:35.189545] Created   ChangeType=Created FullPath=D:\tmp\ProbeTest\move_source.txt IsDir=false
[23:53:35.189748] Changed   ChangeType=Changed FullPath=D:\tmp\ProbeTest\Source IsDir=true
[23:53:45.202035] Renamed   ChangeType=Renamed OldFullPath=D:\tmp\ProbeTest\Source\rename_source.txt FullPath=D:\tmp\ProbeTest\Source\rename_source_new.txt IsDir=false
[23:53:45.202222] Changed   ChangeType=Changed FullPath=D:\tmp\ProbeTest\Source IsDir=true
[23:53:47.884850] Deleted   ChangeType=Deleted FullPath=D:\tmp\ProbeTest\delete_normal.txt
[23:53:50.134957] Deleted   ChangeType=Deleted FullPath=D:\tmp\ProbeTest\delete_complete.txt
[23:53:58.844350] === FsWatcherProbe 終了 ===
```

### 操作と記録されたイベントの対応

| # | 操作 | 手順 | 記録されたイベント | 所見 |
|---|---|---|---|---|
| 1 | 新規作成 | `ProbeTest` 直下で右クリック→新規作成→テキストドキュメント | `Created` 1件のみ | このケースでは `Changed` の追随なし(内容書き込みが無い空ファイルのため) |
| 2 | 上書き保存 | `write_target.txt` をメモ帳で開き追記して保存 | `Changed` 1件のみ | |
| 3 | コピー(別フォルダ→監視ルート) | `Source\copy_source.txt` をコピーし `ProbeTest` に貼り付け | `Changed`(`Source` フォルダ自体、コピー元を開いた副作用) → `Created` → `Changed` ×2(貼り付け先) | 貼り付け先では `Created` 1件+`Changed` 2件がほぼ同時発火(doc/FileSystemWatcher調査.md の既知の事実と一致) |
| 4 | 移動(切り取り&貼り付け、フォルダ間) | `Source\move_source.txt` を切り取り `ProbeTest` に貼り付け | `Deleted`(移動元)→`Created`(移動先)→`Changed`(`Source` フォルダ) | **`Renamed` イベントにはならない。** 同一ボリューム内でもフォルダをまたぐ移動は Delete+Create で表現される |
| 5 | リネーム(同一フォルダ内) | `Source\rename_source.txt` をF2でリネーム | `Renamed`(Old/NewFullPath付き)→`Changed`(`Source` フォルダ) | 同一フォルダ内の名前変更は素直に `Renamed` になる(#4のフォルダ間移動と対照的) |
| 6 | 通常削除(ゴミ箱移動) | `delete_normal.txt` をDeleteキー | `Deleted` 1件 | |
| 7 | 完全削除(Shift+Delete) | `delete_complete.txt` をShift+Delete | `Deleted` 1件 | **#6と全く同じ形式。ゴミ箱移動か完全削除かは `FileSystemWatcher` だけでは判別不可能**(Sysmon=ToolDなら判別できる。[ToolD README](../ToolD-Sysmon/README.md)参照) |

このセッションから確認できた重要な事実:

- **フォルダをまたぐ移動(切り取り&貼り付け)は `Renamed` にならず `Deleted`+`Created` の2件になる。** 同一フォルダ内の名前変更(#5)だけが `Renamed` になる。移動と改名を区別するにはパス比較(ファイル名部分が同じか)だけでは不十分で、タイムスタンプの近さと組み合わせた推定が必要になる
- ファイル操作のためにエクスプローラーで親フォルダを開閉すると、対象ファイルとは無関係に親フォルダ自体の `Changed`(`IsDir=true`)が付随して発生する(#3, #4, #5)。ノイズとして扱うか、移動/リネームの裏付けとして使うかは評価次第
- 通常削除と完全削除は `FileSystemWatcher` レベルでは区別できない(#6, #7)

既知の事実(詳細は [doc/FileSystemWatcher調査.md](../../doc/FileSystemWatcher調査.md) 参照):

- 1回のファイル作成で `Created` 1件 + `Changed` 2件がほぼ同時に発火することがある
- コピー操作ではコピー元にも `Changed`(アクセス時刻更新)が発火することがある
- 大量操作時にバッファオーバーフロー(`Error` イベント、以降のイベントを一部取りこぼす)が発生しうる
