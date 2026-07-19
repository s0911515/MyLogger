// ToolI-SaclProbeが出力する加工後CSV([ログファイル名].summary.csv)を、さらにエンドユーザーが
// 見やすいCSVに2段階で加工する補助ツール。
//
// 第1段階: CSVは1イベント(4656/4658/4660/4663/4670/4907)=1行だが、ProcessId+HandleId
// (同一ハンドルの生涯)で相関が取れるものを1つの「操作セグメント」にまとめる(ハンドル要求→
// アクセス試行→クローズ、あるいは→削除、を1つの操作とみなす)。操作種別は日本語(読み取り/
// 書き込み/属性変更/リネーム・移動・削除/権限変更/SACL変更)で判定する。「リネーム/移動/削除」
// は、Delete権限は要求されたがそのハンドル単体では削除確定(EventID=4660)を確認できなかった
// ことを表す(リネーム・移動・削除の空振りアクセスのいずれもあり得るため、断定せずこの名前の
// ままにしている。詳細はDetermineOperation関数のコメント参照)。
//
// 除外(実機検証で確認済みのノイズ): フォルダに対する「読み取り」は、単なる一覧表示による
// ノイズと確認済みのため出力から除外する(1回の操作でも複数階層のフォルダに同時多発し、実際に
// 何が起きたかの情報を一切含まないため)。フォルダに対するそれ以外の操作(削除・リネーム/移動・
// 書き込み等)や、ファイルに対する読み取りは除外しない(生ログ側では引き続き全件を確認できる)。
//
// 第2段階: 同じユーザー・同じ対象ファイル・同じプロセス(PID)・同じ操作種別のセグメントは、
// 同一操作の繰り返し(例: エクスプローラーが同じフォルダを定期的に読み取りポーリングする等)と
// みなし、1行に集約する(時刻は最初に観測した時点、RecordIdは全件列挙で表す)。相関で断定
// できない(ユーザー/ファイル/プロセス/操作種別のいずれかが異なる)ものは決してまとめない。
//
// 使い方:
//   dotnet run --project probe-tools\ToolI-SaclProbe\LogFormatter -- <ToolIのCSVファイル> [出力CSVファイル]
//
// 操作種別の判定は文字列一致ではなく、CSVの「アクセス内容」列末尾に付記した生のAccessMask
// (16進)を読み直し、FileSystemRightsのビット演算で行う。CSVの「アクセス内容」は.NETのFlags
// 列挙型のToString()による複合名(例: 個々のビットではなく"Write"とまとめて表示される)ことが
// あり、文字列一致では判定を取りこぼすため。
//
// 出力CSVは全フィールドを常にダブルクォートで囲む(RecordId列がカンマ区切りの複数値になる
// ため、区切りカンマと値中のカンマを一貫して区別できるようにするための方針)。

using System.Globalization;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = System.Text.Encoding.UTF8; // コンソールの既定コードページによる日本語文字化けを防ぐ

if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.WriteLine("使い方: LogFormatter.exe <ToolIのCSVファイル(*.summary.csv)> [出力CSVファイル]");
    return 1;
}

var csvPath = args[0];
if (!File.Exists(csvPath))
{
    Console.WriteLine($"指定されたCSVファイルが存在しません: {csvPath}");
    return 1;
}

var outputPath = args.Length > 1 ? args[1] : DeriveDefaultOutputPath(csvPath);

const FileSystemRights WriteBits = FileSystemRights.WriteData | FileSystemRights.AppendData;
const FileSystemRights WriteAttrBits = FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
const FileSystemRights DeleteBits = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
const FileSystemRights PermBits = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
const FileSystemRights ReadBits = FileSystemRights.ReadData;

var rows = new List<CsvRow>();
using (var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
{
    var isHeader = true;
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (isHeader) { isHeader = false; continue; }
        if (line.Length == 0) continue;
        var row = ParseRow(line);
        if (row is not null) rows.Add(row);
    }
}

if (rows.Count == 0)
{
    Console.WriteLine("CSVにデータ行がありませんでした(ヘッダのみ、または空)。");
    return 0;
}

