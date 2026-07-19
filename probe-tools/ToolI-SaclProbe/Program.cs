// 指定フォルダにSACL(システムアクセス制御リスト、監査ACE)を設定し、その結果Windowsセキュリティ
// 監査ログに記録される内容を、可能な限り隅々まで(生XML・公式フォーマット済みメッセージ・
// 個別フィールドの3通り)整形して出力する専用プローブ。
//
// 仕組み: NTFSオブジェクト(ファイル・フォルダ)にSACLを設定し、「オブジェクトアクセス > ファイル
// システム」監査サブカテゴリを有効化すると、SACLで指定した種類のアクセスが行われるたびに
// Securityログにイベントが記録される。本ツールは対象フォルダに Everyone に対する、
// ReadData・書き込み系・削除系・権限変更系(下記 AuditedRights 定数、成功・失敗両方)の
// 監査ACEを設定し、以下のイベントIDを購読する。単なるハンドル同期フラグ(Synchronize)や
// 属性/権限の読み取り系(ReadAttributes等)はエクスプローラー等の閲覧だけでノイズになるため
// 除外している(詳細はAuditedRights定数のコメントとREADME参照)。
//   4656 ハンドル要求 (ObjectName・AccessMask・AccessList等を含む、最も情報量が多い)
//   4663 オブジェクトへのアクセス試行 (実際に行われたアクセス)
//   4658 ハンドルクローズ (ObjectNameを含まない。HandleIdでのみ4656と対応付け可能)
//   4660 オブジェクト削除 (ObjectNameを含まない。HandleIdでのみ4656と対応付け可能)
//   4670 オブジェクトの権限変更
//   4907 オブジェクトの監査設定(SACL)変更 (本ツール自身がSACLを設定した操作もここに記録される)
//
// 4658/4660はイベント自体にObjectNameを含まないため、4656/4663/4670/4907で観測した
// HandleId→ObjectNameの対応を記憶しておき、対象フォルダ配下だったハンドルのHandleIdが
// 4658/4660で再度現れたときに「対象フォルダの操作である」と判定する(ResolvedObjectNameとして
// ログに付記する)。この対応付けをしないと4658/4660は常に除外されてしまい、削除イベントを
// 取りこぼす。
//
// 出力は2種類、どちらも対象フォルダ一致イベントを1件も漏らさず記録する(隅々まで記録するという
// 本ツールの目的そのもの。閲覧のみのアクセスも除外しない)。(1) [ログファイル] は生XML・
// 公式メッセージ込みの完全な形。(2) [ログファイル].summary.csv は、生XML等を省いた人が読みやすい
// 列形式(Excelでの二次加工・フィルタ・ピボット向け)。CSVには「分類」列があり、そのハンドルの
// 生涯を通じて一度でも書き込み・削除・権限変更系のアクセス(SignalRights、AuditedRightsから
// ReadDataを除いたもの)があれば「シグナル」、無ければ「閲覧のみ」と判定する。除外はしない。
//
// SACLはEveryone・FullControlという広い範囲で設定するため、放置すると対象フォルダへの
// あらゆるアクセスで際限なくセキュリティログが増え続ける。そのため終了時(Ctrl+C)に
// 元のSACL(取得できた場合)へ必ず復元する。監査サブカテゴリ自体の有効化はシステム全体設定で
// 低コストなため、ToolC-ProcessAuditProbeと同様に有効化したまま残す(復元しない)。
//
// 使い方 (管理者権限の PowerShell で):
//   dotnet run --project probe-tools\ToolI-SaclProbe -- <監査対象フォルダ> [ログファイル]

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

Console.OutputEncoding = System.Text.Encoding.UTF8; // コンソールの既定コードページによる日本語文字化けを防ぐ

