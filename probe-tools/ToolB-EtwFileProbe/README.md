# ToolB-EtwFileProbe

ETW(Event Tracing for Windows)のカーネル FileIO イベントで実際に取得できる情報を漏れなく確認する
ための最小プローブ。MyLogger 本体のパス分類・テーブル分離等は一切介さない。目的が「ETWで何が取れるか
正確に知ること」であるため、既知のノイズ除外を除き、各イベント型が持つフィールドはできる限りすべて
記録する。

これは「テーブルA」(ファイルイベント)専用のツールで、プロセス作成イベント(PID→ユーザー名)は
[ToolC-ProcessAuditProbe](../ToolC-ProcessAuditProbe) が独立して記録する。**このツールの中では両者を
突き合わせない**(突合はテーブルA・Bを見比べて後日別途行う)。

## 仕組み

`Microsoft.Diagnostics.Tracing.TraceEvent`(ETWのカーネルプロバイダー、`FileIOInit | FileIO` キーワード)
を使い、OSカーネルが発生させる生のファイルI/Oイベントを購読する。購読しているのは
Create/Write/Read/Flush/Rename/Delete/FileDelete/SetInfo/Cleanup/Close の10種類(後半4種は、完全削除
(Shift+Delete)の調査過程でリフレクションにより `KernelTraceEventParser` の全イベント一覧を洗い出し、
追加で購読するようにしたもの。詳細は下記「ログサンプル」参照)。
`FileSystemWatcher`(ToolA)とは全く別の取得経路であり、PID・プロセス名・詳細フラグまで取得できる点が
最大の違い。ただし**ユーザー名は取得できない**(ETWのFileIOイベントにはユーザー/SIDの情報が無いことを
リフレクションで確認済み)。ユーザー名が必要な場合は ToolC を併用する。

## 絞り込み(かけているのは以下の3点のみ、それ以外の加工はしない)

1. 自プロセス(このツール自身)によるイベントは出力しない(自己増殖ループ防止)
2. `watch-paths.txt` に列挙したパス配下**以外**のイベントは出力しない
3. `exclude-processes.txt` に列挙したプロセス名によるイベント、および既知のノイズ
   (フォルダ自体のオープン・`desktop.ini`・`Thumbs.db`・`$RECYCLE.BIN`・`System Volume Information`)
   は出力しない

## 使い方

```powershell
# 管理者権限のPowerShellで実行(ETWカーネルプロバイダーの有効化に必要)

# ソースから実行
dotnet run --project probe-tools\ToolB-EtwFileProbe -- [ログファイル]

# 同梱のビルド済みexe(.NETランタイム不要)
.\dist\EtwFileProbe.exe [ログファイル]
```

- `[ログファイル]` 省略時は実行ファイルと同じフォルダの `etwfileprobe.log`
- 初回起動時、実行ファイルと同じフォルダに `watch-paths.txt` と `exclude-processes.txt` が
  既定値で自動生成される(無ければ)。次回以降は編集した内容がそのまま使われる。この2ファイルは
  このソースフォルダにも参照用として既定値のまま同梱しているので、ビルド後の出力先(または配布先)
  フォルダにコピーしておけば毎回自動生成を待たずに編集できる
- 起動後、対象パス配下でファイル操作を行い、**Ctrl+C** で停止するとログが確定する

## 設定ファイル

### `watch-paths.txt` — 監視対象パス

```
# 1行1パス。ドライブ全体を監視するなら D:\ のようにドライブ直下を指定する。
# 特定フォルダに絞るとノイズが減る (例: D:\tmp\EtwVerify)。# で始まる行はコメント。
# ここに列挙したパス配下のファイルI/Oのみをログに出力する。
D:\
```