// ProcessId+HandleId(同一ハンドルの生涯)でグループ化する。ToolI本体が使うキーと同じ考え方
// (HandleIdはプロセスごとに独立かつ再利用されるため、ProcessId単体・HandleId単体では不可)。
//
// ただし単純に(ProcessId,HandleId)だけでGroupByすると、同一プロセスが同じHandleId番号を
// 使い回した場合(実機で確認済み。ToolI本体のREADME参照)、時間的に無関係な別々の操作
// (例: a.txt削除→b.txt削除)が1グループに混ざってしまう。そのため時系列順に処理し、
// 4658(クローズ)・4660(削除)でそのハンドルの生涯が終わったとみなしてグループを確定させ、
// 同じキーが後から再登場したら別グループとして扱う。
var groups = new List<List<CsvRow>>();
var openSegments = new Dictionary<string, List<CsvRow>>();
foreach (var row in rows.OrderBy(r => r.Time).ThenBy(r => r.RecordId))
{
    var key = string.IsNullOrEmpty(row.HandleId) ? $"norow-{row.RecordId}" : $"{row.ProcessId}|{row.HandleId}";

    if (row.EventId == 4656 && openSegments.TryGetValue(key, out var stale))
    {
        // 前のクローズ/削除を観測できないまま次のオープンが来た(取りこぼし等)。
        // 古いセグメントはその時点で確定させ、新しいセグメントを開始する。
        groups.Add(stale);
        openSegments.Remove(key);
    }

    if (!openSegments.TryGetValue(key, out var segment))
    {
        segment = new List<CsvRow>();
        openSegments[key] = segment;
    }
    segment.Add(row);

    if (row.EventId is 4658 or 4660)
    {
        groups.Add(segment);
        openSegments.Remove(key);
    }
}
// 最後までクローズ/削除が来なかった(ツール停止時点で開いたままだった)セグメントも出力する。
groups.AddRange(openSegments.Values);
groups = groups.Select(g => g.OrderBy(r => r.Time).ToList()).OrderBy(g => g[0].Time).ToList();

// 第1段階: ハンドル単位のセグメントごとに、操作種別・対象・プロセス・ユーザー等を確定する。
// 時刻はそのハンドルで最初に観測した時刻、RecordIdはそのセグメントを構成した全イベントを列挙する。
var allSegments = groups.Select(group =>
{
    var operation = DetermineOperation(group);
    var target = group.Select(r => r.ObjectName).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "(不明)";
    var targetType = group.Select(r => r.TargetType).FirstOrDefault(s => !string.IsNullOrEmpty(s) && s != "判別不明") ?? "判別不明";
    var processName = group.Select(r => r.ProcessName).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "(不明)";
    var processId = group.Select(r => r.ProcessId).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "(不明)";
    var user = group.Select(r => r.User).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "(不明)";
    var recordIds = group.Select(r => r.RecordId).Distinct().OrderBy(x => x).ToList();
    return new Segment(group.Min(r => r.Time), operation, target, targetType, processName, processId, user, recordIds);
}).ToList();

// フォルダに対する「読み取り」は、実機検証でエクスプローラー等の一覧表示による純粋なノイズと
// 確認済み(単一の操作でも複数階層のフォルダに同時多発する、対象を特定する情報を含まない等)
// のため、ここで除外する。フォルダに対するそれ以外の操作(削除・リネーム/移動・書き込み等)は
// 実際に何が起きたかの情報を持つため、除外しない。
var folderReadCount = allSegments.Count(s => s.Operation == "読み取り" && s.TargetType == "フォルダ");
var segments = allSegments.Where(s => !(s.Operation == "読み取り" && s.TargetType == "フォルダ")).ToList();

