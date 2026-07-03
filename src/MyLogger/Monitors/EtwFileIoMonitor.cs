using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Options;
using MyLogger.Config;
using MyLogger.Logging;
using MyLogger.Util;

namespace MyLogger.Monitors;

/// <summary>
/// ETW (カーネル FileIO イベント) で全プロセスのファイル書き込みを監視し、
/// 書き込み先が「ネットワーク共有」または「リムーバブルドライブ」の場合のみ記録する。
/// どのプロセスがどこへ何バイト書いたかまで捕捉できるため、
/// 共有フォルダへのファイルコピー (エクスプローラー / robocopy / スクリプト等) の検出に使う。
/// 管理者権限 (サービスの場合は LocalSystem) が必要。
/// </summary>
public sealed class EtwFileIoMonitor : BackgroundService
{
    private const string SessionName = "MyLogger-FileIO";

    private readonly EtwOptions _options;
    private readonly ActivityLogger _activityLogger;
    private readonly ILogger<EtwFileIoMonitor> _logger;
    private TraceEventSession? _session;

    private sealed class IoAggregate
    {
        public long Bytes;
        public DateTime FirstUtc;
        public DateTime LastUtc;
        public required string ProcessName;
        public required string Action;   // Write / Read
        public required string Target;   // network / removable
    }

    // (PID, パス, 操作) 単位で書き込みを集約し、連続 I/O を 1 レコードにまとめる
    private readonly ConcurrentDictionary<(int Pid, string Path, string Action), IoAggregate> _pending = new();

    // FileObject → ファイルを開いたプロセス。
    // SMB へのキャッシュ書き込みは System (PID 4) のスレッドが遅延実行するため、
    // Write イベントを「実際にファイルを開いたプロセス」に帰属させるのに使う。
    private readonly ConcurrentDictionary<ulong, (int Pid, string Name, DateTime CreatedUtc)> _openers = new();

    // 情報系イベント (Renamed / Deleted) の重複排除 (ETW コールバックは単一スレッド)
    private (int Pid, string Path, string Action)? _lastInfoKey;
    private DateTime _lastInfoUtc;

    public EtwFileIoMonitor(
        IOptions<MonitorOptions> options,
        ActivityLogger activityLogger,
        ILogger<EtwFileIoMonitor> logger)
    {
        _options = options.Value.Etw;
        _activityLogger = activityLogger;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("EtwFileIoMonitor は無効化されています");
            return;
        }
        if (!TraceEventSession.IsElevated() ?? false)
        {
            _logger.LogError("管理者権限がないため ETW 監視を開始できません。ネットワーク共有へのコピーは記録されません");
            return;
        }

        var processingTask = Task.Factory.StartNew(
            () => RunSession(stoppingToken),
            stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var flushTask = FlushLoopAsync(stoppingToken);

        stoppingToken.Register(() =>
        {
            try { _session?.Stop(); } catch { /* 停止時 */ }
        });

        try
        {
            await Task.WhenAll(processingTask, flushTask);
        }
        catch (OperationCanceledException)
        {
            // 停止時
        }
        finally
        {
            FlushPending(force: true);
            _session?.Dispose();
        }
    }

