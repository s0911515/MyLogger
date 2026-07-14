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
| 7 | 完全削除(Shift+Delete) | `delete_complete.txt` をShift+Delete | `Deleted` 1件 | **このセッション(監視パスを`D:\tmp\ProbeTest`に限定)では#6と全く同じ形式で判別不可能だったが、これは監視範囲が原因だった。ドライブ全体を監視した場合は判別可能。詳細は下記「追加検証」参照** |

このセッションから確認できた重要な事実:

- **フォルダをまたぐ移動(切り取り&貼り付け)は `Renamed` にならず `Deleted`+`Created` の2件になる。** 同一フォルダ内の名前変更(#5)だけが `Renamed` になる。移動と改名を区別するにはパス比較(ファイル名部分が同じか)だけでは不十分で、タイムスタンプの近さと組み合わせた推定が必要になる
- ファイル操作のためにエクスプローラーで親フォルダを開閉すると、対象ファイルとは無関係に親フォルダ自体の `Changed`(`IsDir=true`)が付随して発生する(#3, #4, #5)。ノイズとして扱うか、移動/リネームの裏付けとして使うかは評価次第
- 通常削除と完全削除は、**監視パスに`$RECYCLE.BIN`が含まれない場合は** `FileSystemWatcher` レベルで区別できない(#6, #7)。含まれる場合は区別できる(下記「追加検証」参照)

## 追加検証: 監視パスをドライブ全体(`D:\`)にした場合(実測、2026-07-15)

上記の検証は監視パスを`D:\tmp\ProbeTest`に限定していたため、`$RECYCLE.BIN`(`D:\$RECYCLE.BIN`、
ドライブ直下)は監視範囲外だった。監視パスを`D:\`に変えて#6・#7を再検証したところ、**通常削除と
完全削除は判別可能**という訂正結果が得られた。

```
[06:11:14.811038] === FsWatcherProbe 開始 監視パス=D:\ ログ=D:\tmp\toolA-sample.log ===
[06:11:14.839822] 監視開始。プロセス終了(Ctrl+C / kill)まで待機します。
[06:11:46.973335] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\S-1-5-21-...\$IMCC2VH.txt IsDir=false
[06:11:46.973674] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\S-1-5-21-...\$IMCC2VH.txt IsDir=false
[06:11:46.973817] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\S-1-5-21-...                IsDir=true
[06:11:46.974091] Deleted   ChangeType=Deleted FullPath=D:\tmp\ProbeTest\delete_normal.txt
[06:11:46.974272] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\S-1-5-21-...\$RMCC2VH.txt IsDir=false
[06:11:46.974433] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\S-1-5-21-...                IsDir=true
[06:11:46.992059] Changed   ChangeType=Changed FullPath=D:\tmp\ProbeTest                            IsDir=true
[06:11:48.845865] Deleted   ChangeType=Deleted FullPath=D:\tmp\ProbeTest\delete_complete.txt
[06:11:48.872827] Changed   ChangeType=Changed FullPath=D:\tmp\ProbeTest                            IsDir=true
```

**通常削除(`delete_normal.txt`)**: `Deleted`(元パス)の前後に、`$RECYCLE.BIN`配下への`Created`が
**2件**伴う。

- `$IMCC2VH.txt`(`$I`=Index、メタデータファイル。元のパス・削除日時等の復元用情報を保持)
- `$RMCC2VH.txt`(`$R`=Renamed、実体ファイル。**削除されたファイルの中身そのものが、この新しいパスに
  リネームされている**)

**完全削除(`delete_complete.txt`)**: `Deleted`(元パス)のみで、`$RECYCLE.BIN`への`Created`は
一切伴わない。

### 訂正: 通常削除と完全削除は判別可能(監視範囲がドライブ全体の場合)

上記の通り、**`Deleted`イベントの直後に、同じ拡張子・近い8文字のランダム名を持つ`$RECYCLE.BIN\...\$R*`
への`Created`が伴うかどうか**で、通常削除(ゴミ箱経由)と完全削除(ゴミ箱を経由しない)を判別できる。
`$RECYCLE.BIN`を監視範囲に含めない場合(前述の`D:\tmp\ProbeTest`限定のテスト)は、この判別材料自体が
観測できないため「判別不可能」という結論になっていたが、これは`FileSystemWatcher`自体の限界ではなく
**監視範囲の設定次第**だったことになる。

さらに、**ETW(ToolB)の`Rename`イベントでは移動先の新パスが一切分からなかった**のに対し、
**FileSystemWatcher(本ツール)は移動先の実パス(`$R...`)をそのまま`Created`イベントで教えてくれる**。
ETWでは取れない情報がFileSystemWatcher側で取れる好例であり、2つのツールの相補性を示す発見と言える
([ToolB-EtwFileProbe/README.md](../ToolB-EtwFileProbe/README.md) 参照)。

なお、ToolB(ETW)は`$RECYCLE.BIN`配下を大量ノイズ(既存アイテム全件のメタデータ再走査)のため既定で
除外しており、この情報は取得できない。本ツール(ToolA)をドライブ全体で使えば`$R...`への`Created`は
見えるが、それだけでは「削除される前の元のパス」までは分からない(`$R...`はランダムな名前にリネーム
されているため)。元のパスまで解決したい場合は、`$I...`メタデータファイルをデコードする専用ツール
[ToolH-RecycleBinProbe](../ToolH-RecycleBinProbe) を使うこと。

既知の事実(詳細は [doc/FileSystemWatcher調査.md](../../doc/FileSystemWatcher調査.md) 参照):

- 1回のファイル作成で `Created` 1件 + `Changed` 2件がほぼ同時に発火することがある
- コピー操作ではコピー元にも `Changed`(アクセス時刻更新)が発火することがある
- 大量操作時にバッファオーバーフロー(`Error` イベント、以降のイベントを一部取りこぼす)が発生しうる
