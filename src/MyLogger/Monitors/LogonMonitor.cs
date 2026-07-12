using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using Microsoft.Extensions.Options;
using MyLogger.Config;
using MyLogger.Logging;
using MyLogger.Util;

namespace MyLogger.Monitors;

/// <summary>
/// ユーザーのサインイン・サインアウトを Windows セキュリティ監査ログ (イベント ID 4624/4634/4647) の
/// 購読で記録する (要件定義書 3.2 ④)。
/// コンソールログイン (LogonType 2)、リモートデスクトップ (LogonType 10)、
/// SMB 認証など (LogonType 3) を対象とし、サービスアカウントや匿名ログオンはノイズのため除外する。
///
/// ログオフイベント (4634/4647) は LogonType を持たないため、対象と判定したログオン (4624) の
/// TargetLogonId を記憶しておき、対応するログオフのみを記録する
/// (バックグラウンドサービス等の無関係なログオフセッションをノイズとして除外するため)。
///
/// 事前に監査ポリシーの有効化が必要 (AuditPolicyConfigurator が起動時に自動設定する):
///   auditpol /set /subcategory:"{0CCE9215-69AE-11D9-BED3-505054503030}" /success:enable /failure:enable  (ログオン)
///   auditpol /set /subcategory:"{0CCE9216-69AE-11D9-BED3-505054503030}" /success:enable /failure:enable  (ログオフ)
/// </summary>
public sealed class LogonMonitor : BackgroundService
{
    private readonly LogonOptions _options;
    private readonly ActivityLogger _activityLogger;
    private readonly ILogger<LogonMonitor> _logger;
    private EventLogWatcher? _watcher;

    // 記録対象とする LogonType (2=コンソール, 3=ネットワーク/SMB, 10=RDP)
    private static readonly HashSet<string> IncludedLogonTypes = new() { "2", "3", "10" };

    // 記録対象と判定した 4624 の TargetLogonId → 記録時刻 (対応する 4634/4647 のみを記録するため)
    private readonly ConcurrentDictionary<string, DateTime> _trackedLogonIds = new();
    private DateTime _lastSweep = DateTime.UtcNow;
    private static readonly TimeSpan LogonIdTtl = TimeSpan.FromDays(1);

    public LogonMonitor(
        IOptions<MonitorOptions> options,
        ActivityLogger activityLogger,
        ILogger<LogonMonitor> logger)
    {
        _options = options.Value.Logon;
        _activityLogger = activityLogger;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("LogonMonitor は無効化されています");
            return Task.CompletedTask;
        }

        try
        {
            var eventIds = _options.IncludeFailedLogon
                ? "EventID=4624 or EventID=4634 or EventID=4647 or EventID=4625"
                : "EventID=4624 or EventID=4634 or EventID=4647";
            var query = new EventLogQuery("Security", PathType.LogName, $"*[System[({eventIds})]]");
            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnEventRecord;
            _watcher.Enabled = true;
            _logger.LogInformation("ログイン/ログアウト監視 (セキュリティ監査ログ) を開始しました");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "セキュリティログを読み取る権限がありません。管理者権限で実行してください");
        }
        catch (EventLogException ex)
        {
            _logger.LogError(ex, "セキュリティログの購読に失敗しました");
        }

        stoppingToken.Register(() =>
        {
            if (_watcher is not null)
            {
                _watcher.Enabled = false;
                _watcher.Dispose();
            }
        });
        return Task.CompletedTask;
    }

    private void OnEventRecord(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventRecord is not { } record) return;
        try
        {
            var fields = SecurityEventParser.ParseEventData(record.ToXml());

            fields.TryGetValue("TargetUserName", out var user);
            fields.TryGetValue("TargetDomainName", out var domain);
            fields.TryGetValue("LogonType", out var logonType);
            fields.TryGetValue("IpAddress", out var ip);
            fields.TryGetValue("WorkstationName", out var workstation);
            fields.TryGetValue("TargetLogonId", out var logonId);

            // マシンアカウント (ユーザー名が $ で終わる) と ANONYMOUS LOGON はノイズのため除外
            if (string.IsNullOrEmpty(user) || user.EndsWith('$') ||
                string.Equals(user, "ANONYMOUS LOGON", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (record.Id == 4624 || record.Id == 4625)
            {
                // ログオン成功/失敗は対象 LogonType (コンソール/RDP/ネットワーク) のみに絞る。
                if (!(logonType is not null && IncludedLogonTypes.Contains(logonType)))
                {
                    return;
                }
                // 成功したログオンの LogonId を記憶し、対応するログオフのみ後で記録できるようにする。
                if (record.Id == 4624 && !string.IsNullOrEmpty(logonId))
                {
                    _trackedLogonIds[logonId] = DateTime.UtcNow;
                    SweepTrackedLogonIds();
                }
            }
            else
            {
                // ログオフ (4634/4647) には LogonType が付かないため、記録対象と判定した
                // ログオンの LogonId と対応するものだけを記録する (無関係なログオフはノイズのため除外)。
                if (string.IsNullOrEmpty(logonId) || !_trackedLogonIds.TryRemove(logonId, out _))
                {
                    return;
                }
            }

            var action = record.Id switch
            {
                4624 => "Login",
                4625 => "LoginFailed",
                _ => "Logout", // 4634 / 4647
            };

            var logonTypeLabel = logonType switch
            {
                "2" => "Console",
                "3" => "Network(SMB等)",
                "10" => "RDP",
                _ => logonType,
            };

            _activityLogger.Log(new ActivityEvent
            {
                Timestamp = record.TimeCreated ?? DateTimeOffset.Now.DateTime,
                Source = "logon",
                Action = action,
                Path = string.Empty,
                User = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}",
                RemoteIp = string.IsNullOrEmpty(ip) || ip == "-" ? null : ip,
                Detail = workstation is null ? logonTypeLabel : $"{logonTypeLabel} / {workstation}",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ログオン監査イベントの解析に失敗しました (EventID={Id})", e.EventRecord?.Id);
        }
        finally
        {
            e.EventRecord?.Dispose();
        }
    }

    /// <summary>対応するログオフが来ないまま残り続ける LogonId を定期的に間引く (異常切断等)。</summary>
    private void SweepTrackedLogonIds()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSweep < TimeSpan.FromHours(1)) return;
        _lastSweep = now;
        foreach (var kv in _trackedLogonIds)
        {
            if (now - kv.Value > LogonIdTtl) _trackedLogonIds.TryRemove(kv.Key, out _);
        }
    }
}
