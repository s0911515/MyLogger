# MyLogger — ファイル操作監視サービス(情報漏洩対策)

Windows 上のファイル操作をログに記録する常駐アプリ(Windows サービス)です。
情報漏洩対策を目的とし、特に以下の 3 方向を監視します。

| # | 監視対象 | 仕組み | source 値 |
|---|---------|--------|----------|
| 1 | ローカルディスク上のファイル操作(作成・変更・リネーム・削除) | FileSystemWatcher | `local-fs` |
| 2 | **ネットワーク共有 / USB メモリへのファイルコピー(送信方向)** | ETW(カーネル FileIO イベント) | `network-io` |
| 3 | **ネットワークから自 PC の共有フォルダへのアクセス(受信方向)** | セキュリティ監査ログ(イベント ID 5140/5145 等)の購読 | `smb-server` |

②は「どのプロセスが・どの UNC パスへ・何バイト書き込んだか」まで記録できるため、
エクスプローラーでのコピーはもちろん、robocopy やスクリプトによるコピーも捕捉できます。
さらに `AuditReadProcesses`(既定: `rdpclip`)に挙げたプロセスによるファイル読み取りは
ローカルファイルでも記録するため、**リモートデスクトップのクリップボード共有による
接続元マシンへのファイルコピー**(サーバー側で rdpclip.exe がファイルを読み取る)も捕捉できます。
③は「どの IP のどのユーザーが・共有内のどのファイルに・どんな権限でアクセスしたか」を記録します。

## 必要環境

- Windows 10/11 または Windows Server(x64)
- .NET 8 SDK(ビルド時)/ .NET 8 Runtime(実行時)
- 管理者権限(ETW セッションとセキュリティログ購読に必要。サービスは LocalSystem で動作)

## セットアップ

管理者権限の PowerShell で:

```powershell
# 1. サービスのビルドとインストール(自動起動・異常時再起動付き)
.\scripts\install-service.ps1

# 2. 共有フォルダアクセス監査の有効化(受信方向③の記録に必要)
.\scripts\enable-audit.ps1
```

アンインストールは `.\scripts\uninstall-service.ps1`。

### デバッグ実行(コンソール)

```powershell
# 管理者 PowerShell で実行すると全機能が動く。
# 非管理者でも起動はするが、②③は警告を出して停止し、①のみ動作する。
dotnet run --project src\MyLogger
```

## ログ

- 出力先: `C:\ProgramData\MyLogger\logs\activity-yyyy-MM-dd.jsonl`(日次ローテーション、既定 90 日保持)
- 形式: 1 行 1 イベントの JSON(JSONL)。SIEM やログ収集基盤への取り込みを想定

### 記録例

```jsonc
// ① ローカルでファイル作成
{"ts":"2026-07-03T23:37:54+09:00","source":"local-fs","action":"Created","path":"C:\\Users\\taro\\Documents\\秘密.xlsx","target":"fixed"}

// ② ネットワーク共有へのコピー(エクスプローラーが 5MB 書き込んだ)
{"ts":"2026-07-03T23:40:12+09:00","source":"network-io","action":"Write","path":"\\\\fileserver\\share\\秘密.xlsx","target":"network","process":"explorer","pid":1234,"bytes":5242880}

// ② USB メモリへのコピー
{"ts":"2026-07-03T23:41:00+09:00","source":"network-io","action":"Write","path":"E:\\秘密.xlsx","target":"removable","process":"explorer","pid":1234,"bytes":5242880}

// ② RDP クリップボード共有によるファイル持ち出し (rdpclip がファイルを読み取った)
{"ts":"2026-07-04T06:03:02+09:00","source":"network-io","action":"Read","path":"D:\\DEV\\秘密.xlsx","target":"fixed","process":"rdpclip","pid":5678,"bytes":213694}

// ③ ネットワークから自 PC の共有フォルダ内ファイルへのアクセス
{"ts":"2026-07-03T23:45:30+09:00","source":"smb-server","action":"ShareFileAccess","path":"C:\\Shared\\顧客リスト.csv","share":"\\\\*\\Shared","user":"CORP\\hanako","remoteIp":"192.168.1.50","access":"ReadData"}
```

### 主なフィールド

