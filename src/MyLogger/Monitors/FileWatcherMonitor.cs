using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MyLogger.Config;
using MyLogger.Logging;
using MyLogger.Util;

namespace MyLogger.Monitors;

/// <summary>
/// ローカル固定ドライブ上のファイル操作 (作成 / 変更 / リネーム / 削除 / 移動) を
/// FileSystemWatcher で監視して記録する。
/// ネットワーク / リムーバブルへの書き込みは EtwFileIoMonitor が担当する。
///
/// target_user/process/pid の解決 (ETWとの相関) はここでは行わない。ETW相関の成否は
/// タイミングの偶然に左右され、この場で即座に解決しようとすると取りこぼしが避けられないため、
/// 未帰属のまま local_fs_pending (テーブルA) に書き込むだけに留め、tools/ReconcileLocalFs による
/// バッチ突合(テーブルB=local_fs_etw_openとの結合)で後から解決する設計にしている
/// (経緯は doc/ローカルファイル監視の仕組み.md 参照)。
/// </summary>
public sealed class FileWatcherMonitor : BackgroundService
{
    private readonly FileWatcherOptions _options;
    private readonly string _logDirectory;
    private readonly ActivityLogger _activityLogger;
    private readonly ILogger<FileWatcherMonitor> _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly TimeSpan _moveWindow;
    private readonly TimeSpan _createChangeSuppressWindow;

    // 重複排除: (パス|操作) → 最終記録時刻
    private readonly ConcurrentDictionary<string, DateTime> _recent = new();
    private DateTime _lastSweep = DateTime.UtcNow;

    // フォルダをまたぐ移動検知用: ファイル名 → 保留中の作成/削除イベント
    private sealed record PendingFsEvent(string FullPath, DateTime Utc);
    private readonly ConcurrentDictionary<string, PendingFsEvent> _pendingCreates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingFsEvent> _pendingDeletes =
        new(StringComparer.OrdinalIgnoreCase);

    // Created/Moved 直後の Changed 抑制用: フルパス → 記録時刻 (本来の発生時刻)
    private readonly ConcurrentDictionary<string, DateTime> _recentlyCreated =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DefaultExcludes =
    {
        @"C:\Windows\",
        @"C:\ProgramData\Microsoft\",
        @"C:\Program Files\WindowsApps\",
    };

    // ドライブ文字に依存せず、パス中のどこにあっても除外する既定パターン。
    // ($RECYCLE.BIN / System Volume Information は監視対象ドライブが C: 以外でも作られるため)
    private static readonly string[] DefaultExcludeSegments =
    {
        @"\$RECYCLE.BIN\",
        @"\System Volume Information\",
    };

    public FileWatcherMonitor(
        IOptions<MonitorOptions> options,
        ActivityLogger activityLogger,
        ILogger<FileWatcherMonitor> logger)
    {
        _options = options.Value.FileWatcher;
        _logDirectory = Environment.ExpandEnvironmentVariables(options.Value.DataDirectory);
        _activityLogger = activityLogger;
        _moveWindow = TimeSpan.FromMilliseconds(Math.Max(0, _options.MoveCorrelationWindowMs));
        _createChangeSuppressWindow = TimeSpan.FromMilliseconds(Math.Max(0, _options.CreateChangeSuppressWindowMs));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("FileWatcherMonitor は無効化されています");
            return;
        }

        var paths = WatchedPathResolver.Resolve(_options);

        foreach (var path in paths)
        {
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024, // 最大値。バースト時の取りこぼしを減らす
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Created += (_, e) => OnCreatedOrDeleted("Created", e.FullPath);
                watcher.Changed += (_, e) => OnChanged(e.FullPath);
                watcher.Deleted += (_, e) => OnCreatedOrDeleted("Deleted", e.FullPath);
                watcher.Renamed += (_, e) => OnRenamed(e.OldFullPath, e.FullPath);
                watcher.Error += (_, e) =>
                {
                    _logger.LogWarning(e.GetException(),
                        "FileSystemWatcher バッファオーバーフロー等が発生しました ({Path})。一部イベントが欠落した可能性があります", path);
                    _activityLogger.Log(new ActivityEvent
                    {
                        Source = "local-fs",
                        Action = "MonitorOverflow",
                        Path = path,
                        Detail = "イベントバッファ溢れにより一部のファイル操作が記録されていない可能性があります",
                    });
                };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                _logger.LogInformation("ローカル監視を開始しました: {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "監視を開始できませんでした: {Path}", path);
            }
        }

