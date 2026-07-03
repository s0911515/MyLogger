namespace MyLogger.Config;

public sealed class MonitorOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>アクティビティログ (JSONL) の出力先ディレクトリ。</summary>
    public string LogDirectory { get; set; } = @"C:\ProgramData\MyLogger\logs";

    /// <summary>ログファイルの保持日数。0 以下で無制限。</summary>
    public int RetentionDays { get; set; } = 90;

    public FileWatcherOptions FileWatcher { get; set; } = new();
    public EtwOptions Etw { get; set; } = new();
    public SmbAuditOptions SmbAudit { get; set; } = new();
}

/// <summary>ローカルディスク上のファイル操作監視 (FileSystemWatcher)。</summary>
public sealed class FileWatcherOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>監視対象パス。空の場合は全ての固定ドライブのルートを監視する。</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>このプレフィックスに一致するパスは記録しない (ノイズ抑制)。</summary>
    public List<string> ExcludePathPrefixes { get; set; } = new();

    /// <summary>この拡張子のファイルは記録しない。</summary>
    public List<string> ExcludeExtensions { get; set; } = new();

    /// <summary>同一パス・同一操作をこの秒数内に重複記録しない。</summary>
    public int DedupeSeconds { get; set; } = 2;
}

/// <summary>ETW によるネットワーク共有 / リムーバブルドライブへの書き込み監視。</summary>
public sealed class EtwOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>リムーバブルドライブ (USB メモリ等) への書き込みも記録するか。</summary>
    public bool IncludeRemovable { get; set; } = true;

    /// <summary>ネットワーク上のファイルの読み取り (社外への持ち出しの前段) も記録するか。</summary>
    public bool IncludeNetworkReads { get; set; } = false;

    /// <summary>書き込みイベントを集約してログに書き出す間隔 (秒)。</summary>
    public int WriteFlushSeconds { get; set; } = 5;

    /// <summary>記録対象外のプロセス名 (拡張子なし、大文字小文字無視)。</summary>
    public List<string> ExcludeProcesses { get; set; } = new()
    {
        "MsMpEng", "SearchIndexer", "SearchProtocolHost", "MyLogger"
    };

    /// <summary>
    /// ここに挙げたプロセスのファイル読み取りは、読み取り先がローカルでも常に記録する。
    /// 既定の rdpclip は RDP のクリップボード共有を担うプロセスで、
    /// リモートデスクトップ接続元へのファイルコピー時にサーバー側でファイルを読み取る。
    /// 空にするとこの機能は無効。
    /// </summary>
    public List<string> AuditReadProcesses { get; set; } = new() { "rdpclip" };
}

/// <summary>ネットワークからの自 PC 共有フォルダへのアクセス監視 (セキュリティ監査ログ)。</summary>
public sealed class SmbAuditOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>IPC$ (名前付きパイプ等の管理接続) へのアクセスを記録するか。ノイズが多いため既定は無効。</summary>
    public bool IncludeIpcShare { get; set; } = false;

    /// <summary>
    /// 属性読み取り等のみのアクセス (ReadAttributes / ReadControl など) を記録しない。
    /// エクスプローラーで共有を開くだけで大量に発生するため既定で有効。
    /// データの読み書き・削除を伴うアクセスは常に記録される。
    /// </summary>
    public bool IgnoreAttributeOnlyAccess { get; set; } = true;
}
