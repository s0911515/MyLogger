// ETW (カーネル FileIO イベント) で実際に取得できる情報を漏れなく確認するための最小プローブ。
// MyLogger 本体のパス分類・テーブル分離等は一切介さない。目的が「ETWで何が取れるか正確に知ること」
// であるため、各イベント型が持つフィールドはできる限り全て記録する(3.のノイズ除外を除き加工しない)。
//
// これは「テーブルA」(ファイルイベント)専用のツール。プロセス作成イベント(テーブルB相当)は
// 別ツール ToolC-ProcessAuditProbe が独立して記録する。このツールの中では両者を突き合わせない
// (突合はテーブルA・Bを見比べて後日別途行う)。
// FileSystemWatcher 側の同種のプローブは ToolA-FsWatcherProbe を参照。
//
// 購読しているイベント種別は Create/Write/Read/Flush/Rename/Delete/FileDelete/SetInfo/Cleanup/Close
// の10種。うち後半4種(FileDelete/SetInfo/Cleanup/Close)は、完全削除(Shift+Delete)が
// FileIODelete(FileDispositionInformationのSetInfo, InfoClass=13)に現れないケースを実機で確認した
// ことを受けて追加した(リフレクションで KernelTraceEventParser の全イベント一覧を洗い出し、
// 実際に何が取れるか確認するため追加購読している)。
//
// 使い方 (管理者権限の PowerShell で):
//   dotnet run --project probe-tools\ToolB-EtwFileProbe -- [ログファイル]
//
// かける絞り込みは以下の3点のみ:
//   1. 自プロセス (このツール自身) によるイベントは出力しない (自己増殖ループ防止。実機で確認済み)
//   2. watch-paths.txt (実行ファイルと同じフォルダ、無ければ既定値で自動生成) に列挙したパス
//      配下以外のイベントは出力しない (既定はDドライブ全体。評価対象フォルダに絞るとノイズが減る)
//   3. exclude-processes.txt (実行ファイルと同じフォルダ、無ければ既定値で自動生成) に列挙した
//      プロセス名/フォルダ自体のオープン/desktop.ini等/$RECYCLE.BINは出力しない (既知のノイズ源)

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

Console.OutputEncoding = System.Text.Encoding.UTF8; // コンソールの既定コードページによる日本語文字化けを防ぐ

if (!(TraceEventSession.IsElevated() ?? false))
{
    Console.WriteLine("管理者権限で実行してください (ETW カーネルプロバイダーの有効化に必要)。");
    return 1;
}

var logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "etwfileprobe.log");

var myPid = Environment.ProcessId;
var watchPathsPath = Path.Combine(AppContext.BaseDirectory, "watch-paths.txt");
var watchedPaths = LoadWatchedPaths(watchPathsPath);
var excludeProcessesPath = Path.Combine(AppContext.BaseDirectory, "exclude-processes.txt");
var excludedProcesses = LoadExcludedProcesses(excludeProcessesPath);

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

bool ShouldSkip(int processId, string processName, string? path) =>
    processId == myPid
    || excludedProcesses.Contains(processName)
    || !IsOnWatchedPath(path)
    || IsFolderBrowsingNoise(path!);