| フィールド | 内容 |
|-----------|------|
| `source` | 検出元(`local-fs` / `network-io` / `smb-server`) |
| `action` | `Created` `Changed` `Renamed` `Deleted` `Write` `Read` `ShareFileAccess` `ShareConnected` `ShareCreated` など |
| `path` / `oldPath` | 対象パス(ネットワークは UNC に正規化)/ リネーム前パス |
| `target` | 書き込み先分類: `fixed` / `network` / `removable` |
| `process` / `pid` | 操作したプロセス(② のみ) |
| `bytes` | 集約された読み書きバイト数(② のみ) |
| `user` / `remoteIp` / `share` / `access` | アクセス元情報(③ のみ) |

## 設定(src/MyLogger/appsettings.json)

```jsonc
"Monitoring": {
  "LogDirectory": "C:\\ProgramData\\MyLogger\\logs",
  "RetentionDays": 90,               // ログ保持日数。0 以下で無制限
  "FileWatcher": {
    "Enabled": true,
    "Paths": [ "D:\\" ],             // 監視対象のドライブ / フォルダ。空 = 全固定ドライブ。
                                     // 例: [ "D:\\", "C:\\Users\\" ] のようにフォルダ単位でも指定可
    "ExcludePathPrefixes": [],       // 除外パス(前方一致)。C:\Windows 等は既定で除外済み
    "ExcludeExtensions": [".tmp"],   // 除外拡張子
    "DedupeSeconds": 2               // 同一パス・同一操作の重複排除ウィンドウ
  },
  "Etw": {
    "Enabled": true,
    "IncludeRemovable": true,        // USB 等への書き込みも記録
    "IncludeNetworkReads": false,    // 共有上のファイル読み取り(持ち出しの前段)も記録する場合 true
    "WriteFlushSeconds": 5,          // 書き込みイベントの集約間隔
    "ExcludeProcesses": ["MsMpEng"], // 記録対象外プロセス
    "AuditReadProcesses": ["rdpclip"] // このプロセスの読み取りはローカルでも記録
                                      // (RDP クリップボード経由の持ち出し検知。空で無効)
  },
  "SmbAudit": {
    "Enabled": true,
    "IncludeIpcShare": false,        // IPC$(管理接続)も記録する場合 true
    "IgnoreAttributeOnlyAccess": true // 属性読み取りのみのアクセスを間引く(読み書き・削除は常に記録)
  }
}
```

変更後は `Restart-Service MyLogger`(インストール構成の場合は publish フォルダ内の appsettings.json を編集)。

## アーキテクチャ

```
MyLogger.exe (Windows サービス / LocalSystem)
├─ FileWatcherMonitor   … FileSystemWatcher × 固定ドライブ    → local-fs
├─ EtwFileIoMonitor     … ETW カーネル FileIO を購読し
│                          ネットワーク / リムーバブル宛のみ抽出 → network-io
├─ SmbAuditMonitor      … Security ログ (5140/5142-5145) を購読 → smb-server
└─ ActivityLogger       … 非同期キュー経由で JSONL に書き出し(日次ローテ・保持期間管理)
```

役割分担により同一操作の二重記録を避けています(ローカル宛は①のみ、ネットワーク/リムーバブル宛は②のみが記録)。

## 制限事項・注意

- **完全な DLP 製品の代替ではありません。** ブラウザーによる Web アップロード、メール添付、クラウド同期クライアント経由の持ち出しはファイル I/O としては捕捉できません(クラウド同期はローカルの同期フォルダへの書き込みとして①に残ります)。
- FileSystemWatcher は OS のバッファ経由のため、大量の同時イベントで取りこぼす可能性があります(発生時は `MonitorOverflow` イベントを記録)。
- 「詳細なファイル共有」監査(イベント 5145)はアクセスの多いファイルサーバーではセキュリティログを大量に生成します。`wevtutil sl Security /ms:1073741824` などでログサイズの拡張を推奨します。
- ETW のリネームイベントでは変更後の名前が取得できないため、ネットワーク上のリネームは変更前パスのみ記録されます。
- RDP クリップボード検知は「ファイルのコピー」が対象です。開いた文書内の**テキスト**をコピー&ペーストした場合はファイル I/O が発生しないため検出できません(画面キャプチャも同様)。RDP のドライブリダイレクト(`\\tsclient\...`)経由のコピーはネットワーク書き込みとして記録されます。
- rdpclip はテキストのクリップボード同期も行うため、RDP セッション中は持ち出し以外の読み取りが記録される可能性もあります。`bytes` とパスで実際のファイル転送かを判断してください。
- ログ改ざん対策が必要な場合は、出力先を書き込み専用の収集サーバーへ転送するなど別途対策してください。
- 従業員の操作を監視する場合は、社内規程やプライバシーへの配慮(周知・同意)を必ず確認してください。
