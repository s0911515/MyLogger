// System.IO.FileSystemWatcher の生の挙動を事実確認するための最小プローブ。
// MyLogger 本体のフィルタ・重複排除・移動統合ロジックは一切介さず、
// FileSystemWatcher が実際に発火したイベントをそのまま記録する。
//
// 使い方: dotnet run --project probe-tools\ToolA-FsWatcherProbe -- <監視パス> [ログファイル]
//   監視パス省略時は D:\FsWatcherProbe\testarea (無ければ自動作成)。

Console.OutputEncoding = System.Text.Encoding.UTF8; // コンソールの既定コードページによる日本語文字化けを防ぐ

var watchPath = args.Length > 0 ? args[0] : @"D:\FsWatcherProbe\testarea";
var logPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "fswatcherprobe.log");

Directory.CreateDirectory(watchPath);

var logWriter = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
{
    AutoFlush = true,
};

void Log(string line)
{
    var stamped = $"[{DateTime.Now:HH:mm:ss.ffffff}] {line}";
    Console.WriteLine(stamped);
    logWriter.WriteLine(stamped);
}

Log($"=== FsWatcherProbe 開始 監視パス={watchPath} ログ={logPath} ===");

using var watcher = new FileSystemWatcher(watchPath)
{
    IncludeSubdirectories = true,
    NotifyFilter = NotifyFilters.FileName
                 | NotifyFilters.DirectoryName
                 | NotifyFilters.Attributes
                 | NotifyFilters.Size
                 | NotifyFilters.LastWrite
                 | NotifyFilters.LastAccess
                 | NotifyFilters.CreationTime
                 | NotifyFilters.Security,
    InternalBufferSize = 8192, // 既定値。バッファオーバーフローの再現用にそのまま既定を使う
};

watcher.Created += (_, e) => Log($"Created   ChangeType={e.ChangeType} FullPath={e.FullPath} IsDir={SafeIsDirectory(e.FullPath)}");
watcher.Changed += (_, e) => Log($"Changed   ChangeType={e.ChangeType} FullPath={e.FullPath} IsDir={SafeIsDirectory(e.FullPath)}");
watcher.Deleted += (_, e) => Log($"Deleted   ChangeType={e.ChangeType} FullPath={e.FullPath}");
watcher.Renamed += (_, e) => Log($"Renamed   ChangeType={e.ChangeType} OldFullPath={e.OldFullPath} FullPath={e.FullPath} IsDir={SafeIsDirectory(e.FullPath)}");
watcher.Error += (_, e) =>
{
    var ex = e.GetException();
    Log($"Error     {ex?.GetType().Name}: {ex?.Message}");
};

watcher.EnableRaisingEvents = true;

using var exitSignal = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitSignal.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => exitSignal.Set();

Log("監視開始。プロセス終了(Ctrl+C / kill)まで待機します。");
exitSignal.Wait();

watcher.EnableRaisingEvents = false;
Log("=== FsWatcherProbe 終了 ===");
logWriter.Dispose();

static string SafeIsDirectory(string path)
{
    try
    {
        return Directory.Exists(path) ? "true" : (File.Exists(path) ? "false" : "unknown(既に消滅)");
    }
    catch
    {
        return "unknown(例外)";
    }
}
