using System.Xml.Linq;
using Clinkon1C.Core;

namespace Clinkon1C.Modules.TechLog;

public enum TjPreset { Errors, Locks, SlowDb, SlowCalls, Performance, Disabled, Unknown }

public class TjConfig
{
    public bool     IsEnabled    { get; set; }
    public TjPreset Preset       { get; set; } = TjPreset.Disabled;
    public string   Location     { get; set; } = @"C:\Logs\1C\TJ";
    public int      HistoryHours { get; set; } = 24;
    public int      ThresholdMs  { get; set; } = 5000;
    public string   ConfigPath   { get; set; } = "";
    public long     LogSizeBytes { get; set; }
}

public class TechLogModule
{
    public TjConfig Config { get; private set; } = new();

    public void Refresh()
    {
        Config = ReadStatus();
        Logger.Info($"TechLog: статус={Config.Preset}, включён={Config.IsEnabled}");
    }

    // ── Статус из logcfg.xml ─────────────────────────────────────────────────

    private static TjConfig ReadStatus()
    {
        var cfg  = new TjConfig();
        var path = FindConfigPath();
        if (path == null) return cfg;

        cfg.ConfigPath = path;
        try
        {
            var doc = XDocument.Load(path);
            var ns  = "http://v8.1c.ru/v8/tech-log";
            var log = doc.Root?.Element(XName.Get("log", ns));

            if (log == null) { cfg.IsEnabled = false; return cfg; }

            cfg.Location     = log.Attribute("location")?.Value ?? cfg.Location;
            cfg.IsEnabled    = true;

            if (int.TryParse(log.Attribute("history")?.Value, out var h))
                cfg.HistoryHours = h;

            // Собираем множество имён событий
            var events     = log.Elements(XName.Get("event", ns)).ToList();
            var eventNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in events)
                foreach (var eq in ev.Elements(XName.Get("eq", ns)))
                    if (eq.Attribute("property")?.Value == "Name")
                    {
                        var v = eq.Attribute("value")?.Value;
                        if (!string.IsNullOrEmpty(v)) eventNames.Add(v);
                    }

            // Порог из первого <gt property="Duration">
            var gtEl = events
                .SelectMany(e => e.Elements(XName.Get("gt", ns)))
                .FirstOrDefault(e => e.Attribute("property")?.Value == "Duration");
            if (gtEl != null && long.TryParse(gtEl.Attribute("value")?.Value, out var tus))
                cfg.ThresholdMs = (int)(tus / 1000);

            cfg.Preset    = DetectPreset(eventNames);
            cfg.IsEnabled = cfg.Preset != TjPreset.Disabled && cfg.Preset != TjPreset.Unknown;
        }
        catch (Exception ex)
        {
            Logger.Warn($"TechLog: не удалось прочитать {path}: {ex.Message}");
        }

