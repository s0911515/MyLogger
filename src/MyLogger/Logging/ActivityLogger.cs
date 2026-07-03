using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MyLogger.Config;

namespace MyLogger.Logging;

/// <summary>1 件のファイル操作イベント。JSONL としてログに書き出される。</summary>
public sealed record ActivityEvent
{
    /// <summary>イベント発生時刻 (ローカル時刻)。</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>検出元: local-fs (FileSystemWatcher) / network-io (ETW) / smb-server (監査ログ)。</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>操作種別: Created / Changed / Renamed / Deleted / Write / Read / NetworkShareAccess など。</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>対象ファイル / フォルダのパス。</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>リネーム時の変更前パス。</summary>
    [JsonPropertyName("oldPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OldPath { get; init; }

    /// <summary>書き込み先の分類: fixed / network / removable。</summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; init; }

    /// <summary>操作を行ったプロセス名。</summary>
    [JsonPropertyName("process")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessName { get; init; }

    [JsonPropertyName("pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProcessId { get; init; }

    /// <summary>書き込み / 読み取りバイト数 (ETW 集約値)。</summary>
    [JsonPropertyName("bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Bytes { get; init; }

    /// <summary>操作したユーザー (ドメイン\ユーザー名)。</summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; init; }

    /// <summary>アクセス元 IP アドレス (受信方向のみ)。</summary>
    [JsonPropertyName("remoteIp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteIp { get; init; }

    /// <summary>アクセスされた共有名 (受信方向のみ)。</summary>
    [JsonPropertyName("share")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShareName { get; init; }

    /// <summary>要求されたアクセス権の内訳 (受信方向のみ)。</summary>
    [JsonPropertyName("access")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Access { get; init; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
}

/// <summary>
/// アクティビティイベントを日次ローテーションの JSONL ファイルへ非同期に書き出すロガー。
/// 監視スレッド (ETW コールバック等) をブロックしないよう Channel 経由で書き込む。
/// </summary>
public sealed class ActivityLogger : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Channel<ActivityEvent> _channel;
    private readonly string _logDirectory;
    private readonly int _retentionDays;
    private readonly ILogger<ActivityLogger> _logger;
    private Task? _writerTask;
    private readonly CancellationTokenSource _cts = new();

    public ActivityLogger(IOptions<MonitorOptions> options, ILogger<ActivityLogger> logger)
    {
        _logDirectory = Environment.ExpandEnvironmentVariables(options.Value.LogDirectory);
        _retentionDays = options.Value.RetentionDays;
        _logger = logger;
        _channel = Channel.CreateBounded<ActivityEvent>(new BoundedChannelOptions(50_000)
        {
            // ログ書き込みが追い付かない場合は最古のイベントから捨てる (監視側を止めない)
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>イベントをキューに積む。どのスレッドから呼んでもよい。</summary>
    public void Log(ActivityEvent evt) => _channel.Writer.TryWrite(evt);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_logDirectory);
        _writerTask = Task.Run(() => WriteLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_writerTask is not null)
        {
            await Task.WhenAny(_writerTask, Task.Delay(5000, cancellationToken));
        }
        _cts.Cancel();
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        string? currentDate = null;
        StreamWriter? writer = null;
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            {
                var date = evt.Timestamp.ToString("yyyy-MM-dd");
                if (writer is null || date != currentDate)
                {
                    writer?.Dispose();
                    currentDate = date;
                    var path = Path.Combine(_logDirectory, $"activity-{date}.jsonl");
                    writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false));
                    CleanupOldLogs();
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(evt, JsonOptions));

                // まとめて来ている間は都度 Flush しない
                if (_channel.Reader.Count == 0)
                {
                    await writer.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止時
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "アクティビティログの書き込みに失敗しました");
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private void CleanupOldLogs()
    {
        if (_retentionDays <= 0) return;
        try
        {
            var threshold = DateTime.Now.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "activity-*.jsonl"))
            {
                if (File.GetLastWriteTime(file) < threshold)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "古いログファイルの削除に失敗しました");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        if (_writerTask is not null)
        {
            try { await _writerTask; } catch { /* 停止時の例外は無視 */ }
        }
        _cts.Dispose();
    }
}