既定は `D:\`(ドライブ全体)。評価対象フォルダが決まっている場合は、そのフォルダ1行に絞ると
無関係なノイズ(OS/他アプリのファイルI/O)が大幅に減り、評価しやすくなる。複数行指定も可能。

### `exclude-processes.txt` — 除外プロセス名

```
# 1行1プロセス名 (拡張子なし)。# で始まる行はコメント。
# ここに列挙したプロセスによるファイル操作はログに出力しない。
claude
Code
System
```

既定はエディタ・このセッション自体等、観測対象ではない常駐プロセス。ノイズが多いプロセスがあれば
自由に追加してよい。

## 記録されるログの内容

1行1イベント、ETW生タイムスタンプ(`EtwTime`、マイクロ秒精度)付き。イベント種別ごとに記録項目が
異なる(取得できる全フィールドを記録):

```
[14:32:01.123456] Create PID=12345 TID=6789 Process=explorer FileObject=FFFFA1B2C3D4 Disposition=Create Options=... Attributes=... Share=... EtwTime=14:32:01.123400 Path=D:\tmp\a.txt
[14:32:01.234567] Write  PID=12345 TID=6789 Process=explorer FileObject=FFFFA1B2C3D4 FileKey=FFFFA1B2C3D0 Offset=0 IoSize=4096 IoFlags=0 EtwTime=14:32:01.234500 Path=D:\tmp\a.txt
[14:32:02.000000] Rename PID=12345 TID=6789 Process=explorer FileObject=FFFFA1B2C3D4 FileKey=FFFFA1B2C3D0 InfoClass=... ExtraInfo=... EtwTime=14:32:02.000000 Path=D:\tmp\b.txt
```

共通項目: `PID`(プロセスID)、`TID`(スレッドID)、`Process`(プロセス名)、`FileObject`(カーネル
オブジェクトのアドレス、同一ファイルハンドルの操作を紐付けるのに使える)、`EtwTime`(ETW生タイムスタンプ)、
`Path`。イベント種別固有項目: Create=`Disposition`/`Options`/`Attributes`/`Share`、
Write/Read=`FileKey`/`Offset`/`IoSize`/`IoFlags`、Rename/Delete/SetInfo=`FileKey`/`InfoClass`/`ExtraInfo`、
FileDelete=`FileKey`のみ、Cleanup/Close=`FileObject`/`FileKey`のみ。

終了時に `EventsLost`(ETWバッファ溢れによる取りこぼし件数)も出力される。

### 既知の制約: `Options`(CreateOptions)フィールドの表示名について

TraceEventライブラリの`CreateOptions`列挙型は、表示名に誤って`FILE_ATTRIBUTE_*`(本来はファイル
**属性**用の名前)を流用している。実際のビット位置は`NtCreateFile`の**CreateOptions**フラグ
(`FILE_DIRECTORY_FILE=0x1`, `FILE_NON_DIRECTORY_FILE=0x40`, `FILE_DELETE_ON_CLOSE=0x1000` 等)であり、
たまたま同じビット位置に`FileAttributes`の名前が割り当てられているだけで**意味が異なる**。実機の
リフレクションで確認済み。このツールは`Options=`の直後に生の16進値も併記する
(例: `Options=FILE_ATTRIBUTE_OFFLINE(0x1000)`)ので、正しく解釈したい場合はNtCreateFileの
CreateOptionsフラグ表と照らし合わせること(`0x1000`は実際には`FILE_DELETE_ON_CLOSE`)。

## ログサンプル(実測、2026-07-15)

ToolA と同じ `D:\tmp\ProbeTest` に対して同じ7操作を行い記録した実際のログ(抜粋・一部整形)。
このセッションでは `watch-paths.txt` の編集が反映されておらず `D:\` 全体を監視していたため、
`Hidemaru`(テキストエディタ)が上位フォルダの `.editorconfig` を探索するイベント等、対象操作とは
無関係なイベントも混ざっている(絞り込みの効果を示す実例として、あえてそのまま残す)。

```
[00:00:49.910920] === EtwFileProbe 開始 監視対象パス=[D:\] 自PID=119872 除外プロセス=[claude, Code, System] ===
[00:01:16.297095] Create PID=20040 Process=explorer Disposition=CREATE_NEW Path=...\新規 テキスト ドキュメント.txt
[00:01:17.679806] Create PID=20040 Process=explorer Disposition=OPEN_EXISTING Path=...\新規 テキスト ドキュメント.txt (×3、Explorerの再オープン)
[00:01:17.891122] Create PID=20040 Process=explorer Disposition=OPEN_EXISTING Path=write_target.txt (複数)
[00:01:18.455225] Create PID=105284 Process=Hidemaru Path=D:\tmp\ProbeTest\.editorconfig
[00:01:18.455470] Create PID=105284 Process=Hidemaru Path=D:\tmp\.editorconfig
[00:01:18.455581] Create PID=105284 Process=Hidemaru Path=D:\.editorconfig
[00:01:18.456386] Read   PID=105284 Process=Hidemaru Offset=0  IoSize=4     Path=write_target.txt
[00:01:18.456483] Read   PID=105284 Process=Hidemaru Offset=0  IoSize=16352 Path=write_target.txt
[00:01:18.456565] Read   PID=105284 Process=Hidemaru Offset=30 IoSize=16352 Path=write_target.txt
[00:01:21.961084] Write  PID=105284 Process=Hidemaru Offset=0  IoSize=34    Path=write_target.txt
[00:01:30.239137] Read   PID=20040 Process=explorer  Offset=0  IoSize=18    Path=Source\copy_source.txt
[00:01:30.303651] Create PID=20040 Process=explorer  Disposition=CREATE_NEW Path=copy_source.txt (コピー先。対応するWriteイベントは記録されなかった)
[00:01:35.388955] Rename PID=20040 Process=explorer  InfoClass=10 ExtraInfo=0 Path=Source\move_source.txt (移動先のパスはこのイベントに含まれない)
[00:01:43.115167] Rename PID=20040 Process=explorer  InfoClass=10 ExtraInfo=0 Path=Source\rename_source.txt (#移動と全く同じ形式)
[00:01:46.462605] Delete PID=20040 Process=explorer  InfoClass=13 ExtraInfo=1 Path=delete_normal.txt
[00:01:46.462691] Delete PID=20040 Process=explorer  InfoClass=13 ExtraInfo=0 Path=delete_normal.txt
[00:01:46.463035] Rename PID=20040 Process=explorer  InfoClass=10 ExtraInfo=0 Path=delete_normal.txt (ごみ箱への移動もRenameとして記録される)
[00:01:48.675037] Create PID=20040 Process=explorer  Disposition=OPEN_EXISTING Share=Delete Path=delete_complete.txt
```

#7(完全削除)のみ、バグ修正・イベント追加購読後に再検証した(2回、いずれも同じ結果で再現性を確認):

```
[05:32:13.680074] Create PID=20040 Process=explorer Disposition=OPEN_EXISTING Options=2097152(0x200000) Share=ReadWrite, Delete Path=delete_complete.txt
[05:32:13.691125] Create PID=20040 Process=explorer Disposition=OPEN_EXISTING Options=FILE_ATTRIBUTE_ARCHIVE(0x20)         Share=ReadWrite, Delete Path=delete_complete.txt
[05:32:14.701412] Create PID=20040 Process=explorer Disposition=OPEN_EXISTING Options=FILE_ATTRIBUTE_DEVICE, FILE_ATTRIBUTE_OFFLINE(0x1040) Share=Delete Path=delete_complete.txt
[05:32:21.414096] === EtwFileProbe 終了 (出力イベント数=48, EventsLost=0) === (Delete/FileDelete/SetInfo/Cleanup/Closeいずれも記録されず、ファイルは実際に消滅)
```

`0x1040 = FILE_NON_DIRECTORY_FILE(0x40) | FILE_DELETE_ON_CLOSE(0x1000)`。2回とも同じビットが立っており、
再現性のある挙動と確認できた。

再検証時、コピー操作(#3)では追加購読した `SetInfo` により以下も観測できた(前回は見えていなかった):

```
Create  PID=20040 Process=explorer Disposition=CREATE_NEW Path=copy_source.txt (コピー先)
SetInfo PID=20040 Process=explorer InfoClass=20 ExtraInfo=12 Path=copy_source.txt (FileEndOfFileInformation: ファイルサイズ確定)
SetInfo PID=20040 Process=explorer InfoClass=4  ExtraInfo=0  Path=copy_source.txt (FileBasicInformation: タイムスタンプ/属性設定)
```

**コピー先への`Write`イベントは今回も記録されなかったが、`SetInfo`(`InfoClass=20`=EOF設定)がコピー完了の
間接的な裏付けとして使える**ことが分かった。新規作成(#1)でも `CREATE_NEW` 直後に
`SetInfo(InfoClass=19` = `FileAllocationInformation`, 領域確保`)` が付随することを確認した。

### 操作と記録されたイベントの対応

| # | 操作 | 主な記録イベント | 所見 |
|---|---|---|---|
| 1 | 新規作成 | `Create`(`Disposition=CREATE_NEW`)1件 + `Create`(`OPEN_EXISTING`、Explorerの再オープン)複数 | `Disposition`で「真の新規作成」と「既存を開いただけ」を区別できる |
| 2 | 上書き保存(Hidemaruで編集) | `Create`(Explorer/Hidemaru双方から複数)+ `Read`×3(Offset 0, 0, 30)+ `Write`(Offset=0, IoSize=34) | **実際の書き込みオフセット・バイト数まで取得できる**(ToolAには無い情報)。副次的にHidemaruが`ProbeTest`→`D:\tmp`→`D:\`と上位フォルダの`.editorconfig`を探索する様子まで見えた |
| 3 | コピー | コピー元への`Create`+`Read`(IoSize=18)、コピー先への`Create`(`CREATE_NEW`)+`SetInfo`(`InfoClass=20`=EOF設定、`InfoClass=4`=タイムスタンプ設定) | **コピー先への`Write`イベントは今回も記録されなかった。** 小サイズファイルはキャッシュマネージャーのFast I/O経路で処理されIRPが発生しないためと考えられる(過去のLOCAL_COPY検知試作時の知見と一致)。ただし`SetInfo`(EOF設定)がコピー完了の間接的な裏付けとして使える |
| 4 | 移動(フォルダ間、切り取り&貼り付け) | `Create`複数 + `Rename`(`InfoClass=10`, `ExtraInfo=0`、パスは**移動元のまま**) | ToolAでは`Deleted`+`Created`だったが、ETWでは`Rename`1件になる。**移動先の新パスはこのイベントに含まれない** |
| 5 | リネーム(同一フォルダ内) | `Create`複数 + `Rename`(`InfoClass=10`, `ExtraInfo=0`) | **#4(移動)と全く同じ形式。** ETW単体では「フォルダ間移動」と「同一フォルダ内リネーム」を区別できない(ToolAとは逆に、こちらはToolAの方が区別できる) |
| 6 | 通常削除(ゴミ箱移動) | `Create`複数 + `Delete`(`InfoClass=13`)×2 + `Rename`(`InfoClass=10`) | ゴミ箱への移動は内部的に「削除」と「リネーム(ごみ箱内への移動)」の組み合わせで記録される。[doc/Sysmon/Sysmon.md](../../doc/Sysmon/Sysmon.md)の「通常削除はゴミ箱へのRename」という知見と一致 |
| 7 | 完全削除(Shift+Delete) | `Create`(最後は`Options=FILE_ATTRIBUTE_OFFLINE(0x1040)`=実際は`FILE_NON_DIRECTORY_FILE`\|`FILE_DELETE_ON_CLOSE`)のみ。`Delete`/`FileDelete`/`SetInfo`/`Cleanup`/`Close`いずれも記録されず(2回再現) | **バグではなく、この削除経路ではFileIOレイヤーに明示的な削除イベントが一切現れないと判明。** 詳細は下記「重要な発見」参照 |

### 重要な発見

- **完全削除(Shift+Delete)はFileIOイベントとして明示的な「削除」の痕跡を残さないことがある(2回の
  再検証で再現性を確認済み)。** ファイルが実際に消滅したことも確認した上で、`Delete`/`FileDelete`/
  `SetInfo`/`Cleanup`/`Close`のいずれのイベントも記録されなかった。唯一の手がかりは最後の`Create`
  イベントの`Options`に`FILE_DELETE_ON_CLOSE`(0x1000)ビットが立っていたことのみで、これは「ハンドルを
  閉じたら自動的に削除する」という指定であり、別途Delete系のIRPが発行されない。**削除の証跡が、削除専用の
  イベントではなく、Create時点のフラグにしか残らないケースがある**、という重要な制約が判明した
- 上記の調査過程で、TraceEventの`CreateOptions`表示名が`FILE_ATTRIBUTE_*`を誤流用したバグ持ちである
  ことも判明(詳細は上記「既知の制約」参照)。生の16進値を確認しなければ`FILE_DELETE_ON_CLOSE`の
  検出は不可能だった
- **ToolAとETWで「移動」と「リネーム」の区別能力が逆転している。** FileSystemWatcher(ToolA)は
  フォルダ間移動を`Deleted`+`Created`、同一フォルダ内リネームを`Renamed`と明確に区別する。一方ETW
  (本ツール)は両方とも同じ`Rename`イベント(`InfoClass=10`)になり区別できない。**2つを併用すれば、
  ETWのPID/プロセス名付与とFileSystemWatcherの移動/リネーム区別を組み合わせられる**、という設計上の
  示唆が得られた
- **ETWの`Rename`イベントには移動・変更後の新パスが含まれない**(このツールが記録しているフィールド
  セットでは、旧パスのみが`Path`に出る)
- 小サイズファイルのコピー先には`Write`イベントが記録されないことがある(Fast I/O経路のため)
- ゴミ箱への通常削除(`InfoClass=13`)は「削除」ではなく内部的に「リネーム(ごみ箱内への移動)」を伴う

### 既知の不具合(発見・修正済み)

初回テスト時点のビルドには、終了処理で `session.Stop()` の**後**に `session.EventsLost` を読もうと
して `COMException`(WMIプロバイダーがインスタンス名を認識できない)で異常終了するバグがあった。
`EventsLost` は停止前に読む必要があると判明したため、`Stop()` 呼び出し前に読むよう修正済み
(`dist/` のexeも再ビルド済み)。再検証ではこのバグは再発せず正常終了したが、それでも#7の削除
イベントは記録されなかった(バグとは無関係な、上記「重要な発見」の通りの仕様)。
