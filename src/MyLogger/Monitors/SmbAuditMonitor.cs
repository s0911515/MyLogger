using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Options;
using MyLogger.Config;
using MyLogger.Logging;

namespace MyLogger.Monitors;

/// <summary>
/// この PC の共有フォルダにネットワーク経由でアクセスされた操作を、
/// Windows セキュリティ監査ログ (イベント ID 5140 / 5145 / 5142-5144) の購読で記録する。
///
/// 事前に監査ポリシーの有効化が必要 (scripts/enable-audit.ps1 を参照):
///   auditpol /set /subcategory:"{0CCE9224-69AE-11D9-BED3-505054503030}" /success:enable /failure:enable  (ファイル共有)
///   auditpol /set /subcategory:"{0CCE9244-69AE-11D9-BED3-505054503030}" /success:enable /failure:enable  (詳細なファイル共有)
/// </summary>
public sealed class SmbAuditMonitor : BackgroundService
{
    private readonly SmbAuditOptions _options;
    private readonly ActivityLogger _activityLogger;
    private readonly ILogger<SmbAuditMonitor> _logger;
    private EventLogWatcher? _watcher;

    public SmbAuditMonitor(
        IOptions<MonitorOptions> options,
        ActivityLogger activityLogger,
        ILogger<SmbAuditMonitor> logger)
    {
        _options = options.Value.SmbAudit;
        _activityLogger = activityLogger;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SmbAuditMonitor は無効化されています");
            return Task.CompletedTask;
        }

        try
        {
            // 5140: 共有オブジェクトへのアクセス / 5145: 共有内ファイルへの詳細アクセス
            // 5142-5144: 共有の作成・変更・削除
            var query = new EventLogQuery("Security", PathType.LogName,
                "*[System[(EventID=5140 or EventID=5142 or EventID=5143 or EventID=5144 or EventID=5145)]]");
            _watcher = new EventLogWatcher(query);
            _watcher.EventRecordWritten += OnEventRecord;
            _watcher.Enabled = true;
            _logger.LogInformation("共有フォルダアクセス監視 (セキュリティ監査ログ) を開始しました");
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
            var fields = ParseEventData(record.ToXml());

            fields.TryGetValue("ShareName", out var shareName);
            if (!_options.IncludeIpcShare && shareName is not null &&
                shareName.EndsWith(@"\IPC$", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            fields.TryGetValue("SubjectUserName", out var user);
            fields.TryGetValue("SubjectDomainName", out var domain);
            fields.TryGetValue("IpAddress", out var ip);
            fields.TryGetValue("ShareLocalPath", out var localPath);
            fields.TryGetValue("RelativeTargetName", out var relativeTarget);
            fields.TryGetValue("AccessMask", out var accessMask);

            // 属性読み取りのみのアクセスを間引く (5145 のみ。データの読み書き・削除は必ず残す)
            if (_options.IgnoreAttributeOnlyAccess && record.Id == 5145 &&
                TryParseMask(accessMask, out var mask) && (mask & MeaningfulAccessBits) == 0)
            {
                return;
            }

            // アクセスされたファイルの実パスを組み立てる (5145 のみ RelativeTargetName を持つ)
            var path = BuildPath(localPath, relativeTarget) ?? shareName ?? "(不明)";

            var action = record.Id switch
            {
                5140 => "ShareConnected",     // 共有への接続
                5142 => "ShareCreated",       // 共有の新規作成 (漏洩経路の作成として重要)
                5143 => "ShareModified",
                5144 => "ShareDeleted",
                5145 => "ShareFileAccess",    // 共有内ファイルへのアクセス
                _ => $"Event{record.Id}",
            };

            _activityLogger.Log(new ActivityEvent
            {
                Timestamp = record.TimeCreated ?? DateTimeOffset.Now.DateTime,
                Source = "smb-server",
                Action = action,
                Path = path,
                ShareName = shareName,
                User = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}",
                RemoteIp = ip,
                Access = DecodeAccessMask(accessMask),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "監査イベントの解析に失敗しました (EventID={Id})", e.EventRecord?.Id);
        }
        finally
        {
            e.EventRecord?.Dispose();
        }
    }

    private static string? BuildPath(string? localPath, string? relativeTarget)
    {
        if (string.IsNullOrEmpty(localPath)) return null;
        // ShareLocalPath は "\??\C:\Shared" 形式で来る
        var basePath = localPath.StartsWith(@"\??\", StringComparison.Ordinal) ? localPath[4..] : localPath;
        if (string.IsNullOrEmpty(relativeTarget)) return basePath;
        return Path.Combine(basePath, relativeTarget);
    }

    /// <summary>イベント XML の EventData/Data 要素を名前付きで取り出す。</summary>
    private static Dictionary<string, string> ParseEventData(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
        var nodes = doc.SelectNodes("//e:EventData/e:Data", ns);
        if (nodes is null) return result;
        foreach (XmlNode node in nodes)
        {
            var name = node.Attributes?["Name"]?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                result[name] = node.InnerText;
            }
        }
        return result;
    }

    /// <summary>データの読み書き・削除・権限変更を表すアクセスビット (属性参照のみは含まない)。</summary>
    private const uint MeaningfulAccessBits =
        0x00000001 | // ReadData
        0x00000002 | // WriteData
        0x00000004 | // AppendData
        0x00000020 | // Execute
        0x00000040 | // DeleteChild
        0x00000100 | // WriteAttributes
        0x00010000 | // Delete
        0x00040000 | // WriteDac
        0x00080000;  // WriteOwner

    private static bool TryParseMask(string? accessMask, out uint mask)
    {
        mask = 0;
        if (string.IsNullOrEmpty(accessMask)) return false;
        var span = accessMask.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? accessMask.AsSpan(2) : accessMask.AsSpan();
        return uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out mask);
    }

    /// <summary>アクセスマスク (例 "0x2") を読みやすい権利名のリストに変換する。</summary>
    private static string? DecodeAccessMask(string? accessMask)
    {
        if (string.IsNullOrEmpty(accessMask)) return null;
        if (!TryParseMask(accessMask, out var mask))
        {
            return accessMask;
        }

        var sb = new StringBuilder();
        void Add(uint bit, string name)
        {
            if ((mask & bit) == 0) return;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(name);
        }
        Add(0x00000001, "ReadData");
        Add(0x00000002, "WriteData");
        Add(0x00000004, "AppendData");
        Add(0x00000020, "Execute");
        Add(0x00000040, "DeleteChild");
        Add(0x00000080, "ReadAttributes");
        Add(0x00000100, "WriteAttributes");
        Add(0x00010000, "Delete");
        Add(0x00020000, "ReadControl");
        Add(0x00040000, "WriteDac");
        Add(0x00080000, "WriteOwner");
        return sb.Length > 0 ? sb.ToString() : accessMask;
    }
}
