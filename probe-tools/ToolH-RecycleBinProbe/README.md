# ToolH-RecycleBinProbe

ゴミ箱(`$RECYCLE.BIN`)への格納をリアルタイムに監視し、削除された**元のファイルパス**を解決する
専用プローブ。[ToolA-FsWatcherProbe](../ToolA-FsWatcherProbe)の調査で「`$RECYCLE.BIN`への
`Created`はFileSystemWatcherで見えるが、`$R`形式にリネームされた実体ファイル名からは元のパスが
分からない」ことが判明したため作成した。[ToolB-EtwFileProbe](../ToolB-EtwFileProbe)は
`$RECYCLE.BIN`を大量ノイズのため既定除外しているので、その代替でもある。

## 仕組み

`$RECYCLE.BIN`配下では、1ファイル削除につき `$R<任意6文字程度>` と `$I<同じ任意部分>` という、
同じサフィックスを持つペアが作られる。

- `$Rxxxxxx.ext`: 実体。削除されたファイルの中身そのものが、この新しい名前にリネームされる
- `$Ixxxxxx.ext`: メタデータ。**元のパス・元のファイルサイズ・削除日時**をバイナリ形式で保持する
  (Microsoftの公式仕様は非公開だが、広く知られているリバースエンジニアリング結果に基づく)

元のパスは`$I`ファイルを読まないと分からない。本ツールは`$R*`の`Created`を検知したら、対応する
`$I*`(ファイル名の`$R`を`$I`に置き換えるだけで特定できる)を読み、デコードする。

### なぜ後からまとめて解析せず、その場で解決するのか

ゴミ箱は手動で空にされたり、サイズ上限による自動クリーンアップで消えたりする可能性がある。
`$I`/`$R`ファイルが存在するうちに解決しておかないと、情報が永久に失われる。

### なぜFileSystemWatcherのハンドラ内で直接読まないのか

`FileSystemWatcher`はイベントハンドラの処理が終わるまでOS側バッファ(`ReadDirectoryChangesW`)を
再発行しない。ハンドラ内で同期的にファイルI/Oを行うと、この再発行が遅れてバッファオーバーフロー
(イベント取りこぼし)のリスクが増える(MyLogger本体で過去に実機確認済みの問題。詳細は
[doc/FileSystemWatcher調査.md](../../doc/FileSystemWatcher調査.md)参照)。そのため`Created`検知は
キューに積むだけにし、**別スレッドのループ**で`$I`ファイルの読み取り・デコードを行う(ETWのWMI解決で
実績のある「ハンドラからは切り離す」パターンと同じ)。

## 使い方

```powershell
# 管理者権限は不要(自分がゴミ箱に捨てたファイルは自分のSIDフォルダ配下で読み書きできるため)

# ソースから実行
dotnet run --project probe-tools\ToolH-RecycleBinProbe -- [ログファイル]

# 同梱のビルド済みexe(.NETランタイム不要)
.\dist\RecycleBinProbe.exe [ログファイル]
```

- `[ログファイル]` 省略時は実行ファイルと同じフォルダの `recyclebinprobe.log`
- 起動後、対象ドライブでファイルをゴミ箱に削除し、**Ctrl+C** で停止するとログが確定する
- `watch-drives.txt` が無ければ既定値(`D:`)で自動生成される

## 設定ファイル: `watch-drives.txt`

```
# 1行1ドライブレター (コロン付き、例: D:)。# で始まる行はコメント。
# ここに列挙したドライブの $RECYCLE.BIN を監視する。
D:
```

複数ドライブを監視したい場合は複数行指定する。

## 記録されるログの内容

2種類のログ行がある。

### 1. 生イベント(`$RECYCLE.BIN`配下の全FileSystemWatcherイベント、無加工)

ToolAと同じ形式。フィルタは一切かけていない(このツールの目的自体がゴミ箱の中身を知ることのため)。

```
Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$IXXXXXXX.txt IsDir=false
Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID>\$IXXXXXXX.txt IsDir=false
Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$RXXXXXXX.txt IsDir=false
```

### 2. 解決結果(`Resolved`、$Iファイルのデコード結果)

`$R*`の`Created`を検知するたびに、対応する`$I*`を解決してこの行が出力される(生イベントとは別行)。

