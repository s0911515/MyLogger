# ToolI-SaclProbe

指定フォルダに**SACL(システムアクセス制御リスト、監査ACE)**を設定し、その結果Windows
セキュリティ監査ログに記録される内容を、生XML・Windows公式のフォーマット済みメッセージ・
個別フィールドの3通りで、可能な限り隅々まで記録する専用プローブ。

MyLogger本体は現状SACLベースのオブジェクトアクセス監査を使っていないが(ファイル監視は
`FileSystemWatcher`/ETW中心)、SACLで「誰が・いつ・どのアクセス種別で」ファイルを操作したかが
どこまで捕捉できるかを実機で評価するためのツール。

実機で採取した生ログ・加工後CSV・LogFormatter出力のサンプルを[`SAMPLELOG/`](SAMPLELOG)に
同梱している(`setup-test-env.ps1`のフォルダ単位テストを含む一連の操作の実測結果)。

## 仕組み

NTFSオブジェクト(ファイル・フォルダ)にSACLを設定し、「オブジェクトアクセス > ファイルシステム」
監査サブカテゴリを有効化すると、SACLで指定した種類のアクセスが行われるたびにSecurityログに
イベントが記録される。本ツールは対象フォルダに **Everyone に対する、下記`AuditedRights`
(読み取り1種+書き込み系+削除系+権限変更系、成功・失敗両方)** の監査ACEを設定し(コンテナ・
オブジェクト継承つき、つまり配下のファイル・サブフォルダにも適用)、以下のイベントIDを購読する。

| EventID | 内容 | ObjectNameを含むか |
|---|---|---|
| 4656 | ハンドル要求(最も情報量が多い。AccessMask・AccessList・AccessReason付き) | ○ |
| 4663 | オブジェクトへのアクセス試行(実際に行われたアクセス) | ○ |
| 4658 | ハンドルクローズ | **×(HandleIdのみ)** |
| 4660 | オブジェクト削除 | **×(HandleIdのみ)** |
| 4670 | オブジェクトの権限(DACL)変更 | ○ |
| 4907 | オブジェクトの監査設定(SACL)変更。**本ツール自身がSACLを設定した操作もここに記録される** | ○ |

### SACLの設定粒度: 誰に・どこに・何を(+いつ)

SACLの監査ACE(`SYSTEM_AUDIT_ACE`)は、大きく3つの軸を組み合わせて設定する。

| 軸 | 意味 | .NETでの指定方法 | 本ツールでの設定 |
|---|---|---|---|
| **誰に**(トラスティー) | どのユーザー・グループのアクセスを監査対象にするか | `FileSystemAuditRule`のコンストラクタ第1引数(`SecurityIdentifier`または`IdentityReference`) | `WellKnownSidType.WorldSid`(Everyone)。特定の1ユーザー・特定のグループのみに絞ることもできる |
| **どこに**(オブジェクト+継承範囲) | どのファイル・フォルダに適用し、配下にどこまで及ぼすか | SACLを設定する対象のパス自体 + `InheritanceFlags`(`ContainerInherit`/`ObjectInherit`)・`PropagationFlags`(`NoPropagateInherit`/`InheritOnly`) | 対象フォルダ自身 + `ContainerInherit`(サブフォルダに継承)+ `ObjectInherit`(直下のファイルに継承)。つまり配下ツリー全体に及ぶ |
| **何を**(アクセス権・操作種別) | 読み取り・書き込み・削除・権限変更等、どの操作を監査対象にするか | `FileSystemRights`(フラグの組み合わせ) | 下記`AuditedRights`(ReadData+Write系+Delete系+権限変更系。実機での試行錯誤の末に絞り込んだ) |

さらに4つ目の軸として、**いつ(成功時/失敗時)**も指定できる(`AuditFlags.Success` /
`AuditFlags.Failure`)。本ツールは両方(`Success | Failure`)を有効化している(拒否された
アクセス試行も含め、SACLで何が捕捉できるかを網羅的に知ることが目的のため)。

実機で設定後に確認したSDDL(SACL設定完了ログより)を分解すると、この4軸がそのまま1つの
ACE文字列に現れているのが分かる。

```
S:AI(AU;OICISAFA;CCDCLCRPDTCRSDWDWO;;;WD)
     ^^ ^^^^^^^^ ^^^^^^^^^^^^^^^^^^ ^^ ^^
     |  |        |                 |  └─ 誰に: WD = Everyone (World)
     |  |        |                 └──── (オブジェクト種別GUID。未使用)
     |  |        └────────────────────── 何を: 下記AuditedRights相当のカスタムアクセスマスク
     |  └─────────────────────────────── いつ・どこに: OI+CI(継承)+SA+FA(成功時・失敗時とも監査)
     └────────────────────────────────── ACEの種類: AU = SYSTEM_AUDIT_ACE_TYPE(監査ACE)
```

(`FullControl`だった旧版では`FA`=`FILE_ALL_ACCESS`という1つの総称コードだったのに対し、
絞り込んだ現在は個別の権限ビットの組み合わせがそのままカスタムマスクとして展開されている)

### 「何を」の全選択肢と、本ツールでの絞り込み

`FileSystemRights`で指定できる主な値は以下の通り(複合フラグは省略)。

| 値 | 意味 | 本ツールでの採用 |
|---|---|---|
| `ReadData` / `ListDirectory` | 内容の読み取り・一覧表示 | **採用**(閲覧されたこと自体も追跡したいため明示的に残す) |
| `WriteData` / `CreateFiles` | 内容の書き込み・直下へのファイル作成 | 採用 |
| `AppendData` / `CreateDirectories` | 追記・直下へのサブフォルダ作成 | 採用 |
| `ReadExtendedAttributes` | 拡張属性の読み取り | 除外(閲覧だけで発生しノイズになる) |
| `WriteExtendedAttributes` | 拡張属性の書き込み | 採用 |
| `ExecuteFile` / `Traverse` | 実行・通過 | 除外(実行監視が目的でなければ不要) |
| `DeleteSubdirectoriesAndFiles` | 配下の一括削除権 | 採用 |
| `ReadAttributes` | 基本属性(タイムスタンプ等)の読み取り | 除外(**エクスプローラーの表示だけで大量発生する最大のノイズ源**) |
| `WriteAttributes` | 基本属性の書き込み | 採用 |
| `Delete` | 削除 | 採用 |
| `ReadPermissions`(`READ_CONTROL`) | DACL/所有者の読み取り | 除外(ACL閲覧の追跡が目的でなければ不要) |
| `ChangePermissions` | DACLの変更 | 採用(発生頻度は低いが権限奪取の兆候として高シグナル) |
| `TakeOwnership` | 所有者の変更 | 採用(同上) |
| `Synchronize` | ハンドル同期用の内部フラグ | 除外(**Win32の`CreateFile`がほぼ常に自動付与するため、ほぼ100%ノイズ**) |

