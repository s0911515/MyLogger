using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly ProcessUserResolver _processUserResolver;
    private readonly List<string> _watchedLocalPaths;
    private readonly ILogger<EtwFileIoMonitor> _logger;
    private TraceEventSession? _session;

    private sealed class IoAggregate
    {
        public long Bytes;
        public DateTime FirstUtc;
        public DateTime LastUtc;
        public required string ProcessName;
        public required string Action;   // Write / Read
        public required string? Target;  // network / removable / fixed
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

    private sealed record PendingLocalOpen(string Path, int Pid, string ProcessName, string EventType, DateTimeOffset Timestamp);

    // ローカルオープンの WMI ユーザー解決を ETW コールバックスレッドから切り離すためのキュー。
    // (経緯は RecordLocalOpen のコメント参照)
    private readonly Channel<PendingLocalOpen> _localOpenQueue = Channel.CreateBounded<PendingLocalOpen>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public EtwFileIoMonitor(
        IOptions<MonitorOptions> options,
        ActivityLogger activityLogger,
        ProcessUserResolver processUserResolver,
        ILogger<EtwFileIoMonitor> logger)
    {
        _options = options.Value.Etw;
        _activityLogger = activityLogger;
        _processUserResolver = processUserResolver;
        _watchedLocalPaths = WatchedPathResolver.Resolve(options.Value.FileWatcher);
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
        var resolveTask = ResolveLocalOpensLoopAsync(stoppingToken);

        stoppingToken.Register(() =>
        {
            // EventsLost はセッションが生きている間 (Stop() の前) しか WMI 経由で取得できない。
            // Process() が戻った後 (Stop 済み) に読もうとすると COMException になる (実機で確認済み)。
            try
            {
                if (_session is { EventsLost: > 0 } session)
                {
                    _logger.LogWarning(
                        "ETW セッションでイベントの取りこぼしが発生しました (EventsLost={EventsLost})。" +
                        "システム全体の FileIO イベント量がカーネルバッファ (BufferSizeMB) を上回った" +
                        "可能性があります。①のプロセス相関 (local_fs_etw_open) の精度が低下している場合があります",
                        session.EventsLost);
                }
            }
            catch { /* 停止時、EventsLost の取得自体に失敗しても無視する */ }

            try { _session?.Stop(); } catch { /* 停止時 */ }
        });

        try
        {
            await Task.WhenAll(processingTask, flushTask, resolveTask);
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
            _session = new TraceEventSession(SessionName)
            {
                // 既定値 (64MB) だと、システム全体の FileIO イベント量が多い環境
                // (常時大量のファイルアクセスがあるマシン) でカーネルバッファを使い切り、
                // ETW セッション自体がイベントを取りこぼす (EventsLost) おそれがあるため、
                // 実用上の目安として大きめに設定する。
                BufferSizeMB = 256,
            };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.FileIOInit |
                KernelTraceEventParser.Keywords.FileIO);

            var kernel = _session.Source.Kernel;
            kernel.FileIOCreate += OnCreate;
            kernel.FileIOWrite += data => OnReadWrite(data, "Write");
            kernel.FileIOFlush += OnFlush;
            if (_options.IncludeNetworkReads || _options.AuditReadProcesses.Count > 0)
            {
                kernel.FileIORead += data => OnReadWrite(data, "Read");
            }
            kernel.FileIORename += data => OnInfoOp(data, "Renamed");
            kernel.FileIODelete += data => OnInfoOp(data, "Deleted");

            _logger.LogInformation("ETW 監視を開始しました (ネットワーク共有 / リムーバブルへの書き込み)");
            _session.Source.Process(); // Stop() されるまでブロック (EventsLost のログ出力は停止処理側で行う)
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "ETW セッションが異常終了しました");
        }
    }

    /// <summary>
    /// ネットワーク / リムーバブル上のファイルオープンを記録し、
    /// 後続の Write イベントのプロセス帰属に使う (ログには出さない)。
    /// あわせて、監視対象のローカル固定ドライブ上のオープンは local_fs_etw_open (テーブルB) に記録し、
    /// tools/ReconcileLocalFs が①の操作元プロセスを後から突合できるようにする。
    /// FileIOCreate はハンドルのオープン/新規作成そのものであり、キャッシュされた読み書きと違って
    /// Fast I/O の影響を受けず確実に発生するため、この相関は高い信頼性で成立する。
    /// </summary>
    private void OnCreate(FileIOCreateTraceData data)
    {
        var rawPath = data.FileName;
        if (string.IsNullOrEmpty(rawPath) || data.ProcessID == 4) return;

        var (path, target) = ClassifyPath(rawPath);

        if (target is null)
        {
            var isWatchedLocal = IsWatchedLocalPath(path);
            if (isWatchedLocal)
            {
                RecordLocalOpen(path, data.ProcessID, data.ProcessName, "Create");
            }
            // ネットワーク / リムーバブル宛に加え、監視対象パス内のローカルオープン、
            // および読み取り監査対象プロセス (rdpclip 等) のローカルオープンも _openers に記録する。
            // ローカルファイルへの書き込みも System (PID 4) のスレッドが遅延実行することがあり、
            // その際にこの PID→実プロセスの対応表が無いと帰属を解決できないため
            // (それ以外のローカルオープンまで記録すると際限なく増えるため対象を絞っている)。
            if (!isWatchedLocal && !IsAuditedReadProcess(data.ProcessName)) return;
        }

        if (_openers.Count > 8192) _openers.Clear(); // 念のための上限 (通常は TTL 掃除で足りる)
        _openers[data.FileObject] = (data.ProcessID, data.ProcessName, DateTime.UtcNow);
    }

    private bool IsWatchedLocalPath(string path)
    {
        foreach (var watched in _watchedLocalPaths)
        {
            if (path.StartsWith(watched, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 監視対象パス内のローカルオープン/書き込み/フラッシュを local_fs_etw_open (テーブルB) に記録する。
    /// PID→ユーザーの WMI 解決は、そのプロセスがまだ実行中であるこの捕捉のタイミングでしか行えない
    /// (①側の tools/ReconcileLocalFs によるバッチ突合は後日行われる可能性があり、その時点では
    /// 既にプロセスが終了しWMIから引けなくなっているため)。そのため、ここで即座に解決してから
    /// 保存する。
    /// </summary>
    /// <summary>
    /// PID→ユーザーの WMI 解決 (<see cref="ProcessUserResolver.Resolve"/>) は数ms〜十数msかかることがある。
    /// ETW コールバックは `TraceEventSession.Source.Process()` によりシステム全体の FileIO イベントを
    /// 単一スレッドで順番に処理しているため、このコールバックの中で同期的に WMI を呼ぶと、そのぶん
    /// だけ後続のイベント処理が遅延し、カーネル側バッファを使い切って取りこぼす原因になり得る
    /// (かつて SACL フォールバックが FileSystemWatcher のハンドラ内で `Thread.Sleep` していたのと
    /// 同種の問題。実機で①の相関がほぼ成立しなくなる不具合として発現し、原因調査の末に判明した)。
    /// そのため、ここでは WMI 解決を行わずキューに積むだけに留め、実際の解決は
    /// <see cref="ResolveLocalOpensLoopAsync"/> が別スレッドで行う。
    /// </summary>
    private void RecordLocalOpen(string path, int pid, string processName, string eventType)
    {
        _localOpenQueue.Writer.TryWrite(new PendingLocalOpen(path, pid, processName, eventType, DateTimeOffset.Now));
    }

    /// <summary>ETW コールバックから切り離して PID→ユーザーの WMI 解決を行い、テーブルBへ書き込む。</summary>
    private async Task ResolveLocalOpensLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var pending in _localOpenQueue.Reader.ReadAllAsync(ct))
            {
                var user = _processUserResolver.Resolve(pending.Pid);
                _activityLogger.LogEtwOpen(new EtwOpenEvent(
                    Timestamp: pending.Timestamp,
                    Path: pending.Path,
                    EventType: pending.EventType,
                    ProcessName: pending.ProcessName,
                    ProcessId: pending.Pid,
                    TargetUser: user));
            }
        }
        catch (OperationCanceledException)
        {
            // 停止時
        }
    }

    /// <summary>
    /// FlushFileBuffers 等によるフラッシュを local_fs_etw_open への追加の手がかりとして使う。
    /// 保存時のキャッシュ書き込み (FileIOWrite) は Fast I/O 経由だと ETW に載らないことがあるが、
    /// フラッシュはキャッシュを迂回して実ディスクへ同期する操作であり、通常の IRP 経由の
    /// イベントとして確実に発生するため、多くのアプリが保存後に呼ぶこの操作を補助的に追跡する。
    /// </summary>
    private void OnFlush(FileIOSimpleOpTraceData data)
    {
        var rawPath = data.FileName;
        if (string.IsNullOrEmpty(rawPath)) return;

        var (path, target) = ClassifyPath(rawPath);
        if (target is not null) return; // ネットワーク/リムーバブルは Write 側で十分カバーされる

        // System (PID 4) による遅延フラッシュは、ファイルを開いたプロセスに帰属させる。
        // 解決できなければ "System" をそのまま記録するより記録しない方がまし
        // (①側は UNKNOWN のまま tools/ReconcileLocalFs による突合に委ねる)。
        var pid = data.ProcessID;
        var processName = data.ProcessName;
        if (pid == 4 && _openers.TryGetValue(data.FileObject, out var opener))
        {
            pid = opener.Pid;
            processName = opener.Name;
        }
        if (pid == 4) return;

        if (IsWatchedLocalPath(path) && !IsExcludedProcess(processName))
        {
            RecordLocalOpen(path, pid, processName, "Flush");
        }
    }

    private void OnReadWrite(FileIOReadWriteTraceData data, string action)
    {
        var rawPath = data.FileName;
        if (string.IsNullOrEmpty(rawPath)) return;

        var (path, target) = ClassifyPath(rawPath);

        // System (PID 4) による遅延 I/O は、ファイルを開いたプロセスに帰属させる
        var pid = data.ProcessID;
        var processName = data.ProcessName;
        if (pid == 4 && _openers.TryGetValue(data.FileObject, out var opener))
        {
            pid = opener.Pid;
            processName = opener.Name;
        }

        if (target is null && action == "Write")
        {
            // ローカル宛の書き込み自体は① (FileWatcherMonitor) が担当するため記録しないが、
            // 保存までハンドルを保持し続けるアプリ (メモ帳等) では FileIOCreate (開いた瞬間) だけでは
            // 突合時に古すぎる候補になってしまう可能性があるため、書き込みのタイミングでも
            // 監視対象パス内に限り記録を更新しておく。
            // pid が 4 (System) のまま解決できなかった場合は、誤った帰属を残すより記録しない方がよい。
            if (pid != 4 && IsWatchedLocalPath(path) && !IsExcludedProcess(processName))
            {
                RecordLocalOpen(path, pid, processName, "Write");
            }
            return;
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
            User = _processUserResolver.Resolve(data.ProcessID),
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
                // 名前付きパイプ (\\host\PIPE\name) や IPC$ への通信は、共有フォルダを開いた際などに
                // 自動的に発生する RPC 制御通信であり、ユーザーによるファイルの持ち出しではないため対象外とする。
                if (IsPipeOrIpcPath(path)) return (path, null);
                return (path, "network");
            case PathTarget.Removable when _options.IncludeRemovable:
                return (path, "removable");
            default:
                // ローカル固定ドライブの生パス (\Device\HarddiskVolumeN\...) はドライブレター形式に
                // 変換する (FileWatcherMonitor 側のパス表記と合わせ、local_fs_etw_open との
                // 突合に使えるようにするため)。
                return (PathClassifier.ResolveDevicePathToDriveLetter(path), null);
        }
    }

    private static bool IsPipeOrIpcPath(string path) =>
        path.Contains(@"\PIPE\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\IPC$\", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(@"\IPC$", StringComparison.OrdinalIgnoreCase);

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
                User = _processUserResolver.Resolve(kv.Key.Pid),
            });
        }
    }
}