```
Resolved  RPath=D:\$RECYCLE.BIN\<SID>\$RR9YWK8.txt IPath=D:\$RECYCLE.BIN\<SID>\$IR9YWK8.txt DetectedAt=08:07:41.540053 Version=2 OriginalSize=34 DeletionTime=2026-07-15 08:07:41.532 OriginalPath=D:\tmp\toolh-test2.txt IFileSize=74 IFileHex=02000000000000002200000000000000C0096292E513DD011700000044003A005C0074006D0070005C0074006F006F006C0068002D00740065007300740032002E007400780074000000
```

- `RPath`/`IPath`: 実体ファイル・メタデータファイルそれぞれのフルパス
- `DetectedAt`: `$R`の`Created`を検知した時刻(生イベント側の記録時刻とほぼ一致)
- `Version`: `$I`ファイルのフォーマットバージョン(`1`=Windows Vista~8.1、`2`=Windows 10以降。
  このマシンでは`2`を確認)
- `OriginalSize`: 削除前の元ファイルサイズ(バイト)
- `DeletionTime`: 削除日時(ローカル時刻。`$I`ファイル内のFILETIMEをデコード)
- `OriginalPath`: **削除される前の元のフルパス**(これが本ツールの主目的)
- `IFileSize`/`IFileHex`: `$I`ファイルの生バイト数・16進ダンプ全体(デコードで拾いきれていない
  フィールドが今後見つかった場合に備え、生データも残す)

$Iファイルが規定時間内(最大1秒、100ms×10回リトライ)に読めなかった場合は
`解決失敗(...)` という行が出力される。

## $Iファイルのバイナリ形式(実機で確認済み)

| オフセット | サイズ | 内容 |
|---|---|---|
| 0 | 8バイト | バージョン(`1` または `2`) |
| 8 | 8バイト | 元のファイルサイズ |
| 16 | 8バイト | 削除日時(Windows FILETIME) |
| 24 | 4バイト | パス長(文字数、**null終端を含む**。バージョン2のみ) |
| 28〜 (v2) / 24〜 (v1) | 可変 | 元のパス(UTF-16LE、null終端) |

バージョン1は上記の代わりに、オフセット24から固定260文字(520バイト)のnull終端文字列。

実測例(上記ログサンプルの`IFileHex`を8バイトずつ区切ったもの):

```
02 00 00 00 00 00 00 00   version = 2
20 00 00 00 00 00 00 00   originalSize = 0x20 = 32
70 DB D6 69 E5 13 DD 01   deletionTime (FILETIME)
16 00 00 00               pathLengthChars = 22 (null終端込み)
44 00 3A 00 5C 00 ...     "D:\..." (UTF-16LE)
```

## 追加検証: フォルダ+複数ファイルの一括削除(実測、2026-07-15)

`D:\tmp\ProbeTest` 配下のフォルダ・ファイルをエクスプローラーで全選択し、Deleteキーで一括削除した
実際のログ(無加工・全件)。