実機検証で、`FullControl`のままだと「何も操作していないのに」大量のイベントが記録されることが
分かった(詳細は次項)。特に`Synchronize`(ほぼ全アクセスに自動付与)と`ReadAttributes`
(エクスプローラーの表示だけで発生)が支配的だったため、この2つを含む読み取り系
(`ReadExtendedAttributes`・`ReadPermissions`・`ExecuteFile`も含む)を除外し、内容の変更・
削除・権限変更(高シグナル)と`ReadData`(閲覧の追跡価値を優先して明示的に残した)だけを
監査対象にした。ソースコードでは`AuditedRights`という名前の定数にまとめている。

```csharp
const FileSystemRights AuditedRights =
    FileSystemRights.ReadData
    | FileSystemRights.WriteData
    | FileSystemRights.AppendData
    | FileSystemRights.Delete
    | FileSystemRights.DeleteSubdirectoriesAndFiles
    | FileSystemRights.WriteAttributes
    | FileSystemRights.WriteExtendedAttributes
    | FileSystemRights.ChangePermissions
    | FileSystemRights.TakeOwnership;
```

### 重要な制約: `AuditedRights`はイベントの発火条件であり、`AccessMask`の内容を絞り込むものではない(実機で発見)

実機での再検証(`D:\tmp\ProbeTest`を対象に約33秒、無操作+一部手動操作)で、対象フォルダ一致
244件のうち121件(約半分)が、`AccessMask=0x1`(`ReadData`のみ)や`AccessMask=0x100081`
(`Synchronize`+`ReadAttributes`+`ReadData`)という**閲覧のみ**のアクセスだった。

ここで`AccessMask=0x100081`に**除外したはずの`ReadAttributes`ビットが含まれている**点が
重要な発見。`AuditedRights`によるビットの絞り込みは「そのアクセス要求を監査対象にするか
どうかの判定」(要求されたアクセスマスクと監査ACEのマスクに1ビットでも重なりがあれば発火)
にしか効かず、**一度発火すると、ログに記録される`AccessMask`はSACLで絞ったビットではなく
アプリが実際に要求した全ビットがそのまま出る。** `CreateFile`は通常`Synchronize`・
`ReadAttributes`・`ReadData`をまとめて1回で要求するため、`ReadData`を監査対象に含めている
限り、除外したはずの`ReadAttributes`起因の閲覧アクセスも実質的に素通りする。

この閲覧ノイズの発生源は`explorer.exe`だけとは限らない。実機では**テキストエディタ
(秀丸エディタ)がバックグラウンドで対象フォルダを定期的にポーリングしていたことも確認した**
(該当ファイルを開いていなくても発生しうる)。`ReadData`をあえて残す設計(閲覧の追跡価値を
優先)を採用している以上、このようなノイズは想定内として受け入れる方針としている。

### さらに絞り込みたい・広げたい場合

3軸それぞれ、対応する箇所を変更すればよい。

- **誰に**を絞る: `WellKnownSidType.WorldSid`を`new SecurityIdentifier(userOrGroupSid)`や
  `NTAccount("DOMAIN\\User").Translate(typeof(SecurityIdentifier))`に置き換える
- **どこに**を絞る: 対象フォルダ自体は変えず、`InheritanceFlags.None`にすればそのフォルダ
  直下のみ(継承なし)、`PropagationFlags.InheritOnly`にすればフォルダ自身は対象外で配下のみ、
  といった調整ができる
- **何を**をさらに絞る/広げる: `AuditedRights`定数の中身を増減する。例えば「削除だけ知りたい」
  なら`FileSystemRights.Delete`のみに、逆に「SACLで捕捉できる全範囲を網羅的に知りたい」なら
  `FileSystemRights.FullControl`に戻せばよい(ただし後者は上記ノイズが再発する)

### 4658/4660のObjectName欠落とHandleId突合

4658(ハンドルクローズ)と4660(削除)は、イベント自体に`ObjectName`フィールドを含まない
(`HandleId`のみ)。そのため、これらを愚直にフィルタすると常に除外されてしまい、特に**削除
イベントを取りこぼす**。本ツールは4656/4663/4670/4907で観測した`HandleId`→`ObjectName`の
対応をメモリ上に保持しておき、後から届く4658/4660の`HandleId`が対象フォルダ配下だったものと
一致すれば「対象フォルダの操作である」と判定し、ログに`ResolvedObjectName=...`として付記する
(元のイベントデータには無い情報のため、このツールが解決したことを明示している)。

**`HandleId`単体は突合キーとして使えない(実機で確認・修正済み)。** `HandleId`はOSの
ハンドルテーブル値そのもので、(1)ハンドルテーブルは**プロセスごとに独立**しており数値は
プロセスをまたいで一意ではない、(2)ハンドルがクローズされると同一プロセス内でも**すぐに
別オブジェクトのオープンに再利用される**(実機で、同一プロセスが`sample.txt`をクローズした
直後に別オブジェクト(親フォルダ)を開いた際、全く同じ`HandleId`が再度現れることを確認した)。
そのため突合キーは`ProcessId`+`HandleId`の組とし、さらに4658/4660で解決したエントリは
その場でマップから削除する(番号再利用による誤対応付けを防ぐため)。

### なぜ復元処理があるのか

