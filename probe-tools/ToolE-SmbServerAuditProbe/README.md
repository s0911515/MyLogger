# ToolE-SmbServerAuditProbe

「インバウンドSMB」(このPCの共有フォルダに対する、ネットワーク越しの外部アクセス)を記録する
最小プローブ。Windows セキュリティ監査ログの「ファイル共有」監査イベント(5140/5142-5145)を購読する。

MyLogger本体には既に同じ仕組みが実装済み([`src/MyLogger/Monitors/SmbAuditMonitor.cs`](../../src/MyLogger/Monitors/SmbAuditMonitor.cs))。
本ツールはそれと**同じ監査ログ購読方式**を、他マシンでの検証用に独立ツールとして切り出したもの。
ローカルファイルイベント(ToolB)、プロセス作成(ToolC)、アウトバウンドSMB(ToolF/G)とは独立して
記録し、突き合わせは行わない。

## 仕組み

「ファイル共有」(`{0CCE9224-...}`)・「詳細なファイル共有」(`{0CCE9244-...}`)の2つの監査サブ
カテゴリを有効にすると、他マシンからこのPCの共有フォルダにアクセスがあるたびに、Securityログに
以下のイベントが記録される。

| イベントID | 意味 |
|---|---|
| 5140 | 共有オブジェクトへの接続(共有への接続そのもの) |
| 5142 | 共有の新規作成 |
| 5143 | 共有の設定変更 |
| 5144 | 共有の削除 |
| 5145 | 共有内のファイル/フォルダへの詳細アクセス(ファイル単位。開けるかどうかのアクセスチェック含む) |

`ShareName`(共有名)・`SubjectUserName`/`SubjectDomainName`(アクセスしたユーザー)・`IpAddress`
(接続元IP)・`ShareLocalPath`(共有のローカルパス)・`RelativeTargetName`(共有ルートからの相対
パス、5145のみ)・`AccessMask`/`AccessList`(アクセス種別)などが記録される。**ユーザー名・接続元IP
までワンショットで取れる**のが、ETW(ToolB)や単純なFileSystemWatcher(ToolA)に対する優位点。

## 事前準備(重要): 共有フォルダが無いとイベントは発生しない

このマシン自身に共有フォルダが無い、または誰もアクセスしていない場合は何も記録されない。
検証用に共有を作る例:

```powershell
New-Item -ItemType Directory -Path D:\tmp\SmbTestShare -Force
New-SmbShare -Name TestShare -Path D:\tmp\SmbTestShare -FullAccess Everyone
```

作成後、**別マシン**(または同一マシンの別ユーザーセッション)から `\\<このマシン名>\TestShare` に
アクセスして検証する。同一マシン・同一ユーザーで `\\localhost\TestShare` にアクセスしても
ローカルショートカットが使われ5140/5145が発生しないことがあるため、可能な限り別マシンからの
アクセスで検証すること。

## 絞り込み(かけているのは以下の1点のみ)

- `IPC$`(管理共有。ファイル一覧取得や名前付きパイプ通信などの内部通信で頻発し、実ファイルアクセス
  ではないため既知のノイズ)へのアクセスは出力しない。それ以外は該当イベントのフィールドをすべて
  そのまま記録する。

## 使い方

```powershell
# 管理者権限のPowerShellで実行(監査ポリシーの設定・セキュリティログの購読に必要)

# ソースから実行
dotnet run --project probe-tools\ToolE-SmbServerAuditProbe -- [ログファイル]

# 同梱のビルド済みexe(.NETランタイム不要)
.\dist\SmbServerAuditProbe.exe [ログファイル]
```

- `[ログファイル]` 省略時は実行ファイルと同じフォルダの `smbserverauditprobe.log`
- 起動時に「ファイル共有」「詳細なファイル共有」の両監査サブカテゴリを自動的に有効化する
  (既に有効なら何もしない、冪等)