// 監査対象の操作種別(「何を」軸)。FullControlのうち、単なるハンドル同期用の内部フラグ
// (Synchronize)・属性/権限の読み取り系(ReadAttributes・ReadExtendedAttributes・
// ReadPermissions)・実行(ExecuteFile)はエクスプローラー等の閲覧だけで大量に発生しノイズに
// なるため除外し、内容の変更・削除・権限変更(高シグナル)とReadData(明示的に残す判断)のみを
// 監査対象にする。
const FileSystemRights AuditedRights =
    FileSystemRights.ReadData
    | FileSystemRights.WriteData
    | FileSystemRights.AppendData
    | FileSystemRights.Delete
    | FileSystemRights.DeleteSubdirectoriesAndFiles
    | FileSystemRights.WriteAttributes
    | FileSystemRights.WriteExtendedAttributes
    | FileSystemRights.ChangePermissions
    | FileSystemRights.TakeOwnership;

// 加工後CSVに載せる「高シグナル」なアクセス種別。AuditedRightsからReadDataを除いたもの
// (ReadDataのみの閲覧アクセスは実機で大量発生することを確認済みのため、CSVでは除外する)。
const FileSystemRights SignalRights = AuditedRights & ~FileSystemRights.ReadData;

if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    Console.WriteLine("管理者権限で実行してください (SACL設定・監査ポリシー変更・セキュリティログの購読に必要)。");
    return 1;
}

if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.WriteLine("使い方: SaclProbe.exe <監査対象フォルダ> [ログファイル]");
    return 1;
}

var targetFolder = Path.GetFullPath(args[0]);
if (!Directory.Exists(targetFolder))
{
    Console.WriteLine($"指定フォルダが存在しません: {targetFolder}");
    return 1;
}

var logPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "saclprobe.log");
var csvPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(logPath)) ?? ".",
    Path.GetFileNameWithoutExtension(logPath) + ".summary.csv");

var logWriter = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
{
    AutoFlush = true,
};

// Excelでそのまま開いても文字化けしないようUTF-8 BOM付きで書く。
var csvIsNew = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
var csvWriter = new StreamWriter(new FileStream(csvPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(true))
{
    AutoFlush = true,
};
if (csvIsNew)
{
    csvWriter.WriteLine("時刻,EventID,イベント種別,分類,対象,対象種別,対象種別備考,プロセス名,プロセスID,ユーザー,アクセス内容,HandleId,RecordId");
}
var csvRowCount = 0L;
var csvSignalCount = 0L;

void Log(string line)
{
    var stamped = $"[{DateTime.Now:HH:mm:ss.ffffff}] {line}";
    Console.WriteLine(stamped);
    logWriter.WriteLine(stamped);
}

Log($"=== SaclProbe(ツールI: SACL設定・監査ログ検証) 開始 対象フォルダ={targetFolder} " +
    $"生ログ={logPath} 加工後CSV={csvPath} ===");

EnsureAuditSubcategoriesEnabled(Log);

var dirInfo = new DirectoryInfo(targetFolder);
string? originalAuditSddl = null;
try
{
    var currentSecurity = dirInfo.GetAccessControl(AccessControlSections.Audit);
    originalAuditSddl = currentSecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Audit);
    Log($"元のSACL(SDDL、Audit部のみ)を退避しました。終了時にこの状態へ復元します: " +
        $"{(string.IsNullOrEmpty(originalAuditSddl) ? "(空 = 元々SACLなし)" : originalAuditSddl)}");
}
catch (Exception ex)
{
    Log($"[警告] 元のSACL取得に失敗しました。終了時の復元は行いません: {ex.Message}");
}