SACLはEveryone(誰に)+配下ツリー全体(どこに)という広い範囲で設定するため、`AuditedRights`
(何を)を絞り込んだ現在も、放置すると対象フォルダへのあらゆるアクセスで際限なくセキュリティ
ログが増え続けることに変わりはない。そのため**終了時(Ctrl+C)に元の
SACLへ必ず復元する**(元のSACLをSDDL文字列として起動時に退避し、終了時にそのまま書き戻す)。
監査サブカテゴリ自体の有効化はシステム全体の低コストな設定のため、
[ToolC-ProcessAuditProbe](../ToolC-ProcessAuditProbe)と同様に有効化したまま残す(復元しない)。

## テスト用フォルダの準備

毎回手動でフォルダ・ファイルを作る代わりに、probe-tools共通の
[`setup-test-env.ps1`](../setup-test-env.ps1)(ToルA〜D等でも使っているもの)を使う。
書き込み・削除・コピー/移動/リネーム用のファイルが一式そろった状態で `D:\tmp\ProbeTest` に
作り直される(既存があれば削除される)。

```powershell
.\probe-tools\setup-test-env.ps1
# 既定は D:\tmp\ProbeTest。パスを変えたい場合:
.\probe-tools\setup-test-env.ps1 -TestRoot D:\tmp\MySaclTest
```

作られる構成:

```
D:\tmp\ProbeTest\
  write_target.txt      (上書き保存のテスト用)
  delete_normal.txt     (通常削除のテスト用)
  delete_complete.txt   (完全削除のテスト用)
  Source\
    copy_source.txt     (コピーのテスト用)
    move_source.txt     (移動のテスト用)
    rename_source.txt   (リネームのテスト用)
```

`Source`はSACL設定前から存在するサブフォルダなので、ToルIをこのルートに向けて起動すると
**既存の子フォルダ・子ファイルにも監査ACEが継承されること**(「どこに」軸の確認)も併せて
検証できる。

### テスト時の注意: 何も操作していなくてもイベントが出る(実機で確認・`AuditedRights`絞り込みで大幅軽減)

`FullControl`だった旧版では、ToルIを起動しただけで以下2種類のイベントが**自分の操作なしに**
記録されていた。

- **ツール自身のSACL設定によるイベント(`4907`、`ProcessName=...\SaclProbe.exe`)**: 対象
  フォルダ本体だけでなく、既存の子ファイル・子フォルダ**それぞれ**に対して1件ずつ発生する
  (継承が即座に伝播するため)。バグではなく実際の挙動で、`AuditedRights`に絞り込んだ現在も
  引き続き発生する(避けられない)
- **explorer.exeによる継続的なアクセス(`4656`/`4663`/`4658`)**: 対象フォルダ(または親
  フォルダ)をExplorerウィンドウで開いたままにしていると、SACL変更自体がディレクトリ変更
  通知としてExplorerに伝わり、自動的に再列挙・再描画するために発生していた。原因は
  `ReadData`・`ReadAttributes`・`READ_CONTROL`(`ReadPermissions`)へのアクセス

**`AuditedRights`への絞り込み後、`ReadAttributes`/`ReadPermissions`(explorer.exeノイズの
直接原因)と、ツール自身の設定後確認読み取りが使っていた`ReadPermissions`/`ACCESS_SYS_SEC`を
監査対象から外したことで、両方のノイズが実機で解消したことを確認した。** 操作なしで起動〜
Ctrl+C停止しただけの再検証では、対象フォルダ+ファイル1個の構成で`4907`が2件(避けられない
自己申告分)出るのみで、`4656`/`4658`等の余計なイベントは一切発生しなかった。実際に読み取り・
書き込み・削除を行った別の検証では、システム全体で観測した65件中17件が正しく対象フォルダの
操作として抽出され(残り48件は無関係な他プロセス・他フォルダのイベントとして自動的に除外)、
`ResolvedObjectName`による削除イベントの突合も引き続き正しく機能した。

それでも上記の`4907`(ツール自身のSACL設定)はテストのたびに必ず出るため、ログを読むときは
`ProcessName=...\SaclProbe.exe`の行と、自分が実際に行った操作に対応する行(別プロセス・別
タイムスタンプ)を見分けること。**ただし後述の加工後CSVを使えば、この見分け作業自体が不要になる**
(閲覧のみのアクセスはCSVに出ないため)。

## 生ログと加工後CSVの2種類を出力する

ログファイル(`*.log`)・CSV(`[ログファイル名].summary.csv`)とも、**対象フォルダ一致
イベントを1件も除外せず、閲覧のみのアクセスも含めて全件出力する**(隅々まで記録するという
本ツールの目的そのもの。閲覧アクセスを捨てるとその後の二次加工で使えなくなるため、絞り込みは
行わない)。CSVは生XML・Descriptionを省いた列形式で、Excel等での二次加工(フィルタ・ピボット・
`分類`列での絞り込み等)を想定している。

- **列**: `時刻,EventID,イベント種別,分類,対象,対象種別,対象種別備考,プロセス名,プロセスID,ユーザー,アクセス内容,HandleId,RecordId`
  - `アクセス内容`は`AccessMask`を`FileSystemRights`名に変換したもの+元の16進値。生XMLや
    `Description`は含めない(詳細を見たい場合は`RecordId`を手がかりに生ログ側を検索する)
  - **`分類`列**: そのハンドルの生涯(オープン〜クローズ)を通じて、一度でも書き込み・削除・
    権限変更系のアクセス(`SignalRights` = `AuditedRights`から`ReadData`を除いたもの)があれば
    `シグナル`、無ければ`閲覧のみ`と判定する(**行を除外するのではなく分類するだけ**)。
    `4670`(権限変更)・`4907`(SACL変更)・`4660`(削除)はAccessMaskを持たない、または
    そのハンドルで書き込みが無くても重要な事実のため無条件で`シグナル`。`4658`(クローズ)は
    対応する`4656`/`4663`の履歴に従う
  - **`対象種別`列(`ファイル`/`フォルダ`/`判別不明`)と`対象種別備考`列**: セキュリティ監査
    イベント自体にはファイル/フォルダを区別するフィールドが無い(`ObjectType`は常に`File`。
    実機で、明らかにフォルダのオブジェクトでも`ObjectType=File`になることを確認済み)ため、
    以下の優先順位で判定する。
    1. 拡張子があれば`ファイル`(ファイルシステムは見ない、推定)
    2. 拡張子が無ければ`Directory.Exists()`で確認(`フォルダ`)
    3. それでも判定できなければ`File.Exists()`で確認(拡張子の無いファイル対応、`ファイル`)
    4. いずれも実在しなければ(記録時点ですでに削除・リネーム済み等)`判別不明`
    
    1.は推定、2.3.はCSV書き出し時点のファイルシステムを見た確認結果であり確信度が異なるため、
    `対象種別備考`列に判定方法をそのまま残す(例: `拡張子から推定(ファイルシステムは未確認)`・
    `Directory.Existsで確認`・`拡張子なし、かつ記録時点で対象が存在しないため判定不能`)