// 第2段階: 同じユーザー・同じ対象・同じプロセス(PID)・同じ操作種別のセグメントは、
// 同一操作の繰り返しとみなして1行に集約する(時刻は最初に観測した時点、RecordIdは全件の和集合)。
var aggregated = segments
    .GroupBy(s => (s.User, s.Target, s.ProcessId, s.Operation))
    .Select(g => new AggregatedRow(
        Time: g.Min(s => s.Time),
        Operation: g.Key.Operation,
        Target: g.Key.Target,
        Target2: "",
        TargetType: g.Select(s => s.TargetType).FirstOrDefault(t => t != "判別不明") ?? "判別不明",
        ProcessName: g.Select(s => s.ProcessName).First(),
        ProcessId: g.Key.ProcessId,
        User: g.Key.User,
        RecordIds: g.SelectMany(s => s.RecordIds).Distinct().OrderBy(x => x).ToList()))
    .OrderBy(r => r.Time)
    .ToList();

// 第3段階(ヒューリスティック、断定ではなく推定): コピーは監査ログ上「コピー元の読み取り」
// 「コピー先の書き込み/権限変更(ACL継承)」という別々のオブジェクトへの別操作としてしか現れず、
// OSレベルでの紐付け情報は無い。そのため以下の条件をすべて満たす場合に限り「推定コピー」として
// 1行にまとめる: 同じプロセス(PID)・同じユーザー・ファイル名(パスは除く)が一致・近い時刻
// (既定5秒以内)。断定できる相関ではないため、操作列には必ず「推定コピー」と明示し、
// 対象列にコピー元、コピー先列にコピー先をそれぞれ別列で残す(他の分類のような断定的な統合とは
// 意図的に区別する)。
const double CopyHeuristicWindowSeconds = 5.0;
var usedIndices = new HashSet<int>();
var estimatedCopies = new List<AggregatedRow>();
for (var i = 0; i < aggregated.Count; i++)
{
    if (usedIndices.Contains(i) || aggregated[i].Operation != "読み取り") continue;
    for (var j = 0; j < aggregated.Count; j++)
    {
        if (i == j || usedIndices.Contains(j)) continue;
        var dest = aggregated[j];
        if (dest.Operation != "書き込み" && dest.Operation != "権限変更") continue;
        if (dest.ProcessId != aggregated[i].ProcessId || dest.User != aggregated[i].User) continue;
        if (string.Equals(dest.Target, aggregated[i].Target, StringComparison.OrdinalIgnoreCase)) continue;
        if (!string.Equals(Path.GetFileName(dest.Target), Path.GetFileName(aggregated[i].Target), StringComparison.OrdinalIgnoreCase)) continue;
        if (Math.Abs((dest.Time - aggregated[i].Time).TotalSeconds) > CopyHeuristicWindowSeconds) continue;

        var src = aggregated[i];
        usedIndices.Add(i);
        usedIndices.Add(j);
        estimatedCopies.Add(new AggregatedRow(
            Time: src.Time < dest.Time ? src.Time : dest.Time,
            Operation: "推定コピー",
            Target: src.Target,
            Target2: dest.Target,
            TargetType: src.TargetType != "判別不明" ? src.TargetType : dest.TargetType,
            ProcessName: src.ProcessName,
            ProcessId: src.ProcessId,
            User: src.User,
            RecordIds: src.RecordIds.Concat(dest.RecordIds).Distinct().OrderBy(x => x).ToList()));
        break;
    }
}
var finalRows = aggregated
    .Where((_, idx) => !usedIndices.Contains(idx))
    .Concat(estimatedCopies)
    .OrderBy(r => r.Time)
    .ToList();

var writer = new StreamWriter(new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true))
{
    AutoFlush = true,
};
WriteCsvLine(writer, new[] { "日付", "時刻", "操作", "対象種別", "対象", "コピー先", "プロセス名", "プロセスID", "ドメイン名", "ユーザー名", "RecordId" });
foreach (var row in finalRows)
{
    var (domain, userName) = SplitUser(row.User);
    var cells = new[]
    {
        row.Time.ToString("yyyy-MM-dd"),
        row.Time.ToString("HH:mm:ss.ffffff"),
        DecorateOperation(row.Operation, row.TargetType),
        row.TargetType,
        row.Target,
        row.Target2,
        row.ProcessName,
        row.ProcessId,
        domain,
        userName,
        string.Join(",", row.RecordIds),
    };
    WriteCsvLine(writer, cells);
}
writer.Dispose();

