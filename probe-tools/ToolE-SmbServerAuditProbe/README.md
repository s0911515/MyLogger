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

## 記録されるログの内容

1行1イベント、`[HH:mm:ss.ffffff]`(マイクロ秒精度)のタイムスタンプ付き。該当イベントの全フィールド
をそのまま `Key=Value` で並べて出力する(特定フィールドだけを選んで加工することはしない)。

```
[14:32:01.100000] ShareConnected(Event5140) SubjectUserSid=S-1-5-21-... SubjectUserName=alice SubjectDomainName=CONTOSO SubjectLogonId=0x1a2b3c ObjectType=File ShareName=\\*\TestShare ShareLocalPath=\??\D:\tmp\SmbTestShare IpAddress=192.168.1.50 IpPort=51234 AccessMask=0x1 EtwTime=14:32:01.098765
[14:32:01.250000] ShareFileAccess(Event5145) SubjectUserSid=S-1-5-21-... SubjectUserName=alice SubjectDomainName=CONTOSO SubjectLogonId=0x1a2b3c ObjectType=File ShareName=\\*\TestShare ShareLocalPath=\??\D:\tmp\SmbTestShare RelativeTargetName=copy_source.txt AccessMask=0x1 AccessList=%%4416 EtwTime=14:32:01.249000
```