- **文字コード**: UTF-8(BOM付き)。Excelでダブルクリックしてもそのまま日本語が読める
- ログファイルと同様、`FileMode.Append`で追記していく(複数回の実行結果が同じCSVに積み上がる。
  ヘッダ行はファイルが空の時だけ書き込む)

実機検証(読み取り・書き込み・削除を実施)で、対象フォルダ一致17件全件がCSVに出力され、
`Get-Content`(閲覧のみ)による`4656`/`4663`/`4658`が`分類=閲覧のみ`、実際の書き込み・削除は
`分類=シグナル`として正しく区別されることを確認した。

## 補助ツール: LogFormatter(操作単位への集約、CSV出力)

[`LogFormatter/`](LogFormatter)は、上記CSVをさらにエンドユーザー向けに2段階で加工する
独立した補助ツール(別exe、`LogFormatter/dist/LogFormatter.exe`)。出力もCSV(Excel等で
そのまま開いて見やすい形)。

- **第1段階(ハンドル単位への集約)**: CSVは1イベント=1行だが、`ProcessId`+`HandleId`
  (同一ハンドルの生涯: オープン→アクセス試行→クローズ、または→削除)で相関が取れるものを
  1つの「操作セグメント」にまとめる。操作種別は日本語(`読み取り`/`書き込み`/`属性変更`/
  `削除`/`リネーム/移動/削除`/`権限変更`/`SACL変更`)で判定する
- **除外(実機検証で確認済みのノイズ)**: **フォルダに対する「読み取り」は出力から除外する。**
  実機で、1回のフォルダコピー操作だけで対象フォルダ・親フォルダ・祖父フォルダの**複数階層に
  同時多発的に**読み取りが記録され(単なる一覧表示によるもの)、実際に何が起きたかの情報を
  一切含まないことを確認した。フォルダに対するそれ以外の操作(削除・リネーム/移動・書き込み等)
  やファイルに対する読み取りは除外しない(生ログ側では引き続き全件を確認できる)
- **第2段階(繰り返しの集約)**: **同じユーザー・同じ対象ファイル・同じプロセス(PID)・
  同じ操作種別**のセグメントは、同一操作の繰り返し(例: エクスプローラーが同じフォルダを
  定期的に読み取りポーリングする等)とみなし、1行に集約する(`回数`・`開始時刻`〜`終了時刻`
  で表す)。相関で断定できないもの(ユーザー・ファイル・プロセス・操作種別のいずれかが
  異なるもの)は決してまとめない
- **第3段階(推定コピーの検出、ヒューリスティック)**: コピーは監査ログ上「コピー元の読み取り」
  「コピー先の書き込み/権限変更」という**別々のオブジェクトへの別操作**としてしか現れず、
  OSレベルでの紐付け情報は無い。そのため同じプロセス(PID)・同じユーザー・ファイル名(パスは
  除く)が一致・5秒以内という条件を**すべて**満たす場合に限り、`推定コピー`として1行にまとめる
  (対象列は`コピー元 → コピー先`の形)。**これは断定ではなく推定**であるため、操作列に必ず
  `推定コピー`と明示し、他の(断定できる)分類とは意図的に区別している

### 使い方

```powershell
dotnet run --project probe-tools\ToolI-SaclProbe\LogFormatter -- <ToolIのCSVファイル> [出力CSVファイル]
# または同梱のビルド済みexe
probe-tools\ToolI-SaclProbe\LogFormatter\dist\LogFormatter.exe <ToolIのCSVファイル> [出力CSVファイル]
```

`[出力CSVファイル]`省略時は`<CSVファイル>`の`.summary.csv`を`.readable.csv`に置き換えた名前
(例: `saclprobe.summary.csv` → `saclprobe.readable.csv`)。実行のたびに**上書き**される
(元CSVが更新されていれば再実行で最新化する前提の変換ツールのため)。UTF-8(BOM付き)で
Excelでもそのまま日本語が読める。

### 出力列

`開始時刻,終了時刻,回数,操作,対象種別,対象,プロセス名,プロセスID,ユーザー,RecordId範囲`

- **`対象種別`列**: ToルI本体のCSVから引き継いだ`ファイル`/`フォルダ`/`判別不明`(判定方法は
  ToルI本体側の説明を参照)
- **`操作`列**: `{対象種別}の{操作}`という形式で、対象種別を含めて表示する(例:
  `ファイルの読み取り`・`フォルダの削除`・`判別不明のリネーム/移動/削除`)。同じ「削除」でも
  ファイルかフォルダかで意味合いが変わるため、一目で分かるようにしている。判定できなかった
  場合も`判別不明の...`として不確かさを隠さない

### 出力例(実機、2026-07-20)

`setup-test-env.ps1`のフォルダ単位テスト(フォルダごとの通常削除・完全削除)を行った際の
実際の出力(抜粋)。