var opCounts = finalRows.GroupBy(r => DecorateOperation(r.Operation, r.TargetType)).ToDictionary(g => g.Key, g => g.Sum(r => r.RecordIds.Count));
Console.WriteLine($"=== LogFormatter(ToルI補助ツール: 操作単位への集約) 入力CSV={csvPath} 出力={outputPath} ===");
Console.WriteLine($"入力行数={rows.Count} ハンドルセグメント数={groups.Count}(うちフォルダの読み取りとして除外={folderReadCount}) 集約後行数={finalRows.Count}(うち推定コピー={estimatedCopies.Count})");
Console.WriteLine($"集計(操作別、集約前の実件数): {string.Join(" ", opCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"))}");
return 0;

static string DeriveDefaultOutputPath(string csvPath)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".";
    var name = Path.GetFileName(csvPath);
    const string suffix = ".summary.csv";
    var baseName = name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        ? name[..^suffix.Length]
        : Path.GetFileNameWithoutExtension(name);
    return Path.Combine(dir, baseName + ".readable.csv");
}

static long ParseLongOrDefault(string s) => long.TryParse(s, out var value) ? value : 0;

/// <summary>「ユーザー」列("DOMAIN\user"形式)を、ドメイン名とユーザー名に分割する。
/// バックスラッシュが無ければドメイン名は空とする。</summary>
static (string Domain, string UserName) SplitUser(string combined)
{
    var idx = combined.IndexOf('\\');
    return idx >= 0 ? (combined[..idx], combined[(idx + 1)..]) : ("", combined);
}

/// <summary>「操作」列を「ファイルの読み取り」「フォルダの削除」のように対象種別付きで表示する。
/// 対象種別が確定できない場合は「フォルダまたはファイルの...」とし、不確かさを隠さない
/// (「対象種別」列自体は「判別不明」のまま変えない)。</summary>
static string DecorateOperation(string operation, string targetType)
{
    var prefix = targetType switch
    {
        "ファイル" => "ファイルの",
        "フォルダ" => "フォルダの",
        _ => "フォルダまたはファイルの",
    };
    return prefix + operation;
}

static void WriteCsvLine(TextWriter writer, IEnumerable<string> cells)
{
    writer.WriteLine(string.Join(",", cells.Select(CsvEscapeAlways)));
}

