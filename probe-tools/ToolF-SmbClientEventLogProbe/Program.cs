// このPCが「クライアント」として他マシンのSMB共有にアクセスした際のイベントを、
// Microsoft-Windows-SmbClient 系のイベントチャンネル(EventLogWatcher購読)で記録する最小プローブ。
// これは「アウトバウンドSMB」(このPCから他マシンの共有への操作)の実装方式①(イベントログ購読方式)。
// 実装方式②(ETW直接購読方式)は ToolG-SmbClientEtwProbe を参照。どちらが有用かを比較するため、
// あえて別ツールとして分けている。
//
// 【重要・未検証】実機で確認できたのは以下まで:
//   ・対象チャンネルは既定で有効(このマシンでは Operational/Audit/Connectivity/Security の4チャンネルとも
//     enabled=true だった)
//   ・Operational/Security チャンネルには過去のサーバー接続・認証失敗イベントが実際に記録されていた
//     (ServerName・LogonId・SecurityStatus等、ファイル名は含まれないセッション/認証レベルの情報)
//   ・Audit チャンネル(ファイル単位の操作を記録する想定の名前)は、この開発機ではレコード0件だった。
//     実際にファイル単位の操作(open/create等)が記録されるかどうかは、このマシンでは外向きの実SMB
//     ファイルアクセスが発生していないため未検証。他マシンへの実アクセスで確認が必要。
// イベントIDは事前に絞り込まず、対象チャンネルの全イベントをそのまま記録する(方針は他ツールと同じ:
// 何が取れるかを正確に知ることが目的)。
//
// 使い方 (管理者権限の PowerShell で):
//   dotnet run --project probe-tools\ToolF-SmbClientEventLogProbe -- [ログファイル]

using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using System.Xml.Linq;

Console.OutputEncoding = System.Text.Encoding.UTF8; // コンソールの既定コードページによる日本語文字化けを防ぐ

if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.WriteLine("管理者権限で実行してください (Security系チャンネルの購読・チャンネル有効化に必要)。");
    return 1;
}

var logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "smbclienteventlogprobe.log");
var channelsPath = Path.Combine(AppContext.BaseDirectory, "channels.txt");
var channels = LoadChannels(channelsPath);

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

Log($"=== SmbClientEventLogProbe(ツールF: アウトバウンドSMB イベントログ購読方式) 開始 " +
    $"ログ={logPath} 対象チャンネル=[{string.Join(", ", channels)}] ({channelsPath}) ===");

var eventCount = 0L;
var watchers = new List<EventLogWatcher>();

foreach (var channel in channels)
{
    EnsureChannelEnabled(channel);
    try
    {
        var watcher = new EventLogWatcher(new EventLogQuery(channel, PathType.LogName, "*"));
        watcher.EventRecordWritten += (_, e) =>
        {
            if (e.EventRecord is null) return;
            using var record = e.EventRecord;
            try
            {
                var fields = ParseEventData(record.ToXml());
                var allFields = string.Join(" ", fields.Select(kv => $"{kv.Key}={kv.Value}"));
                Interlocked.Increment(ref eventCount);
                Log($"Channel={channel} EventID={record.Id} Level={SafeLevelName(record)} {allFields} " +
                    $"EtwTime={record.TimeCreated:HH:mm:ss.ffffff}");
            }
            catch (Exception ex)
            {
                Log($"[{channel} パース失敗] {ex.Message}");
            }
        };
        watcher.Enabled = true;
        watchers.Add(watcher);
        Log($"購読開始: {channel}");
    }
    catch (Exception ex)
    {
        Log($"[購読失敗: {channel}] {ex.Message} (チャンネルが存在しないか、無効化されている可能性があります)");
    }
}

using var exitSignal = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitSignal.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => exitSignal.Set();

Log("監視開始。プロセス終了(Ctrl+C / kill)まで待機します。");
exitSignal.Wait();

foreach (var watcher in watchers)
{
    watcher.Enabled = false;
    watcher.Dispose();
}
Log($"=== SmbClientEventLogProbe 終了 (出力イベント数={eventCount}) ===");
logWriter.Dispose();
return 0;

/// <summary>対象チャンネルが無効化されている場合は有効化する(既に有効なら何もしない、冪等)。
/// チャンネルが存在しない等の理由で失敗した場合は無視して購読を試みる。</summary>
static void EnsureChannelEnabled(string channelName)
{
    try
    {
        using var config = new EventLogConfiguration(channelName);
        if (config.IsEnabled) return;
        config.IsEnabled = true;
        config.SaveChanges();
    }
    catch
    {
        // 存在しないチャンネル・変更不可な既定チャンネル等は無視する
    }
}

/// <summary>監視対象チャンネル名の一覧を設定ファイルから読み込む。ファイルが無ければ既定値で新規作成する。</summary>
static List<string> LoadChannels(string path)
{
    if (!File.Exists(path))
    {
        File.WriteAllLines(path, new[]
        {
            "# 1行1チャンネル名。# で始まる行はコメント。",
            "# ここに列挙したイベントチャンネルを購読する(存在しない/無効な場合は起動時にエラー表示して継続する)。",
            "Microsoft-Windows-SMBClient/Operational",
            "Microsoft-Windows-SmbClient/Audit",
            "Microsoft-Windows-SmbClient/Connectivity",
            "Microsoft-Windows-SmbClient/Security",
        });
    }

    var result = new List<string>();
    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
        result.Add(trimmed);
    }
    return result;
}

static string SafeLevelName(EventRecord record)
{
    try { return record.LevelDisplayName ?? record.Level?.ToString() ?? "?"; }
    catch { return record.Level?.ToString() ?? "?"; }
}

/// <summary>イベントXMLの &lt;Data Name="..."&gt;値&lt;/Data&gt; を name→value の表にする。</summary>
static Dictionary<string, string> ParseEventData(string eventXml)
{
    var result = new Dictionary<string, string>();
    var doc = XDocument.Parse(eventXml);
    foreach (var data in doc.Descendants().Where(el => el.Name.LocalName == "Data"))
    {
        var name = data.Attribute("Name")?.Value;
        if (!string.IsNullOrEmpty(name)) result[name] = data.Value;
    }
    return result;
}
