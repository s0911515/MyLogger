using System.Collections.Concurrent;
using System.Management;

namespace MyLogger.Util;

/// <summary>
/// プロセス ID からそのプロセスの実行ユーザー (DOMAIN\user) を解決する。
/// WMI (Win32_Process.GetOwner) は 1 回あたり数 ms 程度かかるため、PID 単位で結果をキャッシュする。
/// 解決に成功した結果は長時間(<see cref="PositiveCacheTtl"/>)キャッシュするが、
/// 失敗した結果は短時間(<see cref="NegativeCacheTtl"/>)しかキャッシュしない。
/// タイミング等による一時的な失敗を長時間引きずって target_user が "UNKNOWN" のままになるのを防ぐため。
/// </summary>
public sealed class ProcessUserResolver
{
    private sealed record CacheEntry(string? User, DateTime CachedAtUtc);

    private readonly ConcurrentDictionary<int, CacheEntry> _cache = new();
    private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(5);

    private readonly ILogger<ProcessUserResolver> _logger;

    public ProcessUserResolver(ILogger<ProcessUserResolver> logger)
    {
        _logger = logger;
    }

    /// <summary>解決できない場合は null を返す (呼び出し側で "UNKNOWN" 等にフォールバックする)。</summary>
    public string? Resolve(int processId)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(processId, out var cached))
        {
            var ttl = cached.User is null ? NegativeCacheTtl : PositiveCacheTtl;
            if (now - cached.CachedAtUtc < ttl)
            {
                return cached.User;
            }
        }

        var user = QueryOwner(processId);

        _cache[processId] = new CacheEntry(user, now);
        if (_cache.Count > 4096)
        {
            // 念のための上限。TTL 切れエントリを間引く。
            foreach (var kv in _cache)
            {
                var ttl = kv.Value.User is null ? NegativeCacheTtl : PositiveCacheTtl;
                if (now - kv.Value.CachedAtUtc > ttl) _cache.TryRemove(kv.Key, out _);
            }
        }
        return user;
    }

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// WMI (`Win32_Process.GetOwner`) はごく稀に応答が返らず**ハングする**ことがある実機で確認済み。
    /// この呼び出し元 (ETWコールバック由来の解決ループ等) は単一スレッドで順番に処理するため、
    /// 1回のハングが以降の全ての解決処理を永久に止めてしまう(①のプロセス相関がある時点から
    /// 一切記録されなくなる不具合として発現した)。そのため <see cref="Task.Run"/> + タイムアウトで
    /// 必ず一定時間で復帰できるようにする(タイムアウトした場合、WMI側の呼び出し自体は裏で残る
    /// 可能性があるが、少なくとも呼び出し元をブロックし続けることは無い)。
    /// </summary>
    private string? QueryOwner(int processId)
    {
        try
        {
            var task = Task.Run(() => QueryOwnerCore(processId));
            if (task.Wait(QueryTimeout))
            {
                return task.Result;
            }
            _logger.LogWarning(
                "プロセス {Pid} の所有者取得 (WMI) がタイムアウトしました ({TimeoutSeconds}秒)。" +
                "このプロセスの target_user は解決できません", processId, QueryTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "プロセス {Pid} の所有者取得中に例外が発生しました", processId);
            return null;
        }
    }

    private string? QueryOwnerCore(int processId)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}");
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                var args = new object[2];
                var rc = (uint)mo.InvokeMethod("GetOwner", args);
                if (rc != 0)
                {
                    _logger.LogDebug(
                        "プロセス {Pid} の所有者取得に失敗しました (GetOwner 戻り値={ReturnCode})。次回以降に再試行します",
                        processId, rc);
                    return null;
                }

                var name = args[0] as string;
                var domain = args[1] as string;
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }
                return string.IsNullOrEmpty(domain) ? name : $@"{domain}\{name}";
            }
        }

        // 該当プロセスが見つからない (既に終了した等)
        _logger.LogDebug("プロセス {Pid} が WMI から見つかりませんでした (既に終了した可能性)", processId);
        return null;
    }
}
