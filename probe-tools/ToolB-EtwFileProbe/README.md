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
を使い、OSカーネルが発生させる生のファイルI/Oイベント(Create/Write/Read/Flush/Rename/Delete)を購読する。
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

# 自己完結ビルド済みexeの場合
.\EtwFileProbe.exe [ログファイル]
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
Write/Read=`FileKey`/`Offset`/`IoSize`/`IoFlags`、Rename/Delete=`FileKey`/`InfoClass`/`ExtraInfo`。

終了時に `EventsLost`(ETWバッファ溢れによる取りこぼし件数)も出力される。
