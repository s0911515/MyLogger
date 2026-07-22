# 検証ツール一式(実機評価用)

MyLogger のファイル操作監視における「何が」「誰が」を、どの仕組みでどこまで捕捉できるかを実機で
確認するための、独立した最小ツール群です。**MyLogger 本体には依存していません。**

各ツールは**それぞれ独立してログファイルに記録するだけ**で、ツール間の突合(誰が操作したかの結合)は
一切行いません。突合は全ツールでの記録が揃ってから、別途行います(詳しくは各ツールのREADME参照)。

## ローカルアクセス(このPC上でのファイル操作)

| ツール | 何を記録するか | 仕組み | ドキュメント |
|---|---|---|---|
| **ToolA-FsWatcherProbe** | ファイルの作成/変更/削除/リネーム(パスのみ、誰が操作したかは分からない) | `System.IO.FileSystemWatcher` | [README](ToolA-FsWatcherProbe/README.md) |
| **ToolB-EtwFileProbe** | ファイルI/O(作成/読み書き/フラッシュ/リネーム/削除)。プロセスID・プロセス名・詳細フラグ付き | ETW カーネル FileIO プロバイダー | [README](ToolB-EtwFileProbe/README.md) |
| **ToolC-ProcessAuditProbe** | プロセスが生成された瞬間の PID→ユーザー名の対応 | Windowsセキュリティ監査ログ(イベント4688、OS標準機能) | [README](ToolC-ProcessAuditProbe/README.md) |
| **ToolD-Sysmon** | ファイル作成/コピー・完全削除等の比較用参考記録 | Sysinternals Sysmon(カーネルミニフィルタドライバー) | [README](ToolD-Sysmon/README.md) |
| **ToolH-RecycleBinProbe** | ゴミ箱に格納されたファイルの元のパス・元のサイズ・削除日時 | `System.IO.FileSystemWatcher` + `$I`メタデータファイルのデコード | [README](ToolH-RecycleBinProbe/README.md) |
| **ToolI-SaclProbe** | 指定フォルダにSACL(監査ACE)を設定し、以後のアクセス(ハンドル要求/アクセス試行/削除/権限変更/SACL変更自体)を記録。ユーザー名・プロセス名・AccessMask付き | Windowsセキュリティ監査ログ(イベント4656/4658/4660/4663/4670/4907、OS標準機能) | [README](ToolI-SaclProbe/README.md) |

## ネットワークアクセス(SMB経由のファイル操作)

| ツール | 方向 | 何を記録するか | 仕組み | ドキュメント |
|---|---|---|---|---|
| **ToolE-SmbServerAuditProbe** | インバウンド(他PC→このPCの共有) | 共有への接続・共有内ファイルアクセス。ユーザー名・接続元IP付き | Windowsセキュリティ監査ログ(イベント5140/5142-5145)。MyLogger本体の`SmbAuditMonitor`と同方式 | [README](ToolE-SmbServerAuditProbe/README.md) |
| **ToolF-SmbClientEventLogProbe** | アウトバウンド(このPC→他PCの共有) | SMBクライアントの接続・認証イベント(実装方式①: イベントログ購読) | `Microsoft-Windows-SmbClient` 系イベントチャンネル | [README](ToolF-SmbClientEventLogProbe/README.md) |
| **ToolG-SmbClientEtwProbe** | アウトバウンド(このPC→他PCの共有) | SMBクライアントの生イベント(実装方式②: ETW直接購読) | `Microsoft-Windows-SMBClient` ETWプロバイダー | [README](ToolG-SmbClientEtwProbe/README.md) |

