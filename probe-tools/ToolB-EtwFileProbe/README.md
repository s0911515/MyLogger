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

### 既知の制約(発見・修正済み): `Options`(CreateOptions)フィールドの表示名バグ

TraceEventライブラリの`CreateOptions`列挙型は、表示名に誤って`FILE_ATTRIBUTE_*`(本来はファイル
**属性**用の名前)を流用しており、実際のビット位置とは異なる意味の名前が表示されていた
(発見当初は`Options=FILE_ATTRIBUTE_OFFLINE(0x1000)`のように表示され、これが実際には
`FILE_DELETE_ON_CLOSE`だと**ログを見ただけでは分からなかった**)。

**出典(CreateOptionsフラグの正しい定義)**: Microsoft公式ドキュメント
[NtCreateFile function (ntifs.h) - Windows drivers | Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntcreatefile)
の`CreateOptions`パラメータ表。該当箇所を引用:

> FILE_NON_DIRECTORY_FILE (0x00000040) | The file is *not* a directory. ...
> FILE_DELETE_ON_CLOSE (0x00001000) | The system deletes the file when the last handle to the file is
> passed to NtClose. If this flag is set, the DELETE flag must be set in the DesiredAccess parameter.

このドキュメントの正しいビット定義に基づき、**ツール側で独自にCreateOptionsをデコードするよう修正した**
(`DecodeCreateOptions`関数、TraceEventのenumは使わない)。修正後は下記「ログサンプル」の通り、
`Options=FILE_NON_DIRECTORY_FILE|FILE_DELETE_ON_CLOSE(0x1040)`のように正しいフラグ名がそのまま
ログに出力される。

## ログサンプル(実測、2026-07-15、最新版)

