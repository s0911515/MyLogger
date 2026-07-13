// Microsoft-Windows-SMBClient ETWプロバイダー (GUID {988c59c5-0a1c-45b6-a555-0c62276e327d}、
// `logman query providers` で実機確認済み) を直接購読する、アウトバウンドSMB
// (このPCから他マシンの共有への操作) の実装方式②(ETW直接購読方式)。
// 実装方式①(イベントログ購読方式)は ToolF-SmbClientEventLogProbe を参照。どちらが有用かを比較する
// ため、あえて別ツールとして分けている。ToolB(ローカルFileIO)と同じ TraceEvent ライブラリを使うが、
// こちらはカーネルプロバイダーではなく通常のマニフェストベースプロバイダーを購読する点が異なる。
//
// 【重要・未検証】このプロバイダーが実際にファイル単位の操作(open/read/write等)まで出すのか、
// 接続/セッションレベルの情報にとどまるのかは、このマシンでは外向きの実SMBファイルアクセスが
// 発生していないため未検証。実機で他マシンの共有にアクセスして確認が必要。
// イベントIDは事前に絞り込まず、このプロバイダーが出す全イベント・全フィールドをそのまま記録する。
//
// 使い方 (管理者権限の PowerShell で):
//   dotnet run --project probe-tools\ToolG-SmbClientEtwProbe -- [ログファイル]

using Microsoft.Diagnostics.Tracing.Session;

Console.OutputEncoding = System.Text.Encoding.UTF8; // コンソールの既定コードページによる日本語文字化けを防ぐ

if (!(TraceEventSession.IsElevated() ?? false))
{
    Console.WriteLine("管理者権限で実行してください (ETW プロバイダーの有効化に必要)。");
    return 1;
}

var logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "smbclientetwprobe.log");
const string ProviderName = "Microsoft-Windows-SMBClient";

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

Log($"=== SmbClientEtwProbe(ツールG: アウトバウンドSMB ETW直接購読方式) 開始 ログ={logPath} プロバイダー={ProviderName} ===");

var eventCount = 0L;

const string SessionName = "SmbClientEtwProbe";
using var session = new TraceEventSession(SessionName);
session.EnableProvider(ProviderName);

session.Source.Dynamic.All += data =>
{
    Interlocked.Increment(ref eventCount);
    var fields = string.Join(" ", data.PayloadNames.Select(name => $"{name}={FormatPayload(data.PayloadByName(name))}"));
    Log($"Event={data.EventName} ID={(int)data.ID} PID={data.ProcessID} TID={data.ThreadID} " +
        $"Opcode={data.OpcodeName} {fields} EtwTime={data.TimeStamp:HH:mm:ss.ffffff}");
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

session.Stop();
Log($"=== SmbClientEtwProbe 終了 (出力イベント数={eventCount}, EventsLost={session.EventsLost}) ===");
logWriter.Dispose();
return 0;

static string FormatPayload(object? value) => value switch
{
    null => "null",
    byte[] bytes => Convert.ToHexString(bytes),
    _ => value.ToString() ?? "null",
};