try
{
    var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
    var newSecurity = dirInfo.GetAccessControl(AccessControlSections.Audit);
    var rule = new FileSystemAuditRule(
        everyone,
        AuditedRights,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AuditFlags.Success | AuditFlags.Failure);
    newSecurity.AddAuditRule(rule);
    dirInfo.SetAccessControl(newSecurity);

    var appliedSddl = dirInfo.GetAccessControl(AccessControlSections.Audit)
        .GetSecurityDescriptorSddlForm(AccessControlSections.Audit);
    Log($"SACL設定完了: Everyone に {AuditedRights} の成功・失敗監査(コンテナ・オブジェクト継承)を追加。" +
        $"設定後のSACL(SDDL)={appliedSddl}");
}
catch (Exception ex)
{
    Log($"[エラー] SACL設定に失敗しました。処理を中止します: {ex}");
    logWriter.Dispose();
    csvWriter.Dispose();
    return 1;
}

var eventCount = 0L;
var matchedCount = 0L;
// 対象フォルダ配下だったハンドルの HandleId -> (ObjectName, そのハンドルの生涯で一度でも
// SignalRightsに該当するアクセスがあったか)。4658/4660がObjectNameを含まないための対応表。
var handleToObject = new ConcurrentDictionary<string, (string ObjectName, bool HasSignal)>();
// 終了時サマリ用。対象フォルダ一致イベントのみを、プロセス名別・EventID別に集計する。
var matchedByProcess = new ConcurrentDictionary<string, long>();
var matchedByEventId = new ConcurrentDictionary<string, long>();

var eventIds = new[] { 4656, 4658, 4660, 4663, 4670, 4907 };
var idFilter = string.Join(" or ", eventIds.Select(id => $"EventID={id}"));
var securityQuery = new EventLogQuery("Security", PathType.LogName, $"*[System[({idFilter})]]");
using var watcher = new EventLogWatcher(securityQuery);

