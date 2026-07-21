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
//
// 出力は2種類。(1) [ログファイル] は生の Key=Value 形式(従来通り、値に生のタブ・改行を含む
// フィールドがあり複数物理行にまたがることがある)。(2) [ログファイル]と同名でCSV版
// ([ログファイル].csv)を同時に書き出す(元々は別ツールLogFormatterだったが、依頼により
// 本体に統合した)。CSVは列を固定してその場で1行ずつ追記していく(イベント種別によって
// フィールドが違うため、既知の列に無いフィールドは「その他」列にそのまままとめる。生ログと
// 違って情報を取りこぼさないための設計)。
// さらにCSVには、AccessMask・AccessList・AccessReasonの3列について、それぞれ
// 「AccessMask解釈」「AccessList解釈」「AccessReason解釈」という解釈列を追加する。
// これらのフィールドはWindowsが「%%NNNN」というプレースホルダ(メッセージテーブル由来の
// コード)や生の16進ビットマスクのまま記録するため、人が見ても意味が分からない。
// AccessMaskは.NET標準のFileSystemRights列挙型でビット単位に解釈できるため機械的に信頼できるが、
// AccessList/AccessReasonの%%コードは本ツールの中で確認できた範囲のコードだけを対応表に持つ
// (未知のコードはコードのまま出力し、決して黙って捨てない)。

using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

Console.OutputEncoding = System.Text.Encoding.UTF8; // コンソールの既定コードページによる日本語文字化けを防ぐ

if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.WriteLine("管理者権限で実行してください (監査ポリシーの設定・セキュリティログの購読に必要)。");
    return 1;
}

var logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "smbserverauditprobe.log");
var csvPath = DeriveCsvPath(logPath);

var logWriter = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
{
    AutoFlush = true,
};

// Excelでそのまま開いても文字化けしないようUTF-8 BOM付きで書く。列は固定し、既知のフィールドに
// 無いものは「その他」列にまとめる(イベント種別によってフィールド構成が違うため)。
var csvIsNew = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
var csvWriter = new StreamWriter(new FileStream(csvPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(true))
{
    AutoFlush = true,
};
var csvKnownFields = new[]
{
    "SubjectUserSid", "SubjectUserName", "SubjectDomainName", "SubjectLogonId", "ObjectType",
    "IpAddress", "IpPort", "ShareName", "ShareLocalPath", "RelativeTargetName",
    "AccessMask", "AccessList", "AccessReason",
};

// %%NNNN プレースホルダ → アクセス権名。File System監査(ToルI-SaclProbe)のFormatDescription出力
// で実機確認できたもの(%%1537・%%1538・%%1541・%%1542・%%4416・%%4417・%%4423・%%4424)に加え、
// 同じ体系で並ぶ残りのNTFSアクセス権(%%1539・%%1540・%%4418〜4422)を含めている。未確認のコードが
// 出てきた場合はコードのまま出力し、誤った解釈を断定しない。
var AccessRightNames = new Dictionary<string, string>
{
    ["%%1537"] = "DELETE",
    ["%%1538"] = "READ_CONTROL",
    ["%%1539"] = "WRITE_DAC",
    ["%%1540"] = "WRITE_OWNER",
    ["%%1541"] = "SYNCHRONIZE",
    ["%%1542"] = "ACCESS_SYSTEM_SECURITY",
    ["%%4416"] = "ReadData(ListDirectory)",
    ["%%4417"] = "WriteData(AddFile)",
    ["%%4418"] = "AppendData(AddSubdirectory)",
    ["%%4419"] = "ReadEA",
    ["%%4420"] = "WriteEA",
    ["%%4421"] = "Execute(Traverse)",
    ["%%4422"] = "DeleteChild",
    ["%%4423"] = "ReadAttributes",
    ["%%4424"] = "WriteAttributes",
};

// AccessReason中の「なぜ許可/拒否されたか」を示すコード。実機(ToルI-SaclProbe)で確認できたのは
// %%1801(ACEによる許可)と%%1804(所有権による許可)の2つのみ。%%1802(拒否)は未確認だが、
// 対になる概念として存在が知られているため含めている。それ以外の未知コードはコードのまま出力する。
var AccessReasonNames = new Dictionary<string, string>
{
    ["%%1801"] = "許可",
    ["%%1802"] = "拒否",
    ["%%1804"] = "所有権による許可",
};

if (csvIsNew)
{
    var header = new List<string> { "時刻", "操作", "EventID" };
    foreach (var name in csvKnownFields)
    {
        header.Add(name);
        if (name is "AccessMask" or "AccessList" or "AccessReason") header.Add(name + "解釈");
    }
    header.Add("その他");
    WriteCsvLine(csvWriter, header);
}

void Log(string line)
{
    var stamped = $"[{DateTime.Now:HH:mm:ss.ffffff}] {line}";
    Console.WriteLine(stamped);
    logWriter.WriteLine(stamped);
}

EnsureFileShareAuditEnabled();

Log($"=== SmbServerAuditProbe(ツールE: インバウンドSMB 共有アクセス監査 イベント5140/5142-5145) 開始 ログ={logPath} CSV={csvPath} ===");

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

        WriteCsvRow(csvWriter, csvKnownFields, DateTime.Now, action, record.Id, fields);
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
csvWriter.Dispose();
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

static string DeriveCsvPath(string logPath)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(logPath)) ?? ".";
    var baseName = Path.GetFileNameWithoutExtension(logPath);
    return Path.Combine(dir, baseName + ".csv");
}

