namespace Clinkon1C.Modules.TechLog;

public static class TechLogAnalyzer
{
    public static string Format(TjConfig cfg, List<TjEvent> events, DateTime from, DateTime to)
    {
        if (!cfg.IsEnabled)
            return "(ТЖ выключен — нечего анализировать)";

        return cfg.Preset switch
        {
            TjPreset.Errors      => FormatErrors(cfg, events, from, to),
            TjPreset.Locks       => FormatLocks(cfg, events, from, to),
            TjPreset.SlowDb      => FormatSlowDb(cfg, events, from, to),
            TjPreset.SlowCalls   => FormatSlowCalls(cfg, events, from, to),
            TjPreset.Performance => FormatPerformance(cfg, events, from, to),
            _                    => $"(пресет «{TechLogModule.PresetLabel(cfg.Preset)}» — анализ не поддерживается)",
        };
    }

    // ── Ошибки (EXCP) ────────────────────────────────────────────────────────

    private static string FormatErrors(TjConfig cfg, List<TjEvent> all, DateTime from, DateTime to)
    {
        var events = Filter(all, "EXCP");
        var sb     = Header("ОШИБКИ", events.Count, from, to);

        if (events.Count == 0)
        {
            sb.AppendLine("  Ошибок не найдено.");
            return sb.ToString();
        }

        // Группировка по первой строке Descr
        var groups = new System.Collections.Generic.Dictionary<string, (int Count, DateTime Last)>(
            StringComparer.Ordinal);
        foreach (var e in events)
        {
            var key = Shorten(FirstLine(e.Descr), 80);
            if (groups.TryGetValue(key, out var g))
                groups[key] = (g.Count + 1, g.Last > e.Time ? g.Last : e.Time);
            else
                groups[key] = (1, e.Time);
        }

        // Топ-20 по частоте
        var top = new System.Collections.Generic.List<(string Key, int Count, DateTime Last)>();
        foreach (var kv in groups) top.Add((kv.Key, kv.Value.Count, kv.Value.Last));
        top.Sort((a, b) => b.Count.CompareTo(a.Count));
        if (top.Count > 20) top.RemoveRange(20, top.Count - 20);

        sb.AppendLine();
        sb.AppendLine("  Частота по тексту ошибки");
        sb.AppendLine("  " + new string('┄', 72));
        sb.AppendLine($"  {"#",-4} {"Кол-во",7}  {"Последний раз",-20}  Описание");
        sb.AppendLine("  " + new string('─', 72));

        for (int i = 0; i < top.Count; i++)
        {
            var (key, cnt, last) = top[i];
            sb.AppendLine($"  {i + 1,-4} {cnt,7}  {last:dd.MM HH:mm:ss}            {key}");
        }

        return sb.ToString();
    }

    // ── Блокировки (TLOCK / TTIMEOUT) ────────────────────────────────────────

    private static string FormatLocks(TjConfig cfg, List<TjEvent> all, DateTime from, DateTime to)
    {
        var tlocks   = Filter(all, "TLOCK");
        var timeouts = Filter(all, "TTIMEOUT");
        int total    = tlocks.Count + timeouts.Count;

        var sb = Header("БЛОКИРОВКИ", total, from, to);
        if (total == 0) { sb.AppendLine("  Блокировок не найдено."); return sb.ToString(); }

        long maxDur  = 0;
        foreach (var e in timeouts) if (e.DurationUs > maxDur) maxDur = e.DurationUs;
        foreach (var e in tlocks)   if (e.DurationUs > maxDur) maxDur = e.DurationUs;

        sb.AppendLine($"  Таймаутов (TTIMEOUT):  {timeouts.Count}   " +
                      $"Ожидания TLOCK: {tlocks.Count}   " +
                      $"Макс. ожидание: {DurLabel(maxDur)}");
        sb.AppendLine();

        // Почасовой срез
        var byHour = new System.Collections.Generic.SortedDictionary<string, int>();
        foreach (var e in timeouts)
        {
            var h = e.Time.ToString("dd.MM HH:00");
            byHour.TryGetValue(h, out var c);
            byHour[h] = c + 1;
        }

        if (byHour.Count > 0)
        {
            sb.AppendLine("  Таймауты по часам");
            sb.AppendLine("  " + new string('┄', 40));
            int maxPerHour = 0;
            foreach (var kv in byHour) if (kv.Value > maxPerHour) maxPerHour = kv.Value;
            foreach (var kv in byHour)
                sb.AppendLine($"  {kv.Key}    {kv.Value,5}  {Bar(kv.Value, maxPerHour, 20)}");
            sb.AppendLine();
        }

        // Топ-10 по длительности
        var topLocks = new System.Collections.Generic.List<TjEvent>(tlocks);
        topLocks.AddRange(timeouts);
        topLocks.Sort((a, b) => b.DurationUs.CompareTo(a.DurationUs));
        if (topLocks.Count > 10) topLocks.RemoveRange(10, topLocks.Count - 10);

        sb.AppendLine("  Топ-10 по длительности ожидания");
        sb.AppendLine("  " + new string('┄', 60));
        sb.AppendLine($"  {"#",-4} {"Тип",-9}  {"Ожидание",9}  Контекст");
        sb.AppendLine("  " + new string('─', 60));
        for (int i = 0; i < topLocks.Count; i++)
        {
            var e   = topLocks[i];
            var ctx = Shorten(FirstLine(e.Context), 40);
            sb.AppendLine($"  {i + 1,-4} {e.EventType,-9}  {DurLabel(e.DurationUs),9}  {ctx}");
        }

        return sb.ToString();
    }

    // ── Долгие запросы к СУБД (DBMSSQL / DBPOSTGRS) ─────────────────────────

    private static string FormatSlowDb(TjConfig cfg, List<TjEvent> all, DateTime from, DateTime to)
    {
        var events = new System.Collections.Generic.List<TjEvent>();
        events.AddRange(Filter(all, "DBMSSQL"));
        events.AddRange(Filter(all, "DBPOSTGRS"));

        var sb     = Header("ЗАПРОСЫ К СУБД", events.Count, from, to);
        if (events.Count == 0) { sb.AppendLine("  Событий не найдено."); return sb.ToString(); }

        long totalUs = 0;
        long maxUs   = 0;
        int  gt1s    = 0;
        int  gt5s    = 0;
        int  gt_thr  = 0;
        long thr     = (long)cfg.ThresholdMs * 1000;

        foreach (var e in events)
        {
            totalUs += e.DurationUs;
            if (e.DurationUs > maxUs) maxUs = e.DurationUs;
            if (e.DurationUs > 1_000_000)   gt1s++;
            if (e.DurationUs > 5_000_000)   gt5s++;
            if (e.DurationUs > thr)         gt_thr++;
        }
        long avgUs = events.Count > 0 ? totalUs / events.Count : 0;

        sb.AppendLine($"  Всего: {events.Count}   Средняя: {DurLabel(avgUs)}   Макс: {DurLabel(maxUs)}");
        sb.AppendLine($"  Долгих (>{cfg.ThresholdMs} мс): {gt_thr}   > 1 с: {gt1s}   > 5 с: {gt5s}");
        sb.AppendLine();

        // Гистограмма
        int lt1  = events.Count - gt1s;
        int b1_5 = gt1s - gt5s;
        sb.AppendLine("  Распределение по длительности");
        sb.AppendLine("  " + new string('┄', 55));
        AppendBar(sb, $"  < 1 с     {lt1,7}", lt1,  events.Count, 22);
        AppendBar(sb, $"  1–5 с     {b1_5,7}", b1_5, events.Count, 22);
        AppendBar(sb, $"  > 5 с     {gt5s,7}", gt5s, events.Count, 22);
        sb.AppendLine();

        // Топ-10 по длительности
        var top = new System.Collections.Generic.List<TjEvent>(events);
        top.Sort((a, b) => b.DurationUs.CompareTo(a.DurationUs));
        if (top.Count > 10) top.RemoveRange(10, top.Count - 10);

        sb.AppendLine("  Топ-10 по длительности");
        sb.AppendLine("  " + new string('┄', 76));
        sb.AppendLine($"  {"#",-4} {"Длит.",9}  {"Контекст",-30}  Запрос");
        sb.AppendLine("  " + new string('─', 76));
        for (int i = 0; i < top.Count; i++)
        {
            var e   = top[i];
            var ctx = Shorten(LastSeg(e.Context), 28);
            var sql = Shorten(e.Sql, 30);
            sb.AppendLine($"  {i + 1,-4} {DurLabel(e.DurationUs),9}  {ctx,-30}  {sql}");
        }

        return sb.ToString();
    }

    // ── Долгие серверные вызовы (SCALL) ──────────────────────────────────────

    private static string FormatSlowCalls(TjConfig cfg, List<TjEvent> all, DateTime from, DateTime to)
    {
        var events = Filter(all, "SCALL");
        var sb     = Header("СЕРВЕРНЫЕ ВЫЗОВЫ", events.Count, from, to);
        if (events.Count == 0) { sb.AppendLine("  Событий не найдено."); return sb.ToString(); }

        long maxUs  = 0;
        long maxMem = 0;
        long thr    = (long)cfg.ThresholdMs * 1000;
        int  gt_thr = 0;
        foreach (var e in events)
        {
            if (e.DurationUs > maxUs)  maxUs  = e.DurationUs;
            if (e.MemoryBytes > maxMem) maxMem = e.MemoryBytes;
            if (e.DurationUs > thr)    gt_thr++;
        }

        sb.AppendLine($"  Всего: {events.Count}   Долгих (>{cfg.ThresholdMs} мс): {gt_thr}   Макс: {DurLabel(maxUs)}");
        if (maxMem > 0)
            sb.AppendLine($"  Макс. память: {TechLogModule.FormatSize(maxMem)}");
        sb.AppendLine();

        var top = new System.Collections.Generic.List<TjEvent>(events);
        top.Sort((a, b) => b.DurationUs.CompareTo(a.DurationUs));
        if (top.Count > 10) top.RemoveRange(10, top.Count - 10);

        sb.AppendLine("  Топ-10 по длительности");
        sb.AppendLine("  " + new string('┄', 70));
        sb.AppendLine($"  {"#",-4} {"Длит.",9}  {"Память",10}  Контекст");
        sb.AppendLine("  " + new string('─', 70));
        for (int i = 0; i < top.Count; i++)
        {
            var e   = top[i];
            var mem = e.MemoryBytes > 0 ? TechLogModule.FormatSize(e.MemoryBytes) : "—";
            var ctx = Shorten(LastSeg(e.Context), 50);
            sb.AppendLine($"  {i + 1,-4} {DurLabel(e.DurationUs),9}  {mem,10}  {ctx}");
        }

        return sb.ToString();
    }

    // ── Производительность (сводка) ───────────────────────────────────────────

    private static string FormatPerformance(TjConfig cfg, List<TjEvent> all, DateTime from, DateTime to)
    {
        var excps    = Filter(all, "EXCP");
        var timeouts = Filter(all, "TTIMEOUT");
        var tlocks   = Filter(all, "TLOCK");
        var dbEvents = new System.Collections.Generic.List<TjEvent>();
        dbEvents.AddRange(Filter(all, "DBMSSQL"));
        dbEvents.AddRange(Filter(all, "DBPOSTGRS"));

        long thr    = (long)cfg.ThresholdMs * 1000;
        int  slowDb = 0;
        long maxDb  = 0;
        foreach (var e in dbEvents)
        {
            if (e.DurationUs > thr) slowDb++;
            if (e.DurationUs > maxDb) maxDb = e.DurationUs;
        }
        long maxLock = 0;
        foreach (var e in timeouts) if (e.DurationUs > maxLock) maxLock = e.DurationUs;

        var sb = Header("ПРОИЗВОДИТЕЛЬНОСТЬ — СВОДКА", all.Count, from, to);

        sb.AppendLine($"  Ошибок (EXCP):           {excps.Count,7}");
        sb.AppendLine($"  Таймауты блокировок:     {timeouts.Count,7}" +
                      (maxLock > 0 ? $"   (макс. ожидание: {DurLabel(maxLock)})" : ""));
        sb.AppendLine($"  Ожидания TLOCK:          {tlocks.Count,7}");
        sb.AppendLine($"  Долгих запросов к СУБД:  {slowDb,7}" +
                      (maxDb > 0 ? $"   (макс: {DurLabel(maxDb)})" : ""));

        if (excps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  ══ Топ-3 ошибки ══");
            var groups = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in excps)
            {
                var key = Shorten(FirstLine(e.Descr), 70);
                groups.TryGetValue(key, out var c);
                groups[key] = c + 1;
            }
            var topErr = new System.Collections.Generic.List<(string, int)>();
            foreach (var kv in groups) topErr.Add((kv.Key, kv.Value));
            topErr.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            for (int i = 0; i < Math.Min(3, topErr.Count); i++)
                sb.AppendLine($"  {topErr[i].Item2,5}×  {topErr[i].Item1}");
        }

        if (slowDb > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  ══ Топ-3 долгих запроса ══");
            var topDb = new System.Collections.Generic.List<TjEvent>(dbEvents);
            topDb.Sort((a, b) => b.DurationUs.CompareTo(a.DurationUs));
            for (int i = 0; i < Math.Min(3, topDb.Count); i++)
            {
                var e   = topDb[i];
                var ctx = Shorten(LastSeg(e.Context), 28);
                var sql = Shorten(e.Sql, 28);
                sb.AppendLine($"  {DurLabel(e.DurationUs),9}  {ctx,-30}  {sql}");
            }
        }

        return sb.ToString();
    }

    // ── Вспомогательные ──────────────────────────────────────────────────────

    private static System.Text.StringBuilder Header(
        string name, int count, DateTime from, DateTime to)
    {
        var sb     = new System.Text.StringBuilder();
        var period = from == to
            ? from.ToString("dd.MM HH:mm")
            : $"{from:dd.MM HH:mm} – {to:dd.MM HH:mm}";
        sb.AppendLine($"{name}  —  событий: {count}  период: {period}");
        sb.AppendLine(new string('═', 70));
        sb.AppendLine();
        return sb;
    }

    private static System.Collections.Generic.List<TjEvent> Filter(
        System.Collections.Generic.List<TjEvent> all, string type)
    {
        var result = new System.Collections.Generic.List<TjEvent>();
        foreach (var e in all)
            if (string.Equals(e.EventType, type, StringComparison.OrdinalIgnoreCase))
                result.Add(e);
        return result;
    }

    private static string DurLabel(long us)
    {
        if (us < 1_000)       return $"{us} мкс";
        if (us < 1_000_000)   return $"{us / 1000} мс";
        if (us < 60_000_000)  return $"{us / 1_000_000.0:F1} с";
        return $"{us / 60_000_000} мин {us / 1_000_000 % 60} с";
    }

    private static string Bar(int val, int max, int width)
    {
        if (max == 0) return "";
        int filled = width * val / max;
        return new string('█', filled) + new string('░', width - filled);
    }

    private static void AppendBar(System.Text.StringBuilder sb,
        string prefix, int val, int total, int width)
    {
        var pct  = total > 0 ? $"{100.0 * val / total:F1}%" : "0%";
        var bar  = Bar(val, total, width);
        sb.AppendLine($"{prefix}  {bar}  {pct,6}");
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int nl = s.IndexOf('\n');
        return nl > 0 ? s.Substring(0, nl).Trim() : s.Trim();
    }

    // Берём последний сегмент Context "Модуль.Метод:строка" → "Метод:строка"
    private static string LastSeg(string ctx)
    {
        if (string.IsNullOrEmpty(ctx)) return "";
        var line = FirstLine(ctx);
        int dot  = line.LastIndexOf('.');
        return dot >= 0 && dot < line.Length - 1 ? line.Substring(dot + 1) : line;
    }

    private static string Shorten(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        if (s.Length <= maxLen) return s;
        return s.Substring(0, maxLen - 1) + "…";
    }
}
