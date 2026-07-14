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

コマンドライン引数(`CommandLine`)を記録するには、一般に上記とは別にグループポリシー
「監査ポリシーの構成でプロセス作成イベントにコマンドラインを含める」の有効化が必要とされている
(このツールは自動設定しない。`gpedit.msc` → コンピューターの構成 → 管理用テンプレート →
システム → 監査プロセス作成)。**ただし実機検証では、このGPOを操作した記憶が無いにもかかわらず
`CommandLine`が全イベントで取得できた**(下記「ログサンプル」参照)。既定で有効なWindowsのバージョン・
エディションがある可能性があるため、「既定では取得できない」と決めつけずマシンごとに実際のログで
確認すること。

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
- `User`: `ドメイン\ユーザー名`(`SubjectDomainName`\`SubjectUserName`)。**実ユーザーとは限らず、
  `WORKGROUP\<コンピューター名>$`のようなマシンアカウントのこともある**(下記「重要な発見」参照)
- `NewProcessName`: 実行ファイルのフルパス
- `CreatorPid`: 親プロセスのPID(16進表記)。実測では複数段の親子関係(シェル→シェル→コマンド)も
  正しく追跡できた
- `CommandLine`: コマンドライン引数。GPOが必要とされる場合があるが、実機では未設定のまま
  全イベントで取得できた(下記「ログサンプル」参照)

## ログサンプル(実測、2026-07-15)

管理者PowerShellでToolCを起動した状態で、①`notepad.exe`起動、②`cmd /c dir`実行、
③`Start-Process powershell -ArgumentList '-Command "Write-Host hello"'`実行、④Notepadを閉じる、
という操作を行って記録した実際のログ(無加工・全件、読みやすさのため改行は追加していない)。

```
[08:19:03.419193] === ProcessAuditProbe(ツールC: プロセス作成監査 イベント4688) 開始 ログ=D:\tmp\toolC-sample.log ===
[08:19:03.448275] 監視開始。プロセス終了(Ctrl+C / kill)まで待機します。
[08:19:04.301634] ProcessCreate NewPid=0x243d8 User=WIN-DESK-2022\s0911 NewProcessName=D:\DEV\MyLogger\probe-tools\ToolC-ProcessAuditProbe\dist\ProcessAuditProbe.exe CreatorPid=0x1d8f0 CommandLine="D:\DEV\MyLogger\probe-tools\ToolC-ProcessAuditProbe\dist\ProcessAuditProbe.exe" D:\tmp\toolC-sample.log EtwTime=08:19:03.277953
[08:19:04.302469] ProcessCreate NewPid=0x1bee0 User=WIN-DESK-2022\s0911 NewProcessName=C:\Windows\System32\auditpol.exe CreatorPid=0x243d8 CommandLine="auditpol.exe" /set /subcategory:"{0CCE922B-69AE-11D9-BED3-505054503030}" /success:enable EtwTime=08:19:03.358277
[08:19:04.303297] ProcessCreate NewPid=0x7fec User=WIN-DESK-2022\s0911 NewProcessName=C:\Windows\System32\conhost.exe CreatorPid=0x1bee0 CommandLine=\??\C:\WINDOWS\system32\conhost.exe 0xffffffff -ForceV1 EtwTime=08:19:03.373053
[08:19:19.604557] ProcessCreate NewPid=0x43b0 User=WORKGROUP\WIN-DESK-2022$ NewProcessName=C:\Windows\System32\smartscreen.exe CreatorPid=0x698 CommandLine=C:\Windows\System32\smartscreen.exe -Embedding EtwTime=08:19:18.590237
[08:19:19.604898] ProcessCreate NewPid=0x263b0 User=WIN-DESK-2022\s0911 NewProcessName=C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2605.29.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe CreatorPid=0x4e48 CommandLine="C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2605.29.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe" EtwTime=08:19:18.655875
[08:19:34.610868] ProcessCreate NewPid=0x716c User=WIN-DESK-2022\s0911 NewProcessName=C:\Windows\System32\cmd.exe CreatorPid=0x4e48 CommandLine="C:\WINDOWS\system32\cmd.exe" /c dir EtwTime=08:19:33.603300
[08:19:34.611273] ProcessCreate NewPid=0x1c5dc User=WIN-DESK-2022\s0911 NewProcessName=C:\Windows\System32\conhost.exe CreatorPid=0x716c CommandLine=\??\C:\WINDOWS\system32\conhost.exe 0xffffffff -ForceV1 EtwTime=08:19:33.628424
[08:19:50.496223] ProcessCreate NewPid=0x177f8 User=WIN-DESK-2022\s0911 NewProcessName=C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe CreatorPid=0x4e48 CommandLine="C:\WINDOWS\system32\WindowsPowerShell\v1.0\PowerShell.exe" EtwTime=08:19:49.487142
[08:19:53.297513] ProcessCreate NewPid=0x17644 User=WIN-DESK-2022\s0911 NewProcessName=C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe CreatorPid=0x177f8 CommandLine="C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -Command "Write-Host hello" EtwTime=08:19:52.292574
[08:20:04.214962] === ProcessAuditProbe 終了 (出力イベント数=19) ===
```

