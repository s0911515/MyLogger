# MyLogger — Windows ファイル入出力監視システム(情報漏洩対策)

Windows 上のファイル操作・ネットワーク共有アクセス・ユーザー認証を記録する常駐アプリ(Windows サービス)です。
[doc/要件定義書.md](doc/要件定義書.md) の要件に基づき実装しています。重要情報の不正持ち出しや誤操作による
情報漏洩リスクに備え、インシデント発生時の原因究明と監査証跡の確保を目的とします。

セットアップ・動作確認の具体的な手順は [doc/セットアップ・動作確認手順書.md](doc/セットアップ・動作確認手順書.md) を参照してください。
①(ローカルファイル監視)の FileSystemWatcher/ETW ハイブリッド構成の詳細は
[doc/ローカルファイル監視の仕組み.md](doc/ローカルファイル監視の仕組み.md) にまとめています。

## 監視対象(要件定義書 3.2 との対応)

| # | 監視対象操作 | 仕組み | 詳細 |
|---|---|---|---|
| ① | ローカルファイル操作(作成/変更/削除/リネーム) | `FileSystemWatcher` | リネームは変更前後両方のパスを記録。読み込みのみは対象外 |
| ② | ネットワークからの共有ファイルアクセス(被接続) | セキュリティ監査ログ購読(イベント ID 5140/5142-5145) | 読み込みのみも記録対象 |
| ③ | ネットワークへの共有ファイル持ち出し(接続) | ETW カーネル FileIO イベント購読 | どのプロセスが・どこへ・何バイト書いたかを記録 |
| ④ | ユーザー認証(ログイン/ログアウト) | セキュリティ監査ログ購読(イベント ID 4624/4634/4647) | コンソール・RDP・SMB 認証(ネットワークログオン)を含む |
| ⑤ | RDP クリップボード経由のファイル持ち出し | ETW(rdpclip プロセスによるローカルファイル読み取りを監査) | クリップボード共有でのファイルコピーを検知 |

②③はどちらも「ネットワーク方向のファイルアクセス」ですが、②は**自 PC の共有フォルダに外部からアクセスされた**場合、
③は**自 PC から外部(共有フォルダ・USB 等)へファイルを持ち出した**場合を指し、検知の仕組みも異なるため役割を分担しています
(同一操作の二重記録を避けるため)。

## 実装方式の詳細

### なぜこの組み合わせなのか(非機能要件 5.1 との関係)

要件定義書 5.1 は「カーネルレベルで常にフックする方式は負荷が高いため、Windows の監査ポリシー/イベントログを
非同期に検知する方式を第一候補とする」としています。本実装は以下の理由からハイブリッド構成を採っています。

- **②(被接続)・④(認証)** は Windows のセキュリティ監査ログに必要な情報(実行ユーザー・接続元 IP・アクセス内容)が
  そのまま載るため、素直にイベントログ購読(`EventLogWatcher`)で実現できます。追加の負荷はイベント発生時のみ。
- **③(持ち出し)** は「自 PC から共有/USB への書き込み」に対応するセキュリティ監査イベントが存在しない
  (5145 はサーバー側=被アクセス側のイベント)ため、代替が効きません。ETW のカーネル FileIO プロバイダーを
  ネットワーク/リムーバブル宛の I/O だけに絞って購読することで、常時フックによる負荷を最小限にしています。
- **①(ローカル)** は `FileSystemWatcher` が最も正確に「作成/変更/削除/リネーム(前後パス付き)」を区別できるため
  採用しています。ETW のリネームイベントは変更前の名前しか取得できないという制限があり、代替になりません。

### ①ローカルファイル操作のノイズ除外

`FileSystemWatcher` はファイルの作成・変更・削除に伴い、その**親フォルダ自体の更新日時(mtime)も
変化する**ため、親フォルダに対しても `Changed` を発火する。これは実際のユーザー操作ではなく副作用的な
通知であり、フォルダ自体の作成・リネーム・削除は `Created`/`Renamed`/`Deleted` で別途正しく記録される
ため、**対象がフォルダである `Changed` イベントは記録しない**(`FileWatcherMonitor` が
`Directory.Exists` で判定して除外)。