```
開始時刻,終了時刻,回数,操作,対象種別,対象,プロセス名,プロセスID,ユーザー,RecordId範囲
2026-07-20 06:23:17.899977,2026-07-20 06:23:17.899981,1,判別不明の読み取り,判別不明,D:\tmp\ProbeTest\delete_folder_normal,C:\Windows\explorer.exe,0x119c,WIN-DESK-2022\s0911,3904351-3904353
2026-07-20 06:23:17.900135,2026-07-20 06:23:17.900222,1,ファイルの削除,ファイル,D:\tmp\ProbeTest\delete_folder_normal\inner.txt,C:\Windows\explorer.exe,0x119c,WIN-DESK-2022\s0911,3904361-3904363
2026-07-20 06:23:17.900271,2026-07-20 06:23:17.904052,2,判別不明のリネーム/移動/削除,判別不明,D:\tmp\ProbeTest\delete_folder_normal,C:\Windows\explorer.exe,0x119c,WIN-DESK-2022\s0911,3904367-3904384
2026-07-20 06:23:19.572543,2026-07-20 06:23:19.572551,1,判別不明の削除,判別不明,D:\tmp\ProbeTest\delete_folder_complete,C:\Windows\explorer.exe,0x119c,WIN-DESK-2022\s0911,3904929-3904931
```

**`inner.txt`(拡張子あり)は即座に`ファイル`と確定できる一方、`delete_folder_normal`/
`delete_folder_complete`(拡張子なし)は、リネーム・移動・削除のいずれによっても元のパスが
その場で消えてしまうため、`Directory.Exists()`が間に合わず`判別不明`のままになることが多い。**
これはToルI本体側の既知の限界がそのまま反映されたものであり、LogFormatter側で無理に断定は
しない(誤ってフォルダ読み取りとして除外してしまうことも無い。除外条件は`対象種別==フォルダ`
と確定した場合のみのため)。

### 判定方法(文字列一致ではなくビット演算)

操作種別は`アクセス内容`列の文字列一致では判定しない。**CSVの`アクセス内容`は.NETの
`[Flags]`列挙型`FileSystemRights`の`ToString()`結果であり、個々のビット名ではなく複合名
(例: `WriteData`+`AppendData`+`WriteAttributes`+`WriteExtendedAttributes`がまとめて
`Write`とだけ表示される)ことがあるため**、文字列一致だと取りこぼす。そのためLogFormatterは
`アクセス内容`列末尾に付記した生の16進AccessMaskを読み直し、`FileSystemRights`のビット演算
(グループ内の全イベントのAccessMaskをOR演算してから判定)で操作種別を決める。優先度は
SACL変更 > 削除 > 権限変更 > リネーム/移動/削除 > 書き込み > 属性変更 > 読み取り > 不明。

### 「削除」と「リネーム/移動/削除」の区別、あえて曖昧なラベルにしている理由

NTFSでは**リネーム・移動(同一ボリューム内)にも`Delete`権限が要求される**(ファイルの識別子を
変更する操作として扱われるため)。そのため当初は「`Delete`ビットが要求された」というだけで
「削除」と判定していたが、これだと実際には消えていないリネーム・移動も「削除」と誤分類されて
しまうことが、[probe-tools/setup-test-env.ps1](../setup-test-env.ps1)の実機テストで発覚した。

```
権限変更  新規 テキスト ドキュメント.txt   ← エクスプローラーでの新規作成(ACL継承)
削除      新規 テキスト ドキュメント.txt   ← ★誤り。直後のリネーム操作だった
削除      Source\move_source.txt          ← ★誤り。Source外への移動操作だった
削除      Source\rename_source.txt        ← ★誤り。リネーム操作だった
削除      delete_normal.txt               ← 正しい(実際に削除された)
```

修正として、`EventID=4660`(オブジェクトが実際に削除された、という確定的な証拠)を伴う場合のみ
「削除」とし、`Delete`ビットの要求はあるが`4660`を伴わない場合は**`リネーム/移動/削除`**という
ラベルにした(「移動」ではなく3語を並べているのは意図的。理由は次項)。

#### なぜ「リネーム/移動」ではなく「リネーム/移動/削除」なのか(実機で判明)

`delete_normal.txt`(通常削除)の生データを詳しく見ると、**Explorerでの1回の削除操作が、
実際には3回のハンドル開閉に分かれていた。**

```
1回目: Delete要求→即クローズ                          (4660なし) → リネーム/移動/削除
2回目: Delete要求→アクセス試行→ 4660(実際に削除!) →クローズ → 削除
3回目: Delete要求→アクセス試行→クローズ                (4660なし) → リネーム/移動/削除
```

つまり削除操作の前後に「削除できるかどうかの権限チェックだけして、その場では何もしない」
空振りアクセスが発生し、実際に削除を完了させたのは3回のうち1回だけだった。このため、
`Delete`ビットの要求はあるが`4660`を伴わないセグメントは、**(a)リネーム、(b)移動、
(c)同じ削除操作の一部である空振りアクセス、のいずれもあり得て単体では区別がつかない。**
断定できないものを無理に「リネーム/移動」だけに絞ってしまうと(c)の可能性を見落とすため、
`リネーム/移動/削除`という曖昧さを保ったラベルにしている。**信頼できるのは「削除」ラベルの
有無であって、「リネーム/移動/削除」ラベルの有無ではない。** 同じ対象に対して`削除`ラベルの
セグメントが別途あれば、そちらが確定的な証拠になる。

移動先のパスやリネーム後の名前が監査ログに一切残らない点は変わらないため、リネームと移動を
これ以上区別することはできない(そもそも同一ボリューム内の移動は、NTFS内部では
`FileRenameInformation`というディレクトリエントリの付け替えだけで完結し、リネームと全く同じ
API呼び出しになる。データの読み書きを一切伴わないため「読み取り→書き込み」のパターンにも
ならない。別ボリュームへの移動であれば、NTFSはディレクトリエントリを共有できないため
実際には「コピー(読み取り+書き込み)→元ファイルの削除(4660を伴う)」という別処理になり、
下記の「推定コピー」+「削除」の組み合わせとして部分的に痕跡が残るはずだが、未検証)。

### 既知の限界: 完全削除(Shift+Delete)で`4660`が出ないケースがある(未解明・追加検証は保留)

`setup-test-env.ps1`が用意する`delete_complete.txt`(完全削除用)をエクスプローラーで
Shift+Delete相当の操作で消した際の生データは以下の通りだった。

```
4656 ReadAttributes,Delete,Synchronize  HandleId=0x6268
4658 (クローズ)                         HandleId=0x6268
4656 Delete                             HandleId=0x573c
4658 (クローズ)                         HandleId=0x573c
```