(上記はToolC自身の起動ノイズ・`conhost.exe`/`WindowsTerminal.exe`等の付随プロセス・偶発的な
バックグラウンドプロセス[`chrome.exe`のレンダラープロセス等]を一部省略した抜粋。全19件の生ログは
実際にはこれらも含む)

### 操作と記録されたイベントの対応

| # | 操作 | 記録されたイベント | 所見 |
|---|---|---|---|
| 1 | `notepad.exe`起動 | `NewProcessName=...WindowsApps\Microsoft.WindowsNotepad_...\Notepad\Notepad.exe` | **classicな`System32\notepad.exe`ではなく、Store配布のUWPアプリに解決された**(Windows 11)。パスベースの検知ルールは要注意 |
| 2 | `cmd /c dir` | `NewProcessName=cmd.exe CommandLine="...cmd.exe" /c dir` | `CommandLine`に引数まで正しく記録されている |
| 3 | `Start-Process powershell -ArgumentList '-Command "Write-Host hello"'` | `NewPid=0x177f8`(引数なしpowershell、`CreatorPid=0x4e48`)→`NewPid=0x17644`(`-Command "Write-Host hello"`、`CreatorPid=0x177f8`) | **`CreatorPid`で2段階の親子関係を正しく追跡できた。** ユーザーが先に新しいPowerShellウィンドウを開き、その中でコマンドを実行したという操作の流れがそのまま連鎖として再現された |
| 4 | Notepadを閉じる | (該当イベントなし) | **予想通り、プロセス終了はこのツールでは記録されない**(4688はプロセス「作成」のみ。終了は別イベントID4689で、本ツールは購読していない) |

### 重要な発見

- **`CommandLine`は、事前にGPOを設定した記憶が無いにもかかわらず、実機では全イベントで取得できた。**
  「既定では取得されないことが多い」という当初の想定は誤りだった可能性がある。少なくともこの検証機
  (Windows 11)では有効になっていた。他マシンで検証する場合は、決めつけずに実際のログで確認すること
- **`User`が実ユーザーではなく、`WORKGROUP\<コンピューター名>$`というマシンアカウントになることが
  ある。** `smartscreen.exe`・`svchost.exe`に加え、**実際にはユーザーが開いたはずの`Windows Terminal`
  関連プロセス(`OpenConsole.exe`・`WindowsTerminal.exe`)もマシンアカウント扱いだった**(パッケージ化
  アプリのブローカー/アクティベーション機構が関与しているためと考えられる)。4688の`User`フィールド
  だけでは「本当に人間が操作したか」を判別できないケースがある
- **`CreatorPid`による複数段の親子関係の追跡は正しく機能する。** #3で確認した通り、シェル→シェル→
  コマンドという連鎖も途切れずに追える
- 稼働中のマシンでは、テスト操作と無関係な背景プロセス(`Chrome`のレンダラープロセス生成等)も
  同時に大量に記録される。実運用では評価対象プロセスの絞り込み(除外リスト等)が必要になりそう