watcher.EventRecordWritten += (_, e) =>
{
    if (e.EventRecord is null) return;
    using var record = e.EventRecord;
    Interlocked.Increment(ref eventCount);
    try
    {
        var xml = record.ToXml();
        var fields = ParseEventData(xml);
        fields.TryGetValue("ObjectName", out var objectName);
        fields.TryGetValue("HandleId", out var handleId);
        fields.TryGetValue("ProcessId", out var handleProcessId);
        // HandleIdはOSのハンドルテーブル値そのもの(プロセスごとに独立、クローズ後は再利用される)で
        // プロセスをまたいだ一意性は無い(実機で「同一プロセス内での使い回し」を確認済み。README参照)。
        // そのため ProcessId+HandleId の組をキーにする。
        var handleKey = $"{handleProcessId}|{handleId}";

        bool isTarget;
        string? resolvedObjectName = null;
        bool isSignal;

        if (!string.IsNullOrEmpty(objectName))
        {
            isTarget = IsUnderTarget(objectName, targetFolder);
            if (!isTarget)
            {
                isSignal = false;
            }
            else if (record.Id == 4670 || record.Id == 4907)
            {
                isSignal = true; // OldSd/NewSdのみでAccessMaskを持たないイベントは常に高シグナル扱い
            }
            else
            {
                var mask = ParseAccessMask(fields);
                // 解析に失敗した場合は安全側(除外しない)に倒す。
                isSignal = !mask.HasValue || (mask.Value & (long)SignalRights) != 0;
            }

            if (isTarget && !string.IsNullOrEmpty(handleId) && handleId != "0x0")
            {
                handleToObject.AddOrUpdate(handleKey,
                    (objectName, isSignal),
                    (_, existing) => (objectName, existing.HasSignal || isSignal));
            }
        }
        else if (!string.IsNullOrEmpty(handleId) && handleToObject.TryGetValue(handleKey, out var mapped))
        {
            isTarget = true;
            resolvedObjectName = mapped.ObjectName;
            // 削除(4660)は常に高シグナル。クローズ(4658)はそのハンドルの生涯の履歴に従う。
            isSignal = record.Id == 4660 || mapped.HasSignal;
            // 4658(クローズ)・4660(削除)でハンドルの生涯は終わるため、以後の番号再利用による
            // 誤対応付けを防ぐためエントリを消す。
            handleToObject.TryRemove(handleKey, out var _unused);
        }
        else
        {
            isTarget = false;
            isSignal = false;
        }

        if (!isTarget) return;

        Interlocked.Increment(ref matchedCount);
        fields.TryGetValue("ProcessName", out var summaryProcessName);
        matchedByProcess.AddOrUpdate(summaryProcessName ?? "(不明)", 1, (_, count) => count + 1);
        matchedByEventId.AddOrUpdate(record.Id.ToString(), 1, (_, count) => count + 1);
        Log(BuildEventLine(record, xml, fields, resolvedObjectName));

        // 生ログと同じ範囲を全件CSVにも出す。閲覧のみか高シグナルかは「分類」列に残すだけにし、
        // 絞り込み(二次加工)はExcel等の利用者側に委ねる。
        Interlocked.Increment(ref csvRowCount);
        csvWriter.WriteLine(BuildCsvRow(record, fields, resolvedObjectName, isSignal));
        if (isSignal) Interlocked.Increment(ref csvSignalCount);
    }
    catch (Exception ex)
    {
        Log($"[イベント処理失敗] EventID={record.Id}: {ex.Message}");
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

Log($"監視開始。{targetFolder} 配下へのアクセスを待機します。Ctrl+Cで停止するとSACLを元に戻して終了します。");
exitSignal.Wait();

watcher.Enabled = false;

if (originalAuditSddl is not null)
{
    try
    {
        var restoreSecurity = dirInfo.GetAccessControl(AccessControlSections.Audit);
        restoreSecurity.SetSecurityDescriptorSddlForm(originalAuditSddl, AccessControlSections.Audit);
        dirInfo.SetAccessControl(restoreSecurity);
        Log("元のSACLに復元しました。");
    }
    catch (Exception ex)
    {
        Log($"[警告] SACL復元に失敗しました。手動で確認してください(icacls \"{targetFolder}\" /audit): {ex.Message}");
    }
}
else
{
    Log("[警告] 元のSACLを退避できていなかったため、復元は行いませんでした。手動で確認してください。");
}

if (matchedCount > 0)
{
    var byProcess = string.Join(" ", matchedByProcess.OrderByDescending(kv => kv.Value)
        .Select(kv => $"{kv.Key}={kv.Value}"));
    var byEventId = string.Join(" ", matchedByEventId.OrderBy(kv => kv.Key)
        .Select(kv => $"{kv.Key}={kv.Value}"));
    Log($"集計(プロセス名別、対象フォルダ一致分のみ): {byProcess}");
    Log($"集計(EventID別、対象フォルダ一致分のみ): {byEventId}");
}

Log($"加工後CSV出力行数={csvRowCount}(うち分類=シグナル: {csvSignalCount}件、閲覧のみ: {csvRowCount - csvSignalCount}件) csv={csvPath}");
Log($"=== SaclProbe 終了 (観測イベント総数={eventCount} 対象フォルダ一致={matchedCount}) ===");
logWriter.Dispose();
csvWriter.Dispose();
return 0;

/// <summary>
/// オブジェクトアクセス監査(「ファイルシステム」)とハンドル操作監査(「ハンドル操作」)の
/// サブカテゴリを冪等に有効化する(MyLogger本体・ToolC-ProcessAuditProbeと同じ方式)。
/// 前者は4656/4663/4660/4670/4907、後者は4658(ハンドルクローズ)を出すために必要。
/// </summary>
static void EnsureAuditSubcategoriesEnabled(Action<string> log)
{
    RunAuditPol("{0CCE921D-69AE-11D9-BED3-505054503030}", "ファイルシステム(オブジェクトアクセス)", log);
    RunAuditPol("{0CCE9223-69AE-11D9-BED3-505054503030}", "ハンドル操作", log);
}

static void RunAuditPol(string subcategoryGuid, string label, Action<string> log)
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
    if (process is null)
    {
        log($"[警告] auditpolの起動に失敗しました({label})。");
        return;
    }
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit(10_000);
    var detail = process.ExitCode == 0 ? "" : $" stdout=[{stdout.Trim()}] stderr=[{stderr.Trim()}]";
    log($"監査サブカテゴリ有効化: {label}({subcategoryGuid}) ExitCode={process.ExitCode}{detail}");
}

static bool IsUnderTarget(string objectName, string targetFolder)
{
    var normalizedTarget = targetFolder.TrimEnd('\\');
    var normalizedObject = objectName.TrimEnd('\\');
    return normalizedObject.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
        || normalizedObject.StartsWith(normalizedTarget + "\\", StringComparison.OrdinalIgnoreCase);
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

/// <summary>
/// 1イベントを1行に整形する。(1)ヘッダ相当の共通メタデータ、(2)個別Dataフィールド全件、
/// (3)Windowsが公式に組み立てる説明文(FormatDescription、イベントビューアーの「全般」タブ相当)、
/// (4)生XML全体(イベントビューアーの「詳細」タブXMLビュー相当)の4種類を、可能な限り隅々まで
/// 1行にまとめて記録する。
/// </summary>
static string BuildEventLine(EventRecord record, string xml, Dictionary<string, string> fields, string? resolvedObjectName)
{
    string? levelName = SafeGet(() => record.LevelDisplayName);
    string? taskName = SafeGet(() => record.TaskDisplayName);
    string? opcodeName = SafeGet(() => record.OpcodeDisplayName);
    string? keywordsNames = SafeGet(() => record.KeywordsDisplayNames is null ? null : string.Join("|", record.KeywordsDisplayNames));
    string? description = SafeGet(() => record.FormatDescription());

    var sb = new StringBuilder();
    sb.Append($"Event EventID={record.Id} RecordId={record.RecordId} TimeCreated={record.TimeCreated:yyyy-MM-dd HH:mm:ss.ffffff} ");
    sb.Append($"Level={record.Level}({levelName ?? "?"}) Task={record.Task}({taskName ?? "?"}) ");
    sb.Append($"Opcode={record.Opcode}({opcodeName ?? "?"}) Keywords=0x{record.Keywords:X}({keywordsNames ?? "?"}) ");
    sb.Append($"ProviderName={record.ProviderName} LogName={record.LogName} MachineName={record.MachineName} ");
    sb.Append($"LoggingProcessId={record.ProcessId} LoggingThreadId={record.ThreadId} UserId={record.UserId?.Value ?? "-"} ");
    if (resolvedObjectName is not null)
    {
        sb.Append($"ResolvedObjectName={resolvedObjectName}(元イベントにObjectNameフィールドなし。HandleIdから対応する4656/4663等を突合) ");
    }
    foreach (var kv in fields)
    {
        sb.Append($"{kv.Key}={Sanitize(kv.Value)} ");
    }
    sb.Append($"Description=[{Sanitize(description ?? "(FormatDescription失敗、メッセージリソース未解決の可能性)")}] ");
    sb.Append($"RawXml=[{Sanitize(xml)}]");
    return sb.ToString();
}

static string Sanitize(string s) => s.Replace("\r\n", " / ").Replace("\n", " / ").Replace("\t", " ");

/// <summary>16進文字列("0x...")のAccessMaskをlongに変換する。解析できなければnull。</summary>
static long? ParseAccessMask(Dictionary<string, string> fields)
{
    if (!fields.TryGetValue("AccessMask", out var hex) || string.IsNullOrEmpty(hex)) return null;
    var trimmed = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
    return long.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : null;
}

static string EventName(int id) => id switch
{
    4656 => "ハンドル要求",
    4658 => "ハンドルクローズ",
    4660 => "削除",
    4663 => "アクセス試行",
    4670 => "権限変更",
    4907 => "SACL変更",
    _ => $"EventID={id}",
};

/// <summary>加工後CSVの1行を組み立てる。生ログと同じ範囲(対象フォルダ一致分)を全件出力し、
/// 閲覧のみか高シグナルかは「分類」列に残すだけにする(除外はしない。二次加工は利用者に委ねる)。
/// ノイズになりがちな生XML・Descriptionは含めない(詳細を見たい場合はRecordIdで生ログを参照)。</summary>
static string BuildCsvRow(EventRecord record, Dictionary<string, string> fields, string? resolvedObjectName, bool isSignal)
{
    fields.TryGetValue("ObjectName", out var objectName);
    fields.TryGetValue("ProcessName", out var processName);
    fields.TryGetValue("ProcessId", out var processId);
    fields.TryGetValue("HandleId", out var handleId);
    fields.TryGetValue("SubjectDomainName", out var domain);
    fields.TryGetValue("SubjectUserName", out var user);
    var userDisplay = string.IsNullOrEmpty(domain) ? (user ?? "") : $@"{domain}\{user}";

    var accessSummary = "";
    if (record.Id == 4656 || record.Id == 4663)
    {
        var mask = ParseAccessMask(fields);
        if (mask.HasValue)
        {
            accessSummary = $"{(FileSystemRights)mask.Value} (0x{mask.Value:X})";
        }
    }

    var target = objectName ?? resolvedObjectName ?? "";
    var (targetType, targetTypeRemarks) = DescribeTargetType(target);
    var cells = new[]
    {
        record.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss.ffffff") ?? "",
        record.Id.ToString(),
        EventName(record.Id),
        isSignal ? "シグナル" : "閲覧のみ",
        target,
        targetType,
        targetTypeRemarks,
        processName ?? "",
        processId ?? "",
        userDisplay,
        accessSummary,
        handleId ?? "",
        record.RecordId?.ToString() ?? "",
    };
    return string.Join(",", cells.Select(CsvEscape));
}

/// <summary>
/// 対象パスがファイルかフォルダかを判定する。セキュリティ監査イベント自体にはファイル/フォルダの
/// 区別を示すフィールドが無い(ObjectTypeは常に"File"、AccessListの表記も
/// "ReadData (または ListDirectory)"のように両論併記でWindows自身も区別していない)ため、
/// 以下の優先順位で判定する。
///   1. 拡張子があれば「ファイル」とみなす(フォルダ名にドットが含まれる稀なケースを除き、
///      ファイルシステムを見ずに一瞬で判定できる。削除直後でも判定できる利点がある)
///   2. 拡張子が無ければ Directory.Exists() で確認する
///   3. それでも判定できなければ File.Exists()(拡張子の無いファイル、例: README等)で確認する
///   4. いずれも実在しなければ(記録時点ですでに削除・リネーム済み等)「判別不明」とする
/// 1.は推定(ファイルシステムを見ていない)、2.3.はCSV書き出し時点のファイルシステムを見た
/// 確認結果であり、確信度が異なるため、判定経緯を「対象種別備考」列に残す。
/// </summary>
static (string TargetType, string Remarks) DescribeTargetType(string target)
{
    if (string.IsNullOrEmpty(target)) return ("判別不明", "対象パスが空");

    if (!string.IsNullOrEmpty(Path.GetExtension(target)))
    {
        return ("ファイル", "拡張子から推定(ファイルシステムは未確認)");
    }

    if (Directory.Exists(target)) return ("フォルダ", "Directory.Existsで確認");
    if (File.Exists(target)) return ("ファイル", "拡張子なしだがFile.Existsで確認");

    return ("判別不明", "拡張子なし、かつ記録時点で対象が存在しないため判定不能(削除・リネーム等の可能性)");
}

static string CsvEscape(string value)
{
    if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    return value;
}

static string? SafeGet(Func<string?> getter)
{
    try
    {
        return getter();
    }
    catch
    {
        return null;
    }
}
