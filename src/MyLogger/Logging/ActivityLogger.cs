using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MyLogger.Config;
using MyLogger.Util;

namespace MyLogger.Logging;

/// <summary>1 件のファイル操作 / 認証イベント。各 Monitor が組み立て、ActivityLogger のキューに積む。</summary>
public sealed record ActivityEvent
{
    /// <summary>イベント発生時刻 (ローカル時刻)。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>検出元: local-fs / network-io / smb-server / logon。</summary>
    public required string Source { get; init; }

    /// <summary>操作種別: Created / Changed / Renamed / Deleted / Write / Read / Login / Logout など。</summary>
    public required string Action { get; init; }

    /// <summary>対象ファイル / フォルダのパス。</summary>
    public required string Path { get; init; }

    /// <summary>リネーム時の変更前パス。</summary>
    public string? OldPath { get; init; }

    /// <summary>書き込み先の分類: fixed / network / removable。</summary>
    public string? Target { get; init; }

    /// <summary>操作を行ったプロセス名。</summary>
    public string? ProcessName { get; init; }

    public int? ProcessId { get; init; }

    /// <summary>書き込み / 読み取りバイト数 (ETW 集約値)。</summary>
    public long? Bytes { get; init; }

    /// <summary>操作を実行したユーザー (ドメイン\ユーザー名)。target_user にマップされる。</summary>
    public string? User { get; init; }

    /// <summary>アクセス元 IP アドレス。source_ip にマップされる。</summary>
    public string? RemoteIp { get; init; }

    /// <summary>アクセスされた共有名。</summary>
    public string? ShareName { get; init; }

