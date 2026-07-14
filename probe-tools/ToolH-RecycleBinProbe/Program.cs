// ゴミ箱($RECYCLE.BIN)への格納をリアルタイムに監視し、削除された元のファイルパスを解決する
// 専用プローブ。ToolA(FsWatcherProbe)の調査で「$RECYCLE.BINへのCreatedはFileSystemWatcherで
// 見えるが、$R形式にリネームされた実体ファイル名からは元のパスが分からない」ことが判明したため
// 作成した。ToolB(ETW)は$RECYCLE.BINを大量ノイズのため既定除外しているので、その代替でもある。
//
// 仕組み: $RECYCLE.BIN配下では、1ファイル削除につき $R<6文字任意> と $I<6文字、Rと同じ任意部分>
// という同じサフィックスを持つペアが作られる。$Rxxxxxx が実体(削除されたファイルの中身そのもの、
// 拡張子も維持される)、$Ixxxxxx がメタデータ(元のパス・元のサイズ・削除日時をバイナリ形式で保持、
// Microsoft非公式・リバースエンジニアリングにより広く知られている形式)。元のパスは$Ixxxxxxを
// 読まないと分からないため、$R*のCreatedを検知したら対応する$I*を読み、元のパスを解決する。
//
// ゴミ箱は空にされる可能性があるため、後からまとめて解析するのではなく検知したその場で解決する。
// ただしFileSystemWatcherのイベントハンドラ内で同期的にファイルI/Oを行うと、ハンドラが戻るまで
// OS側バッファ(ReadDirectoryChangesW)が再発行されずイベント取りこぼしのリスクが増える
// (MyLogger本体で過去に実機確認済みの問題)。そのため検知はキューに積むだけにし、別スレッドの
// ループで$Iファイルの読み取り・解決を行う(ETWのWMI解決で実績のある切り離しパターンと同じ)。
//
// 目的が「ゴミ箱の$Iファイルから何が分かるか」を正確に知ることでもあるため、判明しているフィールド
// (バージョン・元のファイルサイズ・削除日時・元のパス)に加えて、$Iファイルの生バイト列も16進
// ダンプでそのまま記録する。$RECYCLE.BIN配下の生イベント(Created/Changed/Deleted/Renamed)も
// 一切フィルタせずそのまま記録する(このツールの目的そのものがゴミ箱の中身を知ることのため)。
//
// 使い方 (PowerShellで。管理者権限は不要 - 自分がゴミ箱に捨てたファイルは自分のSIDフォルダ配下で
// 読み書きできるため):
//   dotnet run --project probe-tools\ToolH-RecycleBinProbe -- [ログファイル]

using System.Collections.Concurrent;
using System.Text;

Console.OutputEncoding = System.Text.Encoding.UTF8; // コンソールの既定コードページによる日本語文字化けを防ぐ

var logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "recyclebinprobe.log");
var drivesPath = Path.Combine(AppContext.BaseDirectory, "watch-drives.txt");
var drives = LoadWatchDrives(drivesPath);

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

Log($"=== RecycleBinProbe(ツールH: ゴミ箱監視・元パス解決) 開始 ログ={logPath} " +
    $"監視ドライブ=[{string.Join(", ", drives)}] ({drivesPath}) ===");

var eventCount = 0L;
var resolveQueue = new BlockingCollection<(string RPath, DateTime DetectedAt)>();
var watchers = new List<FileSystemWatcher>();

