using System.Text;
using Newtonsoft.Json;

namespace Flywheel.Services;

public static class SessionExtensions
{
    public static void SetObject<T>(this ISession session, string key, T value) =>
        session.SetString(key, JsonConvert.SerializeObject(value));

    public static T? GetObject<T>(this ISession session, string key)
    {
        var json = session.GetString(key);
        return string.IsNullOrWhiteSpace(json) ? default : JsonConvert.DeserializeObject<T>(json);
    }
}

public static class EvidenceBuilder
{
    public static object Build(Dictionary<string, string> csvTexts, int maxRowsPerFile = 200)
    {
        var files = new List<object>();

        foreach (var (name, text) in csvTexts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            while (lines.Count > 0 && lines[0].StartsWith('#')) lines.RemoveAt(0);
            if (lines.Count == 0) continue;

            var header = SplitCsvLine(lines[0]);
            var rows = new List<Dictionary<string, string>>();

            for (int i = 1; i < lines.Count && rows.Count < maxRowsPerFile; i++)
            {
                if (lines[i].StartsWith('#')) continue;
                var cols = SplitCsvLine(lines[i]);
                var obj = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < header.Count; c++)
                    obj[header[c]] = c < cols.Count ? cols[c] : "";
                rows.Add(obj);
            }

            files.Add(new { file = name, columns = header, rows });
        }

        return new { files };
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }
}
