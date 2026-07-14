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

## ネットワークアクセス(SMB経由のファイル操作)

| ツール | 方向 | 何を記録するか | 仕組み | ドキュメント |
|---|---|---|---|---|
| **ToolE-SmbServerAuditProbe** | インバウンド(他PC→このPCの共有) | 共有への接続・共有内ファイルアクセス。ユーザー名・接続元IP付き | Windowsセキュリティ監査ログ(イベント5140/5142-5145)。MyLogger本体の`SmbAuditMonitor`と同方式 | [README](ToolE-SmbServerAuditProbe/README.md) |
| **ToolF-SmbClientEventLogProbe** | アウトバウンド(このPC→他PCの共有) | SMBクライアントの接続・認証イベント(実装方式①: イベントログ購読) | `Microsoft-Windows-SmbClient` 系イベントチャンネル | [README](ToolF-SmbClientEventLogProbe/README.md) |
| **ToolG-SmbClientEtwProbe** | アウトバウンド(このPC→他PCの共有) | SMBクライアントの生イベント(実装方式②: ETW直接購読) | `Microsoft-Windows-SMBClient` ETWプロバイダー | [README](ToolG-SmbClientEtwProbe/README.md) |

**アウトバウンドSMB(ToolF/G)はファイル単位の操作まで取れるか未検証**です。この開発機では外向きの
実SMBファイルアクセスが発生していないため、接続・認証レベルの情報は実機確認できましたが、
ファイル単位の操作(open/read/write等)が記録されるかは他マシンの共有への実アクセスでの検証待ちです
(詳細は各READMEの「実機で確認できたこと」を参照)。ToolFとToolGは実装方式の比較のためあえて別々に
用意しています。

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
