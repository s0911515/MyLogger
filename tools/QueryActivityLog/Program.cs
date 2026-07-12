using Microsoft.Data.Sqlite;

// MyLogger の activity_log を手軽に確認するための読み取り専用の簡易ツール。
// 動作確認手順書 (doc/セットアップ・動作確認手順書.md) から利用する。
//
// 使い方:
//   dotnet run --project tools\QueryActivityLog                       既定 DB のサマリを表示
//   dotnet run --project tools\QueryActivityLog -- <DBパス>            指定 DB のサマリを表示
//   dotnet run --project tools\QueryActivityLog -- <DBパス> "<SQL>"    任意の SELECT 文を実行

var dbPath = args.Length > 0 ? args[0] : @"C:\ProgramData\MyLogger\data\activity.db";
if (!File.Exists(dbPath))
{
    Console.WriteLine($"DB ファイルが見つかりません: {dbPath}");
    Console.WriteLine("MyLogger が一度も起動していないか、パスが誤っている可能性があります。");
    return 1;
}

using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
connection.Open();

if (args.Length > 1)
{
    RunQuery(connection, args[1]);
    return 0;
}

// 既定表示: 件数サマリ + 直近 20 件
using (var countCmd = connection.CreateCommand())
{
    countCmd.CommandText = "SELECT COUNT(*) FROM activity_log";
    Console.WriteLine($"総件数: {countCmd.ExecuteScalar()}");
}

Console.WriteLine();
Console.WriteLine("[action_type 別件数]");
RunQuery(connection, "SELECT action_type, COUNT(*) AS cnt FROM activity_log GROUP BY action_type ORDER BY cnt DESC");

Console.WriteLine();
Console.WriteLine("[直近 20 件]");
RunQuery(connection, """
    SELECT log_id, event_timestamp, action_type, target_user, source_ip, source_path, dest_path, additional_info
    FROM activity_log ORDER BY log_id DESC LIMIT 20
    """);

return 0;

static void RunQuery(SqliteConnection connection, string sql)
{
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    using var reader = cmd.ExecuteReader();

    var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
    Console.WriteLine(string.Join(" | ", columnNames));

    var rowCount = 0;
    while (reader.Read())
    {
        var values = Enumerable.Range(0, reader.FieldCount)
            .Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "");
        Console.WriteLine(string.Join(" | ", values));
        rowCount++;
    }

    if (rowCount == 0)
    {
        Console.WriteLine("(該当なし)");
    }
}
