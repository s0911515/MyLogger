# 検証ツール一式(実機評価用)

MyLogger の①(ローカルファイル操作監視)における「何が」「誰が」を、どの仕組みでどこまで捕捉できるかを
実機で確認するための、独立した4つの最小ツールです。**MyLogger 本体には依存していません。**

各ツールは**それぞれ独立してログファイルに記録するだけ**で、ツール間の突合(誰が操作したかの結合)は
一切行いません。突合は全ツールでの記録が揃ってから、別途行います(詳しくは各ツールのREADME参照)。

| ツール | 何を記録するか | 仕組み | ドキュメント |
|---|---|---|---|
| **ToolA-FsWatcherProbe** | ファイルの作成/変更/削除/リネーム(パスのみ、誰が操作したかは分からない) | `System.IO.FileSystemWatcher` | [README](ToolA-FsWatcherProbe/README.md) |
| **ToolB-EtwFileProbe** | ファイルI/O(作成/読み書き/フラッシュ/リネーム/削除)。プロセスID・プロセス名・詳細フラグ付き | ETW カーネル FileIO プロバイダー | [README](ToolB-EtwFileProbe/README.md) |
| **ToolC-ProcessAuditProbe** | プロセスが生成された瞬間の PID→ユーザー名の対応 | Windowsセキュリティ監査ログ(イベント4688、OS標準機能) | [README](ToolC-ProcessAuditProbe/README.md) |
| **ToolD-Sysmon** | ファイル作成/コピー・完全削除等の比較用参考記録 | Sysinternals Sysmon(カーネルミニフィルタドライバー) | [README](ToolD-Sysmon/README.md) |

## 重要: 各ツールのログは独立しており、自動的には突き合わせません

ToolB(ETWファイルイベント、PIDのみ)と ToolC(プロセス生成、PID→ユーザー名)を、後からPIDと時刻で
突き合わせることで「誰が」を埋められるはずだ、という仮説を検証中です。**この突合はこのツール一式には
含まれておらず、各ログを見比べて別途評価します。** 記録された生ログをそのまま提出してください。

## 事前準備(共通)

- Windows 10/11 または Windows Server(x64)
- ToolA を除く各ツールは**管理者権限のPowerShell**で実行してください(ETW/監査ポリシー/セキュリティ
  ログの購読に必要です)
- ソースから実行する場合は .NET 8 SDK が必要です(`dotnet run --project ...`)
- 配布用に自己完結ビルド(`.exe` 単体で .NET ランタイム不要)にする場合は以下でビルドします:

```powershell
dotnet publish probe-tools\ToolA-FsWatcherProbe -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o <出力先>
dotnet publish probe-tools\ToolB-EtwFileProbe   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o <出力先>
dotnet publish probe-tools\ToolC-ProcessAuditProbe -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o <出力先>
```

## 使い方の流れ(いずれのツールも共通)

1. 管理者PowerShellでツールを起動する(起動したままにする)
2. 別のPowerShell/エクスプローラーで、確認したいファイル操作(作成・コピー・移動・リネーム・削除等)を行う
3. **Ctrl+C** でツールを停止する(ウィンドウを閉じない。正常終了処理でログが確定します)
4. 出力されたログファイルを確認する

詳しい使い方・仕組み・ログの読み方は各ツールのREADMEを参照してください。
