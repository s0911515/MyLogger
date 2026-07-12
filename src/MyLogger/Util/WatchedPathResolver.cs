using MyLogger.Config;

namespace MyLogger.Util;

/// <summary>
/// <see cref="FileWatcherOptions.Paths"/> を実際に監視するパスの一覧に解決する。
/// 空の場合は全ての固定ドライブのルートを対象とする。
/// FileWatcherMonitor (監視の実行) と EtwFileIoMonitor (ローカルファイルの
/// プロセス相関のスコープ判定) の両方から同じ解決結果を参照するために共通化している。
/// </summary>
public static class WatchedPathResolver
{
    public static List<string> Resolve(FileWatcherOptions options)
    {
        return options.Paths.Count > 0
            ? options.Paths.Select(Environment.ExpandEnvironmentVariables).ToList()
            : DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
    }
}
