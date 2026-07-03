using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace MyLogger.Util;

public enum PathTarget
{
    Unknown,
    Fixed,
    Network,
    Removable,
}

/// <summary>
/// パスが「ローカル固定ディスク / ネットワーク / リムーバブル」のどれを指すかを判定し、
/// ETW が返す NT デバイスパスや、割り当て済みネットワークドライブを UNC パスへ正規化する。
/// </summary>
public static class PathClassifier
{
    // ドライブ種別のキャッシュ (USB の抜き差しがあるため TTL 付き)
    private static readonly ConcurrentDictionary<char, (DriveType Type, DateTime CachedAt)> DriveTypeCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// ETW から得たパスを正規化する。
    /// "\Device\Mup\server\share\..." → "\\server\share\..."
    /// "\Device\LanmanRedirector\..." → "\\..."
    /// ドライブレター形式はそのまま返す。
    /// </summary>
    public static string Normalize(string path)
    {
        const string mup = @"\Device\Mup\";
        const string lanman = @"\Device\LanmanRedirector\";
        if (path.StartsWith(mup, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[mup.Length..];
            // \Device\Mup\;X:000000\server\share 形式 (割り当てドライブ経由) のセッション部を除去
            if (rest.StartsWith(';'))
            {
                var idx = rest.IndexOf('\\');
                if (idx >= 0) rest = rest[(idx + 1)..];
            }
            return @"\\" + rest;
        }
        if (path.StartsWith(lanman, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[lanman.Length..];
            if (rest.StartsWith(';'))
            {
                var idx = rest.IndexOf('\\');
                if (idx >= 0) rest = rest[(idx + 1)..];
            }
            return @"\\" + rest;
        }
        return path;
    }

    /// <summary>正規化済みパスの書き込み先種別を判定する。</summary>
    public static PathTarget Classify(string normalizedPath)
    {
        if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathTarget.Network;
        }
        // ETW 内部パス (\Device\HarddiskVolumeX など未変換のもの) はローカル扱い
        if (normalizedPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return PathTarget.Fixed;
        }
        if (normalizedPath.Length >= 2 && normalizedPath[1] == ':')
        {
            var letter = char.ToUpperInvariant(normalizedPath[0]);
            if (letter is < 'A' or > 'Z') return PathTarget.Unknown;
            return GetDriveType(letter) switch
            {
                DriveType.Network => PathTarget.Network,
                DriveType.Removable => PathTarget.Removable,
                DriveType.Fixed => PathTarget.Fixed,
                DriveType.CDRom => PathTarget.Removable,
                _ => PathTarget.Unknown,
            };
        }
        return PathTarget.Unknown;
    }

    /// <summary>
    /// ネットワークドライブのドライブレター付きパスを UNC パスに解決する。
    /// 解決できない場合は元のパスを返す。
    /// </summary>
    public static string ResolveMappedDrive(string path)
    {
        if (path.Length < 2 || path[1] != ':') return path;
        var drive = path[..2];
        var sb = new StringBuilder(1024);
        var size = sb.Capacity;
        if (WNetGetConnection(drive, sb, ref size) == 0)
        {
            return sb + path[2..];
        }
        return path;
    }

    private static DriveType GetDriveType(char letter)
    {
        var now = DateTime.UtcNow;
        if (DriveTypeCache.TryGetValue(letter, out var cached) && now - cached.CachedAt < CacheTtl)
        {
            return cached.Type;
        }
        var type = (DriveType)GetDriveTypeW($"{letter}:\\");
        DriveTypeCache[letter] = (type, now);
        return type;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetDriveTypeW(string lpRootPathName);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
