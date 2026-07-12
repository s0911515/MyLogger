using System.Collections.Concurrent;
using System.DirectoryServices.AccountManagement;

namespace MyLogger.Util;

/// <summary>
/// 設定ファイルの MonitoredUsers (ユーザー名 / DOMAIN\user / ローカルグループ名) に基づき、
/// あるユーザーが監視対象かどうかを判定する。
/// グループ名はローカルグループのメンバーに展開してキャッシュする。
/// 展開に失敗した場合 (ドメイン到達不可等) はそのエントリを無視し、全員監視側にフェイルオープンする
/// (証跡の欠落は誤記録より悪いため)。
/// </summary>
public sealed class UserFilter
{
    /// <summary>target_user を解決できなかったことを表すセンチネル値。</summary>
    public const string Unknown = "UNKNOWN";

    private readonly IReadOnlyList<string> _rawEntries;
    private readonly ILogger _logger;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(15);

    private HashSet<string>? _resolvedUsers; // 展開済みユーザー名の集合 (大文字小文字無視)
    private DateTime _resolvedAtUtc = DateTime.MinValue;
    private readonly object _lock = new();

    public UserFilter(IReadOnlyList<string> monitoredUsers, ILogger logger)
    {
        _rawEntries = monitoredUsers;
        _logger = logger;
    }

    /// <summary>フィルタが無効 (全ユーザー監視) かどうか。</summary>
    public bool IsUnrestricted => _rawEntries.Count == 0;

    /// <summary>
    /// target_user がこの設定で監視対象かどうかを判定する。
    /// UNKNOWN や null は判定不能のため常に true (フェイルオープン)。
    /// </summary>
    public bool IsMonitored(string? targetUser)
    {
        if (IsUnrestricted) return true;
        if (string.IsNullOrEmpty(targetUser) || targetUser == Unknown) return true;

        var resolved = GetResolvedUsers();
        if (resolved.Contains(targetUser)) return true;

        // "DOMAIN\user" のうち user 部分だけでの一致も許可する
        var slash = targetUser.LastIndexOf('\\');
        if (slash >= 0 && resolved.Contains(targetUser[(slash + 1)..])) return true;

        return false;
    }

    private HashSet<string> GetResolvedUsers()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_resolvedUsers is not null && now - _resolvedAtUtc < _refreshInterval)
            {
                return _resolvedUsers;
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _rawEntries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                result.Add(entry);

                try
                {
                    using var ctx = new PrincipalContext(ContextType.Machine);
                    using var group = GroupPrincipal.FindByIdentity(ctx, entry);
                    if (group is null) continue; // ユーザー名またはドメイングループの可能性。無視して継続。

                    foreach (var member in group.GetMembers(recursive: true))
                    {
                        if (!string.IsNullOrEmpty(member.SamAccountName))
                        {
                            result.Add(member.SamAccountName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "MonitoredUsers のグループ展開に失敗しました ({Entry})。このエントリは無視され、フィルタはフェイルオープンで動作します",
                        entry);
                }
            }

            _resolvedUsers = result;
            _resolvedAtUtc = now;
            return result;
        }
    }
}
