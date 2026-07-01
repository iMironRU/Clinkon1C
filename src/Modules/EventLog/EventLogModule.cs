using Clinkon1C.Core;
using Diag = System.Diagnostics;

namespace Clinkon1C.Modules.EventLog;

public enum EventLogFilter { All, ErrorsWarnings, ErrorsOnly }

public class LogEntry1C
{
    public DateTime TimeGenerated { get; init; }
    public string   Level         { get; init; } = "";
    public string   Source        { get; init; } = "";
    public int      EventId       { get; init; }
    public string   Message       { get; init; } = "";
}

public class EventLogModule
{
    private static readonly string[] Sources = { "1C:Enterprise", "1CV8", "Clinkon1C" };
    public const int DefaultMax = 300;

    public List<LogEntry1C> Entries { get; private set; } = new();

    public void Load(int max = DefaultMax)
    {
        Entries = ReadEntries(max);
        Logger.Info($"EventLog: загружено {Entries.Count} записей 1С");
    }

    private static List<LogEntry1C> ReadEntries(int max)
    {
        var result = new List<LogEntry1C>();
        try
        {
            using var log = new Diag.EventLog("Application");
            var all = log.Entries;
            for (int i = all.Count - 1; i >= 0 && result.Count < max; i--)
            {
                Diag.EventLogEntry e;
                try { e = all[i]; } catch { continue; }

                if (!Sources.Any(s =>
                    string.Equals(s, e.Source, StringComparison.OrdinalIgnoreCase))) continue;

                result.Add(new LogEntry1C
                {
                    TimeGenerated = e.TimeGenerated,
                    Level         = LevelLabel(e.EntryType),
                    Source        = e.Source,
                    EventId       = e.EventID & 0xFFFF,
                    Message       = e.Message.Trim(),
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"EventLog.ReadEntries: {ex.Message}");
        }
        return result;
    }

    // 7 символов — совпадает с шириной level в Logger ("INFO   ", "WARN   ", "ERROR  ")
    private static string LevelLabel(Diag.EventLogEntryType t) => t switch
    {
        Diag.EventLogEntryType.Error       => "ОШИБКА ",
        Diag.EventLogEntryType.Warning     => "ПРЕДУПР",
        Diag.EventLogEntryType.Information => "ИНФО   ",
        _                                  => "?      ",
    };

    public static IEnumerable<LogEntry1C> Apply(IEnumerable<LogEntry1C> src, EventLogFilter f) => f switch
    {
        EventLogFilter.ErrorsOnly     => src.Where(e => e.Level.StartsWith("ОШИБКА")),
        EventLogFilter.ErrorsWarnings => src.Where(e => e.Level.StartsWith("ОШИБКА") || e.Level.StartsWith("ПРЕДУПР")),
        _                             => src,
    };

    public static string FilterLabel(EventLogFilter f) => f switch
    {
        EventLogFilter.ErrorsOnly     => "только ошибки",
        EventLogFilter.ErrorsWarnings => "ошибки + предупреждения",
        _                             => "все события",
    };

    public static EventLogFilter NextFilter(EventLogFilter f) => f switch
    {
        EventLogFilter.All            => EventLogFilter.ErrorsWarnings,
        EventLogFilter.ErrorsWarnings => EventLogFilter.ErrorsOnly,
        _                             => EventLogFilter.All,
    };

    // Формат колонок совпадает с Logger.Write:
    // datetime(19)  level(7)  source(16)  message
    // Итого до message: 19 + 2 + 7 + 2 + 16 + 2 = 48 символов
    private const int MsgCol = 48;

    public static string FormatEntries(IEnumerable<LogEntry1C> entries)
    {
        var indent = new string(' ', MsgCol);
        var sb     = new System.Text.StringBuilder();

        foreach (var e in entries)
        {
            var msgLines = e.Message
                .Replace("\r", "")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();

            // Для записей с EventId (не наши) добавляем #id перед первой строкой
            var prefix    = e.EventId > 0 ? $"#{e.EventId} " : "";
            var firstLine = msgLines.Length > 0 ? msgLines[0] : "";

            sb.Append(e.TimeGenerated.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.Append("  ");
            sb.Append(e.Level);            // 7 символов
            sb.Append("  ");
            sb.Append(e.Source.PadRight(16));
            sb.Append("  ");
            sb.Append(prefix);
            sb.AppendLine(firstLine);

            int shown = Math.Min(msgLines.Length, 6);
            for (int i = 1; i < shown; i++)
                sb.Append(indent).AppendLine(msgLines[i]);
            if (msgLines.Length > shown)
                sb.Append(indent).AppendLine("...");

            sb.AppendLine();
        }

        return sb.Length == 0 ? "(записей не найдено)" : sb.ToString();
    }
}
