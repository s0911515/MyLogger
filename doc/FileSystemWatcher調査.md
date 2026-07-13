# System.IO.FileSystemWatcher 実機調査

MyLogger の①(ローカルファイル操作監視)は `FileSystemWatcher` に依存している。この調査は
MyLogger の実装ロジック(除外・重複排除・移動統合)を一切介さず、`FileSystemWatcher` が
**生でどう振る舞うか**を実機で確認し、事実として積み上げることを目的とする。

## 調査方法

- 環境: Windows、ローカル NTFS ドライブ(`D:\`)
- 検証ツール: [`probe-tools/ToolA-FsWatcherProbe`](../probe-tools/ToolA-FsWatcherProbe)(この調査のために新規作成)
  - `FileSystemWatcher` の `Created`/`Changed`/`Deleted`/`Renamed`/`Error` イベントを、
    フィルタ・重複排除・相関処理を一切行わずマイクロ秒精度のタイムスタンプ付きでそのままログ出力するだけの最小ツール。
  - `IncludeSubdirectories = true`、`NotifyFilter` は全フラグ有効、`InternalBufferSize` は既定値(8192)のまま。
- 手順: 監視対象パス配下で基本操作(作成/変更/削除/リネーム/移動、ファイル・フォルダ双方)を
  1 つずつ実行し、操作間に 1〜5 秒の間隔を空けて生イベントを記録した。
- 生ログ全文: `D:\FsWatcherProbe\probe.log`(この文書の一次データ)。以下の「証跡」はこのログの行番号を指す。

再現方法:

```powershell
dotnet run --project probe-tools\ToolA-FsWatcherProbe -- <監視パス> [ログファイル]
```

## 判明した事実

### 1. ファイル作成 → `Created` 1件 + `Changed` 2件がほぼ同時発火

```
[06:36:39.288645] Created   FullPath=...\a.txt IsDir=false
[06:36:39.289168] Changed   FullPath=...\a.txt IsDir=false
[06:36:39.289290] Changed   FullPath=...\a.txt IsDir=false
```
(証跡: 3〜5行目。7〜9行目の「フォルダ内でのファイル作成」、23〜25行目でも同一パターンを再現)

1 回の作成操作に対し、`Created` だけでなく `Changed` も複数(今回はいずれも2件)、1ms未満の間隔で
連続発火する。作成後に別途 `Changed` を「変更操作」として扱うと、同一操作を二重記録してしまう。

### 2. 追記書き込み → `Changed` 1件

```
[06:36:40.332134] Changed   FullPath=...\a.txt IsDir=false
```
(証跡: 6行目)

### 3. 上書き書き込み(切り詰め+書き込み) → `Changed` 2件

```
[06:36:41.377555] Changed   FullPath=...\a.txt IsDir=false
[06:36:41.377834] Changed   FullPath=...\a.txt IsDir=false
```
(証跡: 7〜8行目)

→ `Changed` の発火回数は書き込み方法によって 1 件だったり 2 件だったりし、一定しない。
回数を根拠に操作の種類を判別することはできない。

### 4. 同一フォルダ内リネーム → `Renamed` 1件 + 新パスへの `Changed` 1件

```
[06:37:20.386895] Renamed   OldFullPath=...\a.txt FullPath=...\b.txt IsDir=false
[06:37:20.387141] Changed   FullPath=...\b.txt IsDir=false
```
(証跡: 9〜10行目)

### 5. ファイル削除 → `Deleted` 1件のみ

```
[06:37:21.467348] Deleted   FullPath=...\b.txt
```
(証跡: 11行目。付随する `Changed` は無い)

### 6. フォルダ作成 → `Created` 1件のみ

```
[06:37:22.541489] Created   FullPath=...\sub1 IsDir=true
```
(証跡: 12行目。ファイル作成と異なり付随 `Changed` は発火しない)

### 7. フォルダリネーム → 対象フォルダ自身への `Changed` 1件 → `Renamed`

```
[06:37:24.656726] Changed   FullPath=...\sub1 IsDir=true
[06:37:24.657149] Renamed   OldFullPath=...\sub1 FullPath=...\sub2 IsDir=true
```
(証跡: 16〜17行目)

### 8. 中身入りフォルダの削除 → 子→親の順、親の `Deleted` は最後

```
[06:37:25.731613] Deleted   FullPath=...\sub2\c.txt      (子ファイル)
[06:37:25.731755] Changed   FullPath=...\sub2 IsDir=true  (親フォルダ自身)
[06:37:25.731979] Deleted   FullPath=...\sub2             (親フォルダ自身)
```
(証跡: 18〜20行目)

### 9. 監視ルート直下では「親フォルダへの Changed 伝播」が発火しない(重要・非自明)

これまでの前提(README/実装コメント)は「ファイルの作成・変更・削除に伴い親フォルダ自体の
更新日時(mtime)も変化するため、親フォルダに対しても `Changed` が飛ぶ」というものだった。

実機で確認したところ、この前提は**成立するケースと成立しないケースがある**:

- **監視ルート自身(`testarea` 直下)** に `a.txt`/`b.txt`/`sub1`/`sub2`/`d.txt`/`folderA`/`folderB` を
  作成・削除しても(セッション全体で複数回試行)、**監視ルート自身への `Changed` は一度も発火しなかった**
  (5秒待っても発火せず。証跡: ログ全体、特に3〜25行目)。
- 一方、**監視ルートの1階層下のサブフォルダ(`folderA`/`folderB`)** では、直下の子ファイルの
  作成・削除に伴って、**サブフォルダ自身への `Changed` が確実に発火した**。

```
[06:38:16.766406] Deleted   FullPath=...\folderA\e.txt
[06:38:16.766576] Created   FullPath=...\folderB\e.txt
[06:38:16.766632] Changed   FullPath=...\folderB IsDir=true   ← 移動先フォルダ自身
[06:38:16.766684] Changed   FullPath=...\folderB\e.txt IsDir=false
[06:38:16.766864] Changed   FullPath=...\folderA IsDir=true   ← 移動元フォルダ自身
```
(証跡: 29〜33行目)

→ 「親フォルダへの `Changed` 伝播」自体は事実だが、**監視ルート自身には伝播しない(または
著しく遅延する)** という限定条件が付くことが分かった。MyLogger の既存実装(フォルダを対象とする
`Changed` を種別を問わず一律で除外)は、このどちらのケースでも結果的に正しく動作するため
**コード修正は不要**。ただし README の説明文言は「監視ルート自身では再現しなかった」という
実機観測を追記する価値がある。

### 10. フォルダをまたぐ移動(`Move-Item`/`mv`)→ `Renamed` は発火せず `Deleted`+`Created` に分解される

```
[06:38:16.766406] Deleted   FullPath=...\folderA\e.txt        (移動元)
[06:38:16.766576] Created   FullPath=...\folderB\e.txt        (移動先)
[06:38:16.766632] Changed   FullPath=...\folderB IsDir=true   (移動先フォルダ自身)
[06:38:16.766684] Changed   FullPath=...\folderB\e.txt IsDir=false
[06:38:16.766864] Changed   FullPath=...\folderA IsDir=true   (移動元フォルダ自身)
```
(証跡: 29〜33行目)

`FileSystemWatcher` 単体には「フォルダをまたぐ移動」を意味的に 1 件の操作として統合する機能は
無いことが実機で裏付けられた。`Deleted`(移動元)→`Created`(移動先)の順で個別に発火し、加えて
移動元・移動先双方の親フォルダに対する `Changed` も発火する。MyLogger の `LOCAL_MOVE` 機能が
同名の `Deleted`/`Created` を時間窓で自前に相関させて統合しているのは、この生の挙動を前提とした
妥当な設計であることが確認できた。

## まだ確認していない観点(次回以降の候補)

- バッファオーバーフロー(`InternalBufferSize` を超える大量イベント同時発生時の `Error`)
- ネットワークドライブ/UNC パスでの挙動差
- シンボリックリンク・ジャンクション・マウントポイント配下の挙動
- 属性のみの変更(タイムスタンプ変更、読み取り専用フラグ変更等)による `Changed` の発火有無
- ロックされたファイル(排他オープン中)への操作
- 同一ファイル名での高速な作成→削除の繰り返し(取りこぼしの再現)
- ケース(大文字小文字)のみの変更のリネーム

## 検証ツール

[`probe-tools/ToolA-FsWatcherProbe`](../probe-tools/ToolA-FsWatcherProbe) — `FileSystemWatcher` の生イベントを
タイムスタンプ付きでコンソール/ログファイルに出力するだけの最小ツール。MyLogger 本体のロジックを
一切介さないため、今後も同種の実機調査に使い回せる。
