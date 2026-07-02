using System.Diagnostics;
using Clinkon1C.Modules.EventLog;

namespace Clinkon1C.Modules.Journal;

public static class LogFileReader
{
    private static string LogDir => Path.Combine(
        Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? ".") ?? ".",
        "clinkon1c.logs");

    public static List<LogEntry1C> Read(int maxEntries = 500)
    {
        var files = new List<string>();

        var current = Path.Combine(LogDir, "clinkon.log");
        if (File.Exists(current)) files.Add(current);

        if (Directory.Exists(LogDir))
        {
            var rotated = Directory.GetFiles(LogDir, "clinkon_????????_??????.log");
            // сортируем по имени убыв., берём последние 5
            System.Array.Sort(rotated);
            System.Array.Reverse(rotated);
            int take = Math.Min(5, rotated.Length);
            for (int i = 0; i < take; i++) files.Add(rotated[i]);
        }

        var result = new List<LogEntry1C>();
        foreach (var file in files)
        {
            try
            {
                string text;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8))
                    text = sr.ReadToEnd();

                foreach (var raw in text.Split('\n'))
                {
                    var e = ParseLine(raw.TrimEnd('\r'));
                    if (e != null) result.Add(e);
                }
            }
            catch { }
        }

        // Сортируем от новых к старым, лимит
        result.Sort((a, b) => b.TimeGenerated.CompareTo(a.TimeGenerated));
        if (result.Count > maxEntries) result.RemoveRange(maxEntries, result.Count - maxEntries);
        return result;
    }

    // Формат Logger.Write: "yyyy-MM-dd HH:mm:ss  {level,-7}  {source,-16}  {message}"
    //                       [0..18]  19 [21..27]  28 [30..45]  46 [48..]
    private static LogEntry1C? ParseLine(string line)
    {
        if (line.Length < 48) return null;
        if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                null, System.Globalization.DateTimeStyles.None, out var dt))
            return null;

        var levelRaw = line.Substring(21, 7).TrimEnd();
        var source   = line.Substring(30, 16).TrimEnd();
        var message  = line.Length > 48 ? line.Substring(48) : "";

        // Нормализуем к русским меткам уровня (как в EventLogModule)
        var level = levelRaw switch
        {
            "INFO"  => "ИНФО   ",
            "WARN"  => "ПРЕДУПР",
            "ERROR" => "ОШИБКА ",
            _       => levelRaw.PadRight(7),
        };

        return new LogEntry1C
        {
            TimeGenerated = dt,
            Level         = level,
            Source        = source,
            EventId       = 0,
            Message       = message,
        };
    }
}