    private void RunSession(CancellationToken ct)
    {
        try
        {
            // 同名セッションが残っていても TraceEventSession が作り直す
            _session = new TraceEventSession(SessionName);
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.FileIOInit |
                KernelTraceEventParser.Keywords.FileIO);

            var kernel = _session.Source.Kernel;
            kernel.FileIOCreate += OnCreate;
            kernel.FileIOWrite += data => OnReadWrite(data, "Write");
            if (_options.IncludeNetworkReads || _options.AuditReadProcesses.Count > 0)
            {
                kernel.FileIORead += data => OnReadWrite(data, "Read");
            }
            kernel.FileIORename += data => OnInfoOp(data, "Renamed");
            kernel.FileIODelete += data => OnInfoOp(data, "Deleted");

            _logger.LogInformation("ETW 監視を開始しました (ネットワーク共有 / リムーバブルへの書き込み)");
            _session.Source.Process(); // Stop() されるまでブロック
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "ETW セッションが異常終了しました");
        }
    }

    /// <summary>
    /// ネットワーク / リムーバブル上のファイルオープンを記録し、
    /// 後続の Write イベントのプロセス帰属に使う (ログには出さない)。
    /// </summary>
    private void OnCreate(FileIOCreateTraceData data)
    {
        var rawPath = data.FileName;
        if (string.IsNullOrEmpty(rawPath) || data.ProcessID == 4) return;

        var (_, target) = ClassifyPath(rawPath);
        // ネットワーク / リムーバブル宛に加え、読み取り監査対象プロセス (rdpclip 等) の
        // ローカルファイルオープンも追跡する (System による先読みを帰属させるため)
        if (target is null && !IsAuditedReadProcess(data.ProcessName)) return;

        if (_openers.Count > 8192) _openers.Clear(); // 念のための上限 (通常は TTL 掃除で足りる)
        _openers[data.FileObject] = (data.ProcessID, data.ProcessName, DateTime.UtcNow);
    }

    private void OnReadWrite(FileIOReadWriteTraceData data, string action)
    {
        var rawPath = data.FileName;
        if (string.IsNullOrEmpty(rawPath)) return;

        var (path, target) = ClassifyPath(rawPath);

        // ローカル宛の書き込みは対象外 (① FileWatcherMonitor が担当)
        if (target is null && action == "Write") return;

        // System (PID 4) による遅延 I/O は、ファイルを開いたプロセスに帰属させる
        var pid = data.ProcessID;
        var processName = data.ProcessName;
        if (pid == 4 && _openers.TryGetValue(data.FileObject, out var opener))
        {
            pid = opener.Pid;
            processName = opener.Name;
        }
        if (IsExcludedProcess(processName)) return;

        if (action == "Read")
        {
            if (IsAuditedReadProcess(processName))
            {
                // rdpclip 等による読み取りは RDP クリップボード経由の持ち出しの可能性が
                // 高いため、読み取り先がローカルでも記録する
                target ??= "fixed";
            }
            else if (target is null || !_options.IncludeNetworkReads)
            {
                return;
            }
        }

        var now = DateTime.UtcNow;
        _pending.AddOrUpdate(
            (pid, path, action),
            _ => new IoAggregate
            {
                Bytes = data.IoSize,
                FirstUtc = now,
                LastUtc = now,
                ProcessName = processName,
                Action = action,
                Target = target,
            },
            (_, agg) =>
            {
                Interlocked.Add(ref agg.Bytes, data.IoSize);
                agg.LastUtc = now;
                return agg;
            });
    }

    private void OnInfoOp(FileIOInfoTraceData data, string action)
    {
        var rawPath = data.FileName;
        if (string.IsNullOrEmpty(rawPath)) return;

        var (path, target) = ClassifyPath(rawPath);
        if (target is null) return;

        var processName = data.ProcessName;
        if (IsExcludedProcess(processName)) return;

        // 同一操作が複数イベントとして届くことがあるため 1 秒以内の重複は捨てる
        var key = (data.ProcessID, path, action);
        var now = DateTime.UtcNow;
        if (_lastInfoKey == key && now - _lastInfoUtc < TimeSpan.FromSeconds(1)) return;
        _lastInfoKey = key;
        _lastInfoUtc = now;

        _activityLogger.Log(new ActivityEvent
        {
            Source = "network-io",
            Action = action,
            Path = path,
            Target = target,
            ProcessName = processName,
            ProcessId = data.ProcessID,
        });
    }

    /// <summary>
    /// パスを正規化し、記録対象 (network / removable) なら分類を返す。対象外は null。
    /// </summary>
    private (string Path, string? Target) ClassifyPath(string rawPath)
    {
        var path = PathClassifier.Normalize(rawPath);
        var target = PathClassifier.Classify(path);
        switch (target)
        {
            case PathTarget.Network:
                // 割り当てドライブ (Z: など) は UNC に解決して記録する
                if (path.Length >= 2 && path[1] == ':')
                {
                    path = PathClassifier.ResolveMappedDrive(path);
                }
                return (path, "network");
            case PathTarget.Removable when _options.IncludeRemovable:
                return (path, "removable");
            default:
                return (path, null);
        }
    }

    private bool IsAuditedReadProcess(string processName)
    {
        foreach (var audited in _options.AuditReadProcesses)
        {
            if (string.Equals(processName, audited, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private bool IsExcludedProcess(string processName)
    {
        foreach (var excluded in _options.ExcludeProcesses)
        {
            if (string.Equals(processName, excluded, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>一定時間 I/O が途切れた集約エントリをログに書き出す。</summary>
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.WriteFlushSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            FlushPending(force: false);
        }
    }

    private void FlushPending(bool force)
    {
        var idleThreshold = TimeSpan.FromSeconds(Math.Max(1, _options.WriteFlushSeconds));
        var maxHold = TimeSpan.FromSeconds(60);
        var now = DateTime.UtcNow;

        // オープン元プロセスの記録は TTL (10 分) で掃除する。
        // Close 時に消すと、その後に来る遅延書き込みへ帰属できなくなるため TTL 方式にしている。
        foreach (var kv in _openers)
        {
            if (now - kv.Value.CreatedUtc > TimeSpan.FromMinutes(10))
            {
                _openers.TryRemove(kv.Key, out _);
            }
        }

        foreach (var kv in _pending)
        {
            var agg = kv.Value;
            var idle = now - agg.LastUtc;
            var held = now - agg.FirstUtc;
            if (!force && idle < idleThreshold && held < maxHold) continue;
            if (!_pending.TryRemove(kv.Key, out var removed)) continue;

            _activityLogger.Log(new ActivityEvent
            {
                Timestamp = new DateTimeOffset(removed.FirstUtc).ToLocalTime(),
                Source = "network-io",
                Action = removed.Action,
                Path = kv.Key.Path,
                Target = removed.Target,
                ProcessName = removed.ProcessName,
                ProcessId = kv.Key.Pid,
                Bytes = Interlocked.Read(ref removed.Bytes),
                Detail = held >= maxHold ? "継続中の I/O を途中集計" : null,
            });
        }
    }
}
