using System.Text.RegularExpressions;

namespace Clinkon1C.Modules.TechLog;

public class TjEvent
{
    public DateTime Time       { get; set; }
    public string   EventType  { get; set; } = "";
    public long     DurationUs { get; set; }   // микросекунды
    public string   Context    { get; set; } = "";
    public string   Sql        { get; set; } = "";
    public string   Descr      { get; set; } = "";
    public long     MemoryBytes{ get; set; }
}

public static class TechLogParser
{
    // ТЖ-строка начинается с: digits:digits.digits,EventType,...
    // Пример: 00:05.123456,DBMSSQL,...   или   05.123456,EXCP,...
    private static readonly Regex RxStart = new Regex(
        @"^(?:\d+:)?\d+\.\d+,(\w+),",
        RegexOptions.Compiled);

    private static readonly Regex RxDuration = new Regex(
        @",Duration=(\d+)",
        RegexOptions.Compiled);

    private static readonly Regex RxContext = new Regex(
        @",Context=""([^""]{0,200})",
        RegexOptions.Compiled);

    private static readonly Regex RxSql = new Regex(
        @",(?:Sql|Txt)=""([^""]{0,200})",
        RegexOptions.Compiled);

    private static readonly Regex RxDescr = new Regex(
        @",Descr=""([^""]{0,200})",
        RegexOptions.Compiled);

    private static readonly Regex RxMemory = new Regex(
        @",Memory=(\d+)",
        RegexOptions.Compiled);

    // Имя файла: {name}_{pid}_{YYYYMMDDHHMM}.log или {name}_{pid}_{YYYYMMDDHH}.log
    private static readonly Regex RxFileName = new Regex(
        @"_(\d{10,12})\.log$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (List<TjEvent> Events, DateTime From, DateTime To) Parse(
        string location, int maxHours)
    {
        var events = new List<TjEvent>();
        var from   = DateTime.MaxValue;
        var to     = DateTime.MinValue;

        if (!Directory.Exists(location))
            return (events, DateTime.Now, DateTime.Now);

        var cutoff = DateTime.Now.AddHours(-maxHours);

        string[] files;
        try { files = Directory.GetFiles(location, "*.log", SearchOption.AllDirectories); }
        catch { return (events, DateTime.Now, DateTime.Now); }

        foreach (var file in files)
        {
            var ft = ParseFileTime(file);
            if (ft < cutoff) continue;

            if (ft < from) from = ft;
            if (ft > to)   to   = ft;

            try { ParseFile(file, ft, events); }
            catch { }

            // Предохранитель: не более 100 000 событий
            if (events.Count >= 100_000) break;
        }

        if (from == DateTime.MaxValue) from = DateTime.Now;
        if (to   == DateTime.MinValue) to   = DateTime.Now;

        return (events, from, to);
    }

    private static DateTime ParseFileTime(string path)
    {
        var m = RxFileName.Match(path);
        if (!m.Success) return File.GetLastWriteTime(path);

        var ts = m.Groups[1].Value;

        if (ts.Length == 12 &&
            DateTime.TryParseExact(ts, "yyyyMMddHHmm",
                null, System.Globalization.DateTimeStyles.None, out var dt12))
            return dt12;

        if (ts.Length == 10 &&
            DateTime.TryParseExact(ts, "yyyyMMddHH",
                null, System.Globalization.DateTimeStyles.None, out var dt10))
            return dt10;

        return File.GetLastWriteTime(path);
    }

    private static void ParseFile(string path, DateTime baseTime, List<TjEvent> events)
    {
        // Открываем с ReadWrite share — 1С может держать файл открытым
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8))
            text = sr.ReadToEnd();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '{') continue;  // пропускаем заголовок файла

            var m = RxStart.Match(line);
            if (!m.Success) continue;

            var dur = RxDuration.Match(line);

            var ev = new TjEvent
            {
                Time       = baseTime,
                EventType  = m.Groups[1].Value,
                DurationUs = dur.Success && long.TryParse(dur.Groups[1].Value, out var d) ? d : 0,
            };

            var ctx   = RxContext.Match(line);
            if (ctx.Success)    ev.Context     = ctx.Groups[1].Value.Trim();

            var sql   = RxSql.Match(line);
            if (sql.Success)    ev.Sql         = sql.Groups[1].Value.Trim();

            var descr = RxDescr.Match(line);
            if (descr.Success)  ev.Descr       = descr.Groups[1].Value.Trim();

            var mem   = RxMemory.Match(line);
            if (mem.Success && long.TryParse(mem.Groups[1].Value, out var mb))
                ev.MemoryBytes = mb;

            events.Add(ev);
        }
    }
}
