# Sysmon ファイル監視 動作検証テスト項目

## 目的

`config.xml` の監視ルールと `getLog.ps1` の整形出力が、以下を満たすかを確認する。

- 必要なイベント（新規作成・書き込み・コピー・ムーブ・リネーム・通常削除・完全削除）が漏れなく検知/記録されること
- 監視対象外（`D:\tmp\` 以外）の操作や、Windows内部の一時ファイル操作などの**ノイズが出力されないこと**

---

## 事前準備（毎回のテスト前に実施）

1. PowerShellを開き、以下を実行してテスト環境をリセットする。

   ```powershell
   D:\TOOL\Sysmon\setup-test-env.ps1
   ```

2. 出力された `Baseline time (JST)` の時刻をメモする。これ以降に発生したログのみを検証対象とする。
3. リセット後のフォルダ構成は以下の通り。

   ```text
   D:\tmp\SysmonTest\
   ├─ write_target.txt
   ├─ delete_normal.txt
   ├─ delete_complete.txt
   └─ Source\
       ├─ copy_source.txt
       ├─ move_source.txt
       └─ rename_source.txt
   ```

4. すべてのテスト操作は **エクスプローラ上のマウス/キーボード操作**で行う（PowerShellやコマンドでのファイル操作はしない）。
5. 全項目の操作が終わったら `getLog.ps1` を実行し、Baseline time 以降のログを目視で照合する。

---

## テスト項目一覧

| # | 操作 | 手順（エクスプローラ） | 対象ファイル（操作前 → 操作後） | 期待されるログ |
|---|------|------|------|------|
| 1 | 新規作成 | `D:\tmp\SysmonTest` を開き、右クリック →「新規作成」→「テキスト ドキュメント」を作成 | (新規) → `D:\tmp\SysmonTest\new_create.txt` | イベント「作成/コピー」が `new_create.txt` に対して1件 |
| 2 | 書き込み（上書き保存） | `write_target.txt` をメモ帳で開き、1行追記して上書き保存 | `D:\tmp\SysmonTest\write_target.txt` | イベント「作成/コピー」が `write_target.txt` に対して発生するか確認（発生有無自体が確認ポイント） |
| 3 | コピー | `Source\copy_source.txt` をコピーし、`D:\tmp\SysmonTest` にペースト | `Source\copy_source.txt` → `D:\tmp\SysmonTest\copy_source.txt` | イベント「作成/コピー」が貼り付け先パスに対して1件 |
| 4 | ムーブ（フォルダ間移動） | `Source\move_source.txt` を切り取り、`D:\tmp\SysmonTest` に貼り付け（またはドラッグ&ドロップ） | `Source\move_source.txt` → `D:\tmp\SysmonTest\move_source.txt` | 何らかのログが出るか、あるいは無反応か確認（同一ボリューム内移動の検知有無が確認ポイント） |
| 5 | リネーム | `Source\rename_source.txt` を選択しF2キーでリネーム | `Source\rename_source.txt` → `Source\renamed_result.txt` | 何らかのログが出るか、あるいは無反応か確認（リネームの検知有無が確認ポイント） |
| 6 | 通常削除（ゴミ箱移動） | `delete_normal.txt` を選択しDeleteキー（またはゴミ箱アイコンへドラッグ） | `D:\tmp\SysmonTest\delete_normal.txt` → ゴミ箱 | イベント「通常削除(ゴミ箱移動)」に元パス `D:\tmp\SysmonTest\delete_normal.txt` が復元されて1件 |
| 7 | 完全削除（Shift+Delete） | `delete_complete.txt` を選択しShift+Deleteキー→確認ダイアログで「はい」 | `D:\tmp\SysmonTest\delete_complete.txt` → 消去 | イベント「完全削除(Shift+Del)」が `delete_complete.txt` に対して1件 |
| 8 | 監視対象外操作（ノイズ確認・ネガティブテスト） | デスクトップ上に新規テキストファイルを作成し、そのまま完全削除（Shift+Delete）する | `C:\Users\<ユーザー>\Desktop\新規テキスト ドキュメント.txt`（作成→削除） | ログに**何も出力されないこと**を確認 |

---

## 実施記録（2026/07/12実施、Baseline 05:48:35）

| # | 実施時刻 | 想定通りか | 備考 |
|---|---------|-----------|------|
| 1 | 05:49:04 | △ | `FileCreate`は検知されたが、作成直後にファイル名をリネームすると**リネーム後の名前はログに反映されない**（デフォルト名`新規 テキスト ドキュメント.txt`のまま記録される）|
| 2 | (実施済み) | ✕ | メモ帳での上書き保存はログに一切出力されなかった。上書き保存の実装（一時ファイル書き込み→置換）がリネームと同様の経路を通るため検知漏れと推定 |
| 3 | 05:49:36 | ○ | `作成/コピー`が貼り付け先パスに対して1件、想定通り検知 |
| 4 | (実施済み・再実施済み) | ✕ | 2回実施したが、いずれもログに一切出力されなかった。ファイルシステム上は移動が成功していることを`Get-ChildItem`で確認済み。同一ボリューム内の移動はSysmonの監視対象イベントに存在しないため検知不可と結論 |
| 5 | (実施済み) | ✕ | リネームもログに出力されなかった。項目1のファイル名がリネーム前の名前のまま残っていたことからも裏付けられる |
| 6 | 05:50:15 | ○ | 元パス`D:\tmp\SysmonTest\delete_normal.txt`が正しく復元され、想定通り検知 |
| 7 | 05:50:21 | ○ | 想定通り検知 |
| 8 | (実施済み) | ○ | ログに一切出力されず、ノイズ確認としては正解（監視対象外操作が正しくフィルタされている）|

**結論**：新規作成・コピー・通常削除・完全削除は検知OK。ムーブ・リネームはSysmon単体では検知不可（詳細は [Readme.md](Readme.md) の「Sysmonでできること/できないこと」を参照）。
