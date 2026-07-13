# ToolG-SmbClientEtwProbe

「アウトバウンドSMB」(このPCのユーザーが他マシンの共有フォルダにアクセスした操作)を記録する
プローブ。**実装方式②: ETW直接購読方式**。`Microsoft-Windows-SMBClient` ETWプロバイダー
(GUID `{988c59c5-0a1c-45b6-a555-0c62276e327d}`、この開発機の `logman query providers` で実在を
確認済み)を、ToolB(ローカルFileIO)と同じ `TraceEvent` ライブラリで直接購読する。

実装方式①(イベントログ購読方式)は [ToolF-SmbClientEventLogProbe](../ToolF-SmbClientEventLogProbe)
を参照。2方式を比較できるよう、あえて別ツールとして分けている。

## ToolFとの違い(実装方式の比較)

| | ToolF(イベントログ購読) | ToolG(ETW直接購読、本ツール) |
|---|---|---|
| 取得元 | Windowsが既にイベントログへ整形・保存した後のデータ | ETWバッファから直接、リアルタイムに近い生イベント |
| 過去ログの参照 | 可能(`Get-WinEvent`等で遡って見られる、実際に本ツールもチャンネルの既存ログを起点に購読) | 不可(セッション開始後のイベントのみ) |
| 管理者権限 | 必要(Security系チャンネル購読のため) | 必要(ETWセッション作成のため) |
| 実装の複雑さ | シンプル(`EventLogWatcher`) | ToolBと同様の構成(`TraceEventSession`) |
| 実機で見えたイベント種別 | 接続・認証レベル(下記ToolF README参照) | 下記「実機で確認できたこと」参照 |

## 実機で確認できたこと・できなかったこと

起動直後、実際に以下のような**設定スナップショット系イベント**(`SmbRegistryKey`、イベントID
30410)が多数記録されることを確認した(このマシンでの実測値):

```
Event=SmbRegistryKey ID=30410 PID=60172 TID=86264 Opcode=情報 RegName=RequireSecuritySignature RegValue=0 EtwTime=08:10:01.979783
Event=SmbRegistryKey ID=30410 PID=60172 TID=86264 Opcode=情報 RegName=EnableSMBQUIC RegValue=1 EtwTime=08:10:01.979788
```

これはSMBクライアントの構成レジストリ値をセッション開始時にダンプするイベントで、**ファイル単位の
操作(open/read/write等)ではない**。プロバイダーが実際に稼働していること・ETW購読の仕組み自体は
機能することは実証できたが、**ファイル単位の操作イベントが実際に出るかどうかは、外向きの実SMB
ファイルアクセスが必要なため、このマシンでは未検証**。ToolFと同様、イベントIDを絞り込まず全イベント
を記録する探索的な作りにしている。

## 使い方

```powershell
# 管理者権限のPowerShellで実行(ETWプロバイダーの有効化に必要)

# ソースから実行
dotnet run --project probe-tools\ToolG-SmbClientEtwProbe -- [ログファイル]

# 自己完結ビルド済みexeの場合
.\SmbClientEtwProbe.exe [ログファイル]
```

- `[ログファイル]` 省略時は実行ファイルと同じフォルダの `smbclientetwprobe.log`
- 起動後、別マシンの共有フォルダにファイル操作(開く・作成・コピー等)を行い、**Ctrl+C** で停止する
  とログが確定する
- 起動直後に `SmbRegistryKey` イベントが十数件出るのは正常な挙動(構成スナップショット。ノイズでは
  ないが、ファイル操作の証跡でもない)

## 記録されるログの内容

1行1イベント。イベント名・ID・PID/TID・Opcodeに加え、そのイベントが持つ全ペイロードフィールドを
そのまま `Key=Value` で並べて出力する(特定フィールドだけを選んで加工することはしない)。

```
[14:32:01.100000] Event=SmbRegistryKey ID=30410 PID=1234 TID=5678 Opcode=情報 RegName=EnableSMBQUIC RegValue=1 EtwTime=14:32:01.098765
```

終了時に `EventsLost`(ETWバッファ溢れによる取りこぼし件数)も出力される。
