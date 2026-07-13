# ToolF-SmbClientEventLogProbe

「アウトバウンドSMB」(このPCのユーザーが他マシンの共有フォルダにアクセスした操作)を記録する
プローブ。**実装方式①: イベントログ購読方式**。`Microsoft-Windows-SmbClient` 系のイベント
チャンネルを `EventLogWatcher` で購読する(ToolC/E と同じ手法)。

実装方式②(ETW直接購読方式)は [ToolG-SmbClientEtwProbe](../ToolG-SmbClientEtwProbe) を参照。
アウトバウンドSMBはMyLogger本体・従来のプローブ群のどこにも実装がなかった領域のため、どちらの
方式がより有用か(取れる情報の質・量、負荷、実装の単純さ)を比較する目的で、あえて別ツールとして
分けている。

## 仕組み

`Microsoft-Windows-SMBClient`(SMBクライアント機能のプロバイダー)が書き込む複数のイベント
チャンネルのうち、既定で以下の4つを購読する(`channels.txt` で変更可能):

| チャンネル | 実機で確認できた内容 |
|---|---|
| `Microsoft-Windows-SMBClient/Operational` | サーバー名・マルチチャンネル対応状況など、接続の一般情報 |
| `Microsoft-Windows-SmbClient/Connectivity` | 接続の確立・切断(サーバー名・IP・ポート・トランスポート種別) |
| `Microsoft-Windows-SmbClient/Security` | 認証成功/失敗(サーバー名・プリンシパル名・SecurityStatus・LogonId) |
| `Microsoft-Windows-SmbClient/Audit` | (下記「未検証」を参照) |

## 【重要】実機で確認できたこと・できなかったこと

- 対象4チャンネルとも、この開発機では**既定で有効**だった(`wevtutil gl` で確認済み)
- `Operational`/`Connectivity`/`Security` には実際の過去イベントが記録されていた。ただし内容は
  **接続・認証レベル**の情報(サーバー名・IPアドレス・ポート・LogonId・認証エラーコード等)で、
  **アクセスしたファイル名は含まれていなかった**
- `Audit` チャンネル(名前からしてファイル単位の操作を記録する用途と推測される)は、この開発機では
  レコード数**0件**だった。これは外向きの実SMBファイルアクセスがこのマシンで発生していないためで、
  チャンネル自体が機能しないと確認したわけではない。**ファイル単位の操作が実際に記録されるかは、
  他マシンの共有への実アクセスで別途検証が必要**
- 上記より、本ツールは「イベントIDを絞り込まず、対象チャンネルの全イベントをそのまま記録する」
  探索的な作りにしてある。有用なイベントIDが判明したら、後で絞り込みを追加すればよい

## 使い方

```powershell
# 管理者権限のPowerShellで実行(Security系チャンネルの購読・チャンネル有効化に必要)

# ソースから実行
dotnet run --project probe-tools\ToolF-SmbClientEventLogProbe -- [ログファイル]

# 自己完結ビルド済みexeの場合
.\SmbClientEventLogProbe.exe [ログファイル]
```

- `[ログファイル]` 省略時は実行ファイルと同じフォルダの `smbclienteventlogprobe.log`
- 起動時、対象チャンネルが無効化されていれば自動的に有効化を試みる(冪等)
- 起動後、別マシンの共有フォルダにファイル操作(開く・作成・コピー等)を行い、**Ctrl+C** で停止する
  とログが確定する

## 設定ファイル: `channels.txt`

```
# 1行1チャンネル名。# で始まる行はコメント。
# ここに列挙したイベントチャンネルを購読する(存在しない/無効な場合は起動時にエラー表示して継続する)。
Microsoft-Windows-SMBClient/Operational
Microsoft-Windows-SmbClient/Audit
Microsoft-Windows-SmbClient/Connectivity
Microsoft-Windows-SmbClient/Security
```

他のチャンネル(例: `Microsoft-Windows-SmbClient/Diagnostic`。既定で無効・非常に詳細で負荷が高い
可能性がある)を試したい場合はここに追記する。

## 記録されるログの内容

1行1イベント。どのチャンネル・どのイベントIDかを含め、該当イベントの全フィールドをそのまま
`Key=Value` で並べて出力する。

```
[14:32:01.100000] Channel=Microsoft-Windows-SmbClient/Security EventID=31001 Level=エラー Reason=10 Status=3221225581 SecurityStatus=2148074254 LogonId=10159942125 ServerNameLength=14 ServerName=\192.168.0.175 PrincipalNameLength=18 PrincipalName=cifs/192.168.0.175 UserNameLength=0 UserName= EtwTime=14:32:01.098765
[14:32:05.200000] Channel=Microsoft-Windows-SMBClient/Operational EventID=30904 Level=警告 ServerNameLength=14 ServerName=\192.168.0.175 EtwTime=14:32:05.190000
```
