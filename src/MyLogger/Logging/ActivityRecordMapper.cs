using System.Text.Json;
using MyLogger.Util;

namespace MyLogger.Logging;

/// <summary>SQLite の activity_log テーブル 1 行に対応する値。</summary>
public sealed record ActivityRecord(
    DateTimeOffset EventTimestamp,
    string ActionType,
    string TargetUser,
    string? SourceIp,
    string? SourcePath,
    string? DestPath,
    string? AdditionalInfo);

/// <summary>
/// 各 Monitor が組み立てる <see cref="ActivityEvent"/> (検出元ごとの生データ) を、
/// 要件定義書 §4 の共通ログフォーマット (<see cref="ActivityRecord"/>) へ変換する。
/// </summary>
public static class ActivityRecordMapper
{
    public static ActivityRecord Map(ActivityEvent evt)
    {
        var (actionType, sourcePath, destPath) = MapAction(evt);
        return new ActivityRecord(
            EventTimestamp: evt.Timestamp,
            ActionType: actionType,
            TargetUser: string.IsNullOrEmpty(evt.User) ? "UNKNOWN" : evt.User,
            SourceIp: evt.RemoteIp,
            SourcePath: sourcePath,
            DestPath: destPath,
            AdditionalInfo: BuildAdditionalInfo(evt));
    }

    private static (string ActionType, string? SourcePath, string? DestPath) MapAction(ActivityEvent evt)
    {
        return (evt.Source, evt.Action, evt.Target) switch
        {
            ("local-fs", "Created", _) => ("LOCAL_CREATE", null, evt.Path),
            ("local-fs", "Changed", _) => ("LOCAL_CHANGE", null, evt.Path),
            ("local-fs", "Deleted", _) => ("LOCAL_DELETE", evt.Path, null),
            ("local-fs", "Renamed", _) => ("LOCAL_RENAME", evt.OldPath, evt.Path),
            ("local-fs", "Moved", _) => ("LOCAL_MOVE", evt.OldPath, evt.Path),
            ("local-fs", "MonitorOverflow", _) => ("LOCAL_MONITOR_OVERFLOW", null, evt.Path),

            ("network-io", "Write", "network") => ("NETWORK_EXFIL_WRITE", null, evt.Path),
            ("network-io", "Write", "removable") => ("REMOVABLE_WRITE", null, evt.Path),
            ("network-io", "Read", "fixed") => ("RDP_CLIPBOARD_COPY", evt.Path, null),
            ("network-io", "Read", "network") => ("NETWORK_READ", evt.Path, null),
            ("network-io", "Renamed", "network") => ("NETWORK_RENAME", evt.Path, null),
            ("network-io", "Renamed", "removable") => ("REMOVABLE_RENAME", evt.Path, null),
            ("network-io", "Deleted", "network") => ("NETWORK_DELETE", evt.Path, null),
            ("network-io", "Deleted", "removable") => ("REMOVABLE_DELETE", evt.Path, null),

            ("smb-server", "ShareFileAccess", _) => (SmbAccessClassifier.IsWriteAccess(evt.Access) ? "SHARE_WRITE" : "SHARE_READ", null, evt.Path),
            ("smb-server", "ShareConnected", _) => ("SHARE_CONNECTED", null, evt.Path),
            ("smb-server", "ShareCreated", _) => ("SHARE_CREATED", null, evt.Path),
            ("smb-server", "ShareModified", _) => ("SHARE_MODIFIED", null, evt.Path),
            ("smb-server", "ShareDeleted", _) => ("SHARE_DELETED", null, evt.Path),

            ("logon", "Login", _) => ("LOGIN", null, null),
            ("logon", "LoginFailed", _) => ("LOGIN_FAILED", null, null),
            ("logon", "Logout", _) => ("LOGOUT", null, null),

            _ => ($"{evt.Source.ToUpperInvariant()}_{evt.Action.ToUpperInvariant()}", null, evt.Path),
        };
    }

    private static string? BuildAdditionalInfo(ActivityEvent evt)
    {
        Dictionary<string, object>? extra = null;
        void Add(string key, object? value)
        {
            if (value is null) return;
            extra ??= new Dictionary<string, object>();
            extra[key] = value;
        }

        Add("process", evt.ProcessName);
        Add("pid", evt.ProcessId);
        Add("bytes", evt.Bytes);
        Add("share", evt.ShareName);
        Add("access", evt.Access);
        Add("detail", evt.Detail);

        return extra is null ? null : JsonSerializer.Serialize(extra);
    }
}