`Delete`権限を要求するハンドルが2回開閉されているが、**`4660`(実際に削除された証拠)が
一度も記録されていない。** そのため現在の実装ではこれも「リネーム/移動/削除」に分類される
(実際には完全に削除されているにもかかわらず、上記の通り誤りとまでは言い切れない曖昧な
ラベルなので、これは「区別できない」の範囲内ではある)。[ToルH-RecycleBinProbe](../ToolH-RecycleBinProbe)
で判明している「Shift+Deleteは`$RECYCLE.BIN`に一切痕跡を残さない」という挙動と関係がある
可能性があるが、確認ダイアログ待ちで観測ウィンドウ外に出ただけなのか、完全削除がそもそも
`4660`を伴わない別経路を通るのかは、現時点では未確認(追加の実機検証は今回は見送り)。

### コピーの実機検証結果(2026-07-20)

`setup-test-env.ps1`の`Source\copy_source.txt`をエクスプローラーでコピーした際、推定コピー
検出により以下のように1行へ集約されることを確認した。

```
推定コピー  D:\tmp\ProbeTest\Source\copy_source.txt → D:\tmp\ProbeTest\copy_source.txt  explorer.exe
```

裏付けとなった生の挙動: `Source\copy_source.txt`への`読み取り`と、`copy_source.txt`
(コピー先、拡張子直前までファイル名が一致)への`権限変更`(新規オブジェクトへのACL継承)が、
同一プロセス・同一ユーザーで1ミリ秒未満の差で発生していた。

### 既知の落とし穴(実機で発見・修正済み): 単純な(ProcessId,HandleId)グルーピングは危険

開発中、単純に`ProcessId`+`HandleId`だけでCSV全体をグループ化したところ、**同一プロセスが
別々のタイミングで同じHandleId番号を使い回した際に、時間的に無関係な2つの削除操作
(`a.txt`削除→`b.txt`削除)が1つの操作に誤って統合されてしまう不具合**が実機で見つかった
(ToルI本体の「HandleIdは使い捨ての番号」という既知の特性が、ここでも問題になった)。

対策として、CSVを時系列順に走査し、`4658`(クローズ)・`4660`(削除)が来た時点でその
`(ProcessId,HandleId)`の「生涯」を確定させてグループを閉じ、同じキーが後から再登場したら
別グループとして扱うよう修正した。修正後、`a.txt`削除・`b.txt`削除がそれぞれ独立した行として
正しく分離されることを実機で確認済み。

### ファイル/フォルダの判定方法、そしてそれが監査ログだけでは不可能な理由

フォルダに対する読み取りを除外する(前述)には、そもそも対象がファイルかフォルダかを
判定する必要がある。**しかしセキュリティ監査イベント自体には、その区別を示すフィールドが
存在しない。** 実機で、明らかにフォルダのオブジェクト(`ObjectName=D:\tmp\ProbeTest\delete_folder_complete`)
に対する`4907`イベントですら`ObjectType=File`と記録されていることを確認した(Windowsの
オブジェクトマネージャーの世界では、フォルダも「File」オブジェクトの一種として扱われるため)。
`AccessList`の表記も`ReadData (または ListDirectory)`のように両論併記されており、
Windows自身がこの区別をしていないことが分かる。

そのため、ログとは独立に**CSV書き出し時点の実際のファイルシステムを見て**判定するしかない。
以下の優先順位で判定する(詳細は前述のCSV列の説明を参照)。

1. 拡張子があれば`ファイル`(ファイルシステムを見ない、確認なしの推定)
2. 拡張子が無ければ`Directory.Exists()`で確認
3. それでも判定できなければ`File.Exists()`で確認(拡張子の無いファイル対応)
4. いずれも実在しなければ`判別不明`

#### 既知の落とし穴(実機で発見・対策済み): 削除直後の判定はレースコンディションになりうる

当初は拡張子を見ずに常に`Directory.Exists()`/`File.Exists()`だけで判定していたところ、
**フォルダへの読み取りの直後(1ミリ秒未満)にそのフォルダ自体が削除されるケース**(削除操作の
一部として発生する「削除できるか確認するための読み取り」)で、ToルIの非同期イベント処理が
`Directory.Exists()`を呼んだ時点ではすでに削除済みになっており、`フォルダ`ではなく`不明`と
判定されてしまう不具合が実機で見つかった(同じフォルダへの直前の読み取りでは正しく`フォルダ`
と判定できていたことから、タイミングの問題と特定できた)。この`不明`はLogFormatterの
フォルダ読み取り除外条件(`対象種別==フォルダ`)に一致しないため、本来除外されるべき
フォルダ読み取りノイズが数件すり抜けていた。

対策として、拡張子による判定(ファイルシステムを見ない、削除後でも判定できる)を最優先にし、
`Directory.Exists()`/`File.Exists()`によるファイルシステム確認は拡張子が無い場合のみの
フォールバックにした。これにより大半のノイズ(拡張子ありのファイル)はこの種のレース
コンディションと無縁になった。一方、拡張子の無いフォルダが削除される瞬間に読み取られた
場合は、依然として判定できないことがある(実機で再現済み)。この場合は無理に断定せず
`判別不明`のまま出力する(LogFormatterのフィルタは`フォルダ`と確定した場合のみ除外するため、
`判別不明`は安全側に倒れて出力に残る)。判定の経緯(推定か確認済みか、確認できなかった理由)は
`対象種別備考`列にそのまま記録する。

## 使い方

```powershell
# 管理者権限のPowerShellで(SACL設定・監査ポリシー変更・セキュリティログの購読に必要)

# ソースから実行
dotnet run --project probe-tools\ToolI-SaclProbe -- <監査対象フォルダ> [ログファイル]

# 同梱のビルド済みexe(.NETランタイム不要)
.\dist\SaclProbe.exe <監査対象フォルダ> [ログファイル]
```

- `<監査対象フォルダ>` は必須。存在するフォルダのフルパスを指定する
- `[ログファイル]` 省略時は実行ファイルと同じフォルダの `saclprobe.log`
- 加工後CSVは`[ログファイル]`から自動導出される(既定なら`saclprobe.summary.csv`)。個別に
  指定するオプションはない