foreach (var drive in drives)
{
    var recycleBinPath = Path.Combine($"{drive}\\", "$RECYCLE.BIN");
    if (!Directory.Exists(recycleBinPath))
    {
        Log($"[スキップ] {recycleBinPath} が存在しません");
        continue;
    }

    FileSystemWatcher watcher;
    try
    {
        watcher = new FileSystemWatcher(recycleBinPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
    }
    catch (Exception ex)
    {
        Log($"[購読失敗] {recycleBinPath}: {ex.Message}");
        continue;
    }

    watcher.Created += (_, e) =>
    {
        Interlocked.Increment(ref eventCount);
        Log($"Created   ChangeType={e.ChangeType} FullPath={e.FullPath} IsDir={SafeIsDirectory(e.FullPath)}");
        var fileName = Path.GetFileName(e.FullPath);
        if (fileName.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
        {
            resolveQueue.Add((e.FullPath, DateTime.Now));
        }
    };
    watcher.Changed += (_, e) =>
    {
        Interlocked.Increment(ref eventCount);
        Log($"Changed   ChangeType={e.ChangeType} FullPath={e.FullPath} IsDir={SafeIsDirectory(e.FullPath)}");
    };
    watcher.Deleted += (_, e) =>
    {
        Interlocked.Increment(ref eventCount);
        Log($"Deleted   ChangeType={e.ChangeType} FullPath={e.FullPath}");
    };
    watcher.Renamed += (_, e) =>
    {
        Interlocked.Increment(ref eventCount);
        Log($"Renamed   ChangeType={e.ChangeType} OldFullPath={e.OldFullPath} FullPath={e.FullPath} IsDir={SafeIsDirectory(e.FullPath)}");
    };
    watcher.Error += (_, e) =>
    {
        var ex = e.GetException();
        Log($"Error     {ex?.GetType().Name}: {ex?.Message}");
    };

    watcher.EnableRaisingEvents = true;
    watchers.Add(watcher);
    Log($"監視開始: {recycleBinPath}");
}

var resolveTask = Task.Run(ResolveLoop);

using var exitSignal = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitSignal.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => exitSignal.Set();

Log("監視開始。プロセス終了(Ctrl+C / kill)まで待機します。");
exitSignal.Wait();

foreach (var watcher in watchers) watcher.EnableRaisingEvents = false;
resolveQueue.CompleteAdding();
resolveTask.Wait(TimeSpan.FromSeconds(5));
Log($"=== RecycleBinProbe 終了 (出力イベント数={eventCount}) ===");
logWriter.Dispose();
return 0;

void ResolveLoop()
{
    foreach (var (rPath, detectedAt) in resolveQueue.GetConsumingEnumerable())
    {
        ResolveOne(rPath, detectedAt);
    }
}

void ResolveOne(string rPath, DateTime detectedAt)
{
    var dir = Path.GetDirectoryName(rPath) ?? "";
    var rName = Path.GetFileName(rPath);
    var iName = "$I" + rName[2..]; // "$Rxxxxxx.ext" -> "$Ixxxxxx.ext" (同じ任意部分を共有)
    var iPath = Path.Combine(dir, iName);

    byte[]? bytes = null;
    Exception? lastError = null;
    for (var attempt = 0; attempt < 10; attempt++)
    {
        try
        {
            if (File.Exists(iPath))
            {
                bytes = File.ReadAllBytes(iPath);
                break;
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }
        Thread.Sleep(100);
    }

    if (bytes is null)
    {
        Log($"Resolved  RPath={rPath} IPath={iPath} DetectedAt={detectedAt:HH:mm:ss.ffffff} " +
            $"解決失敗(10回リトライしても$Iファイルが読めませんでした: {lastError?.Message ?? "見つかりません"})");
        return;
    }

    var hex = Convert.ToHexString(bytes);
    try
    {
        var (version, originalSize, deletionTime, originalPath) = DecodeIFile(bytes);
        Log($"Resolved  RPath={rPath} IPath={iPath} DetectedAt={detectedAt:HH:mm:ss.ffffff} " +
            $"Version={version} OriginalSize={originalSize} DeletionTime={deletionTime:yyyy-MM-dd HH:mm:ss.fff} " +
            $"OriginalPath={originalPath} IFileSize={bytes.Length} IFileHex={hex}");
    }
    catch (Exception ex)
    {
        Log($"Resolved  RPath={rPath} IPath={iPath} DetectedAt={detectedAt:HH:mm:ss.ffffff} " +
            $"デコード失敗: {ex.Message} IFileSize={bytes.Length} IFileHex={hex}");
    }
}

/// <summary>
/// $I ファイル(ゴミ箱のメタデータファイル)をデコードする。Microsoftの公式仕様は非公開だが、
/// 広く知られているリバースエンジニアリング結果に基づく。
/// バージョン1 (Windows Vista~8.1): 8(version)+8(元サイズ)+8(削除日時FILETIME)+520(元パス、
///   固定長260文字UTF-16、null終端)
/// バージョン2 (Windows 10以降): 8(version)+8(元サイズ)+8(削除日時FILETIME)+4(パス長・文字数)
///   +可変長(元パス、UTF-16)
/// </summary>
static (long Version, long OriginalSize, DateTime DeletionTime, string OriginalPath) DecodeIFile(byte[] bytes)
{
    var version = BitConverter.ToInt64(bytes, 0);
    var originalSize = BitConverter.ToInt64(bytes, 8);
    var deletionFileTime = BitConverter.ToInt64(bytes, 16);
    var deletionTime = DateTime.FromFileTime(deletionFileTime);

    string originalPath;
    if (version == 1)
    {
        var pathBytes = bytes.AsSpan(24, Math.Min(520, Math.Max(0, bytes.Length - 24)));
        var raw = Encoding.Unicode.GetString(pathBytes);
        var nullIndex = raw.IndexOf('\0');
        originalPath = nullIndex >= 0 ? raw[..nullIndex] : raw;
    }
    else
    {
        // pathLengthChars は末尾のnull終端文字を含む文字数のため、そのままデコードすると
        // 末尾に不可視のnull文字が残ってログ表示が崩れる(実機で確認済み)。null終端で切り詰める。
        var pathLengthChars = BitConverter.ToInt32(bytes, 24);
        var pathByteLength = Math.Min(pathLengthChars * 2, Math.Max(0, bytes.Length - 28));
        var pathBytes = bytes.AsSpan(28, Math.Max(0, pathByteLength));
        var raw = Encoding.Unicode.GetString(pathBytes);
        var nullIndex = raw.IndexOf('\0');
        originalPath = nullIndex >= 0 ? raw[..nullIndex] : raw;
    }

    return (version, originalSize, deletionTime, originalPath);
}

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

/// <summary>監視対象ドライブの一覧を設定ファイルから読み込む。ファイルが無ければ既定値で新規作成する。</summary>
static List<string> LoadWatchDrives(string path)
{
    if (!File.Exists(path))
    {
        File.WriteAllLines(path, new[]
        {
            "# 1行1ドライブレター (コロン付き、例: D:)。# で始まる行はコメント。",
            "# ここに列挙したドライブの $RECYCLE.BIN を監視する。",
            "D:",
        });
    }

    var result = new List<string>();
    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
        result.Add(trimmed.TrimEnd('\\'));
    }
    return result;
}