- 起動後、別マシンから共有フォルダにアクセスし、**Ctrl+C** で停止するとログが確定する
- **`[ログファイル]`と同時に、同名でCSV版(`[ログファイル].csv`。例: `smbtest.log`→`smbtest.csv`)
  も自動的に出力する。** 元は別ツール(LogFormatter)だったが、依頼によりツール本体に統合した

## 記録されるログの内容(生ログ・CSVの2種類)

### 生ログ(`.log`)

1行1イベント(ただし値に生の改行を含むため実際には複数の物理行にまたがることがある)、
`[HH:mm:ss.ffffff]`(マイクロ秒精度)のタイムスタンプ付き。該当イベントの全フィールドをそのまま
`Key=Value`で並べて出力する(特定フィールドだけを選んで加工することはしない)。従来通り変更なし。

```
[06:36:42.773165] ShareFileAccess(Event5145) SubjectUserSid=S-1-5-21-3393082697-3192495989-2329455330-1012 SubjectUserName=smbtest SubjectDomainName=WIN-DESK-2022 SubjectLogonId=0x643867fb ObjectType=File IpAddress=192.168.1.20 IpPort=55411 ShareName=\\*\ProbeTest ShareLocalPath=\??\D:\tmp\ProbeTest RelativeTargetName=\ AccessMask=0x100081 AccessList=%%1541 ... EtwTime=06:36:41.748799
```

(上記は実機採取ログ[`SAMPLELOG/smbtest.log`](SAMPLELOG)からの抜粋。`AccessList`/`AccessReason`
は元の値に生のタブ・改行を含むため、この抜粋では省略している)

### CSV(`.csv`)

生ログと同時にその場で1行ずつ追記される。列は固定(動的ではない)。1イベントの複数物理行への
またがりも解消され、1行1レコードになる。UTF-8(BOM付き)・全フィールド常時ダブルクォート。

`時刻,操作,EventID,SubjectUserSid,SubjectUserName,SubjectDomainName,SubjectLogonId,ObjectType,IpAddress,IpPort,ShareName,ShareLocalPath,RelativeTargetName,AccessMask,AccessMask解釈,AccessList,AccessList解釈,AccessReason,AccessReason解釈,その他`

- **列を固定にした理由**: CSVはイベントの発生と同時にその場で1行ずつ書き出すため(ファイル全体を
  読み終えてから列を決める、ということができない)、あらかじめ列を決めておく必要がある
- **「その他」列**: 5140/5145は実機で確認済みの固定列でほぼ全フィールドをカバーできるが、
  5142/5143/5144(共有の作成・変更・削除)は未検証のため、上記の固定列に無いフィールドが
  出てくる可能性がある。その場合も情報を取りこぼさないよう、この列に`Key=Value`形式でそのまま
  まとめる
- **`AccessMask解釈`列**: `AccessMask`(16進のアクセスマスク)を、.NET標準の`FileSystemRights`
  列挙型でビット単位に解釈する。ToールI-SaclProbeの解釈ロジックと同じ機械的な変換であり、
  信頼度が高い(例: `0x100081` → `ListDirectory, ReadAttributes, Synchronize`)
- **`AccessList解釈`列**: `AccessList`に含まれる`%%NNNN`というプレースホルダコード(Windowsの
  メッセージテーブル由来で、それ自体は人が読んでも意味が分からない)を、実機で確認できた範囲の
  対応表で1つずつ名前に変換する(例: `%%1541` → `SYNCHRONIZE(%%1541)`)。**未知のコードは
  コードのまま出力し、誤った解釈を断定しない**
- **`AccessReason解釈`列**: `AccessReason`は`%%右コード: %%理由コード 詳細`という形式の繰り返し。
  右コードは`AccessList解釈`と同じ対応表、理由コード(`%%1801`=許可、`%%1804`=所有権による許可、
  実機確認済みはこの2つのみ)は別の対応表で解釈し、詳細(SDDLのACEや特権名)はそのまま残す
  (例: `ReadAttributes: 許可 D:(A;;FA;;;WD)`)

## 実機での検証結果(2026-07-22)

