using System.Text.Json;
using Microsoft.Data.Sqlite;

// ①(ローカルファイル操作)の未帰属イベント (local_fs_pending, テーブルA) と、ETWで捕捉した
// ローカルオープン情報 (local_fs_etw_open, テーブルB) をパス・時刻で突き合わせ、
// target_user/process/pid を解決した最終レコードを activity_log (テーブルC) に書き込む。
//
// リアルタイムでの相関(FileWatcherMonitorの中で即座に解決する方式)は、ETWとFileSystemWatcherが
// 別々の非同期経路であるためタイミングの偶然に左右され、取りこぼしが避けられないと判明した。
// そのため相関は諦め、両テーブルとも生のまま永続化しておき、後からこのツールで突き合わせる設計にした
// (詳細は doc/ローカルファイル監視の仕組み.md 参照)。A/Bともに書き込み後に変更されないため、
// このツールを何度再実行しても安全 (未突合の行だけを処理し、処理済みの行はreconciled_atで除外する)。
//
// 使い方:
//   dotnet run --project tools\ReconcileLocalFs                既定 DB を突合
//   dotnet run --project tools\ReconcileLocalFs -- <DBパス>     指定 DB を突合
//
// 現時点では手動実行のみを想定している (自動スケジュール実行は未実装)。

const string Unknown = "UNKNOWN";

// ETWのオープンは「保存までハンドルを保持し続けるアプリ」を考慮し、対象イベントより最大30秒前まで
// 候補として遡る (LocalFileOpenTracker が採用していたTTLと同じ考え方)。前方向は、ETWとFileSystemWatcher
// の到着順が僅かに前後することがある実機での観測を踏まえ、5秒だけ許容する。
var lookBehind = TimeSpan.FromSeconds(30);
var lookAhead = TimeSpan.FromSeconds(5);

var dbPath = args.Length > 0 ? args[0] : @"C:\ProgramData\MyLogger\data\activity.db";
if (!File.Exists(dbPath))
{
    Console.WriteLine($"DB ファイルが見つかりません: {dbPath}");
    return 1;
}

using var connection = new SqliteConnection($"Data Source={dbPath}");
connection.Open();

var pending = LoadPendingRows(connection);
Console.WriteLine($"未突合の local_fs_pending: {pending.Count} 件");

var resolved = 0;
var unknown = 0;

using (var transaction = connection.BeginTransaction())
{
    foreach (var row in pending)
    {
        // FileWatcherMonitor.ResolveAttribution と同じ優先順位:
        // 主パス = source_path があればそれ、無ければ dest_path。副パス = その逆。
        var primaryPath = row.SourcePath ?? row.DestPath;
        var secondaryPath = row.DestPath ?? row.SourcePath;

        var candidate = FindBestCandidate(connection, primaryPath, row.EventTimestamp, lookBehind, lookAhead)
            ?? (secondaryPath != primaryPath
                ? FindBestCandidate(connection, secondaryPath, row.EventTimestamp, lookBehind, lookAhead)
                : null);

        string targetUser;
        string? processName = null;
        int? pid = null;

        if (candidate is not null)
        {
            targetUser = string.IsNullOrEmpty(candidate.TargetUser) ? Unknown : candidate.TargetUser;
            processName = candidate.ProcessName;
            pid = candidate.Pid;
            resolved++;
        }
        else
        {
            targetUser = Unknown;
            unknown++;
        }

        InsertActivityLog(connection, transaction, row, targetUser, processName, pid);
        MarkReconciled(connection, transaction, row.Id);
    }

    transaction.Commit();
}

Console.WriteLine($"完了: {resolved} 件解決 / {unknown} 件 UNKNOWN");
Console.WriteLine("(注意: このツールは Monitoring.MonitoredUsers によるフィルタを現時点では適用しません)");
return 0;