また、ファイル削除は既定でごみ箱(`$RECYCLE.BIN`)への移動として実装されており、そのままでは
ごみ箱内部の管理ファイル(`$Ixxxxxx.拡張子`/`$Rxxxxxx.拡張子`)への作成・変更もノイズとして記録
されてしまう。`$RECYCLE.BIN`・`System Volume Information` はドライブ文字によらず既定で除外している
(監視対象が `C:\` 以外のドライブであっても除外される)。

### ①フォルダをまたぐ移動の検知(LOCAL_MOVE)

同一ボリューム内でも、エクスプローラーの「切り取り→貼り付け」等でフォルダをまたいで移動すると、
単純な名前変更(`Renamed`)ではなく `Deleted`+`Created` として通知される場合がある。
`FileWatcherMonitor` は**同一ファイル名の `Deleted`/`Created` が `FileWatcher.MoveCorrelationWindowMs`
(既定 500ms)以内に両方発生したら、移動元/移動先パス付きの `LOCAL_MOVE` として 1 件に統合する**。
片方しか来なかった場合は、通常通り `LOCAL_CREATE`/`LOCAL_DELETE` として記録される(この判定のため、
作成/削除イベントの記録に最大 `MoveCorrelationWindowMs` 分の遅延が生じる)。

ファイル名の一致のみによる相関のため、偶然同名のファイルが別フォルダで同時期に作成/削除された場合に
誤って `LOCAL_MOVE` と判定するリスクは理論上あるが、時間窓を短くすることで低く抑えている。
`MoveCorrelationWindowMs` を 0 以下にするとこの機能を無効化できる(常に `LOCAL_CREATE`/`LOCAL_DELETE`)。

`event_timestamp` にはこの保留処理による遅延の影響を受けないよう、判定が確定した時刻ではなく
**本来の発生時刻**(FileSystemWatcher が実際にイベントを検知した時刻)を記録している。そのため、
`log_id`(DB への書き込み順)と `event_timestamp` の順序が一致しない場合がある。時系列で解析する際は
`log_id` ではなく `event_timestamp` でソートすること。

### ①Created 直後の Changed の抑制

[doc/FileSystemWatcher調査.md](doc/FileSystemWatcher調査.md) で実機確認した通り、ファイルコピー等は
OS 的に「ファイル作成」と「内容の書き込み」に分かれた別々の操作であり、`FileSystemWatcher` はこれを
`Created`+`Changed`(場合によっては複数件)という別々の通知として報告する。ユーザーから見れば1回の
コピー操作なのに `LOCAL_CREATE`+`LOCAL_CHANGE` の2件に分裂して記録されてしまうため、
`FileWatcherMonitor` は**`Created`(または `LOCAL_MOVE` の移動先)の直後、`FileWatcher.
CreateChangeSuppressWindowMs`(既定 2000ms)以内に同一パスへ発生した `Changed` を記録しない**。

`Created` が `LOCAL_MOVE` 判定のため `_pendingCreates` に保留されている間(前節参照)に `Changed` が
先に届くケースもあるが、この場合も同様に抑制する(保留中の `Created` が `Changed` より後に DB へ
書き込まれてしまう、実機で確認された順序の逆転現象への対処を兼ねる)。パス(ファイル名)の完全一致でのみ
判定するため、誤って無関係な `Changed` を抑制してしまうリスクは低い。ウィンドウを外れた本来の再編集
(数秒後の再保存等)は通常通り `LOCAL_CHANGE` として記録される。`CreateChangeSuppressWindowMs` を
0 以下にするとこの機能を無効化できる(常に `Created` と `Changed` を別々に記録する)。

### ①操作元プロセスの記録 — テーブルA/Bとバッチ突合方式

`FileSystemWatcher` はどのプロセスが操作したかを教えてくれない。当初は `EtwFileIoMonitor` が
捕捉した ETW イベントをメモリ上のキャッシュ(`LocalFileOpenTracker`)に積み、`FileWatcherMonitor`
がリアルタイムに突き合わせる方式を実装していたが、**ETWとFileSystemWatcherは別々のOS通知経路を
別々のスレッドで処理しており、どちらが先に届くかの保証が無い**ため、相関が取りこぼされることが
実機で確認された(同じ操作を2回行って1回目失敗・2回目成功、という結果が再現した)。

そこで **2026-07-13 に「リアルタイムでの相関」自体を諦め**、以下の3テーブル構成に変更した
(詳細な経緯・調査過程は [doc/ローカルファイル監視の仕組み.md](doc/ローカルファイル監視の仕組み.md) 参照)。

| テーブル | 内容 | 書き込み | 変更されるか |
|---|---|---|---|
| `local_fs_pending`(テーブルA) | `FileWatcherMonitor` が検知した①のイベント(`LOCAL_CREATE`等、重複排除・`LOCAL_MOVE`統合・Created直後のChanged抑制は従来通りリアルタイムに適用済み)。`target_user`/`process`/`pid` は未解決 | リアルタイム | 不可(`reconciled_at` のみ後から更新) |
| `local_fs_etw_open`(テーブルB) | `EtwFileIoMonitor` が監視対象パス内で捕捉したオープン/書き込み/フラッシュ。**捕捉した瞬間に** WMI で `target_user` まで解決してから保存する(後述の理由により、遅延させると解決不能になるため) | リアルタイム | 不可 |
| `activity_log`(テーブルC) | 最終ログ。②③④⑤は従来通りリアルタイムで直接書き込まれる。①だけは、テーブルA・Bを`tools/ReconcileLocalFs` で後から突合した結果が書き込まれる | **手動実行のバッチ** | 一度書いたら不変 |

テーブルA・Bはどちらも一度書いたら変更しない(追記のみ)。テーブルCの生成(バッチ)を何度再実行しても
安全なように、テーブルAには `reconciled_at`(処理済みマーカー)を持たせ、未処理の行だけを対象にする。
この設計により、「ログ行が後から書き換わる」ことがなく(監査証跡としての完全性を保ちつつ)、
相関ロジックを将来改良した場合は**過去分まで遡ってテーブルCを作り直せる**という利点もある
(生データがテーブルA・Bとして残っているため)。

**なぜテーブルBは「捕捉した瞬間に」ユーザーまで解決しておく必要があるのか**: PID→ユーザー名の
WMI解決(`Win32_Process.GetOwner`)は、**そのプロセスがまだ実行中でなければ機能しない**。
テーブルCの生成(バッチ)は数分〜数日後に実行される可能性があり、その時点では操作元プロセスは
とっくに終了している。そのため `EtwFileIoMonitor` はETWイベントを捕捉したその場でユーザー名まで
解決し、解決済みの文字列としてテーブルBに保存する(バッチ側はパス・時刻だけで突合すればよく、
WMI呼び出しは不要)。

オープン(`FileIOCreate`)だけでなく書き込み(`FileIOWrite`)・フラッシュ(`FileIOFlush`)時にも
テーブルBへ記録するのは、メモ帳のように**ファイルを開いてから保存するまで同じハンドルを保持し
続けるアプリ**では、開いた瞬間の記録だけでは古すぎる候補になってしまう可能性があるため。
書き込みイベントは Fast I/O の影響で ETW に載らないことがある(制限事項参照)ため、フラッシュ
(キャッシュを迂回するため Fast I/O の影響を受けにくい)も保険として追跡している。

ローカルファイルへの書き込みも、Windows のキャッシュマネージャーにより **`System`(PID 4)の
スレッドが遅延実行する**ことがある。`EtwFileIoMonitor` は「どのプロセスがそのファイルを開いたか」の
対応表を使って PID 4 を実際のプロセスへ差し戻す。差し戻しに失敗した場合は `System` をそのまま
記録しない(その場合、突合時に候補が見つからず `UNKNOWN` になる)。

### `tools/ReconcileLocalFs`(テーブルCの生成・手動実行)

```powershell
dotnet run --project tools\ReconcileLocalFs                # 既定DBを突合
dotnet run --project tools\ReconcileLocalFs -- <DBパス>     # 指定DBを突合
```

`local_fs_pending` の未処理行ごとに、`local_fs_etw_open` から**同一パス・時間窓内**(対象イベントの
最大30秒前〜5秒後。30秒は旧`LocalFileOpenTracker`のTTLと同じ考え方)の候補を探し、複数候補があれば
`explorer.exe`(フォルダ表示更新等での誤検知が多い)以外を優先して採用する。見つからなければ
推測はせず `target_user = "UNKNOWN"` とする。処理した行は `reconciled_at` を立てるため、
**何度再実行しても安全**(未処理分だけが処理される)。

現時点では**手動実行のみ**を想定しており、自動スケジュール実行(定期バッチ化)は未実装。また、
`Monitoring.MonitoredUsers` によるフィルタは本ツールでは未適用(今後の課題)。

**却下: オブジェクトアクセス監査(SACL・イベント4663)によるフォールバック。** ETW 相関が失敗した
場合に、監視対象パスへ書き込み監査(SACL)を設定した上でセキュリティ監査ログ(4663)へオンデマンド
問い合わせる方式を実装・実機検証していたが、以下の理由により不採用とし実装を完全に削除した:

- SACL の設定(`SetAccessControl`)がドライブのルート(既定設定の `D:\` 等)では Windows 側の制約により
  ハングする現象があり、既定設定のままではフォールバック自体が機能しないケースが多かった。
- セキュリティイベントログの書き込み遅延を吸収するためのリトライ(300ms 間隔で最大4回)が
  `FileSystemWatcher` のイベントハンドラ内で**同期的に実行**されており、最悪ケースで1イベントあたり
  約900ms 処理をブロックしていた。これは `FileSystemWatcher` のバッファ再発行を遅らせるため、
  むしろバッファオーバーフロー(取りこぼし)のリスクを高める方向に作用する構造的な問題だった。
- `target_user` は必須項目だが、この情報が無くても `UNKNOWN` として常に値は記録される
  (要件定義書 §4)。`additional_info` のプロセス名/PID は任意項目であり、精度が落ちるだけで
  記録自体は欠落しない。得られる精度向上と比べてリスク・複雑さが見合わないと判断した。

**却下: ETW相関失敗時の対話セッション近似値へのフォールバック。** 当初はETW相関に失敗した場合、
WTS API(`WTSGetActiveConsoleSessionId` 等)で「現在アクティブな対話セッションのログオンユーザー」を
`target_user` として使うフォールバックを実装していたが、**複数ユーザーが同時ログオンし得る運用環境
(本番の想定環境)では、これは実際の操作者とは無関係な「たまたま今ログオンしている別のユーザー」を
誤って記録してしまう可能性がある**ため撤回した。中途半端に「それらしい」ユーザー名が入っていると、
調査時に誤った個人を疑う根拠にされかねず、`UNKNOWN`(証跡はあるが特定できないことが一目で分かる)
より実害が大きいと判断したため。単一ユーザーのワークステーションのみを対象とする場合は有効な
近似値になるが、本プロジェクトの対象環境では前提が成立しないため不採用とした。

そのため、①でETW相関が失敗した場合(PIDは分かったが所有者解決に失敗した場合を含む)は、
推測はせず `target_user = "UNKNOWN"` として記録する(後述の「target_user の解決方法」参照)。

### ②共有アクセスのノイズ除外

セキュリティ監査イベント 5145 は、共有内の個別ファイルへのアクセスだけでなく、**共有フォルダの
ルート自体へのアクセス**(`RelativeTargetName` が空、または `\`)も、実際のファイル操作のたびに
付随イベントとして大量に発生する。これは「共有に接続した」以上の情報を持たない冗長イベントであり、
共有への接続自体は 5140(`SHARE_CONNECTED`)で別途記録されるため、`SmbAuditMonitor` は 5145 のうち
共有ルート自体を指すものを記録しない。

また、SMB は 1 回の実質的なファイル操作に対してもハンドルオープン等で複数回の監査イベントを
生成するため、`SmbAuditMonitor` も①と同様に同一パス・同一種別(読み取り/書き込み)の連続イベントを
`SmbAudit.DedupeSeconds`(既定 2 秒)でまとめる。

### ③ネットワーク書き込みのノイズ除外

共有フォルダを開く等の操作を行うと、Windows は `\\host\PIPE\srvsvc` のような**名前付きパイプへの
RPC 制御通信**を自動的に発生させる。これはファイルの持ち出しではなく Windows 内部の管理通信のため、
`EtwFileIoMonitor` は宛先パスに `\PIPE\` または `\IPC$\`(名前付きパイプ・管理共有)を含む書き込みを
記録しない。

### ④ログオフのノイズ除外

ログオフイベント(4634/4647)には、対応するログオンがコンソール/RDP/ネットワークのどれだったかを示す
`LogonType` が含まれない。そのままではバックグラウンドサービスや自動処理によるログオフまで
無差別に記録してしまうため、`LogonMonitor` は記録対象と判定したログオン(4624)の `TargetLogonId` を
記憶しておき、**対応する `TargetLogonId` を持つログオフのみ**を記録する。対応するログオフが来ないまま
残ったエントリは 1 日で自動的に間引かれる。

### target_user(実行ユーザー)の解決方法

要件定義書 §4 では `target_user` を全レコード必須としていますが、監視の仕組みごとに得られる情報が異なるため、
以下の方法で解決しています。

| 監視 | 解決方法 | 精度 |
|---|---|---|
| ②被接続・④認証 | イベントに含まれる `SubjectUserName`/`TargetUserName` をそのまま使用 | 正確 |
| ③持ち出し・⑤RDPクリップボード | ETW イベントの ProcessID を WMI(`Win32_Process.GetOwner`)でユーザーに解決 | 正確(プロセス単位) |
| ①ローカル操作(`tools/ReconcileLocalFs` の突合で解決できた場合) | ETW の `FileIOCreate` から得た PID を、捕捉した時点で WMI によりユーザーに解決済み(③⑤と同じ仕組み) | 正確(プロセス単位) |
| ①ローカル操作(突合で候補が見つからない場合) | `FileSystemWatcher` 単独ではプロセス/ユーザー情報を持たない。推測はせず `target_user = "UNKNOWN"` とする | 不明であることが明示される(誤った個人を記録しない) |

解決に失敗した場合は `target_user = "UNKNOWN"` として**必ず記録を残します**(証跡が欠落する方が、
ユーザー特定の誤りより悪いと判断したため)。①については、複数ユーザーが同時ログオンし得る環境を
想定し、**確証の無い推測(対話セッションのログオンユーザー等)によるユーザー特定は行いません**
(誤った個人を記録するリスクの方が `UNKNOWN` より悪いと判断したため。前節「却下」参照)。
なお①は `tools/ReconcileLocalFs` を実行するまで `activity_log` に現れない点に注意(前節参照)。

`ProcessUserResolver`(③⑤の PID→ユーザー解決)は結果を PID 単位でキャッシュしているが、
**解決に成功した結果は 10 分、失敗した結果は 5 秒だけキャッシュする**(非対称 TTL)。
WMI(`Win32_Process.GetOwner`)は特定のプロセスに対して一時的に解決へ失敗することがあるため、
失敗を長時間キャッシュしてしまうと、その PID からの操作がその後長時間ずっと `UNKNOWN` になり続けて
しまう。短い TTL で自動的に再試行することで、この状態を数秒で回復できるようにしている。
解決失敗時の詳細な理由(`GetOwner` の戻り値や例外)は `Debug` ログレベルで記録される。

### 監視対象ユーザーの絞り込み

`appsettings.json` の `Monitoring.MonitoredUsers` にユーザー名(`user` または `DOMAIN\user`)、
または**ローカルグループ名**を列挙すると、そのユーザー/グループのメンバーのみを記録対象にできます。
グループ名はローカルグループのメンバーに展開(`System.DirectoryServices.AccountManagement`、15分キャッシュ)されます。
空の場合は全ユーザーが対象です。

グループ展開に失敗した場合(ドメイン到達不可等)や `target_user` が `UNKNOWN` の場合は、
**フィルタを適用せず記録します**(フェイルオープン。証跡の欠落を避けるため)。

### 監査ポリシー自動設定(要件 3.1)

起動の度に `AuditPolicyConfigurator`(`IHostedService`)が `auditpol.exe` 経由で以下のサブカテゴリを確認し、
無効または不足していれば強制的に有効化します。手動でのポリシー設定は不要です。

- ファイル共有 / 詳細なファイル共有(②用)
- ログオン / ログオフ(④用)

他の監視処理より先に完了させる必要があるため、ジェネリックホストの `IHostedService` 登録順(先頭)を利用して
同期完了を保証しています。

### データ格納(要件 3.3)・ローテーション(要件 3.4)

- **DB**: SQLite(`Microsoft.Data.Sqlite`)。①〜⑤すべてのログを単一テーブル `activity_log` に格納します。
- **スキーマ**(要件定義書 §4 準拠):

  ```sql
  CREATE TABLE activity_log (
      log_id           INTEGER PRIMARY KEY AUTOINCREMENT,
      event_timestamp  DATETIME NOT NULL,   -- ISO 8601 文字列 (例: 2026-07-04T12:34:56.789+09:00)
      action_type      VARCHAR NOT NULL,    -- 下記「action_type 一覧」参照
      target_user      VARCHAR NOT NULL,    -- DOMAIN\user 形式。不明時は "UNKNOWN"
      source_ip        VARCHAR,             -- 被接続(②)・認証(④)時のみ
      source_path      TEXT,                -- 削除/リネーム前パス等
      dest_path        TEXT,                -- 作成/変更/リネーム後パス等
      additional_info  TEXT                 -- JSON (process, pid, bytes, share, access, detail)
  );
  CREATE INDEX idx_activity_log_timestamp   ON activity_log(event_timestamp);
  CREATE INDEX idx_activity_log_action_type ON activity_log(action_type);
  ```

  将来の検索・CSV 出力アドオンからの参照を想定し、`event_timestamp`/`action_type` にインデックスを張っています。
- **`local_fs_pending`(テーブルA)・`local_fs_etw_open`(テーブルB)**: ①専用の中間テーブル
  (前述「①操作元プロセスの記録」参照)。`activity_log` と同じ接続・同じローテーション周期で管理されます。

  ```sql
  CREATE TABLE local_fs_pending (
      id               INTEGER PRIMARY KEY AUTOINCREMENT,
      event_timestamp  DATETIME NOT NULL,
      action_type      VARCHAR NOT NULL,
      source_path      TEXT,
      dest_path        TEXT,
      reconciled_at    DATETIME             -- NULL = tools/ReconcileLocalFs 未処理
  );
  CREATE TABLE local_fs_etw_open (
      id               INTEGER PRIMARY KEY AUTOINCREMENT,
      event_timestamp  DATETIME NOT NULL,
      path             TEXT NOT NULL,
      event_type       VARCHAR NOT NULL,     -- Create / Write / Flush
      process_name     VARCHAR,
      pid              INTEGER,
      target_user      VARCHAR              -- 捕捉時点で解決済み
  );
  ```
- **WAL モード**: `PRAGMA journal_mode=WAL; synchronous=NORMAL; busy_timeout=5000;` を起動時に設定し、
  検索/出力ツールとの同時実行性を確保します(要件 5.3)。
- **非同期・バルク書き込み**(要件 5.1): 各 Monitor は `System.Threading.Channels.Channel` 経由でイベントを
  キューに積むのみで即座に処理を継続します。書き込みスレッドはキューをバッチ(最大 500 件)でまとめて
  1 トランザクションで `INSERT` するため、ファイル I/O がボトルネックになりにくい設計です。
- **ローテーション**(要件 3.4): `Monitoring.Rotation` で「経過日数(`IntervalDays`)」「サイズ上限(`MaxSizeMB`)」の
  いずれかを設定でき、閾値を超えると次の手順で DB ファイルを切り替えます。

  1. `PRAGMA wal_checkpoint(TRUNCATE)` で WAL の内容を本体に反映
  2. 接続をクローズ
  3. 現在の DB ファイルを `data/archive/activity-yyyyMMdd-HHmmss.db` にリネーム退避
  4. 同じファイル名で新規空 DB を作成し、接続を再開

  ファイル移動(rename)ベースの瞬時切り替えのため、外部の検索/出力ツールが影響を受ける時間はごく短時間です
  (要件 5.3)。`RetentionGenerations` で保持するアーカイブ世代数の上限を設定できます(古いものから削除)。

### NTFS 権限保護(要件 5.2)

起動の度に `PermissionHardener`(`IHostedService`)が、DB ファイルの格納ディレクトリと `appsettings.json` の
ACL を「継承を切り、`NT AUTHORITY\SYSTEM` と `BUILTIN\Administrators` の FullControl のみ」に強制的に
再設定します。一般ユーザーはこれらのファイルを閲覧・削除・改ざんできません。

## action_type 一覧

| action_type | 対応する要件項目 | source_path | dest_path |
|---|---|---|---|
| `LOCAL_CREATE` | ① | – | 作成されたパス |
| `LOCAL_CHANGE` | ① | – | 変更されたパス |
| `LOCAL_DELETE` | ① | 削除されたパス | – |
| `LOCAL_RENAME` | ① | 変更前パス | 変更後パス |
| `LOCAL_MOVE` | ①(フォルダをまたぐ移動、推定検知) | 移動元パス | 移動先パス |
| `LOCAL_MONITOR_OVERFLOW` | ①(運用情報) | – | 監視パス |
| `NETWORK_EXFIL_WRITE` | ③ | – | 書き込み先 UNC パス |
| `REMOVABLE_WRITE` | ③ | – | 書き込み先パス(USB 等) |
| `RDP_CLIPBOARD_COPY` | ⑤ | 読み取られたローカルパス | – |
| `NETWORK_READ` | ③(補助情報、既定オフ) | 読み取られたネットワークパス | – |
| `NETWORK_RENAME` / `NETWORK_DELETE` | ③ | 対象パス | – |
| `REMOVABLE_RENAME` / `REMOVABLE_DELETE` | ③ | 対象パス | – |
| `SHARE_READ` / `SHARE_WRITE` | ② | – | 共有内ファイルパス |
| `SHARE_CONNECTED` | ② | – | 共有パス |
| `SHARE_CREATED` / `SHARE_MODIFIED` / `SHARE_DELETED` | ②(運用情報) | – | 共有パス |
| `LOGIN` / `LOGIN_FAILED` / `LOGOUT` | ④ | – | – |

`source_ip` は ②・④ のイベントに含まれる接続元 IP です。`additional_info` にはプロセス名/PID/バイト数/
共有名/アクセス権/ログオン種別などの補足情報を JSON で格納します。

## 必要環境

- Windows 10/11 または Windows Server(x64)
- .NET 8 SDK(ビルド時)/ .NET 8 Runtime(実行時)
- 管理者権限(監査ポリシー変更、ETW セッション、セキュリティログ購読、ACL 変更に必要。サービスは LocalSystem で動作)

## セットアップ

管理者権限の PowerShell で:

```powershell
.\scripts\install-service.ps1
```

サービスのビルド・インストール・自動起動設定・異常時再起動設定までを行います。
監査ポリシーの有効化・NTFS 権限保護はアプリ自身がサービス起動時に自動で行うため、
別途スクリプトを実行する必要はありません。

アンインストールは `.\scripts\uninstall-service.ps1`(DB ファイルは削除されません)。

### デバッグ実行(コンソール)

```powershell
# 管理者 PowerShell で実行すると全機能が動く。
# 非管理者でも起動はするが、②③④および監査ポリシー自動設定・ACL 保護は失敗し、①のみ動作する。
dotnet run --project src\MyLogger
```

## 設定(src/MyLogger/appsettings.json)

```jsonc
"Monitoring": {
  "DataDirectory": "C:\\ProgramData\\MyLogger\\data",  // DB ファイルの格納先
  "DatabaseFileName": "activity.db",
  "MonitoredUsers": [],              // 監視対象ユーザー/ローカルグループ。空 = 全ユーザー

  "Rotation": {
    "IntervalDays": 30,              // この日数を超えたら DB ファイルを切替 (0 以下で無効)
    "MaxSizeMB": 500,                // このサイズ(MB)を超えたら DB ファイルを切替 (0 以下で無効)
    "RetentionGenerations": 12       // 保持するアーカイブ世代数 (0 以下で無制限)
  },

  "FileWatcher": {
    "Enabled": true,
    "Paths": [ "D:\\" ],             // 監視対象のドライブ / フォルダ。空 = 全固定ドライブ
    "ExcludePathPrefixes": [],       // 除外パス(前方一致)
    "ExcludeExtensions": [".tmp"],   // 除外拡張子
    "DedupeSeconds": 2,              // 同一パス・同一操作の重複排除ウィンドウ
    "MoveCorrelationWindowMs": 500,   // 同名の削除+作成をこのミリ秒以内でLOCAL_MOVEに統合。0以下で無効
    "CreateChangeSuppressWindowMs": 2000 // 作成直後のこのミリ秒以内のChangedを記録しない。0以下で無効
  },

  "Etw": {
    "Enabled": true,
    "IncludeRemovable": true,        // USB 等への書き込みも記録
    "IncludeNetworkReads": false,    // 共有上のファイル読み取りも記録する場合 true
    "WriteFlushSeconds": 5,          // 書き込みイベントの集約間隔
    "ExcludeProcesses": ["MsMpEng"], // 記録対象外プロセス
    "AuditReadProcesses": ["rdpclip"]// RDP クリップボード経由の持ち出し検知用
  },

  "SmbAudit": {
    "Enabled": true,
    "IncludeIpcShare": false,
    "IgnoreAttributeOnlyAccess": true
  },

  "Logon": {
    "Enabled": true,
    "IncludeFailedLogon": false      // ログオン失敗 (4625) も記録する場合 true
  }
}
```

変更後は `Restart-Service MyLogger`(インストール構成の場合は publish フォルダ内の appsettings.json を編集)。

## アーキテクチャ

```
MyLogger.exe (Windows サービス / LocalSystem)
├─ AuditPolicyConfigurator … 起動時に監査ポリシーを確認・強制設定           (3.1)
├─ PermissionHardener      … 起動時に DB/設定ファイルの ACL を強制設定      (5.2)
├─ ActivityLogger          … Channel キュー経由の非同期バッチ書き込み
│                             SQLite (WAL) + ローテーション                (3.3/3.4/5.1/5.3)
│                             activity_log / local_fs_pending / local_fs_etw_open の3テーブルを担当
├─ FileWatcherMonitor      … FileSystemWatcher × 監視対象パス          → ①(local_fs_pending へ)
│                             (移動検知・Created直後のChanged抑制を含む。属性解決はしない)
├─ EtwFileIoMonitor        … ETW カーネル FileIO (ネットワーク/リムーバブル
│                             宛の書き込み、rdpclip の読み取り、監視対象
│                             パス内のローカルオープンを抽出)          → ③⑤(activity_log へ直接)
│                             ①用のローカルオープンは捕捉時にユーザー解決まで行い local_fs_etw_open へ
├─ SmbAuditMonitor         … Security ログ 5140/5142-5145 を購読        → ②
└─ LogonMonitor            … Security ログ 4624/4634/4647 を購読        → ④

tools/ReconcileLocalFs (手動実行の別プロセス) … local_fs_pending × local_fs_etw_open を突合し
                                                 ①の最終レコードを activity_log へ書き込む
```

②③④⑤は各 Monitor が `ActivityEvent` を組み立てて `ActivityLogger.Log()` に渡すのみで、
DB スキーマへの変換(`action_type`/`source_path`/`dest_path`/`additional_info` の算出)は
`ActivityRecordMapper` が一元的に行います。①(`FileWatcherMonitor`)は `ActivityLogger.
LogLocalFsPending()` で `local_fs_pending` に未帰属のまま書き込み、`tools/ReconcileLocalFs` が
後から `activity_log` 用のレコードを作ります。`SmbAuditMonitor`/`LogonMonitor` はセキュリティ
イベント XML のパース処理を `SecurityEventParser` として共通化しています。

## 制限事項・注意

- **【調査中・未解決】開発機での検証で、①のETW相関(`local_fs_etw_open`)がプロセス起動後
  1〜2秒程度しか実際に機能せず、それ以降は監視対象パスに関わらずほぼ記録されなくなる現象を確認した
  (2026-07-13)。バッファサイズ増加・WMI呼び出しの非同期化・タイムアウト追加・Sysmon(競合の疑いで
  アンインストール済み)除去のいずれでも改善しなかった。根本原因は未特定。**この問題が起きても
  ①の記録自体(`local_fs_pending`)は失われず、`target_user`/`process`/`pid` が解決できず
  `UNKNOWN`/nullになるだけ**(証跡の欠落は無い)。詳細調査ログはこのプロジェクトの会話履歴を参照。
  今回の修正(WMI呼び出しの分離・タイムアウト等)自体は正当な改善のため差し戻していない。
- **コピー操作のコピー元パスは記録できません。** Windows の標準機構(FileSystemWatcher/ETW)では、
  コピー操作は「新規ファイルの作成」としてしか観測できず、元ファイルとの対応関係はカーネルミニフィルタ
  なしには追跡できません(ローカル・ネットワーク送信の両方に共通する制約です)。リネーム/移動は
  前後パスとも記録できます。
  検証時に「コピー元候補プロセス(explorer.exe 等)の読み取り→書き込みを ETW で時間相関させる」方式を
  試作しましたが、**小〜中サイズの通常のファイルコピーは Windows のキャッシュマネージャーの
  Fast I/O 経路で処理されるため、ETW のカーネル FileIO イベント(IRP 経由のイベントのみ発火)自体が
  発生せず検知できないこと**、また **エクスプローラーの内部処理(ジャンプリスト更新等の無関係な
  読み書き)を誤ってコピーと判定してしまうこと**を実機で確認したため、採用を見送りました。
- **完全な DLP 製品の代替ではありません。** ブラウザーによる Web アップロード、メール添付、クラウド同期
  クライアント経由の持ち出しはファイル I/O としては捕捉できません(クラウド同期はローカルの同期フォルダへの
  書き込みとして①に残ります)。
- **①は `tools/ReconcileLocalFs` を手動実行するまで `activity_log` に一切現れません。** ①のイベントは
  まず `local_fs_pending` にのみ記録され、テーブルB(`local_fs_etw_open`)との突合(バッチ)を経て
  初めて `activity_log` に書き込まれる設計のためです(前述「①操作元プロセスの記録」参照)。
  `activity_log` をリアルタイムに監視するツール・アラートを組む場合、①だけはこの仕組み上
  リアルタイム性が無い点に注意してください。
- ①の `target_user` は、ETW 相関の候補が見つからなかった場合(PID は分かったが所有者解決に失敗した
  場合を含む)は `UNKNOWN` になります。確証の無い推測で誤った個人を記録しないための仕様です
  (前述「却下」参照)。
- **共有フォルダが監視対象ドライブ(既定 D:\)の中にある場合、同じ操作が①(local-fs)と②(smb-server)の
  両方から記録されます。** 例えば `D:\tmp\share` を共有し、外部から(あるいはループバック経由で)
  ファイルを変更すると、①側は`FileWatcherMonitor`が検知して `LOCAL_CHANGE` 等を記録しますが、
  この① 側の `target_user` は実際の操作者(リモートの SMB ユーザー)を指すとは限りません
  (ETW 相関で解決できれば正確な値になりますが、失敗すれば `UNKNOWN` になります)。実際の
  操作者・接続元 IP は②側の `SHARE_READ`/`SHARE_WRITE` レコードの `target_user`/`source_ip` を
  参照してください。①と②を自動的に紐付けて片方に統合する処理は行っていません(それぞれ独立した
  正しい情報を記録しているため)。
- FileSystemWatcher は OS のバッファ経由のため、大量の同時イベントで取りこぼす可能性があります
  (発生時は `LOCAL_MONITOR_OVERFLOW` を記録)。
- 「詳細なファイル共有」監査(イベント 5145)はアクセスの多いファイルサーバーではセキュリティログを
  大量に生成します。`wevtutil sl Security /ms:1073741824` などでログサイズの拡張を推奨します。
- ETW のリネームイベントでは変更後の名前が取得できないため、ネットワーク上のリネームは変更前パスのみ
  記録されます。
- RDP クリップボード検知は「ファイルのコピー」が対象です。開いた文書内の**テキスト**をコピー&ペースト
  した場合はファイル I/O が発生しないため検出できません(画面キャプチャも同様)。RDP のドライブ
  リダイレクト(`\\tsclient\...`)経由のコピーはネットワーク書き込みとして記録されます。
- CPU/メモリ使用率のハードリミット(Job Object 等によるキャップ)は実装していません。
  `WriteFlushSeconds`/`DedupeSeconds` 等のチューニング項目で負荷を調整してください
  (目標値の目安: 通常時 CPU 5% 以下)。
- MonitoredUsers のグループ展開はローカルグループのみ対応しています。ドメイングループを指定した場合、
  ドメインコントローラーに到達できないと展開に失敗し、フェイルオープン(全員監視)で動作します。
- ログ改ざん対策が必要な場合は、出力先を書き込み専用の収集サーバーへ転送するなど別途対策してください。
- 従業員の操作を監視する場合は、社内規程やプライバシーへの配慮(周知・同意)を必ず確認してください。
