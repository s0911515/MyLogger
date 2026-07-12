namespace MyLogger.Config;

public sealed class MonitorOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>SQLite データベースファイルの格納先ディレクトリ。</summary>
    public string DataDirectory { get; set; } = @"C:\ProgramData\MyLogger\data";

    /// <summary>現在使用中の DB ファイル名。</summary>
    public string DatabaseFileName { get; set; } = "activity.db";

    /// <summary>
    /// 記録対象とするユーザー名 (bare または "DOMAIN\user") / ローカルグループ名の一覧。
    /// 空の場合は全ユーザーを監視対象とする。
    /// </summary>
    public List<string> MonitoredUsers { get; set; } = new();

    public RotationOptions Rotation { get; set; } = new();
    public FileWatcherOptions FileWatcher { get; set; } = new();
    public EtwOptions Etw { get; set; } = new();
    public SmbAuditOptions SmbAudit { get; set; } = new();
    public LogonOptions Logon { get; set; } = new();
}

/// <summary>DB ファイルのローテーション (世代管理) 設定。</summary>
public sealed class RotationOptions
{
    /// <summary>この日数を超えたら DB ファイルを切り替える。0 以下で無効。</summary>
    public int IntervalDays { get; set; } = 30;

    /// <summary>この容量 (MB) を超えたら DB ファイルを切り替える。0 以下で無効。</summary>
    public int MaxSizeMB { get; set; } = 500;

    /// <summary>退避 (archive) した DB ファイルの保持世代数。0 以下で無制限。</summary>
    public int RetentionGenerations { get; set; } = 12;
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

    /// <summary>
    /// 同一ファイル名の削除+作成がこのミリ秒数以内に発生した場合、フォルダをまたぐ移動として
    /// LOCAL_MOVE (移動元/移動先パス付き) に統合する。0 以下で無効
    /// (常に LOCAL_CREATE/LOCAL_DELETE のまま別々に記録する)。
    /// </summary>
    public int MoveCorrelationWindowMs { get; set; } = 500;

    /// <summary>
    /// Created の直後 (このミリ秒数以内) に同一パスへ Changed が発生した場合、記録しない。
    /// コピー等は OS 的に「ファイル作成」と「内容の書き込み」に分かれ、LOCAL_CREATE+LOCAL_CHANGE
    /// の2件に分裂して記録されてしまうため、直後の Changed は同一操作の一部とみなして抑制する。
    /// 0 以下で無効 (常に両方を別々に記録する)。
    /// </summary>
    public int CreateChangeSuppressWindowMs { get; set; } = 2000;
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

    /// <summary>
    /// 同一パス・同一操作種別 (SHARE_READ/SHARE_WRITE 等) の連続イベントをこの秒数内でまとめる。
    /// SMB は 1 回の実質的な操作に対しハンドルオープン等で複数回の監査イベントを生成するため、
    /// FileWatcherMonitor (①) と同様の重複排除を行う。0 以下で無効。
    /// </summary>
    public int DedupeSeconds { get; set; } = 2;
}

/// <summary>ユーザー認証 (ログイン/ログアウト) 監視 (セキュリティ監査ログ)。</summary>
public sealed class LogonOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>ログオン失敗 (イベント ID 4625) も記録するか。</summary>
    public bool IncludeFailedLogon { get; set; } = false;
}