static List<PendingRow> LoadPendingRows(SqliteConnection connection)
{
    using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        SELECT id, event_timestamp, action_type, source_path, dest_path
        FROM local_fs_pending
        WHERE reconciled_at IS NULL
        ORDER BY id
        """;
    using var reader = cmd.ExecuteReader();

    var result = new List<PendingRow>();
    while (reader.Read())
    {
        result.Add(new PendingRow(
            Id: reader.GetInt64(0),
            EventTimestamp: DateTimeOffset.Parse(reader.GetString(1)),
            ActionType: reader.GetString(2),
            SourcePath: reader.IsDBNull(3) ? null : reader.GetString(3),
            DestPath: reader.IsDBNull(4) ? null : reader.GetString(4)));
    }
    return result;
}

/// <summary>
/// 指定パスの ETW オープン候補から、時間窓内かつ最も新しいものを選ぶ。
/// 複数候補がある場合は explorer.exe (フォルダ表示更新等での誤検知が多い) 以外を優先する
/// (LocalFileOpenTracker が採用していたのと同じ優先順位)。
/// </summary>
static EtwCandidate? FindBestCandidate(
    SqliteConnection connection, string? path, DateTimeOffset targetTime, TimeSpan lookBehind, TimeSpan lookAhead)
{
    if (string.IsNullOrEmpty(path)) return null;

    using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        SELECT event_timestamp, process_name, pid, target_user
        FROM local_fs_etw_open
        WHERE path = $path
        """;
    cmd.Parameters.AddWithValue("$path", path);
    using var reader = cmd.ExecuteReader();

    var lower = targetTime - lookBehind;
    var upper = targetTime + lookAhead;

    EtwCandidate? best = null;
    EtwCandidate? bestNonExplorer = null;
    while (reader.Read())
    {
        var ts = DateTimeOffset.Parse(reader.GetString(0));
        if (ts < lower || ts > upper) continue;

        var candidate = new EtwCandidate(
            Timestamp: ts,
            ProcessName: reader.IsDBNull(1) ? null : reader.GetString(1),
            Pid: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            TargetUser: reader.IsDBNull(3) ? null : reader.GetString(3));

        if (best is null || candidate.Timestamp > best.Timestamp) best = candidate;

        var isExplorer = string.Equals(candidate.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        if (!isExplorer && (bestNonExplorer is null || candidate.Timestamp > bestNonExplorer.Timestamp))
        {
            bestNonExplorer = candidate;
        }
    }

    return bestNonExplorer ?? best;
}

static void InsertActivityLog(
    SqliteConnection connection, SqliteTransaction transaction, PendingRow row,
    string targetUser, string? processName, int? pid)
{
    using var cmd = connection.CreateCommand();
    cmd.Transaction = transaction;
    cmd.CommandText = """
        INSERT INTO activity_log (event_timestamp, action_type, target_user, source_path, dest_path, additional_info)
        VALUES ($event_timestamp, $action_type, $target_user, $source_path, $dest_path, $additional_info)
        """;
    cmd.Parameters.AddWithValue("$event_timestamp", row.EventTimestamp.ToString("O"));
    cmd.Parameters.AddWithValue("$action_type", row.ActionType);
    cmd.Parameters.AddWithValue("$target_user", targetUser);
    cmd.Parameters.AddWithValue("$source_path", (object?)row.SourcePath ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$dest_path", (object?)row.DestPath ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$additional_info", (object?)BuildAdditionalInfo(processName, pid) ?? DBNull.Value);
    cmd.ExecuteNonQuery();
}

static string? BuildAdditionalInfo(string? processName, int? pid)
{
    if (processName is null && pid is null) return null;
    var extra = new Dictionary<string, object>();
    if (processName is not null) extra["process"] = processName;
    if (pid is not null) extra["pid"] = pid.Value;
    return JsonSerializer.Serialize(extra);
}

static void MarkReconciled(SqliteConnection connection, SqliteTransaction transaction, long id)
{
    using var cmd = connection.CreateCommand();
    cmd.Transaction = transaction;
    cmd.CommandText = "UPDATE local_fs_pending SET reconciled_at = $now WHERE id = $id";
    cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
    cmd.Parameters.AddWithValue("$id", id);
    cmd.ExecuteNonQuery();
}

internal sealed record PendingRow(long Id, DateTimeOffset EventTimestamp, string ActionType, string? SourcePath, string? DestPath);

internal sealed record EtwCandidate(DateTimeOffset Timestamp, string? ProcessName, int? Pid, string? TargetUser);
