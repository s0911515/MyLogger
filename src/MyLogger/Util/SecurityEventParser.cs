using System.Xml;

namespace MyLogger.Util;

/// <summary>Windows セキュリティ監査ログの EventData を扱う共通ヘルパー。</summary>
public static class SecurityEventParser
{
    /// <summary>イベント XML の EventData/Data 要素を名前付きで取り出す。</summary>
    public static Dictionary<string, string> ParseEventData(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
        var nodes = doc.SelectNodes("//e:EventData/e:Data", ns);
        if (nodes is null) return result;
        foreach (XmlNode node in nodes)
        {
            var name = node.Attributes?["Name"]?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                result[name] = node.InnerText;
            }
        }
        return result;
    }
}
