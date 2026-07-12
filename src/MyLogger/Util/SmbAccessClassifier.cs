namespace MyLogger.Util;

/// <summary>SMB 監査イベント (5145) のデコード済みアクセス権文字列を分類するヘルパー。</summary>
public static class SmbAccessClassifier
{
    /// <summary>データの書き込み・追記・削除・属性変更を含むアクセスかどうか。</summary>
    public static bool IsWriteAccess(string? access)
    {
        if (string.IsNullOrEmpty(access)) return false;
        return access.Contains("WriteData", StringComparison.OrdinalIgnoreCase)
            || access.Contains("AppendData", StringComparison.OrdinalIgnoreCase)
            || access.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || access.Contains("WriteAttributes", StringComparison.OrdinalIgnoreCase);
    }
}
