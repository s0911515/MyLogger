// Windows セキュリティ監査ログの「プロセス作成」イベント (イベントID 4688) を記録する最小プローブ。
// これは「テーブルB」(プロセス生成: PID→ユーザー名の対応)専用のツール。
// ファイルイベント(テーブルA相当)は別ツール ToolB-EtwFileProbe が独立して記録する。
// このツールの中では両者を突き合わせない(突合はテーブルA・Bを見比べて後日別途行う)。
//
// 仕組み: サードパーティ製ツール(Sysmon等)は不要で、Windows標準のセキュリティ監査ログのみを使う。
// 「プロセス作成」監査サブカテゴリを有効化すると、プロセスが生成されるたびにイベント4688が
// Security ログに記録される(NewProcessId・NewProcessName・SubjectUserName等を含む)。
// MyLogger本体の SmbAuditMonitor/LogonMonitor が別のイベントID(5140番台・4624番台)を
// 同じ System.Diagnostics.Eventing.Reader.EventLogWatcher で購読しているのと同じ仕組み。
//
// 使い方 (管理者権限の PowerShell で):
//   dotnet run --project probe-tools\ToolC-ProcessAuditProbe -- [ログファイル]

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

var logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "processauditprobe.log");

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

EnsureProcessCreationAuditEnabled();

Log($"=== ProcessAuditProbe(ツールC: プロセス作成監査 イベント4688) 開始 ログ={logPath} ===");

var eventCount = 0L;

var securityQuery = new EventLogQuery("Security", PathType.LogName, "*[System[(EventID=4688)]]");
using var watcher = new EventLogWatcher(securityQuery);
watcher.EventRecordWritten += (_, e) =>
{
    if (e.EventRecord is null) return;
    using var record = e.EventRecord;
    try
    {
        var fields = ParseEventData(record.ToXml());
        fields.TryGetValue("NewProcessId", out var pidHex);
        fields.TryGetValue("NewProcessName", out var newProcessName);
        fields.TryGetValue("SubjectUserName", out var userName);
        fields.TryGetValue("SubjectDomainName", out var domain);
        fields.TryGetValue("ProcessId", out var creatorPidHex); // 生成元(親)プロセスのPID
        fields.TryGetValue("CommandLine", out var commandLine); // 既定では取得されないことが多い (要GPO)

        var user = string.IsNullOrEmpty(domain) ? userName : $@"{domain}\{userName}";
        Interlocked.Increment(ref eventCount);
        Log($"ProcessCreate NewPid={pidHex} User={user} NewProcessName={newProcessName} " +
            $"CreatorPid={creatorPidHex} CommandLine={commandLine} EtwTime={record.TimeCreated:HH:mm:ss.ffffff}");
    }
    catch (Exception ex)
    {
        Log($"[4688パース失敗] {ex.Message}");
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
Log($"=== ProcessAuditProbe 終了 (出力イベント数={eventCount}) ===");
logWriter.Dispose();
return 0;

/// <summary>プロセス作成の監査サブカテゴリを冪等に有効化する (MyLogger本体のAuditPolicyConfiguratorと同じ方式)。</summary>
static void EnsureProcessCreationAuditEnabled()
{
    var psi = new ProcessStartInfo("auditpol.exe",
        "/set /subcategory:\"{0CCE922B-69AE-11D9-BED3-505054503030}\" /success:enable")
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