        // Объём логов
        cfg.LogSizeBytes = GetDirSize(cfg.Location);
        return cfg;
    }

    private static TjPreset DetectPreset(System.Collections.Generic.HashSet<string> names)
    {
        if (names.Count == 0) return TjPreset.Disabled;

        bool excp  = names.Contains("EXCP");
        bool lock_ = names.Contains("TLOCK") || names.Contains("TTIMEOUT");
        bool db    = names.Contains("DBMSSQL") || names.Contains("DBPOSTGRS");
        bool scall = names.Contains("SCALL");

        if (excp && lock_ && db)         return TjPreset.Performance;
        if (excp  && !lock_ && !db && !scall) return TjPreset.Errors;
        if (lock_  && !excp && !db && !scall) return TjPreset.Locks;
        if (db     && !excp && !lock_ && !scall) return TjPreset.SlowDb;
        if (scall  && !excp && !lock_ && !db) return TjPreset.SlowCalls;

        return TjPreset.Unknown;
    }

    // ── Поиск logcfg.xml ─────────────────────────────────────────────────────

    public static string? FindConfigPath()
    {
        foreach (var root in new[] {
            @"C:\Program Files\1cv8",
            @"C:\Program Files (x86)\1cv8" })
        {
            if (!Directory.Exists(root)) continue;
            var dirs = Directory.GetDirectories(root);
            System.Array.Sort(dirs);
            System.Array.Reverse(dirs);
            foreach (var ver in dirs)
            {
                var p = Path.Combine(ver, "bin", "conf", "logcfg.xml");
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    public static string? FindOrCreateConfigPath()
    {
        var existing = FindConfigPath();
        if (existing != null) return existing;

        // Найти последнюю установленную версию без файла
        foreach (var root in new[] {
            @"C:\Program Files\1cv8",
            @"C:\Program Files (x86)\1cv8" })
        {
            if (!Directory.Exists(root)) continue;
            var dirs = Directory.GetDirectories(root);
            System.Array.Sort(dirs);
            System.Array.Reverse(dirs);
            foreach (var ver in dirs)
            {
                var confDir = Path.Combine(ver, "bin", "conf");
                if (Directory.Exists(confDir))
                    return Path.Combine(confDir, "logcfg.xml");
            }
        }
        return null;
    }

    // ── Запись конфига ───────────────────────────────────────────────────────

    public static void WritePreset(TjPreset preset, string location,
        int historyHours, int thresholdMs, string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? ".");
        var xml = preset == TjPreset.Disabled
            ? DisabledXml()
            : GenerateXml(preset, location, historyHours, (long)thresholdMs * 1000);

        File.WriteAllText(configPath, xml, System.Text.Encoding.UTF8);
        Logger.Info($"TechLog: записан пресет {preset} в {configPath}");
    }

    private static string DisabledXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<config xmlns=\"http://v8.1c.ru/v8/tech-log\"/>\n";

    private static string GenerateXml(TjPreset preset, string location,
        int historyHours, long thresholdUs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<config xmlns=\"http://v8.1c.ru/v8/tech-log\">");
        sb.AppendLine($"  <log location=\"{location}\" history=\"{historyHours}\">");

        switch (preset)
        {
            case TjPreset.Errors:
                sb.AppendLine("    <event>");
                sb.AppendLine("      <eq property=\"Name\" value=\"EXCP\"/>");
                sb.AppendLine("    </event>");
                break;

            case TjPreset.Locks:
                sb.AppendLine("    <event>");
                sb.AppendLine("      <eq property=\"Name\" value=\"TLOCK\"/>");
                sb.AppendLine($"      <gt property=\"Duration\" value=\"{thresholdUs}\"/>");
                sb.AppendLine("    </event>");
                sb.AppendLine("    <event>");
                sb.AppendLine("      <eq property=\"Name\" value=\"TTIMEOUT\"/>");
                sb.AppendLine("    </event>");
                break;

            case TjPreset.SlowDb:
                foreach (var ev in new[] { "DBMSSQL", "DBPOSTGRS" })
                {
                    sb.AppendLine("    <event>");
                    sb.AppendLine($"      <eq property=\"Name\" value=\"{ev}\"/>");
                    sb.AppendLine($"      <gt property=\"Duration\" value=\"{thresholdUs}\"/>");
                    sb.AppendLine("    </event>");
                }
                break;

            case TjPreset.SlowCalls:
                sb.AppendLine("    <event>");
                sb.AppendLine("      <eq property=\"Name\" value=\"SCALL\"/>");
                sb.AppendLine($"      <gt property=\"Duration\" value=\"{thresholdUs}\"/>");
                sb.AppendLine("    </event>");
                break;

            case TjPreset.Performance:
                sb.AppendLine("    <event>");
                sb.AppendLine("      <eq property=\"Name\" value=\"EXCP\"/>");
                sb.AppendLine("    </event>");
                sb.AppendLine("    <event>");
                sb.AppendLine("      <eq property=\"Name\" value=\"TLOCK\"/>");
                sb.AppendLine($"      <gt property=\"Duration\" value=\"{thresholdUs}\"/>");
                sb.AppendLine("    </event>");
                sb.AppendLine("    <event>");
                sb.AppendLine("      <eq property=\"Name\" value=\"TTIMEOUT\"/>");
                sb.AppendLine("    </event>");
                foreach (var ev in new[] { "DBMSSQL", "DBPOSTGRS" })
                {
                    sb.AppendLine("    <event>");
                    sb.AppendLine($"      <eq property=\"Name\" value=\"{ev}\"/>");
                    sb.AppendLine($"      <gt property=\"Duration\" value=\"{thresholdUs}\"/>");
                    sb.AppendLine("    </event>");
                }
                break;
        }

        sb.AppendLine("  </log>");
        sb.AppendLine("</config>");
        return sb.ToString();
    }

    // ── Вспомогательные ──────────────────────────────────────────────────────

    private static long GetDirSize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long size = 0;
        try
        {
            foreach (var f in Directory.GetFiles(path, "*.log", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return size;
    }

    public static string PresetLabel(TjPreset p) => p switch
    {
        TjPreset.Errors      => "Только ошибки",
        TjPreset.Locks       => "Блокировки",
        TjPreset.SlowDb      => "Долгие запросы к СУБД",
        TjPreset.SlowCalls   => "Долгие серверные вызовы",
        TjPreset.Performance => "Производительность",
        TjPreset.Disabled    => "Выключен",
        _                    => "Неизвестно",
    };

    public static string FormatStatus(TjConfig cfg)
    {
        var sb = new System.Text.StringBuilder();
        if (string.IsNullOrEmpty(cfg.ConfigPath))
        {
            sb.AppendLine("logcfg.xml не найден.");
            sb.AppendLine("Возможная причина: платформа 1С не найдена в стандартном");
            sb.AppendLine(@"расположении (C:\Program Files\1cv8 или (x86)\1cv8).");
            sb.AppendLine();
            sb.AppendLine("Нажмите [C] чтобы создать конфиг.");
            return sb.ToString();
        }

        var status = cfg.IsEnabled ? "● ВКЛЮЧЁН" : "○ ВЫКЛЮЧЕН";
        var preset = cfg.IsEnabled ? $" — {PresetLabel(cfg.Preset)}" : "";
        sb.AppendLine($"  Статус:   {status}{preset}");
        sb.AppendLine($"  Конфиг:   {cfg.ConfigPath}");

        var loc     = string.IsNullOrEmpty(cfg.Location) ? "(не задана)" : cfg.Location;
        var logSize = cfg.LogSizeBytes > 0 ? $"   объём: {FormatSize(cfg.LogSizeBytes)}" : "";
        sb.AppendLine($"  Логи:     {loc}{logSize}");

        if (cfg.IsEnabled)
            sb.AppendLine($"  История:  {cfg.HistoryHours} ч   Порог: {cfg.ThresholdMs} мс");

        if (!cfg.IsEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("  Нажмите [C] чтобы включить.");
        }
        else if (!Directory.Exists(cfg.Location))
        {
            sb.AppendLine();
            sb.AppendLine("  Папка логов не существует. 1С создаст её при первом срабатывании.");
        }

        return sb.ToString();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024} КБ";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024 / 1024} МБ";
        return $"{bytes / 1024 / 1024 / 1024} ГБ";
    }
}
