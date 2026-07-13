// Windows セキュリティ監査ログの「ファイル共有」監査イベント (5140/5142-5145) を記録する最小プローブ。
// これは「インバウンドSMB」(このPCの共有フォルダに対する、ネットワーク越しの外部アクセス)専用の
// ツールで、MyLogger本体の Monitors/SmbAuditMonitor.cs と同じ監査ログ購読方式を、他マシンでの検証
// 用に独立ツールとして切り出したもの。ローカルファイルイベント(ToolB)やプロセス作成(ToolC)、
// アウトバウンドSMB(ToolF/G)とは独立して記録し、突き合わせは行わない。
//
// 事前準備: このマシンに共有フォルダが無いとイベントは一切発生しない。例:
//   New-SmbShare -Name TestShare -Path D:\tmp\SmbTestShare -FullAccess Everyone
// を実行してから、別マシン(または同一マシンの別ユーザーセッション)から
// \\<このマシン名>\TestShare にアクセスして検証する。
//
// 使い方 (管理者権限の PowerShell で):
//   dotnet run --project probe-tools\ToolE-SmbServerAuditProbe -- [ログファイル]
//
// かける絞り込みは以下の1点のみ: IPC$ (管理共有、実ファイルアクセスではない) へのアクセスは
// 出力しない。それ以外は該当イベントのフィールドをすべてそのまま記録する。

using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using System.Xml.Linq;

Console.OutputEncoding = System.Text.Encoding.UTF8; // コンソールの既定コードページによる日本語文字化けを防ぐ

if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.WriteLine("管理者権限で実行してください (監査ポリシーの設定・セキュリティログの購読に必要)。");
    return 1;
}

var logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "smbserverauditprobe.log");

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

EnsureFileShareAuditEnabled();

Log($"=== SmbServerAuditProbe(ツールE: インバウンドSMB 共有アクセス監査 イベント5140/5142-5145) 開始 ログ={logPath} ===");

var eventCount = 0L;

var securityQuery = new EventLogQuery("Security", PathType.LogName,
    "*[System[(EventID=5140 or EventID=5142 or EventID=5143 or EventID=5144 or EventID=5145)]]");
using var watcher = new EventLogWatcher(securityQuery);
watcher.EventRecordWritten += (_, e) =>
{
    if (e.EventRecord is null) return;
    using var record = e.EventRecord;
    try
    {
        var fields = ParseEventData(record.ToXml());

        fields.TryGetValue("ShareName", out var shareName);
        if (shareName is not null && shareName.EndsWith(@"\IPC$", StringComparison.OrdinalIgnoreCase)) return;

        var action = record.Id switch
        {
            5140 => "ShareConnected",
            5142 => "ShareCreated",
            5143 => "ShareModified",
            5144 => "ShareDeleted",
            5145 => "ShareFileAccess",
            _ => $"Event{record.Id}",
        };

        var allFields = string.Join(" ", fields.Select(kv => $"{kv.Key}={kv.Value}"));
        Interlocked.Increment(ref eventCount);
        Log($"{action}(Event{record.Id}) {allFields} EtwTime={record.TimeCreated:HH:mm:ss.ffffff}");
    }
    catch (Exception ex)
    {
        Log($"[5140系パース失敗] {ex.Message}");
    }
};
watcher.Enabled = true;

using var exitSignal = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitSignal.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => exitSignal.Set();

Log("監視開始。プロセス終了(Ctrl+C / kill)まで待機します。");
exitSignal.Wait();

watcher.Enabled = false;
Log($"=== SmbServerAuditProbe 終了 (出力イベント数={eventCount}) ===");
logWriter.Dispose();
return 0;

/// <summary>「ファイル共有」「詳細なファイル共有」監査サブカテゴリを冪等に有効化する
/// (MyLogger本体の SmbAuditMonitor が前提とするのと同じ2つのサブカテゴリ)。</summary>
static void EnsureFileShareAuditEnabled()
{
    RunAuditPol("{0CCE9224-69AE-11D9-BED3-505054503030}"); // ファイル共有
    RunAuditPol("{0CCE9244-69AE-11D9-BED3-505054503030}"); // 詳細なファイル共有
}

static void RunAuditPol(string subcategoryGuid)
{
    var psi = new ProcessStartInfo("auditpol.exe",
        $"/set /subcategory:\"{subcategoryGuid}\" /success:enable /failure:enable")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    using var process = Process.Start(psi);
    if (process is null) return;
    process.StandardOutput.ReadToEnd();
    process.StandardError.ReadToEnd();
    process.WaitForExit(10_000);
}

/// <summary>セキュリティイベントXMLの &lt;Data Name="..."&gt;値&lt;/Data&gt; を name→value の表にする。</summary>
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
