# ToolC-ProcessAuditProbe

Windows セキュリティ監査ログの「プロセス作成」イベント(イベントID 4688)を記録する最小プローブ。

これは「テーブルB」(プロセス生成: PID→ユーザー名の対応)専用のツール。ファイルイベント
(テーブルA相当)は [ToolB-EtwFileProbe](../ToolB-EtwFileProbe) が独立して記録する。**このツールの
中では両者を突き合わせない**(突合はテーブルA・Bを見比べて後日別途行う)。

## 仕組み

サードパーティ製ツール(Sysmon等)は不要で、Windows標準のセキュリティ監査ログのみを使う。
「プロセス作成」監査サブカテゴリ(GUID `{0CCE922B-69AE-11D9-BED3-505054503030}`)を `auditpol.exe`
で有効化すると、プロセスが生成されるたびにイベント4688がSecurityログに記録される
(`NewProcessId`・`NewProcessName`・`SubjectUserName`等を含む)。`System.Diagnostics.Eventing.Reader.
EventLogWatcher` でSecurityログを購読する仕組みは、MyLogger本体の `SmbAuditMonitor`/`LogonMonitor` が
別のイベントID(5140番台・4624番台)を購読しているのと同じ方式。

- ETWのFileIOイベント自体にはユーザー/SID情報が無いため(リフレクションで確認済み)、ETWだけでは
  「誰が」操作したかは分からない。本ツールはその欠落を補うための独立した記録源。
- WMI(`Win32_Process.GetOwner`)によるリアルタイムPID→ユーザー名解決は、プロセスが早期終了すると
  失敗する・ハングしうるなど不安定なことが実機検証で判明したため、代替として4688監査を採用した。
  4688はプロセス生成の**瞬間**に記録されるためこの問題が起きない。

## 使い方

```powershell
# 管理者権限のPowerShellで実行(監査ポリシーの設定・セキュリティログの購読に必要)

# ソースから実行
dotnet run --project probe-tools\ToolC-ProcessAuditProbe -- [ログファイル]

# 自己完結ビルド済みexeの場合
.\ProcessAuditProbe.exe [ログファイル]
```

- `[ログファイル]` 省略時は実行ファイルと同じフォルダの `processauditprobe.log`
- 起動時に「プロセス作成」監査サブカテゴリを自動的に有効化する(既に有効なら何もしない、冪等)
- 起動後、対象マシン上で任意のプロセスを起動し、**Ctrl+C** で停止するとログが確定する

## 記録されるログの内容

1行1イベント、`[HH:mm:ss.ffffff]`(マイクロ秒精度)のタイムスタンプ付き。

```
[14:32:01.100000] ProcessCreate NewPid=0x3039 User=CONTOSO\alice NewProcessName=C:\Windows\System32\notepad.exe CreatorPid=0x1a2b CommandLine= EtwTime=14:32:01.098765
```

- `NewPid`: 生成されたプロセスのPID(16進表記、イベントログの原表記のまま)
- `User`: `ドメイン\ユーザー名`(`SubjectDomainName`\`SubjectUserName`)
- `NewProcessName`: 実行ファイルのフルパス
- `CreatorPid`: 親プロセスのPID(16進表記)
- `CommandLine`: コマンドライン引数(既定のOS設定では取得されないことが多い。取得するには
  グループポリシー「プロセス作成イベントにコマンドラインを含める」の有効化が別途必要)