ToolA と同じ `D:\tmp\ProbeTest` に対して同じ7操作を行い記録した、CreateOptions正しくデコード後の
生ログ(**無加工・全件**)。この調査ではMicrosoftの仕様自体が込み入っており解釈に注意を要するため、
解釈・要約より前に生ログそのものを一次データとして残す。`watch-paths.txt` の絞り込みは反映されておらず
`D:\` 全体を監視した状態のため、`Hidemaru`(テキストエディタ)による無関係なファイルI/O(上位フォルダの
`.editorconfig` 探索等)も混ざっている。

```
[05:52:01.578737] === EtwFileProbe(ツールB: ETWファイルイベント) 開始 ログ=D:\tmp\toolB-sample.log 監視対象パス=[D:\](D:\DEV\MyLogger\probe-tools\ToolB-EtwFileProbe\dist\watch-paths.txt) 自PID=4340 除外プロセス=[claude, Code, System] (D:\DEV\MyLogger\probe-tools\ToolB-EtwFileProbe\dist\exclude-processes.txt) ===
[05:52:01.984563] 監視開始。プロセス終了(Ctrl+C / kill)まで待機します。
[05:52:08.889483] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28BA867A640 Disposition=CREATE_NEW Options=FILE_SEQUENTIAL_ONLY|FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x64) Attributes=Normal Share=None EtwTime=05:52:08.667872 Path=D:\tmp\ProbeTest\新規 テキスト ドキュメント.txt
[05:52:08.889912] SetInfo PID=20040 TID=22812 Process=explorer FileObject=FFFFA28BA867A640 FileKey=FFFFB8826BF92180 InfoClass=19 ExtraInfo=0 EtwTime=05:52:08.667997 Path=D:\tmp\ProbeTest\新規 テキスト ドキュメント.txt
[05:52:09.816720] Create PID=20040 TID=16708 Process=explorer FileObject=FFFFA28BA5432A60 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:09.648663 Path=D:\tmp\ProbeTest\新規 テキスト ドキュメント.txt
[05:52:09.817664] Create PID=20040 TID=16708 Process=explorer FileObject=FFFFA28B9F2B25A0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:09.667108 Path=D:\tmp\ProbeTest\新規 テキスト ドキュメント.txt
[05:52:09.961435] Create PID=20040 TID=16708 Process=explorer FileObject=FFFFA28BA5AC68A0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x60) Attributes=None Share=Read EtwTime=05:52:09.691841 Path=D:\tmp\ProbeTest\新規 テキスト ドキュメント.txt
[05:52:10.530297] Create PID=20040 TID=128764 Process=explorer FileObject=FFFFA28B9E53D160 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE|FILE_DISALLOW_EXCLUSIVE(0x20060) Attributes=None Share=ReadWrite EtwTime=05:52:10.400688 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.530580] Create PID=20040 TID=128764 Process=explorer FileObject=FFFFA28B9E53BAA0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x60) Attributes=None Share=ReadWrite EtwTime=05:52:10.400768 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.530824] Create PID=20040 TID=128764 Process=explorer FileObject=FFFFA28B9E53C2C0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT(0x20) Attributes=None Share=ReadWrite EtwTime=05:52:10.400796 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.530921] Create PID=20040 TID=128764 Process=explorer FileObject=FFFFA28B9E53ADA0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_ALERT(0x10) Attributes=None Share=ReadWrite EtwTime=05:52:10.400828 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.531169] Create PID=20040 TID=128764 Process=explorer FileObject=FFFFA28B9E5381C0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=Normal Share=ReadWrite EtwTime=05:52:10.400855 Path=D:\tmp\ProbeTest\write_target.txt\
[05:52:10.603630] Create PID=20040 TID=16708 Process=explorer FileObject=FFFFA28BB307D800 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:10.494671 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.604299] Create PID=20040 TID=16708 Process=explorer FileObject=FFFFA28BAFA2B8A0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:10.501738 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.604866] Create PID=20040 TID=16708 Process=explorer FileObject=FFFFA28B9F2A3A00 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x60) Attributes=None Share=Read EtwTime=05:52:10.509020 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.849305] Create PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28BA8661340 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x60) Attributes=Normal Share=ReadWrite EtwTime=05:52:10.733293 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.866591] Create PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28BA8661680 Disposition=OPEN_EXISTING Options=FILE_SEQUENTIAL_ONLY|FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x64) Attributes=None Share=ReadWrite EtwTime=05:52:10.739177 Path=D:\tmp\ProbeTest\.editorconfig
[05:52:10.866806] Create PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28BA8662860 Disposition=OPEN_EXISTING Options=FILE_SEQUENTIAL_ONLY|FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x64) Attributes=None Share=ReadWrite EtwTime=05:52:10.739204 Path=D:\tmp\.editorconfig
[05:52:10.866984] Create PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28BA8665E00 Disposition=OPEN_EXISTING Options=FILE_SEQUENTIAL_ONLY|FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x64) Attributes=None Share=ReadWrite EtwTime=05:52:10.739222 Path=D:\.editorconfig
[05:52:10.868122] Read   PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28BA8661340 FileKey=FFFFB8829CE8C180 Offset=0 IoSize=4 IoFlags=395520 EtwTime=05:52:10.739297 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.868314] Read   PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28BA8661340 FileKey=FFFFB8829CE8C180 Offset=0 IoSize=16352 IoFlags=0 EtwTime=05:52:10.739366 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.868428] Read   PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28BA8661340 FileKey=FFFFB8829CE8C180 Offset=30 IoSize=16352 IoFlags=0 EtwTime=05:52:10.739391 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.868513] Read   PID=53900 TID=77372 Process=Hidemaru FileObject=FFFFA28BA8661340 FileKey=FFFFB8829CE8C180 Offset=30 IoSize=16352 IoFlags=0 EtwTime=05:52:10.739411 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:10.884074] Create PID=20040 TID=16708 Process=explorer FileObject=FFFFA28B3C960740 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x60) Attributes=None Share=Read EtwTime=05:52:10.809454 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:12.065049] Create PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28B9F2AE300 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x60) Attributes=Normal Share=ReadWrite EtwTime=05:52:11.955418 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:12.065197] Write  PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28B9F2AE300 FileKey=FFFFB8829CE8C180 Offset=0 IoSize=33 IoFlags=395776 EtwTime=05:52:11.955607 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:12.065272] SetInfo PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28B9F2AE300 FileKey=FFFFB8829CE8C180 InfoClass=20 ExtraInfo=21 EtwTime=05:52:11.955669 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:12.065514] SetInfo PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28B9F2AE300 FileKey=FFFFB8829CE8C180 InfoClass=19 ExtraInfo=21 EtwTime=05:52:11.955715 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:12.065656] Create PID=53900 TID=42756 Process=Hidemaru FileObject=FFFFA28B9F2AB240 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x60) Attributes=Normal Share=ReadWrite EtwTime=05:52:11.955932 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:12.067212] Create PID=20040 TID=16708 Process=explorer FileObject=FFFFA28B3C9670E0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x60) Attributes=None Share=Read EtwTime=05:52:11.988989 Path=D:\tmp\ProbeTest\write_target.txt
[05:52:16.731128] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28B9F2CEB00 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:16.570551 Path=D:\tmp\ProbeTest\Source\copy_source.txt
[05:52:16.731355] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28B9F2CA860 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:16.570610 Path=D:\tmp\ProbeTest\Source\copy_source.txt
[05:52:17.548240] Create PID=20040 TID=36852 Process=explorer FileObject=FFFFA28BB1439400 Disposition=OPEN_EXISTING Options=FILE_SEQUENTIAL_ONLY|FILE_NON_DIRECTORY_FILE|FILE_OPEN_REPARSE_POINT(0x200044) Attributes=None Share=Read, Delete EtwTime=05:52:17.357048 Path=D:\tmp\ProbeTest\Source\copy_source.txt
[05:52:17.548435] Create PID=20040 TID=36852 Process=explorer FileObject=FFFFA28BB143C660 Disposition=CREATE_NEW Options=FILE_SEQUENTIAL_ONLY|FILE_NON_DIRECTORY_FILE(0x44) Attributes=Archive Share=None EtwTime=05:52:17.357192 Path=D:\tmp\ProbeTest\copy_source.txt
[05:52:17.548790] SetInfo PID=20040 TID=36852 Process=explorer FileObject=FFFFA28BB143C660 FileKey=FFFFB8827F24B180 InfoClass=20 ExtraInfo=12 EtwTime=05:52:17.357716 Path=D:\tmp\ProbeTest\copy_source.txt
[05:52:17.548989] Read   PID=20040 TID=36852 Process=explorer FileObject=FFFFA28BB1439400 FileKey=FFFFB8820E56B180 Offset=0 IoSize=18 IoFlags=393472 EtwTime=05:52:17.357756 Path=D:\tmp\ProbeTest\Source\copy_source.txt
[05:52:17.549164] SetInfo PID=20040 TID=36852 Process=explorer FileObject=FFFFA28BB143C660 FileKey=FFFFB8827F24B180 InfoClass=4 ExtraInfo=0 EtwTime=05:52:17.357945 Path=D:\tmp\ProbeTest\copy_source.txt
[05:52:17.549616] Create PID=20040 TID=36852 Process=explorer FileObject=FFFFA28B63D5F3C0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE|FILE_DISALLOW_EXCLUSIVE(0x20060) Attributes=None Share=ReadWrite EtwTime=05:52:17.358604 Path=D:\tmp\ProbeTest\Source\copy_source.txt
[05:52:17.549804] Create PID=20040 TID=36852 Process=explorer FileObject=FFFFA28B63D69300 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_NON_DIRECTORY_FILE(0x60) Attributes=None Share=ReadWrite EtwTime=05:52:17.358684 Path=D:\tmp\ProbeTest\Source\copy_source.txt
[05:52:17.549932] Create PID=20040 TID=36852 Process=explorer FileObject=FFFFA28B63D69E60 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT(0x20) Attributes=None Share=ReadWrite EtwTime=05:52:17.358713 Path=D:\tmp\ProbeTest\Source\copy_source.txt
[05:52:17.550037] Create PID=20040 TID=36852 Process=explorer FileObject=FFFFA28B63D6BA00 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_ALERT(0x10) Attributes=None Share=ReadWrite EtwTime=05:52:17.358734 Path=D:\tmp\ProbeTest\Source\copy_source.txt
[05:52:17.550132] Create PID=20040 TID=36852 Process=explorer FileObject=FFFFA28B63D6D8E0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=Normal Share=ReadWrite EtwTime=05:52:17.358757 Path=D:\tmp\ProbeTest\Source\copy_source.txt\
[05:52:20.063482] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28B9E54D3C0 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:19.935524 Path=D:\tmp\ProbeTest\Source\move_source.txt
[05:52:20.063679] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28B9E54F5E0 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:19.935638 Path=D:\tmp\ProbeTest\Source\move_source.txt
[05:52:20.864805] Create PID=20040 TID=91700 Process=explorer FileObject=FFFFA28B9E55C5E0 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:20.751773 Path=D:\tmp\ProbeTest\Source\move_source.txt
[05:52:20.864936] Create PID=20040 TID=91700 Process=explorer FileObject=FFFFA28B9E55B740 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:20.751898 Path=D:\tmp\ProbeTest\Source\move_source.txt
[05:52:20.865153] Rename PID=20040 TID=91700 Process=explorer FileObject=FFFFA28B9E55B740 FileKey=FFFFB882DD43E180 InfoClass=10 ExtraInfo=0 EtwTime=05:52:20.751972 Path=D:\tmp\ProbeTest\Source\move_source.txt
[05:52:23.183584] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28BA2AA20E0 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:22.922591 Path=D:\tmp\ProbeTest\Source\rename_source.txt
[05:52:23.183750] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28BA2A9FD20 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:22.922658 Path=D:\tmp\ProbeTest\Source\rename_source.txt
[05:52:24.780534] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28B9E5389E0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:24.547043 Path=D:\tmp\ProbeTest\Source\rename_source.txt
[05:52:24.780908] Rename PID=20040 TID=22812 Process=explorer FileObject=FFFFA28B9E5389E0 FileKey=FFFFB8827FAD7180 InfoClass=10 ExtraInfo=0 EtwTime=05:52:24.547157 Path=D:\tmp\ProbeTest\Source\rename_source.txt
[05:52:24.782521] Create PID=20040 TID=152628 Process=explorer FileObject=FFFFA28BB1440AA0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT(0x20) Attributes=Normal Share=ReadWrite, Delete EtwTime=05:52:24.573838 Path=D:\tmp\ProbeTest\Source\rename_source.txt
[05:52:28.179786] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28BB3055620 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:28.070719 Path=D:\tmp\ProbeTest\delete_normal.txt
[05:52:28.179960] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28BB3051520 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:28.070777 Path=D:\tmp\ProbeTest\delete_normal.txt
[05:52:28.180754] Create PID=20040 TID=126476 Process=explorer FileObject=FFFFA28B9E54BEA0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT(0x20) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:28.095070 Path=D:\tmp\ProbeTest\delete_normal.txt
[05:52:28.180962] Create PID=20040 TID=126476 Process=explorer FileObject=FFFFA28B9E54E5A0 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:28.096034 Path=D:\tmp\ProbeTest\delete_normal.txt
[05:52:28.181109] Delete PID=20040 TID=126476 Process=explorer FileObject=FFFFA28B9E54E5A0 FileKey=FFFFB8823073C180 InfoClass=13 ExtraInfo=1 EtwTime=05:52:28.096071 Path=D:\tmp\ProbeTest\delete_normal.txt
[05:52:28.181208] Delete PID=20040 TID=126476 Process=explorer FileObject=FFFFA28B9E54E5A0 FileKey=FFFFB8823073C180 InfoClass=13 ExtraInfo=0 EtwTime=05:52:28.096084 Path=D:\tmp\ProbeTest\delete_normal.txt
[05:52:28.181514] Create PID=20040 TID=126476 Process=explorer FileObject=FFFFA28B9E5507C0 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:28.098720 Path=D:\tmp\ProbeTest\delete_normal.txt
[05:52:28.181617] Create PID=20040 TID=126476 Process=explorer FileObject=FFFFA28B9E54DD80 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT(0x200020) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:28.098765 Path=D:\tmp\ProbeTest\delete_normal.txt
[05:52:28.181764] Rename PID=20040 TID=126476 Process=explorer FileObject=FFFFA28B9E54DD80 FileKey=FFFFB8823073C180 InfoClass=10 ExtraInfo=0 EtwTime=05:52:28.098856 Path=D:\tmp\ProbeTest\delete_normal.txt
[05:52:29.571551] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28B9E5574A0 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:29.379996 Path=D:\tmp\ProbeTest\delete_complete.txt
[05:52:29.571760] Create PID=20040 TID=22812 Process=explorer FileObject=FFFFA28B9E5519A0 Disposition=OPEN_EXISTING Options=FILE_OPEN_REPARSE_POINT(0x200000) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:29.380065 Path=D:\tmp\ProbeTest\delete_complete.txt
[05:52:29.577266] Create PID=20040 TID=133520 Process=explorer FileObject=FFFFA28B63D51520 Disposition=OPEN_EXISTING Options=FILE_SYNCHRONOUS_IO_NONALERT(0x20) Attributes=None Share=ReadWrite, Delete EtwTime=05:52:29.402286 Path=D:\tmp\ProbeTest\delete_complete.txt
[05:52:30.692906] Create PID=20040 TID=133520 Process=explorer FileObject=FFFFA28B9EA40CE0 Disposition=OPEN_EXISTING Options=FILE_NON_DIRECTORY_FILE|FILE_DELETE_ON_CLOSE(0x1040) Attributes=None Share=Delete EtwTime=05:52:30.557544 Path=D:\tmp\ProbeTest\delete_complete.txt
[05:52:41.874370] === EtwFileProbe 終了 (出力イベント数=63, EventsLost=0) ===
```

3回目の再現でも`0x1040 = FILE_NON_DIRECTORY_FILE|FILE_DELETE_ON_CLOSE`と正しくデコードされ、
`Delete`/`FileDelete`/`SetInfo`/`Cleanup`/`Close`はいずれも記録されなかった(#7、再現性を3回確認)。
コピー操作(#3)の`SetInfo(InfoClass=20, ExtraInfo=12)`(FileEndOfFileInformation)・
`SetInfo(InfoClass=4)`(FileBasicInformation)、上書き保存(#2)の`SetInfo(InfoClass=20/19)`も
このログにそのまま含まれている。

### 操作と記録されたイベントの対応

| # | 操作 | 主な記録イベント | 所見 |
|---|---|---|---|
| 1 | 新規作成 | `Create`(`Disposition=CREATE_NEW`)1件 + `Create`(`OPEN_EXISTING`、Explorerの再オープン)複数 | `Disposition`で「真の新規作成」と「既存を開いただけ」を区別できる |
| 2 | 上書き保存(Hidemaruで編集) | `Create`(Explorer/Hidemaru双方から複数)+ `Read`×3(Offset 0, 0, 30)+ `Write`(Offset=0, IoSize=34) | **実際の書き込みオフセット・バイト数まで取得できる**(ToolAには無い情報)。副次的にHidemaruが`ProbeTest`→`D:\tmp`→`D:\`と上位フォルダの`.editorconfig`を探索する様子まで見えた |
| 3 | コピー | コピー元への`Create`+`Read`(IoSize=18)、コピー先への`Create`(`CREATE_NEW`)+`SetInfo`(`InfoClass=20`=EOF設定、`InfoClass=4`=タイムスタンプ設定) | **コピー先への`Write`イベントは今回も記録されなかった。** 小サイズファイルはキャッシュマネージャーのFast I/O経路で処理されIRPが発生しないためと考えられる(過去のLOCAL_COPY検知試作時の知見と一致)。ただし`SetInfo`(EOF設定)がコピー完了の間接的な裏付けとして使える |
| 4 | 移動(フォルダ間、切り取り&貼り付け) | `Create`複数 + `Rename`(`InfoClass=10`, `ExtraInfo=0`、パスは**移動元のまま**) | ToolAでは`Deleted`+`Created`だったが、ETWでは`Rename`1件になる。**移動先の新パスはこのイベントに含まれない** |
| 5 | リネーム(同一フォルダ内) | `Create`複数 + `Rename`(`InfoClass=10`, `ExtraInfo=0`) | **#4(移動)と全く同じ形式。** ETW単体では「フォルダ間移動」と「同一フォルダ内リネーム」を区別できない(ToolAとは逆に、こちらはToolAの方が区別できる) |
| 6 | 通常削除(ゴミ箱移動) | `Create`複数 + `Delete`(`InfoClass=13`)×2 + `Rename`(`InfoClass=10`) | ゴミ箱への移動は内部的に「削除」と「リネーム(ごみ箱内への移動)」の組み合わせで記録される。[doc/Sysmon/Sysmon.md](../../doc/Sysmon/Sysmon.md)の「通常削除はゴミ箱へのRename」という知見と一致 |
| 7 | 完全削除(Shift+Delete) | `Create`(最後は`Options=FILE_NON_DIRECTORY_FILE\|FILE_DELETE_ON_CLOSE(0x1040)`)のみ。`Delete`/`FileDelete`/`SetInfo`/`Cleanup`/`Close`いずれも記録されず(3回再現) | **バグではなく、この削除経路ではFileIOレイヤーに明示的な削除イベントが一切現れないと判明。** 詳細は下記「重要な発見」参照 |

### 重要な発見

- **完全削除(Shift+Delete)はFileIOイベントとして明示的な「削除」の痕跡を残さないことがある(3回の
  再検証で再現性を確認済み)。** ファイルが実際に消滅したことも確認した上で、`Delete`/`FileDelete`/
  `SetInfo`/`Cleanup`/`Close`のいずれのイベントも記録されなかった。唯一の手がかりは最後の`Create`
  イベントの`Options`に`FILE_DELETE_ON_CLOSE`(0x1000)ビットが立っていたことのみで、これは「ハンドルを
  閉じたら自動的に削除する」という指定であり、別途Delete系のIRPが発行されない。**削除の証跡が、削除専用の
  イベントではなく、Create時点のフラグにしか残らないケースがある**、という重要な制約が判明した
- 上記の調査過程で、TraceEventの`CreateOptions`表示名が`FILE_ATTRIBUTE_*`を誤流用したバグ持ちである
  ことも判明し、ツール側で正しくデコードするよう修正した(詳細は上記「既知の制約」参照)。修正前は
  生の16進値を確認しなければ`FILE_DELETE_ON_CLOSE`の検出は不可能だった
- **`FILE_DELETE_ON_CLOSE`が立っていることを「完全削除が行われた」の判定根拠として使うのは信頼できない。**
  理由は2つある。(1) 誤検知: このフラグは多くのアプリが一時ファイルの自動クリーンアップに一般的に
  使う仕組みであり、ユーザーによる意図的な削除操作とは無関係に立つことが多い。(2) 見逃し: 同じ
  「完全削除」という結果でも、必ずこの経路を通るとは限らない。実際、`rm`(POSIX形式の削除)による
  削除では、このフラグではなく明示的な`Delete`イベント(`InfoClass=64`、POSIX semantics)として
  記録された。**同じ「完全削除」でも呼び出し元(Explorer/rm/他アプリ)によって全く異なる痕跡の
  残り方をするため、ETWレベルで「完全削除」を一意に検出できるシグネチャは存在しない**、というのが
  この調査の結論
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
