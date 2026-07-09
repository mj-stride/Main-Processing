namespace Ttds.Shared;

public static class CsvUtils
{
    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool needsQuotes = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        return needsQuotes ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}