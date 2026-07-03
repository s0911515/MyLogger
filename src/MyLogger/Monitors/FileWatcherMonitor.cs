using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MyLogger.Config;
using MyLogger.Logging;

namespace MyLogger.Monitors;

/// <summary>
/// ローカル固定ドライブ上のファイル操作 (作成 / 変更 / リネーム / 削除) を
/// FileSystemWatcher で監視して記録する。
/// ネットワーク / リムーバブルへの書き込みは EtwFileIoMonitor が担当する。
/// </summary>
public sealed class FileWatcherMonitor : BackgroundService
{
    private readonly FileWatcherOptions _options;
    private readonly string _logDirectory;
    private readonly ActivityLogger _activityLogger;
    private readonly ILogger<FileWatcherMonitor> _logger;
    private readonly List<FileSystemWatcher> _watchers = new();

    // 重複排除: (パス|操作) → 最終記録時刻
    private readonly ConcurrentDictionary<string, DateTime> _recent = new();
    private DateTime _lastSweep = DateTime.UtcNow;

    private static readonly string[] DefaultExcludes =
    {
        @"C:\Windows\",
        @"C:\ProgramData\Microsoft\",
        @"C:\Program Files\WindowsApps\",
        @"C:\$Recycle.Bin\",
        @"C:\System Volume Information\",
    };

    public FileWatcherMonitor(
        IOptions<MonitorOptions> options,
        ActivityLogger activityLogger,
        ILogger<FileWatcherMonitor> logger)
    {
        _options = options.Value.FileWatcher;
        _logDirectory = Environment.ExpandEnvironmentVariables(options.Value.LogDirectory);
        _activityLogger = activityLogger;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("FileWatcherMonitor は無効化されています");
            return Task.CompletedTask;
        }

        var paths = _options.Paths.Count > 0
            ? _options.Paths.Select(Environment.ExpandEnvironmentVariables).ToList()
            : DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();

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
                watcher.Created += (_, e) => OnEvent("Created", e.FullPath);
                watcher.Changed += (_, e) => OnEvent("Changed", e.FullPath);
                watcher.Deleted += (_, e) => OnEvent("Deleted", e.FullPath);
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
        return Task.CompletedTask;
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        if (IsExcluded(newPath) || !ShouldLog($"{newPath}|Renamed")) return;
        _activityLogger.Log(new ActivityEvent
        {
            Source = "local-fs",
            Action = "Renamed",
            Path = newPath,
            OldPath = oldPath,
            Target = "fixed",
        });
    }

    private void OnEvent(string action, string path)
    {
        if (IsExcluded(path) || !ShouldLog($"{path}|{action}")) return;
        _activityLogger.Log(new ActivityEvent
        {
            Source = "local-fs",
            Action = action,
            Path = path,
            Target = "fixed",
        });
    }

    private bool IsExcluded(string path)
    {
        // 自分自身のログ出力先は必ず除外 (無限ループ防止)
        if (path.StartsWith(_logDirectory, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var prefix in DefaultExcludes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
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