- 起動直後にSACL設定完了のログが出る。その後、対象フォルダでファイル操作を行い、**Ctrl+C**で
  停止すると、SACLを元に戻したうえでログとCSVが確定する

### 注意: 強制終了(kill / Stop-Process / taskkill /F)するとSACLが復元されない

実機で確認済み。`Stop-Process`等でプロセスを強制終了すると`Console.CancelKeyPress`/
`AppDomain.ProcessExit`が実行されず、**設定した監査ACEがフォルダに残ったままになる**。
誤って強制終了してしまった場合は、以下で手動確認・復元できる。

```powershell
# 現在のSACLを確認
icacls "対象フォルダ" /audit
# あるいは
(Get-Acl -Path "対象フォルダ" -Audit).Audit

# 手動で復元(SACLを空にする例)
$sec = Get-Acl -Path "対象フォルダ" -Audit
$sec.SetSecurityDescriptorSddlForm("S:", [System.Security.AccessControl.AccessControlSections]::Audit)
Set-Acl -Path "対象フォルダ" -AclObject $sec
```

## 記録されるログの内容

1イベント1行。以下の4種類の情報を可能な限り隅々まで1行にまとめて記録する。

1. **共通メタデータ**: `EventID`・`RecordId`・`TimeCreated`・`Level`・`Task`・`Opcode`・
   `Keywords`(いずれも数値と表示名の両方)・`ProviderName`・`LogName`・`MachineName`・
   イベントを記録したプロセス(`LoggingProcessId`/`LoggingThreadId`。通常は`lsass.exe`のPID=4)・
   `UserId`
2. **個別フィールド全件**: `<EventData><Data Name="...">`をすべて`Name=Value`形式で列挙
   (`SubjectUserSid`/`SubjectUserName`/`ObjectName`/`HandleId`/`AccessMask`/`AccessList`/
   `AccessReason`/`ProcessId`/`ProcessName`等、イベントIDごとに異なるフィールド集合)
3. **`Description`**: `EventRecord.FormatDescription()`が組み立てる、イベントビューアーの
   「全般」タブに表示されるのと同じ公式の説明文(改行は` / `に置換して1行化)
4. **`RawXml`**: `EventRecord.ToXml()`の生XML全体(イベントビューアーの「詳細」タブのXMLビュー
   相当。改行は1行化)

### 終了時サマリ

Ctrl+C停止時、`元のSACLに復元しました。`の直後に、対象フォルダ一致分のみを対象とした集計を
2行出力する。イベント全件を読まなくても、ノイズと信号の比率をすぐ把握できる。

```
集計(プロセス名別、対象フォルダ一致分のみ): explorer.exe=210 Hidemaru.exe=26 SaclProbe.exe=8
集計(EventID別、対象フォルダ一致分のみ): 4656=81 4658=79 4660=1 4663=74 4907=8 4670=1
```

## 実機での検証結果(2026-07-18、`FullControl`版)

**以下は`AuditedRights`に絞り込む前(`FullControl`)の時点での検証結果。** イベントの構造
(4907での自己検出・4658/4660のHandleId突合等)を示す例としてはそのまま有効だが、実際に発生する
イベント数(特に読み取り系)は現在の絞り込み後より多い。絞り込み後の再検証結果は
「テスト時の注意」の項を参照。

`D:\tmp\SaclTest`(ファイル`sample.txt`を含む)を対象に本ツールを起動し、SACL設定 →
書き込み・新規作成・削除という一連の操作を行った実際のログ(抜粋・整形。`RawXml`は紙面の都合上
一部省略、実際のログには全件のフルXMLが含まれる)。

### 1. 起動直後: SACL設定そのものが4907として記録される

```
[12:12:06.759767] SACL設定完了: Everyone に FullControl の成功・失敗監査(コンテナ・オブジェクト継承)を追加。設定後のSACL(SDDL)=S:AI(AU;OICISAFA;FA;;;WD)
[12:12:07.590385] Event EventID=4907 RecordId=3537806 ... ObjectName=D:\tmp\SaclTest\sample.txt HandleId=0x390 OldSd= NewSd=S:ARAI(AU;IDSAFA;FA;;;WD) ProcessId=0x2248 ProcessName=...\SaclProbe.exe Description=[オブジェクトの監査設定が変更されました。 / ... / 監査設定: / 元のセキュリティ記述子:  / 新しいセキュリティ記述子:  S:ARAI(AU;IDSAFA;FA;;;WD)] RawXml=[...]
[12:12:07.598630] Event EventID=4907 RecordId=3537807 ... ObjectName=D:\tmp\SaclTest HandleId=0x384 OldSd= NewSd=S:ARAI(AU;OICISAFA;FA;;;WD) ...
```

**ツール自身がSACLを設定した操作自体が、対象フォルダとその配下ファイル(継承)の両方について
4907として記録されることを確認した。** `NewSd`フィールドに設定後のSDDLがそのまま入っており、
「いつ・誰が・どのSACLに変更したか」が監査ログだけから追跡できる。

### 2. ファイル書き込み → ハンドル要求・アクセス試行・クローズが一式記録される

```
[12:12:18.123343] EventID=4656 ObjectName=D:\tmp\SaclTest\sample.txt HandleId=0x4fc AccessList=%%1541(SYNCHRONIZE) %%4423(ReadAttributes) AccessMask=0x100080 ProcessId=0x37ec ProcessName=...\claude.exe
[12:12:18.123345] EventID=4658 ResolvedObjectName=D:\tmp\SaclTest\sample.txt(HandleIdから突合) HandleId=0x4fc ProcessId=0x37ec ProcessName=...\claude.exe
```

これは実は本ツールのテスト中、バックグラウンドで動いていた別プロセス(このセッションを実行して
いるClaude Codeのネイティブバイナリ自身)が`sample.txt`にアクセスした際の記録。**SACLは
「誰が」を問わず対象フォルダへのあらゆるアクセスを捕捉するため、意図しない第三者プロセスの
アクセスまで見えてしまう**ことが実機で分かった(検証時のノイズとしてそのまま記録されている)。

