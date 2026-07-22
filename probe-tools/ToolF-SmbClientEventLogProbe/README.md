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
- 上記より、本ツールは「イベントIDを絞り込まず、対象チャンネルの全イベントをそのまま記録する」
  探索的な作りにしてある。有用なイベントIDが判明したら、後で絞り込みを追加すればよい

## 実機での検証結果(2026-07-22): ファイル単位の操作は記録できないことを確定

別マシン(`192.168.1.20`)の共有フォルダに対して接続〜認証〜一通りのファイル操作(新規作成/
書き込み/削除/コピー/移動/リネーム)〜切断までを実際に行い、4チャンネル合計30件のイベントを
採取した。**ファイル操作に対応するイベントは1件も記録されなかった。** 生ログは
[`SAMPLELOG/smbclienttest.log`](SAMPLELOG/smbclienttest.log)に同梱している。

| EventID | チャンネル | 件数 | 内容 |
|---|---|---|---|
| 30830 | Connectivity | 26件(うち24件はテスト対象と無関係な別サーバーへの定期接続確認ノイズ) | 接続先の到達性・エンドポイント情報の通知 |
| 30833 | Connectivity | 2件 | ツリー接続(`\\192.168.1.20\IPC$`→`\\192.168.1.20\share`の順で発生) |
| 31001 | Security | 1件 | 認証関連のエラーステータス(後述) |
| (`Operational`) | Operational | 0件 | - |
| (`Audit`) | Audit | 0件 | - |

### 分かったこと

- **`Audit`チャンネルは実アクセスがあっても0件だった。** 前回(このマシンで外向き実アクセスが
  無かった時点)は「未検証」としていたが、今回は接続から切断まで一通りの操作を行った上で0件
  だったため、**このチャンネルはファイル単位の操作を記録しない、と結論してよい**
- **記録されるのは「どの共有に接続したか」までである。** `EventID=30833`で`ServerName`
  (`\\<サーバー>\<共有名>`)・`SessionId`・`TreeId`が取れ、接続の事実(いつ・どの共有へ)は
  追える。ただし管理共有`IPC$`への接続も同列に記録されるため、実共有への接続と区別するには
  フィルタが必要(ToolEで`IPC$`を除外していたのと同じ理由)
- **切断(セッションログオフ/ツリー切断)に相当するイベントは、明示的に切断した後も一切記録
  されなかった。** 4チャンネルのどこにも切断を示すイベントが現れない
- **テスト対象(`192.168.1.20`)とは無関係な、常時接続中の別サーバー(`192.168.0.175`)への
  定期接続確認イベントが、全30件中26件を占めるノイズだった。** 約5秒おきに`EventID=30830`が
  発生し続けており、実際のテスト対象に関する信号(接続確認1件+認証1件+ツリー接続2件=4件)を
  大きく上回っていた。この開発機特有の既知ノイズ源として注意が必要
- **`EventID=31001`(Security、Level=エラー)は、必ずしも操作失敗を意味しない。** この直後に
  正常なツリー接続・アクセスが成立しているため、初回のセッションセットアップ試行が想定内に
  失敗し再試行で成功した挙動と考えられるが、`SecurityStatus`の正確な意味(具体的なエラー内容)
  は未特定
- **ユーザー名は記録されなかった。** `EventID=31001`の`UserName`は`UserNameLength=0`で空欄
  だった。`PrincipalName=cifs/192.168.1.20`は**接続先(サーバー)のSPNであって、接続元ユーザーの
  識別子ではない**(名前だけ見ると「誰か」を表しているように誤解しやすいので注意)。代わりに
  `LogonId`(認証イベント)や`SessionId`(ツリー接続イベント)という数値IDは記録されているが、
  これを実際のWindowsユーザーアカウント名に変換できるかは未検証(ToolCのPID↔ユーザー名突合と
  同種の手法が使える可能性はあるが、仮説の域を出ない)

### 結局、本ツールのログだけから確実に言えること

- 「いつ・どのIPアドレス・どの共有名に接続したか」(接続の事実)
- それ以上(誰が・何のファイルを・どう操作したか)は分からない。「誰が」はユーザー名としては
  取れず、`LogonId`という未検証の手がかりが残るのみ
- **結論: ToolFのイベントログ購読方式(実装方式①)は、接続・認証・共有名レベルの可視性は
  あるが、ファイル単位の操作の追跡には使えないことが確定した。** 同じ検証を
  [ToolG-SmbClientEtwProbe](../ToolG-SmbClientEtwProbe)(ETW直接購読方式)でも行い、より
  低レベルなETWであれば操作内容まで拾えるかを比較する

## 使い方

```powershell
# 管理者権限のPowerShellで実行(Security系チャンネルの購読・チャンネル有効化に必要)

# ソースから実行
dotnet run --project probe-tools\ToolF-SmbClientEventLogProbe -- [ログファイル]

# 同梱のビルド済みexe(.NETランタイム不要)
.\dist\SmbClientEventLogProbe.exe [ログファイル]
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
