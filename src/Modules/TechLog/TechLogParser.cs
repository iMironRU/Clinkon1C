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
    // ТЖ-строка начинается с: digits:digits.digits-N,EventType,...
    // "-N" — суффикс порядкового номера события внутри той же микросекунды
    // (0..N цифр, ставится 1С всегда, включая "-0"), например:
    //   00:58.622019-0,EXCP,4   или   02:34.622024-922016,EXCPCNTX,1
    // Раньше суффикс не учитывался — regex не совпадал НИ С ОДНОЙ реальной
    // строкой ТЖ, анализ всегда показывал "событий не найдено".
    private static readonly Regex RxStart = new Regex(
        @"^(?:\d+:)?\d+\.\d+-?\d*,(\w+),",
        RegexOptions.Compiled);

    private static readonly Regex RxDuration = new Regex(
        @",Duration=(\d+)",
        RegexOptions.Compiled);

    // Строковые свойства (Context/Descr/Sql/Txt) 1С заключает в ОДИНАРНЫЕ кавычки
    // (Descr='...'), а не в двойные — двойными оформлены только простые
    // идентификаторы вроде Srvr="..." /  Ref="...". Значение может занимать
    // НЕСКОЛЬКО физических строк (например Descr со стек-трейсом EXCP) —
    // ParseFile() склеивает такие строки в один блок до применения этих regex.
    //
    // Context — стек вызовов 1С (модуль : строка : код, вложенно) — на реальных
    // данных доходит до ~2600 символов (медиана ~1900), поэтому лимит намного
    // выше, чем у Descr/Sql (короткие однострочные тексты).
    private static readonly Regex RxContext = new Regex(
        @",Context='([^']{0,4000})",
        RegexOptions.Compiled);

    private static readonly Regex RxSql = new Regex(
        @",(?:Sql|Txt)='([^']{0,1000})",
        RegexOptions.Compiled);

    private static readonly Regex RxDescr = new Regex(
        @",Descr='([^']{0,1000})",
        RegexOptions.Compiled);

    private static readonly Regex RxMemory = new Regex(
        @",Memory=(\d+)",
        RegexOptions.Compiled);

    // Подтверждённая на реальных данных структура ТЖ 1С:
    //   <location>\<process>_<pid>\<YYMMDDHH>.log
    // Час записан прямо в ИМЕНИ ФАЙЛА (YY — двузначный год), без вложенной
    // папки — например "1C\TJ\rphost_5004\26080413.log" (2026-08-04 13:xx).
    private static readonly Regex RxHourFile = new Regex(
        @"(\d{8,10})\.log$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // На случай альтернативной раскладки с отдельной папкой на час
    // (не встречалась в реальных данных, оставлена как доп. fallback).
    private static readonly Regex RxHourFolder = new Regex(
        @"[\\/](\d{8,10})[\\/][^\\/]+\.log$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        // 1) Подтверждённый на реальных данных формат — час прямо в имени файла
        var hfile = RxHourFile.Match(path);
        if (hfile.Success)
        {
            var ts = hfile.Groups[1].Value;
            if (ts.Length == 10 &&
                DateTime.TryParseExact(ts, "yyyyMMddHH",
                    null, System.Globalization.DateTimeStyles.None, out var dtF4))
                return dtF4;
            if (ts.Length == 8 &&
                DateTime.TryParseExact(ts, "yyMMddHH",
                    null, System.Globalization.DateTimeStyles.None, out var dtF2))
                return dtF2;
        }

        // 2) Альтернативная раскладка — час из имени родительской папки
        var hf = RxHourFolder.Match(path);
        if (hf.Success)
        {
            var ts = hf.Groups[1].Value;
            if (ts.Length == 10 &&
                DateTime.TryParseExact(ts, "yyyyMMddHH",
                    null, System.Globalization.DateTimeStyles.None, out var dtY4))
                return dtY4;
            if (ts.Length == 8 &&
                DateTime.TryParseExact(ts, "yyMMddHH",
                    null, System.Globalization.DateTimeStyles.None, out var dtY2))
                return dtY2;
        }

        // 3) Старый формат — метка в самом имени файла (нестандартный <log>)
        var m = RxFileName.Match(path);
        if (m.Success)
        {
            var ts = m.Groups[1].Value;
            if (ts.Length == 12 &&
                DateTime.TryParseExact(ts, "yyyyMMddHHmm",
                    null, System.Globalization.DateTimeStyles.None, out var dt12))
                return dt12;
            if (ts.Length == 10 &&
                DateTime.TryParseExact(ts, "yyyyMMddHH",
                    null, System.Globalization.DateTimeStyles.None, out var dt10))
                return dt10;
        }

        // 4) Fallback — время изменения файла (может отставать для активно дозаписываемых файлов)
        return File.GetLastWriteTime(path);
    }

    private static void ParseFile(string path, DateTime baseTime, List<TjEvent> events)
    {
        // Открываем с ReadWrite share — 1С может держать файл открытым
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8))
            text = sr.ReadToEnd();

        // Одно логическое событие ТЖ может занимать НЕСКОЛЬКО физических строк —
        // например Descr у EXCP со стек-трейсом. Новое событие всегда начинается
        // со строки вида "чч:мм.сссссс-N,Тип,..." (см. RxStart); все строки ПОСЛЕ
        // нeё и ДО следующей такой строки — продолжение последнего свойства
        // (склеиваются переводом строки перед разбором свойств).
        var block = new System.Text.StringBuilder();
        bool hasBlock = false;

        void FlushBlock()
        {
            if (hasBlock) ParseBlock(block.ToString(), baseTime, events);
            block.Clear();
            hasBlock = false;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '{') continue;  // пропускаем заголовок файла

            if (RxStart.IsMatch(line))
            {
                FlushBlock();
                block.Append(line);
                hasBlock = true;
            }
            else if (hasBlock)
            {
                block.Append('\n').Append(line);
            }
        }
        FlushBlock();
    }

    private static void ParseBlock(string block, DateTime baseTime, List<TjEvent> events)
    {
        var m = RxStart.Match(block);
        if (!m.Success) return;

        var dur = RxDuration.Match(block);

        var ev = new TjEvent
        {
            Time       = baseTime,
            EventType  = m.Groups[1].Value,
            DurationUs = dur.Success && long.TryParse(dur.Groups[1].Value, out var d) ? d : 0,
        };

        var ctx   = RxContext.Match(block);
        if (ctx.Success)    ev.Context     = ctx.Groups[1].Value.Trim();

        var sql   = RxSql.Match(block);
        if (sql.Success)    ev.Sql         = sql.Groups[1].Value.Trim();

        var descr = RxDescr.Match(block);
        if (descr.Success)  ev.Descr       = descr.Groups[1].Value.Trim();

        var mem   = RxMemory.Match(block);
        if (mem.Success && long.TryParse(mem.Groups[1].Value, out var mb))
            ev.MemoryBytes = mb;

        events.Add(ev);
    }
}