### 3. ファイル削除 → 4660にResolvedObjectNameが正しく付与される

`New-Item`で`second.txt`を作成後、`Remove-Item`で削除した際の記録(PowerShellの削除操作が
DELETE権限でハンドルを開き→アクセス試行→削除→ハンドルクローズという流れで記録されている)。

```
EventID=4656 ObjectName=D:\tmp\SaclTest\second.txt HandleId=0x7bc AccessList=%%1537(DELETE) %%4423(ReadAttributes) AccessMask=0x10080 ProcessName=...\pwsh.exe
EventID=4663 ObjectName=D:\tmp\SaclTest\second.txt HandleId=0x7bc AccessList=%%4424(WriteAttributes) AccessMask=0x100 ProcessName=...\pwsh.exe
EventID=4663 ObjectName=D:\tmp\SaclTest\second.txt HandleId=0x7bc AccessList=%%4423(ReadAttributes) AccessMask=0x80 ProcessName=...\pwsh.exe
EventID=4663 ObjectName=D:\tmp\SaclTest\second.txt HandleId=0x7bc AccessList=%%1537(DELETE) AccessMask=0x10000 ProcessName=...\pwsh.exe
EventID=4660 ResolvedObjectName=D:\tmp\SaclTest\second.txt(HandleIdから突合) HandleId=0x7bc ProcessName=...\pwsh.exe TransactionId={00000000-0000-0000-0000-000000000000}
EventID=4658 ResolvedObjectName=D:\tmp\SaclTest\second.txt(HandleIdから突合) HandleId=0x7bc ProcessName=...\pwsh.exe
```

**4660(削除)はイベント自体にObjectNameを持たないが、直前の4656/4663で観測した同一
`HandleId=0x7bc`から`ResolvedObjectName`として正しく元のファイル名を復元できることを実機で
確認した。** これがなければ「何が削除されたか」はSecurityログ単体からは分からない。

## 分かったこと

- **SACL変更そのものが監査対象になる(4907)。** SACLを設定した瞬間から、その設定操作自体が
  ログに残る。対象フォルダ・配下ファイル(継承分)それぞれに1件ずつ記録された
- **4658/4660はObjectNameを持たないため、HandleId経由の突合が必須。** 突合しないと削除
  イベント(4660)を丸ごと取りこぼす(このツールの設計上、最も重要な発見)
- **Everyoneの監査は「誰が」を問わず全アクセスを拾う。** 意図した操作以外のノイズ
  (バックグラウンドプロセスのファイルアクセス等)も記録される。特定ユーザーのみに絞りたい
  場合はSIDを変更すればよいが、本ツールでは意図的にEveryoneを使っている
- **`FullControl`(全操作)で監査すると、`Synchronize`(ほぼ全アクセスに自動付与)と
  `ReadAttributes`(エクスプローラーの表示だけで発生)がノイズの大半を占める。** 実機で
  「何も操作していないのに大量のイベントが出る」ことを確認し、この2つを含む読み取り系を
  `AuditedRights`から除外した(詳細は「SACLの設定粒度」の項)。除外後は操作なしでの起動〜
  停止で自己申告分の`4907`以外のイベントが発生しないことを確認済み
- **1回のファイルアクセスで複数の4663が発生する。** 例えば削除操作では
  `WriteAttributes`→`ReadAttributes`→`DELETE`と、アクセス種別ごとに別々の4663が生成された
  (アプリケーション層の1回の削除呼び出しに対し、カーネル側では複数回のアクセスチェックが
  行われている)
- **`HandleId`は「クローズされたら再利用される、使い捨ての番号」であり、1回の操作に必ず1つ
  対応するとは限らない。** 同一プロセス内でも、あるオブジェクトのハンドルをクローズした直後に
  全く別のオブジェクトを開くと同じ`HandleId`が再度現れる(実機で確認)。また元々プロセスごとに
  独立した値のため、別プロセス同士でたまたま同じ数値になることもある。突合には`ProcessId`との
  組が必須
- **AccessListは`%%数値`のプレースホルダのまま生XMLに入っており、`ToXml()`では解決されない。**
  ただし`FormatDescription()`(Description列)や`Get-WinEvent`は正しく`ReadAttributes`等の
  文字列に解決する。本ツールは両方を記録しているため、生の`%%`コードと解決済み文字列の両方が
  ログから追跡できる

## 既知の不具合(実機で発見、未修正)

`EventLogWatcher`のコールバック内で`EventRecord.TaskDisplayName`を呼ぶと、**誤ったタスク
カテゴリ名が返ることがある。** 実機で以下を確認した。

- 本ツールのログ: `EventID=4656 ... Task=12800(Audit Policy Change)`
- 同じレコード(`RecordId=3537810`)を`Get-WinEvent -FilterXPath "*[System[(EventRecordID=...)]]"`
  で取得した場合: `TaskDisplayName = File System`(こちらが正しい。Task=12800の正式なタスク
  カテゴリは「File System」)

`LevelDisplayName`・`OpcodeDisplayName`・`KeywordsDisplayNames`は同じレコードで両方の取得
方法を比較しても一致しており(`情報`/`情報`/`成功の監査`)、問題は`TaskDisplayName`に限定される。
`EventLogWatcher`のプッシュ型コールバックでのプロバイダーメタデータ解決に何らかの問題がある
と推測されるが、根本原因・.NETランタイム側の既知issueかどうかは未調査。

**回避策・注意点**: `Task`の数値そのもの(`RawXml`にも含まれる)は正しいので、カテゴリ名が
必要な場合は数値から判断するか、`RawXml`/`Description`と一部矛盾する可能性がある
`TaskDisplayName`表示を鵜呑みにしないこと。参考までに主要なTask番号:
`12800`=File System、`12801`=Registry、`13312`=Handle Manipulation、`13568`=Audit Policy Change。

## 監査サブカテゴリ・SACLを手動で確認する方法

```powershell
# 監査サブカテゴリの現在値
auditpol /get /subcategory:"File System"
auditpol /get /subcategory:"Handle Manipulation"

# フォルダのSACL(監査ACE)
icacls "対象フォルダ" /audit
```
