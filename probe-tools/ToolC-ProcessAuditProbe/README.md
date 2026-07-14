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

## 監査ポリシーの設定について(自動設定されます)

**このツールは起動時に「プロセス作成」監査サブカテゴリを自動的に有効化します。事前に手動で
`auditpol` を実行しておく必要はありません。** 起動のたびに以下のコマンドを内部的に実行しており、
既に有効な場合は何もしない(冪等)ため、複数回起動しても問題ありません。

```powershell
auditpol.exe /set /subcategory:"{0CCE922B-69AE-11D9-BED3-505054503030}" /success:enable
```

現在の設定状態を手動で確認したい場合:

```powershell
auditpol.exe /get /subcategory:"プロセスの作成"
# または英語環境の場合
auditpol.exe /get /subcategory:"Process Creation"
```

**注意**: この設定はツール終了後も残る、マシン全体(監査ポリシー)に対する変更です。ツールが自動で
元に戻すことはありません。検証終了後に無効化したい場合は、手動で以下を実行してください
(実施済みの評価に影響しないよう、通常はそのままにしておいて問題ありません):

```powershell
auditpol.exe /set /subcategory:"{0CCE922B-69AE-11D9-BED3-505054503030}" /success:disable
```

コマンドライン引数(`CommandLine`)まで記録したい場合は、上記とは別に、グループポリシーで
「監査ポリシーの構成でプロセス作成イベントにコマンドラインを含める」を有効化する必要があります
(このツールは自動設定しません。`gpedit.msc` →
コンピューターの構成 → 管理用テンプレート → システム → 監査プロセス作成)。

## 使い方

```powershell
# 管理者権限のPowerShellで実行(監査ポリシーの設定・セキュリティログの購読に必要)

# ソースから実行
dotnet run --project probe-tools\ToolC-ProcessAuditProbe -- [ログファイル]

# 同梱のビルド済みexe(.NETランタイム不要)
.\dist\ProcessAuditProbe.exe [ログファイル]
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