別マシン(ユーザー名`smbtest`、IP `192.168.1.20`)から、`setup-test-env.ps1`で用意した
`D:\tmp\ProbeTest`を共有してアクセスし、一通りの操作を行った。生ログは
[`SAMPLELOG/smbtest.log`](SAMPLELOG)に同梱している。

### 分かったこと

- **`SubjectUserName`・`IpAddress`は実機で正しく記録される。** このツールの一番の価値(ETWや
  FileSystemWatcherでは取れない、接続元ユーザー・IPが分かる)を確認できた
- **`5140`(共有接続)が一度も記録されなかった。** 180件すべてが`5145`(ファイルアクセス)。
  ツール起動前に既にそのマシンとのSMBセッションが確立済みだったためと考えられる
  (Windowsは同一セッションでの再アクセスに対して`5140`を再発行しない)。検証時は、共有を
  明示的に切断してからツールを起動し直すとよい
- **リネームの新しい名前が、副産物的に記録されていた。** NTFS/SACL(ToルI)やETW(ToルB)では
  リネーム後の新しい名前は一切記録されないことを既に確認済みだが、SMB経由のリネームでは
  以下のように**旧名への`DELETE`アクセスの直後、新しい名前への読み取り系アクセスが別イベントで
  続く**ため、時間的な相関から新名を推測できる。

  ```
  06:37:22.410741  RelativeTargetName=Source\rename_source.txt        AccessMask=0x110080(DELETE、リネーム本体)
  06:37:22.412642  RelativeTargetName=Source\rename_source_hoge.txt   AccessMask=0x100080(属性確認、新名への追従アクセス)
  ```

  これは「新名を記録するフィールドがある」わけではなく、Explorerがリネーム後に新しい名前で
  改めてネットワーク越しにアクセスし直す副作用によるもの。ToルI-SaclProbeの「推定コピー」と
  同じ時間相関ヒューリスティックが、ここにも応用できそうである
- **SMB経由のコピーでZone.Identifier代替データストリームへのアクセスが記録される。**
  `copy_source.txt:Zone.Identifier`のようなエントリが実際に記録されており、Windowsが
  「ネットワーク経由で取得したファイル」としてマーク・確認する挙動が監査ログからも見える
- **`RelativeTargetName`が大文字化して記録されるケースがある**(例: `write_target.txt`が
  `WRITE_TARGET.TXT`として記録される)。原因は未特定(SMBクライアント側のキャッシュや
  8.3形式関連の可能性があるが未調査)

## CSV出力の実機検証結果(2026-07-22、マージ後)

もともとCSV整形は別ツール(LogFormatter)だったが、依頼によりToルE本体に統合した(生成完了後の
ログファイルを読み込んで変換するのではなく、イベント発生と同時にその場でCSV行を書き出す方式に
変更したため、列を動的にはできず固定にした。詳細は前述の「列を固定にした理由」参照)。

同一マシンから実ホスト名経由で共有(`\\WIN-DESK-2022\TestShare2`)にアクセスして動作確認した
ところ、今回は`5140`(共有接続)も含めて正しく記録され、以下の解釈列が実データで正しく機能する
ことを確認した。

```
AccessMask=0x100081 → AccessMask解釈=ListDirectory, ReadAttributes, Synchronize
AccessList=%%1541 %%4416 %%4423 → AccessList解釈=SYNCHRONIZE(%%1541), ReadData(ListDirectory)(%%4416), ReadAttributes(%%4423)
AccessReason=%%1541: %%1801 D:(A;;FA;;;WD) ... → AccessReason解釈=SYNCHRONIZE: 許可 D:(A;;FA;;;WD) / ReadData(ListDirectory): 許可 D:(A;;FA;;;WD) / ReadAttributes: 許可 D:(A;;FA;;;WD)
```

複数物理行にまたがっていた`AccessList`/`AccessReason`も、CSVでは正しく1セルにまとまることも
確認済み。「その他」列は今回確認できた5140/5145の範囲では常に空(全フィールドが固定列で
カバーできている)。5142/5143/5144は未検証のため、想定外のフィールドが来た場合にこの列が
機能するかどうかは今後の検証課題。