/// <summary>全フィールドを常にダブルクォートで囲む(RecordId列がカンマ区切りの複数値になるため、
/// 区切りカンマと値中のカンマを一貫して区別できるようにするための方針)。</summary>
static string CsvEscapeAlways(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

/// <summary>
/// グループ(同一ハンドルの生涯)全体を見て、日本語の操作種別を1つ決める。
/// 文字列一致ではなく、AccessMaskのビット演算で判定する(理由は冒頭コメント参照)。
/// 優先度: SACL変更 &gt; 削除 &gt; 権限変更 &gt; リネーム/移動/削除 &gt; 書き込み &gt; 属性変更 &gt; 読み取り &gt; 不明。
///
/// 「削除」は必ずEventID=4660(オブジェクトが実際に削除された、という確定的な証拠)を伴う
/// セグメントにのみ付ける。NTFSではリネーム・移動(同一ボリューム内)にもDelete権限が要求される
/// (ファイルの識別子を変更する操作として扱われるため)うえ、実機では**1回の削除操作が複数回の
/// ハンドル開閉に分かれ、そのうち4660を伴うのは1回だけ**ということも確認している(削除の前後に
/// 権限チェックだけの空振りアクセスが発生する)。つまりDeleteビットの要求はあるが4660を伴わない
/// セグメントは、リネーム・移動・(同じ操作の一部である)削除の空振りアクセスのいずれかであり、
/// 単体では区別がつかない。断定できないものを無理に断定・統合せず、「リネーム/移動/削除」という
/// 曖昧さを保ったラベルのままにする。判断材料としては、同じ対象に対して別途「削除」ラベルの
/// セグメントが存在すれば実際に削除されたと確定できる(そちらを正とする)。
/// また移動先のパスやリネーム後の名前は監査ログに一切残らないため、リネームと移動をこれ以上
/// 区別することもできない。
/// </summary>
static string DetermineOperation(List<CsvRow> group)
{
    if (group.Any(r => r.EventId == 4907)) return "SACL変更";
    if (group.Any(r => r.EventId == 4660)) return "削除";

    long cumulativeMask = 0;
    foreach (var row in group)
    {
        var mask = ExtractMaskFromAccessSummary(row.AccessSummary);
        if (mask.HasValue) cumulativeMask |= mask.Value;
    }
    var rights = (FileSystemRights)cumulativeMask;

    if (group.Any(r => r.EventId == 4670) || (rights & PermBits) != 0) return "権限変更";
    if ((rights & DeleteBits) != 0) return "リネーム/移動/削除";
    if ((rights & WriteBits) != 0) return "書き込み";
    if ((rights & WriteAttrBits) != 0) return "属性変更";
    if ((rights & ReadBits) != 0) return "読み取り";
    return "不明";
}

/// <summary>「アクセス内容」列末尾の"(0xNNNN)"部分から生のAccessMaskを取り出す。</summary>
static long? ExtractMaskFromAccessSummary(string accessSummary)
{
    var match = Regex.Match(accessSummary, @"\(0x([0-9A-Fa-f]+)\)\s*$");
    if (!match.Success) return null;
    return long.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
        ? value
        : null;
}

static CsvRow? ParseRow(string line)
{
    var cells = ParseCsvLine(line);
    if (cells.Count < 13) return null;
    // 列: 時刻(0),EventID(1),イベント種別(2),分類(3),対象(4),対象種別(5),対象種別備考(6),
    //     プロセス名(7),プロセスID(8),ユーザー(9),アクセス内容(10),HandleId(11),RecordId(12)
    DateTime.TryParse(cells[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time);
    int.TryParse(cells[1], out var eventId);
    var recordId = ParseLongOrDefault(cells[12]);
    return new CsvRow(time, eventId, cells[4], cells[5], cells[7], cells[8], cells[9], cells[10], cells[11], recordId);
}

/// <summary>ToolIが出力するCSV(RFC4180風、ダブルクォート囲み+""エスケープ)を1行分解析する。</summary>
static List<string> ParseCsvLine(string line)
{
    var result = new List<string>();
    var current = new StringBuilder();
    var inQuotes = false;
    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (inQuotes)
        {
            if (c == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else
            {
                current.Append(c);
            }
        }
        else if (c == '"')
        {
            inQuotes = true;
        }
        else if (c == ',')
        {
            result.Add(current.ToString());
            current.Clear();
        }
        else
        {
            current.Append(c);
        }
    }
    result.Add(current.ToString());
    return result;
}

/// <summary>CSVの1行(時刻,EventID,イベント種別,分類,対象,対象種別,プロセス名,プロセスID,ユーザー,アクセス内容,HandleId,RecordId)。</summary>
record CsvRow(
    DateTime Time,
    int EventId,
    string ObjectName,
    string TargetType,
    string ProcessName,
    string ProcessId,
    string User,
    string AccessSummary,
    string HandleId,
    long RecordId);

/// <summary>第1段階: 1つのハンドルの生涯(オープン〜クローズ/削除)を1つの操作とみなしたもの。
/// Timeはそのハンドルで最初に観測した時刻、RecordIdsはそのセグメントを構成した全イベント。</summary>
record Segment(
    DateTime Time,
    string Operation,
    string Target,
    string TargetType,
    string ProcessName,
    string ProcessId,
    string User,
    List<long> RecordIds);

/// <summary>第2段階: 同じユーザー・対象・プロセス・操作種別のSegmentを1行に集約したもの。
/// Target2は推定コピー(第3段階)のコピー先専用で、それ以外の操作では空文字列。</summary>
record AggregatedRow(
    DateTime Time,
    string Operation,
    string Target,
    string Target2,
    string TargetType,
    string ProcessName,
    string ProcessId,
    string User,
    List<long> RecordIds);