bool IsOnWatchedPath(string? path)
{
    if (string.IsNullOrEmpty(path)) return false;
    foreach (var watched in watchedPaths)
    {
        if (path.StartsWith(watched.DriveFormPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (watched.DeviceFormPrefix.Length > 0 && path.StartsWith(watched.DeviceFormPrefix, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
}

// 既知のノイズ発生源のみをexplicitに除外する: それ以外の加工はしない。
//   ・エクスプローラーでフォルダを開くと、フォルダ自体へのオープン(実ファイル操作ではない)が発生する
//   ・同時に、フォルダ表示設定を読むための desktop.ini / Thumbs.db へのアクセスも発生する
//   ・ごみ箱(D:\$RECYCLE.BIN)への削除のたびに、ごみ箱内の既存アイテム全件のメタデータ($Ixxxxxxx等)が
//     再走査され大量のノイズが出る (実機で確認済み。MyLogger本体も同じ理由で $RECYCLE.BIN を既定除外している)
var knownNoiseFileNames = new[] { "desktop.ini", "Thumbs.db" };
var knownNoisePathSegments = new[] { @"\$RECYCLE.BIN\", @"\System Volume Information\" };

bool IsFolderBrowsingNoise(string path)
{
    var fileName = Path.GetFileName(path);
    if (knownNoiseFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)) return true;
    if (knownNoisePathSegments.Any(segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase))) return true;
    return Directory.Exists(path);
}

Log($"=== EtwFileProbe(ツールB: ETWファイルイベント) 開始 ログ={logPath} " +
    $"監視対象パス=[{string.Join(", ", watchedPaths.Select(p => p.DriveFormPrefix))}] ({watchPathsPath}) " +
    $"自PID={myPid} 除外プロセス=[{string.Join(", ", excludedProcesses)}] ({excludeProcessesPath}) ===");

var eventCount = 0L;

const string SessionName = "EtwFileProbe";
using var session = new TraceEventSession(SessionName);
session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.FileIO);

var kernel = session.Source.Kernel;
kernel.FileIOCreate += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    // 注意: TraceEvent の CreateOptions enum は表示名が FILE_ATTRIBUTE_* を誤流用しており、
    // 実際の NtCreateFile CreateOptions ビット (FILE_DELETE_ON_CLOSE=0x1000 等) とは意味が異なる。
    // 実機でこの表示名バグが「Shift+Deleteで明示的なDeleteイベントが出ない理由」の解明を妨げたため、
    // 生の16進値も併記し、呼び出し側で正しいCreateOptionsフラグ表に照らせるようにする。
    Log($"Create PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} FileObject={data.FileObject:X} " +
        $"Disposition={data.CreateDisposition} Options={data.CreateOptions}(0x{(int)data.CreateOptions:X}) Attributes={data.FileAttributes} " +
        $"Share={data.ShareAccess} EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};
kernel.FileIOWrite += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    Log($"Write  PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} FileObject={data.FileObject:X} " +
        $"FileKey={data.FileKey:X} Offset={data.Offset} IoSize={data.IoSize} IoFlags={data.IoFlags} " +
        $"EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};
kernel.FileIORead += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    Log($"Read   PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} FileObject={data.FileObject:X} " +
        $"FileKey={data.FileKey:X} Offset={data.Offset} IoSize={data.IoSize} IoFlags={data.IoFlags} " +
        $"EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};
kernel.FileIOFlush += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    Log($"Flush  PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} FileObject={data.FileObject:X} " +
        $"FileKey={data.FileKey:X} EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};
kernel.FileIORename += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    Log($"Rename PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} FileObject={data.FileObject:X} " +
        $"FileKey={data.FileKey:X} InfoClass={data.InfoClass} ExtraInfo={data.ExtraInfo:X} " +
        $"EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};
kernel.FileIODelete += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    Log($"Delete PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} FileObject={data.FileObject:X} " +
        $"FileKey={data.FileKey:X} InfoClass={data.InfoClass} ExtraInfo={data.ExtraInfo:X} " +
        $"EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};
// 完全削除(Shift+Delete)がFileIODelete(FileDispositionInformationのSetInfo)に現れないケースを
// 実機で確認したため、以下4種を追加購読する(元々6種のみだったが、リフレクションで判明した
// KernelTraceEventParser の未購読イベントのうち、実削除の裏付けとして有望なもの)。
kernel.FileIOFileDelete += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    Log($"FileDelete PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} " +
        $"FileKey={data.FileKey:X} EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};
kernel.FileIOSetInfo += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    Log($"SetInfo PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} FileObject={data.FileObject:X} " +
        $"FileKey={data.FileKey:X} InfoClass={data.InfoClass} ExtraInfo={data.ExtraInfo:X} " +
        $"EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};
kernel.FileIOCleanup += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    Log($"Cleanup PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} FileObject={data.FileObject:X} " +
        $"FileKey={data.FileKey:X} EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};
kernel.FileIOClose += data =>
{
    if (ShouldSkip(data.ProcessID, data.ProcessName, data.FileName)) return;
    Interlocked.Increment(ref eventCount);
    Log($"Close  PID={data.ProcessID} TID={data.ThreadID} Process={data.ProcessName} FileObject={data.FileObject:X} " +
        $"FileKey={data.FileKey:X} EtwTime={data.TimeStamp:HH:mm:ss.ffffff} Path={data.FileName}");
};

var processingTask = Task.Run(() => session.Source.Process());

using var exitSignal = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitSignal.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => exitSignal.Set();

Log("監視開始。プロセス終了(Ctrl+C / kill)まで待機します。");
exitSignal.Wait();

// EventsLost は session.Stop() 後に読むと COMException になることを実機で確認したため、
// 停止前に読んでおく。
var eventsLost = session.EventsLost;
session.Stop();
Log($"=== EtwFileProbe 終了 (出力イベント数={eventCount}, EventsLost={eventsLost}) ===");
logWriter.Dispose();
return 0;

/// <summary>"D:" のようなドライブ指定を "\Device\HarddiskVolumeN" 形式の内部パスに解決する。
/// ETW が返す生パスはこの形式で来ることが多いため、ドライブレター表記のパスと突き合わせるために必要。</summary>
static string ResolveDeviceQualifier(string drive)
{
    var sb = new StringBuilder(260);
    var len = QueryDosDeviceW(drive, sb, sb.Capacity);
    return len > 0 ? sb.ToString() : string.Empty;
}

/// <summary>
/// 監視対象パスの一覧を設定ファイルから読み込む。ファイルが無ければ既定値(Dドライブ全体)で新規作成する。
/// 各行はドライブレター表記(例: D:\tmp\EtwVerify)で指定する。評価対象フォルダに絞るとノイズが減る。
/// </summary>
static List<WatchedPath> LoadWatchedPaths(string path)
{
    if (!File.Exists(path))
    {
        File.WriteAllLines(path, new[]
        {
            "# 1行1パス。ドライブ全体を監視するなら D:\\ のようにドライブ直下を指定する。",
            "# 特定フォルダに絞るとノイズが減る (例: D:\\tmp\\EtwVerify)。# で始まる行はコメント。",
            "# ここに列挙したパス配下のファイルI/Oのみをログに出力する。",
            @"D:\",
        });
    }

    var result = new List<WatchedPath>();
    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
        if (trimmed.Length < 2 || trimmed[1] != ':') continue; // "D:..." 形式でなければ無視

        var normalized = trimmed.EndsWith('\\') ? trimmed : trimmed + @"\";
        var drive = normalized[..2]; // 例: "D:"
        var deviceQualifier = ResolveDeviceQualifier(drive);
        var deviceFormPrefix = deviceQualifier.Length > 0 ? deviceQualifier + normalized[2..] : string.Empty;
        result.Add(new WatchedPath(normalized, deviceFormPrefix));
    }
    return result;
}

/// <summary>
/// 除外プロセス名の一覧を設定ファイルから読み込む。ファイルが無ければ既定値
/// (エディタ・このセッション自体等、観測対象ではない常駐プロセス) で新規作成する。
/// </summary>
static HashSet<string> LoadExcludedProcesses(string path)
{
    if (!File.Exists(path))
    {
        File.WriteAllLines(path, new[]
        {
            "# 1行1プロセス名 (拡張子なし)。# で始まる行はコメント。",
            "# ここに列挙したプロセスによるファイル操作はログに出力しない。",
            "claude",
            "Code",
            "System",
        });
    }

    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
        result.Add(trimmed);
    }
    return result;
}

partial class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
}

/// <summary>監視対象パス1件。ドライブレター表記とETW生パス(デバイス表記)の両方の接頭辞を保持する。</summary>
record WatchedPath(string DriveFormPrefix, string DeviceFormPrefix);