```
[08:13:07.676723] === RecycleBinProbe(ツールH: ゴミ箱監視・元パス解決) 開始 ログ=...\recyclebinprobe.log 監視ドライブ=[D:] (...\watch-drives.txt) ===
[08:13:07.701879] 監視開始: D:\$RECYCLE.BIN
[08:13:07.703697] 監視開始。プロセス終了(Ctrl+C / kill)まで待機します。
[08:13:16.971240] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$IDLH4PL IsDir=false
[08:13:16.971909] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID>\$IDLH4PL IsDir=false
[08:13:16.972088] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID> IsDir=true
[08:13:16.972864] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$RDLH4PL IsDir=true
[08:13:16.973935] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID> IsDir=true
[08:13:16.974055] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$IVKCJNB.txt IsDir=false
[08:13:16.974160] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID>\$IVKCJNB.txt IsDir=false
[08:13:16.974240] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID> IsDir=true
[08:13:16.974353] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$RVKCJNB.txt IsDir=false
[08:13:16.974525] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID> IsDir=true
[08:13:16.974694] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$IT229HT.txt IsDir=false
[08:13:16.974799] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID>\$IT229HT.txt IsDir=false
[08:13:16.974885] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID> IsDir=true
[08:13:16.975040] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$RT229HT.txt IsDir=false
[08:13:16.975118] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID> IsDir=true
[08:13:16.975326] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$ID8KSPG.txt IsDir=false
[08:13:16.975447] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID>\$ID8KSPG.txt IsDir=false
[08:13:16.975598] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID> IsDir=true
[08:13:16.975815] Created   ChangeType=Created FullPath=D:\$RECYCLE.BIN\<SID>\$RD8KSPG.txt IsDir=false
[08:13:16.976023] Changed   ChangeType=Changed FullPath=D:\$RECYCLE.BIN\<SID> IsDir=true
[08:13:16.988001] Resolved  RPath=...\$RDLH4PL   IPath=...\$IDLH4PL   DetectedAt=08:13:16.972945 Version=2 OriginalSize=56 DeletionTime=2026-07-15 08:13:16.964 OriginalPath=D:\tmp\ProbeTest\Source
[08:13:16.988477] Resolved  RPath=...\$RVKCJNB.txt IPath=...\$IVKCJNB.txt DetectedAt=08:13:16.974436 Version=2 OriginalSize=22 DeletionTime=2026-07-15 08:13:16.967 OriginalPath=D:\tmp\ProbeTest\delete_complete.txt
[08:13:16.988898] Resolved  RPath=...\$RT229HT.txt IPath=...\$IT229HT.txt DetectedAt=08:13:16.975096 Version=2 OriginalSize=20 DeletionTime=2026-07-15 08:13:16.969 OriginalPath=D:\tmp\ProbeTest\delete_normal.txt
[08:13:16.989149] Resolved  RPath=...\$RD8KSPG.txt IPath=...\$ID8KSPG.txt DetectedAt=08:13:16.975928 Version=2 OriginalSize=30 DeletionTime=2026-07-15 08:13:16.972 OriginalPath=D:\tmp\ProbeTest\write_target.txt
[08:13:23.158511] === RecycleBinProbe 終了 (出力イベント数=20) ===
```

(`<SID>`は`S-1-5-21-...-1001`を短縮、`RPath`/`IPath`の`...`は`D:\$RECYCLE.BIN\<SID>\`を省略。
`IFileHex`は紙面の都合上省略、生ログには全件含まれる)

### 分かったこと

- **フォルダ自体もゴミ箱に送られ、`$I`ファイルから正しく元パスを解決できる。** `Source`フォルダの
  削除は`$RDLH4PL`(拡張子なし、`IsDir=true`)というエントリになり、`OriginalPath=D:\tmp\ProbeTest\Source`
  まで正しく復元できた。**ファイルは`$R<ランダム>.拡張子`という命名だが、フォルダは拡張子が付かない**
  という違いがある(ファイル名だけで両者を区別できる)。なお`OriginalSize=56`が何を表すか
  (NTFSのディレクトリエントリ自体のサイズと思われるが未確認)は不明瞭で、フォルダの場合の
  `OriginalSize`フィールドの意味は要検証
- **一括削除(4件同時)でも取りこぼしなく全件解決できた。** キューベースの設計(検知はキューに積むだけ、
  解決は別スレッド)が、複数ファイルがほぼ同時に飛んでくるケースでも機能することを確認
- 4件とも独立した`$I`/`$R`ペアになり、削除日時(`DeletionTime`)もミリ秒単位でわずかにズレている
  (08:13:16.964〜.972)ため、削除された順序も分かる
- 4件とも通常削除(ゴミ箱経由)のパターンだったこと自体が、Shift+Deleteではなく通常のDeleteキーで
  削除されたことの間接的な証拠になる(ToolBの調査で「Shift+Deleteは`$RECYCLE.BIN`に一切痕跡を
  残さない」ことが分かっているため。[ToolB-EtwFileProbe/README.md](../ToolB-EtwFileProbe/README.md)参照)

## 既知の不具合(発見・修正済み)

バージョン2のパスデコードで、`pathLengthChars`がnull終端文字を含む文字数であることを見落とし、
デコード結果の末尾に不可視のnull文字(`\0`)が残ってログ表示が崩れるバグがあった(実機で発見)。
null終端で切り詰めるよう修正済み。