    /// <summary>要求されたアクセス権の内訳。</summary>
    public string? Access { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// ①(ローカルファイル操作)の未帰属イベント (local_fs_pending テーブル)。
/// FileWatcherMonitor が組み立てるが、この時点では target_user/process/pid を解決しない
/// (ETW側との相関はリアルタイムでは行わず、tools/ReconcileLocalFs によるバッチ突合に委ねる。
/// 理由は doc/ローカルファイル監視の仕組み.md 参照)。
/// </summary>
public sealed record LocalFsPendingEvent(
    DateTimeOffset Timestamp,
    string ActionType,
    string? SourcePath,
    string? DestPath);

/// <summary>
/// ETW で捕捉したローカル watched パス内のファイルオープン/書き込み/フラッシュ
/// (local_fs_etw_open テーブル)。PID→ユーザーの WMI 解決はプロセスがまだ生きている
/// 捕捉直後にしかできないため、この時点で解決済みの値として記録する。
/// </summary>
public sealed record EtwOpenEvent(
    DateTimeOffset Timestamp,
    string Path,
    string EventType, // Create / Write / Flush
    string? ProcessName,
    int? ProcessId,
    string? TargetUser);

/// <summary>
/// アクティビティイベントを SQLite データベースへ非同期に書き込むロガー。
/// 監視スレッド (ETW コールバック等) をブロックしないよう Channel 経由でキューイングし、
/// バッチ単位でトランザクション INSERT する (§5.1 Bulk Insert)。
/// DB は WAL モードで開き、検索/CSV出力アドオンとの同時実行性を確保する (§5.3)。
/// ファイルサイズ・経過日数のいずれかが閾値を超えたら DB ファイルを切り替えて archive へ退避する (§3.4)。
///
/// activity_log (テーブルC) に加えて、①専用の local_fs_pending (テーブルA) / local_fs_etw_open
/// (テーブルB) も同じ接続・同じローテーション周期で書き込む。3つのテーブルは1本の書き込みループで
/// 扱うため、SqliteConnection を複数スレッドから同時に叩くことはない。
/// </summary>
public sealed class ActivityLogger : IHostedService, IAsyncDisposable
{
    private const int BatchSize = 500;

    private readonly Channel<ActivityEvent> _channel;
    private readonly Channel<LocalFsPendingEvent> _localFsPendingChannel;
    private readonly Channel<EtwOpenEvent> _etwOpenChannel;
    private readonly string _dataDirectory;
    private readonly string _archiveDirectory;
    private readonly string _dbFileName;
    private readonly RotationOptions _rotation;
    private readonly UserFilter _userFilter;
    private readonly ILogger<ActivityLogger> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _writerTask;
    private SqliteConnection? _connection;

    public ActivityLogger(IOptions<MonitorOptions> options, ILogger<ActivityLogger> logger)
    {
        var opts = options.Value;
        _dataDirectory = Environment.ExpandEnvironmentVariables(opts.DataDirectory);
        _archiveDirectory = Path.Combine(_dataDirectory, "archive");
        _dbFileName = opts.DatabaseFileName;
        _rotation = opts.Rotation;
        _logger = logger;
        _userFilter = new UserFilter(opts.MonitoredUsers, logger);
        _channel = Channel.CreateBounded<ActivityEvent>(new BoundedChannelOptions(50_000)
        {
            // 書き込みが追い付かない場合は最古のイベントから捨てる (監視側を止めない)
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _localFsPendingChannel = Channel.CreateBounded<LocalFsPendingEvent>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _etwOpenChannel = Channel.CreateBounded<EtwOpenEvent>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    private string DbPath => Path.Combine(_dataDirectory, _dbFileName);

    /// <summary>イベントをキューに積む。どのスレッドから呼んでもよい。</summary>
    public void Log(ActivityEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>①の未帰属イベント (テーブルA) をキューに積む。どのスレッドから呼んでもよい。</summary>
    public void LogLocalFsPending(LocalFsPendingEvent evt) => _localFsPendingChannel.Writer.TryWrite(evt);

    /// <summary>ETWで捕捉したローカルオープン (テーブルB) をキューに積む。どのスレッドから呼んでもよい。</summary>
    public void LogEtwOpen(EtwOpenEvent evt) => _etwOpenChannel.Writer.TryWrite(evt);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_archiveDirectory);
        _writerTask = Task.Run(() => WriteLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        _localFsPendingChannel.Writer.TryComplete();
        _etwOpenChannel.Writer.TryComplete();
        if (_writerTask is not null)
        {
            await Task.WhenAny(_writerTask, Task.Delay(5000, cancellationToken));
        }
        _cts.Cancel();
    }

    /// <summary>
    /// activity_log / local_fs_pending / local_fs_etw_open の3チャネルを、単一の SqliteConnection
    /// (シングルスレッド) で扱うため、個別に WaitToReadAsync するのではなく定期ポーリングでまとめて
    /// ドレインする。200ms 間隔はバッチ書き込みの遅延として十分小さい。
    /// </summary>
    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            OpenConnection();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            var batch = new List<ActivityEvent>(BatchSize);
            var localFsBatch = new List<LocalFsPendingEvent>(BatchSize);
            var etwOpenBatch = new List<EtwOpenEvent>(BatchSize);

            while (await timer.WaitForNextTickAsync(ct))
            {
                batch.Clear();
                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var evt)) batch.Add(evt);

                localFsBatch.Clear();
                while (localFsBatch.Count < BatchSize && _localFsPendingChannel.Reader.TryRead(out var evt)) localFsBatch.Add(evt);

                etwOpenBatch.Clear();
                while (etwOpenBatch.Count < BatchSize && _etwOpenChannel.Reader.TryRead(out var evt)) etwOpenBatch.Add(evt);

                if (batch.Count == 0 && localFsBatch.Count == 0 && etwOpenBatch.Count == 0) continue;

                if (batch.Count > 0) InsertBatch(batch);
                if (localFsBatch.Count > 0) InsertLocalFsPendingBatch(localFsBatch);
                if (etwOpenBatch.Count > 0) InsertEtwOpenBatch(etwOpenBatch);
                CheckRotation();
            }
        }
        catch (OperationCanceledException)
        {
            // 停止時
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "アクティビティログの書き込みに失敗しました");
        }
        finally
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    private void OpenConnection()
    {
        _connection = new SqliteConnection($"Data Source={DbPath}");
        _connection.Open();
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }
        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS activity_log (
                log_id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_timestamp DATETIME NOT NULL,
                action_type VARCHAR NOT NULL,
                target_user VARCHAR NOT NULL,
                source_ip VARCHAR,
                source_path TEXT,
                dest_path TEXT,
                additional_info TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_activity_log_timestamp ON activity_log(event_timestamp);
            CREATE INDEX IF NOT EXISTS idx_activity_log_action_type ON activity_log(action_type);

            CREATE TABLE IF NOT EXISTS local_fs_pending (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_timestamp DATETIME NOT NULL,
                action_type VARCHAR NOT NULL,
                source_path TEXT,
                dest_path TEXT,
                reconciled_at DATETIME
            );
            CREATE INDEX IF NOT EXISTS idx_local_fs_pending_reconciled ON local_fs_pending(reconciled_at);

            CREATE TABLE IF NOT EXISTS local_fs_etw_open (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_timestamp DATETIME NOT NULL,
                path TEXT NOT NULL,
                event_type VARCHAR NOT NULL,
                process_name VARCHAR,
                pid INTEGER,
                target_user VARCHAR
            );
            CREATE INDEX IF NOT EXISTS idx_local_fs_etw_open_path_time ON local_fs_etw_open(path, event_timestamp);
            """;
        create.ExecuteNonQuery();
    }

    private void InsertBatch(List<ActivityEvent> batch)
    {
        if (_connection is null) return;

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO activity_log
                (event_timestamp, action_type, target_user, source_ip, source_path, dest_path, additional_info)
            VALUES
                ($event_timestamp, $action_type, $target_user, $source_ip, $source_path, $dest_path, $additional_info)
            """;
        var pTimestamp = command.Parameters.Add("$event_timestamp", SqliteType.Text);
        var pAction = command.Parameters.Add("$action_type", SqliteType.Text);
        var pUser = command.Parameters.Add("$target_user", SqliteType.Text);
        var pSourceIp = command.Parameters.Add("$source_ip", SqliteType.Text);
        var pSourcePath = command.Parameters.Add("$source_path", SqliteType.Text);
        var pDestPath = command.Parameters.Add("$dest_path", SqliteType.Text);
        var pAdditionalInfo = command.Parameters.Add("$additional_info", SqliteType.Text);

        foreach (var evt in batch)
        {
            var record = ActivityRecordMapper.Map(evt);
            if (!_userFilter.IsMonitored(record.TargetUser)) continue;

            pTimestamp.Value = record.EventTimestamp.ToString("O");
            pAction.Value = record.ActionType;
            pUser.Value = record.TargetUser;
            pSourceIp.Value = (object?)record.SourceIp ?? DBNull.Value;
            pSourcePath.Value = (object?)record.SourcePath ?? DBNull.Value;
            pDestPath.Value = (object?)record.DestPath ?? DBNull.Value;
            pAdditionalInfo.Value = (object?)record.AdditionalInfo ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void InsertLocalFsPendingBatch(List<LocalFsPendingEvent> batch)
    {
        if (_connection is null) return;

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_fs_pending (event_timestamp, action_type, source_path, dest_path)
            VALUES ($event_timestamp, $action_type, $source_path, $dest_path)
            """;
        var pTimestamp = command.Parameters.Add("$event_timestamp", SqliteType.Text);
        var pAction = command.Parameters.Add("$action_type", SqliteType.Text);
        var pSourcePath = command.Parameters.Add("$source_path", SqliteType.Text);
        var pDestPath = command.Parameters.Add("$dest_path", SqliteType.Text);

        foreach (var evt in batch)
        {
            pTimestamp.Value = evt.Timestamp.ToString("O");
            pAction.Value = evt.ActionType;
            pSourcePath.Value = (object?)evt.SourcePath ?? DBNull.Value;
            pDestPath.Value = (object?)evt.DestPath ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void InsertEtwOpenBatch(List<EtwOpenEvent> batch)
    {
        if (_connection is null) return;

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_fs_etw_open (event_timestamp, path, event_type, process_name, pid, target_user)
            VALUES ($event_timestamp, $path, $event_type, $process_name, $pid, $target_user)
            """;
        var pTimestamp = command.Parameters.Add("$event_timestamp", SqliteType.Text);
        var pPath = command.Parameters.Add("$path", SqliteType.Text);
        var pEventType = command.Parameters.Add("$event_type", SqliteType.Text);
        var pProcessName = command.Parameters.Add("$process_name", SqliteType.Text);
        var pPid = command.Parameters.Add("$pid", SqliteType.Integer);
        var pTargetUser = command.Parameters.Add("$target_user", SqliteType.Text);

        foreach (var evt in batch)
        {
            pTimestamp.Value = evt.Timestamp.ToString("O");
            pPath.Value = evt.Path;
            pEventType.Value = evt.EventType;
            pProcessName.Value = (object?)evt.ProcessName ?? DBNull.Value;
            pPid.Value = (object?)evt.ProcessId ?? DBNull.Value;
            pTargetUser.Value = (object?)evt.TargetUser ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void CheckRotation()
    {
        if (_connection is null) return;
        if (_rotation.IntervalDays <= 0 && _rotation.MaxSizeMB <= 0) return;

        var path = DbPath;
        if (!File.Exists(path)) return;

        var info = new FileInfo(path);
        var tooOld = _rotation.IntervalDays > 0 &&
            DateTime.UtcNow - File.GetCreationTimeUtc(path) > TimeSpan.FromDays(_rotation.IntervalDays);
        var tooBig = _rotation.MaxSizeMB > 0 && info.Length > _rotation.MaxSizeMB * 1024L * 1024L;

        if (!tooOld && !tooBig) return;

        try
        {
            Rotate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB ファイルのローテーションに失敗しました");
        }
    }

    /// <summary>
    /// WAL をチェックポイントしてから DB 接続を閉じ、現在の DB ファイルを archive へ退避、
    /// 新しい空の DB ファイルを開き直す。検索/出力ツールがアクセス不能になる時間を
    /// 最小限にするため、ファイル移動 (rename) ベースで切り替える (§5.3)。
    /// </summary>
    private void Rotate()
    {
        if (_connection is null) return;

        using (var checkpoint = _connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        _connection.Dispose();
        _connection = null;

        var path = DbPath;
        var archiveName = $"activity-{DateTime.Now:yyyyMMdd-HHmmss}.db";
        File.Move(path, Path.Combine(_archiveDirectory, archiveName));

        foreach (var sidecar in new[] { path + "-wal", path + "-shm" })
        {
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }

        _logger.LogInformation("DB ファイルをローテーションしました: {ArchiveName}", archiveName);

        OpenConnection();
        EnforceRetention();
    }

    private void EnforceRetention()
    {
        if (_rotation.RetentionGenerations <= 0) return;
        try
        {
            var archives = Directory.EnumerateFiles(_archiveDirectory, "activity-*.db")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();
            foreach (var old in archives.Skip(_rotation.RetentionGenerations))
            {
                old.Delete();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "古いアーカイブ DB の削除に失敗しました");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _localFsPendingChannel.Writer.TryComplete();
        _etwOpenChannel.Writer.TryComplete();
        // 他 Monitor の異常終了 (BackgroundServiceExceptionBehavior.StopHost) 等で
        // StopAsync と DisposeAsync がほぼ同時に呼ばれ、_cts が既に Dispose 済みのことがある
        // (実機で ObjectDisposedException を確認)。二重呼び出し自体は無害なので無視してよい。
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* 既に停止処理済み */ }
        if (_writerTask is not null)
        {
            try { await _writerTask; } catch { /* 停止時の例外は無視 */ }
        }
        try { _cts.Dispose(); } catch (ObjectDisposedException) { /* 既に破棄済み */ }
    }
}