**ToolF(イベントログ購読方式)は、実機検証の結果、ファイル単位の操作を記録できないことが
確定しました。** 別マシンの共有へ接続〜一通りのファイル操作〜切断まで行っても、接続・認証・
共有名レベルの情報(EventID 30830/30833/31001)しか記録されず、`Audit`チャンネルを含め
ファイル操作に対応するイベントは1件も観測されませんでした(詳細は
[ToolFのREADME](ToolF-SmbClientEventLogProbe/README.md#実機での検証結果2026-07-22-ファイル単位の操作は記録できないことを確定)
参照)。**ToolG(ETW直接購読方式)がファイル単位の操作まで取れるかは検証待ち**です。ToolFと
ToolGは実装方式の比較のためあえて別々に用意しています。

## 全ツールの検証結果まとめ: 操作種別ごとに何がどこまで分かるか

ToolA〜Iそれぞれの実機検証結果を踏まえ、操作種別ごとに「どのツールで何が分かるか」「組み合わせると
どこまでトレースできるか」を整理する(根拠の詳細は各ツールのREADMEを参照)。

### 操作種別 × ツール対応表(ローカル)

| 操作 | ToolA(FSW) | ToolB(ETW) | ToolD(Sysmon) | ToolI(SACL) |
|---|---|---|---|---|
| 新規作成 | ○ `Created`(誰か不明) | ○ `Create(CREATE_NEW)`+PID | ○ `FileCreate`+ユーザー名 | ○ アクセス許可+ユーザー名 |
| 上書き保存 | △ `Changed`のみ | ◎ Read/Writeのオフセット・バイト数まで | △〜✕(一時ファイル置換方式のアプリは検知漏れの可能性) | ○ Writeアクセス+ユーザー名 |
| コピー | △ `Created`+`Changed`×2(推測レベル) | ○ 先への`Create`+`SetInfo`(小サイズは`Write`が出ないことも) | ○ `FileCreate`で検知 | ○「推定コピー」ヒューリスティック |
| 移動(フォルダ間) | **◎ `Deleted`+`Created`、新パスが直接分かる** | △ `Rename`(新パス不明、リネームと区別不可) | ✕ 検知不可(実体の作成/削除を伴わないため) | ✕ 新パス不明 |
| リネーム(同一フォルダ) | **◎ `Renamed`(Old/NewFullPath)** | △ `Rename`(新パス不明、移動と区別不可) | ✕ 検知不可 | ✕ 新パス不明 |
| 通常削除 | ○(ドライブ全体監視+ToolH併用で元パスまで) | △ `Delete`×2+`Rename`(確定シグネチャとしては弱い) | ○ `FileDelete`+`getLog.ps1`で元パス復元 | ○ `Delete`+EventID4660で確定度が高い |
| 完全削除 | ○(`$RECYCLE.BIN`に痕跡皆無で識別可能) | **△〜✕ 呼び出し元(Explorer/rm等)次第で削除イベント自体が一切残らないことがある** | ○ `FileDelete`で確定 | ○ `Delete`はあるが通常削除と区別不可(4660も単体ファイルで出ないことがある) |

(ToolC はファイルI/Oを直接見ておらず、上記の「誰が」を補完する別系統。ToolHは削除専用のため
「通常削除」行にのみ登場)

### 操作種別 × ツール対応表(ネットワーク/SMB)

| 操作 | ToolE(インバウンド、確定) | ToolF(アウトバウンド、確定=不可) | ToolG(アウトバウンド、未検証) |
|---|---|---|---|
| 新規作成/上書き | ○ ユーザー名+IP付き | ✕ | ? |
| コピー | ○ 元/先が別イベント、時刻相関で対応付け | ✕ | ? |
| 移動/リネーム | ○ 旧名→新名の追従アクセスで対応付け可能 | ✕ | ? |
| 通常削除/完全削除 | ○(ただし区別に意味が無い。SMB経由は常に完全削除) | ✕ | ? |

### 「誰が」を埋める情報源

- **ローカル**: ToolC(PID→ユーザー名、ETWとの時刻+PID相関が必要・この一式では未検証の仮説) /
  ToolD・ToolI(相関不要、直接ユーザー名を記録)
- **ネットワーク**: ToolE(直接ユーザー名+IP) / ToolF・ToolG(現状取れない・未確定)

### 推奨組み合わせ(ローカルで最大限トレースするなら)

1. **ToolI(SACL)を主軸に**: ユーザー名・アクセス種別・削除確定(4660)まで単独のカバー範囲が広い
2. **移動/リネームの新旧パスだけはToolAでしか取れない**ので必ず併用
3. **ゴミ箱経由の元パス復元**にはToolH(またはSysmonの`getLog.ps1`)が必要
4. **実際に何バイト読み書きしたか**という一段深い証跡が欲しい場合のみToolB(ETW)を追加

### 組み合わせても埋まらない穴

- 完全削除は呼び出し元アプリ次第で痕跡パターンが変わり、ETWレベルで一意に検出できるシグネチャが
  無い(ToolBの結論)
- Sysmon(`FileCreate`/`FileDelete`限定の設定)は、一時ファイル→リネーム置換方式で上書き保存する
  アプリを検知できない可能性がある
- **フォルダ単位の操作は、ToolI/ToolEでは網羅的に実機検証済みだが、ToolA/B/C/D(ゴミ箱以外)では
  同等の網羅的テストをまだ行っていない**(今後の検証候補)

### 特別な常駐ツール(Sysmon)を使わない場合、どこまで分かるか

ToolD(Sysmon)だけが「追加のカーネルドライバー/常駐サービスのインストール」を必要とするツールで、
それ以外(ToolA/B/C/H/I/E/F/G)はいずれもWindows標準機能(FileSystemWatcher・ETW・セキュリティ監査
ログ)の範囲で完結し、追加インストールは不要である。**Sysmonを使わない前提で、ToolA・ToolC・ToolH・
ToolI(ローカル)とToolE(インバウンドSMB)を組み合わせた場合に何がどこまで分かるか**を整理する。

| 操作 | 組み合わせ | 分かること |
|---|---|---|
| 新規作成 | ToolI(SACL) | 誰が・いつ・どのファイルを作成したか |
| 上書き保存 | ToolI(SACL) | 誰が・いつ・どのファイルに書き込んだか |
| コピー | ToolI(SACL)の「推定コピー」ヒューリスティック | 誰が・コピー元/先パス(時刻+ファイル名相関による推測) |
| 移動(フォルダ間) | ToolI(SACL)+**ToolA(FSW)** | 誰が(SACL)+旧パス・新パス両方(FSWの`Deleted`+`Created`) |
| リネーム(同一フォルダ) | ToolI(SACL)+**ToolA(FSW)** | 誰が(SACL)+旧名・新名両方(FSWの`Renamed`イベント) |
| 通常削除 | ToolI(SACL)+**ToolH** | 誰が・削除確定(4660)・削除前の元パス/元サイズ/削除日時 |
| 完全削除 | ToolI(SACL)+ToolA(ドライブ全体監視で`$RECYCLE.BIN`に痕跡が無いことを確認)、またはToolHで「解決されない」ことを確認 | 誰が・削除確定・通常削除ではないことの判別 |
| インバウンドSMB全操作 | ToolE単体 | 誰が・接続元IP・共有名・アクセス種別・(副産物で)移動/リネームの新名まで |

**結論**: Sysmonのような常駐インストール型ツールを使わなくても、Windows標準機能
(FileSystemWatcher+ETW+セキュリティ監査ログ)の組み合わせだけで、新規作成・上書き・コピー・移動・
リネーム(新旧パス含む)・通常削除(元パス復元込み)・完全削除の判別まで、実運用上必要な情報は
ほぼすべて揃う。

むしろ**移動/リネームの新旧パス判明については、Sysmon自体が原理的に不可能な領域**(同一ボリューム
内の移動・リネームは実体の作成/削除を伴わないため、Sysmonの`FileCreate`/`FileDelete`ベースの検知
では捉えられない)であり、この点はWindows標準機能(ToolAのFileSystemWatcher)の組み合わせの方が
明確に優れている。また、ToolB(ETW)固有の弱点(呼び出し元アプリ次第で完全削除の痕跡が一切残らない
ことがある)も、削除検知をToolI・ToolA・ToolHに任せれば回避できる。ToolBは「実際に何バイト
読み書きしたか」という、より深い証跡が必要な場合にのみ追加投入すればよい補助的な位置づけになる。

### SACL(ToolI)とSMB共有監査(ToolE)、得意分野が違う

ToolI(SACL、ローカル)とToolE(SMB共有監査、インバウンド)で同じ操作(新規作成/書き込み/削除/
コピー/ムーブ/リネーム)を実機検証した結果、「どちらがより詳細か」は一概には言えず、観点によって
得意分野が逆転することが分かった(詳細な根拠は各READMEの「実機での検証結果」を参照)。

| 観点 | ToolI(SACL・ローカル) | ToolE(SMB共有・ネットワーク) |
|---|---|---|
| リネーム/移動後の新しい名前 | **不可**(同一ボリューム内リネームはディレクトリエントリの付け替えのみで、新名への再アクセスが一切発生しないことを実機で網羅的に確認済み) | **可能**(旧名への削除相当アクセス→新名への追従アクセスが別イベントとして発生する副産物。SMBクライアントが新パスへネットワーク越しに確認アクセスをし直すためで、SMB特有の副作用) |
| 削除の確定度 | ○(EventID=4660という「削除確定」専用イベントがある。ただし単体ファイルの完全削除では出ないケースもあり完璧ではない) | 無し(4660相当のイベントが存在せず、`Delete`アクセスマスクの出現のみで推測するしかない) |
| 操作したプロセス | ○(ProcessId/ProcessNameが取れる) | 無し(リモートクライアント側のプロセス情報はSMBプロトコル上そもそも伝送されず、サーバー側では原理的に分からない) |
| 接続元(誰がどこから) | 無し(ローカル操作なのでIPの概念自体が無い) | ○(IpAddress/IpPortが取れる。ToolEの一番の存在意義) |
| ファイル/フォルダの区別 | △(拡張子推定+`Directory.Exists()`フォールバックという能動的な工夫をしても完全ではない) | △(`ObjectType`は常に`"File"`。`CreateDirectories`ビットが単独で出た時だけ副次的にフォルダ作成を示唆する程度) |
| ゴミ箱行きか完全削除か | ToolH(ゴミ箱監視)と併用すれば判別可 | 判別する意味自体が無い(SMB経由の削除はそもそも常にゴミ箱を経由しない) |

要するに、SMB監査がリネーム/移動の前後名を取れるのはSMB監査の情報量が本質的に多いからではなく、
「リモートクライアントがリネーム後に新しいパスへネットワーク越しに確認アクセスをし直す」という、
SMBというプロトコル特有の副作用に基づく。ローカル操作ではこの副作用が発生しないため、SACL側で
どれだけ監査対象の権限を広げても再現できない。一方で「誰の・どのプロセスが操作したか」という
観点ではSACL(ローカル)の方が明確に優れている。

## 重要: 各ツールのログは独立しており、自動的には突き合わせません

ToolB(ETWファイルイベント、PIDのみ)と ToolC(プロセス生成、PID→ユーザー名)を、後からPIDと時刻で
突き合わせることで「誰が」を埋められるはずだ、という仮説を検証中です。**この突合はこのツール一式には
含まれておらず、各ログを見比べて別途評価します。** 記録された生ログをそのまま提出してください。

## 事前準備(共通)

- Windows 10/11 または Windows Server(x64)
- ToolA・ToolH を除く各ツールは**管理者権限のPowerShell**で実行してください(ETW/監査ポリシー/
  セキュリティログの購読に必要です)
- ToolF/G(アウトバウンドSMB)の検証には、実際にアクセスできる**別マシンの共有フォルダ**が必要です。
  ToolE(インバウンドSMB)の検証には、このマシン上に共有フォルダを用意し、別マシンからアクセスして
  もらう必要があります(詳細は各READMEの「事前準備」を参照)

### 単独実行可能なexe(ビルド済み、.NETランタイム不要)

各ツールフォルダの `dist/` に、自己完結ビルド済みの `.exe` を同梱している。他マシンにこの
リポジトリを丸ごとコピー(またはclone)すれば、.NETのインストールなしにそのまま実行できる
(例: `probe-tools\ToolA-FsWatcherProbe\dist\FsWatcherProbe.exe`)。デバッグ用に `.pdb` も同梱。

ソースを変更した場合や、`dist/` を再生成したい場合は以下でビルドし直す:

```powershell
dotnet publish probe-tools\ToolA-FsWatcherProbe -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o probe-tools\ToolA-FsWatcherProbe\dist
dotnet publish probe-tools\ToolB-EtwFileProbe   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o probe-tools\ToolB-EtwFileProbe\dist
dotnet publish probe-tools\ToolC-ProcessAuditProbe -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o probe-tools\ToolC-ProcessAuditProbe\dist
dotnet publish probe-tools\ToolH-RecycleBinProbe   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o probe-tools\ToolH-RecycleBinProbe\dist
dotnet publish probe-tools\ToolI-SaclProbe         -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o probe-tools\ToolI-SaclProbe\dist
dotnet publish probe-tools\ToolI-SaclProbe\LogFormatter -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o probe-tools\ToolI-SaclProbe\LogFormatter\dist
dotnet publish probe-tools\ToolE-SmbServerAuditProbe    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o probe-tools\ToolE-SmbServerAuditProbe\dist
dotnet publish probe-tools\ToolF-SmbClientEventLogProbe -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o probe-tools\ToolF-SmbClientEventLogProbe\dist
dotnet publish probe-tools\ToolG-SmbClientEtwProbe      -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o probe-tools\ToolG-SmbClientEtwProbe\dist
```

ソースから直接実行したい場合は .NET 8 SDK が必要(`dotnet run --project ...`)。

## テスト用フォルダ・ファイルの準備

`setup-test-env.ps1` を実行すると、新規作成/書き込み(上書き保存)/コピー/ムーブ/リネーム/通常削除/
完全削除の各操作をひととおり試せる構成のテストフォルダを用意できます(既存があれば削除して作り直す)。

```powershell
.\probe-tools\setup-test-env.ps1
# 既定は D:\tmp\ProbeTest。パスを変えたい場合:
.\probe-tools\setup-test-env.ps1 -TestRoot D:\tmp\MyProbeTest
```

実行すると `Baseline time` が表示されるので、各ツールを起動したうえで、その時刻以降にエクスプローラ上で
操作を行い、各ツールのログと突き合わせてください。

## 使い方の流れ(いずれのツールも共通)

1. (任意)`setup-test-env.ps1` でテスト用フォルダを準備する
2. 管理者PowerShellでツールを起動する(起動したままにする)
3. 別のPowerShell/エクスプローラーで、確認したいファイル操作(作成・コピー・移動・リネーム・削除等)を行う
4. **Ctrl+C** でツールを停止する(ウィンドウを閉じない。正常終了処理でログが確定します)
5. 出力されたログファイルを確認する

詳しい使い方・仕組み・ログの読み方は各ツールのREADMEを参照してください。