/// <summary>1イベント分のCSV行を組み立てて書き込む。列は固定(csvKnownFields)。イベント種別に
/// よってフィールド構成が異なるため、既知の列に無いフィールドは末尾の「その他」列にまとめ、
/// 情報を取りこぼさない。</summary>
void WriteCsvRow(TextWriter writer, string[] knownFields, DateTime now, string action, int eventId, Dictionary<string, string> fields)
{
    var cells = new List<string> { now.ToString("HH:mm:ss.ffffff"), action, eventId.ToString() };
    var consumed = new HashSet<string>();
    foreach (var name in knownFields)
    {
        fields.TryGetValue(name, out var value);
        cells.Add(Sanitize(value ?? ""));
        consumed.Add(name);

        if (name == "AccessMask") cells.Add(InterpretAccessMask(value));
        else if (name == "AccessList") cells.Add(InterpretAccessCodeList(value));
        else if (name == "AccessReason") cells.Add(InterpretAccessReason(value));
    }
    var extra = fields.Where(kv => !consumed.Contains(kv.Key))
        .Select(kv => $"{kv.Key}={Sanitize(kv.Value)}");
    cells.Add(string.Join(" ", extra));
    WriteCsvLine(writer, cells);
}

static void WriteCsvLine(TextWriter writer, IEnumerable<string> cells)
{
    writer.WriteLine(string.Join(",", cells.Select(CsvEscapeAlways)));
}

/// <summary>全フィールドを常にダブルクォートで囲む(ToルI-SaclProbe側のLogFormatterと同じ方針)。</summary>
static string CsvEscapeAlways(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

/// <summary>値中の生の改行・タブを読みやすい形に置換する。</summary>
static string Sanitize(string s) => s.Replace("\r\n", " / ").Replace("\n", " / ").Replace("\t", " ");

/// <summary>16進のAccessMaskをFileSystemRightsのビット演算で解釈する(機械的に信頼できる。
/// ToルI-SaclProbeのLogFormatterと同じ方式)。</summary>
static string InterpretAccessMask(string? accessMaskHex)
{
    if (string.IsNullOrEmpty(accessMaskHex)) return "";
    var trimmed = accessMaskHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? accessMaskHex[2..] : accessMaskHex;
    if (!long.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out var mask)) return "";
    return ((FileSystemRights)mask).ToString();
}

/// <summary>AccessList中の%%NNNNコードを1つずつ対応表で解釈する。未知のコードはそのまま残す。</summary>
string InterpretAccessCodeList(string? accessList)
{
    if (string.IsNullOrEmpty(accessList)) return "";
    var codes = Regex.Matches(accessList, @"%%\d+").Select(m => m.Value);
    return string.Join(", ", codes.Select(code => AccessRightNames.TryGetValue(code, out var name) ? $"{name}({code})" : code));
}

/// <summary>AccessReasonは"%%右コード: %%理由コード 詳細"の繰り返し形式。右コード・理由コードを
/// それぞれ対応表で解釈し、詳細(SDDLのACEまたは特権名)はそのまま残す。</summary>
string InterpretAccessReason(string? accessReason)
{
    if (string.IsNullOrEmpty(accessReason)) return "";
    var entries = Regex.Matches(accessReason, @"(%%\d+):\s*(%%\d+)?\s*([^%]*)");
    var parts = new List<string>();
    foreach (Match m in entries)
    {
        var rightCode = m.Groups[1].Value;
        var reasonCode = m.Groups[2].Success ? m.Groups[2].Value : "";
        var detail = m.Groups[3].Value.Trim();
        var rightName = AccessRightNames.TryGetValue(rightCode, out var rn) ? rn : rightCode;
        var reasonName = !string.IsNullOrEmpty(reasonCode) ? (AccessReasonNames.TryGetValue(reasonCode, out var rsn) ? rsn : reasonCode) : "";
        parts.Add(string.IsNullOrEmpty(reasonName) ? $"{rightName}: {detail}".Trim() : $"{rightName}: {reasonName} {detail}".Trim());
    }
    return string.Join(" / ", parts);
}