        stoppingToken.Register(() =>
        {
            foreach (var w in _watchers)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
        });

        try
        {
            await SweepPendingLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 停止時
        }
        finally
        {
            FlushPendingMoves(force: true);
        }
    }

    /// <summary>
    /// 同一ボリューム内でもフォルダをまたぐ移動は Renamed ではなく Deleted+Created として
    /// 通知されることがある。同一ファイル名の Created/Deleted が MoveCorrelationWindowMs 以内に
    /// 両方発生したら、移動元/移動先パス付きの LOCAL_MOVE として 1 件に統合する。
    /// 対応する片方が現れなければ、一定時間後に通常の Created/Deleted として記録する (FlushPendingMoves)。
    /// </summary>
    private void OnCreatedOrDeleted(string action, string path)
    {
        if (IsExcluded(path)) return;

        if (_moveWindow <= TimeSpan.Zero)
        {
            EmitLocalEvent(action, path, null);
            return;
        }

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
        {
            EmitLocalEvent(action, path, null);
            return;
        }

        var opposite = action == "Created" ? _pendingDeletes : _pendingCreates;
        if (opposite.TryRemove(fileName, out var counterpart))
        {
            if (DateTime.UtcNow - counterpart.Utc <= _moveWindow)
            {
                var (sourcePath, destPath) = action == "Created"
                    ? (counterpart.FullPath, path)
                    : (path, counterpart.FullPath);
                // 発生時刻はペアの先に検知した方 (counterpart) を採用する
                EmitLocalEvent("Moved", destPath, sourcePath, counterpart.Utc);
                return;
            }
            // 時間窓を超えていた古い保留イベントは、取り出した時点で失われないよう、
            // 本来の発生時刻のまま元の Created/Deleted として確定させてから、今回のイベントの処理を続ける。
            EmitLocalEvent(action == "Created" ? "Deleted" : "Created", counterpart.FullPath, null, counterpart.Utc);
        }

        var mine = action == "Created" ? _pendingCreates : _pendingDeletes;
        mine[fileName] = new PendingFsEvent(path, DateTime.UtcNow);
    }

    private void OnChanged(string path)
    {
        if (IsExcluded(path)) return;

        // ファイルの作成・変更・削除に伴い、親フォルダ自体の更新日時も変わるため
        // FileSystemWatcher は親フォルダに対しても Changed を発火する。
        // フォルダ自体の作成/リネーム/削除は Created/Renamed/Deleted で別途記録されるため、
        // この副作用としての Changed (対象がフォルダの場合) はノイズとして記録しない。
        if (Directory.Exists(path)) return;

        if (IsSuppressedAsCreateFollowUp(path)) return;

        EmitLocalEvent("Changed", path, null);
    }

    /// <summary>
    /// コピー等は OS 的に「ファイル作成」と「内容の書き込み」に分かれるため、Created の直後に
    /// 同じパスへ Changed が発生することがある (実機確認済み: doc/FileSystemWatcher調査.md)。
    /// これを同一操作の一部とみなし、CreateChangeSuppressWindowMs 以内の Changed は記録しない。
    /// Created がまだ LOCAL_MOVE 判定待ちで保留中 (_pendingCreates) の場合も同様に抑制する
    /// (保留中に Changed が先に書き込まれてしまう、実機で確認済みの逆転現象への対処)。
    /// </summary>
    private bool IsSuppressedAsCreateFollowUp(string path)
    {
        if (_createChangeSuppressWindow <= TimeSpan.Zero) return false;

        var fileName = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(fileName) &&
            _pendingCreates.TryGetValue(fileName, out var pending) &&
            string.Equals(pending.FullPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _recentlyCreated.TryGetValue(path, out var createdAtUtc) &&
               DateTime.UtcNow - createdAtUtc <= _createChangeSuppressWindow;
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        if (IsExcluded(newPath)) return;
        EmitLocalEvent("Renamed", newPath, oldPath);
    }

    /// <summary>対応する Created/Deleted が来ないまま残った保留イベントを、通常通り記録する。</summary>
    private void FlushPendingMoves(bool force)
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _pendingCreates)
        {
            if (!force && now - kv.Value.Utc <= _moveWindow) continue;
            if (_pendingCreates.TryRemove(kv.Key, out var evt)) EmitLocalEvent("Created", evt.FullPath, null, evt.Utc);
        }
        foreach (var kv in _pendingDeletes)
        {
            if (!force && now - kv.Value.Utc <= _moveWindow) continue;
            if (_pendingDeletes.TryRemove(kv.Key, out var evt)) EmitLocalEvent("Deleted", evt.FullPath, null, evt.Utc);
        }
    }

    private async Task SweepPendingLoopAsync(CancellationToken ct)
    {
        if (_moveWindow <= TimeSpan.Zero && _createChangeSuppressWindow <= TimeSpan.Zero)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150));
        while (await timer.WaitForNextTickAsync(ct))
        {
            FlushPendingMoves(force: false);
            SweepRecentlyCreated();
        }
    }

    /// <summary>CreateChangeSuppressWindowMs を超えた古いエントリを間引いてメモリ肥大を防ぐ。</summary>
    private void SweepRecentlyCreated()
    {
        if (_createChangeSuppressWindow <= TimeSpan.Zero) return;

        var now = DateTime.UtcNow;
        foreach (var kv in _recentlyCreated)
        {
            if (now - kv.Value > _createChangeSuppressWindow) _recentlyCreated.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    /// イベントを local_fs_pending (テーブルA) に記録する。target_user/process/pid はここでは
    /// 解決しない (クラス冒頭のコメント参照)。<paramref name="occurredAtUtc"/> を省略した場合は
    /// 現在時刻を使う (呼び出しと同期して発生した Changed/Renamed 等はこれで問題ない)。
    /// LOCAL_MOVE 検知のために保留していたイベントを確定させる場合は、記録が遅延した分だけ
    /// 実際の発生時刻とずれてしまわないよう、保留時に記録した本来の発生時刻を明示的に渡すこと。
    /// </summary>
    private void EmitLocalEvent(string action, string path, string? oldPath, DateTime? occurredAtUtc = null)
    {
        if (!ShouldLog($"{path}|{action}")) return;

        var occurred = occurredAtUtc ?? DateTime.UtcNow;

        if (action is "Created" or "Moved")
        {
            _recentlyCreated[path] = occurred;
        }

        var (sourcePath, destPath) = action switch
        {
            "Deleted" => (path, (string?)null),
            "Renamed" or "Moved" => (oldPath, path),
            _ => ((string?)null, path), // Created / Changed
        };

        _activityLogger.LogLocalFsPending(new LocalFsPendingEvent(
            Timestamp: new DateTimeOffset(occurred).ToLocalTime(),
            ActionType: MapActionType(action),
            SourcePath: sourcePath,
            DestPath: destPath));
    }

    private static string MapActionType(string action) => action switch
    {
        "Created" => "LOCAL_CREATE",
        "Changed" => "LOCAL_CHANGE",
        "Deleted" => "LOCAL_DELETE",
        "Renamed" => "LOCAL_RENAME",
        "Moved" => "LOCAL_MOVE",
        _ => $"LOCAL_{action.ToUpperInvariant()}",
    };

    private bool IsExcluded(string path)
    {
        // 自分自身のログ出力先は必ず除外 (無限ループ防止)
        if (path.StartsWith(_logDirectory, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var prefix in DefaultExcludes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var segment in DefaultExcludeSegments)
        {
            if (path.Contains(segment, StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var prefix in _options.ExcludePathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (_options.ExcludeExtensions.Count > 0)
        {
            var ext = Path.GetExtension(path);
            foreach (var excluded in _options.ExcludeExtensions)
            {
                if (string.Equals(ext, excluded, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>同一パス・同一操作の連続イベントを DedupeSeconds 秒間まとめる。</summary>
    private bool ShouldLog(string key)
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(Math.Max(0, _options.DedupeSeconds));

        // 定期的に古いエントリを掃除してメモリ肥大を防ぐ
        if (now - _lastSweep > TimeSpan.FromMinutes(5))
        {
            _lastSweep = now;
            foreach (var kv in _recent)
            {
                if (now - kv.Value > window) _recent.TryRemove(kv.Key, out _);
            }
        }

        if (_recent.TryGetValue(key, out var last) && now - last < window) return false;
        _recent[key] = now;
        return true;
    }
}
