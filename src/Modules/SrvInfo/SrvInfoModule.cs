using System.Text.RegularExpressions;
using Clinkon1C.Core;

namespace Clinkon1C.Modules.SrvInfo;

public class LogPeriod
{
    public string   FileName { get; init; } = "";
    public DateTime Date     { get; init; }
    public long     Size     { get; init; }
    public bool     IsCurrent => FileName == "1Cv8";  // текущий активный период
}

public class BaseEntry
{
    public string         Uuid         { get; init; } = "";
    public string         Name         { get; init; } = "";
    public string         Description  { get; init; } = "";
    public string         DbType       { get; init; } = "";   // MSSQLServer / PostgreSQL / File / etc.
    public string         DbServer     { get; init; } = "";
    public string         DbName       { get; init; } = "";
    public string         ConnStr      { get; init; } = "";
    public bool           IsBlocked    { get; init; }
    public string         BlockReason  { get; init; } = "";
    public DateTime?      BlockedSince { get; init; }
    // Журнал регистрации
    public List<LogPeriod> LogPeriods  { get; set; } = new();
    public long            LogTotalBytes => LogPeriods.Sum(p => p.Size);
    public string?         LogDir       { get; set; }
}

public class ClusterEntry
{
    public string          Uuid       { get; init; } = "";
    public string          Name       { get; init; } = "";
    public int             Port       { get; init; }
    public string          ServerName { get; init; } = "";
    public string          RegPath    { get; init; } = "";   // полный путь к reg_NNNN
    public List<BaseEntry> Bases      { get; init; } = new();
}

public class SrvInfoModule
{
    // Стандартные пути srvinfo (1С кладёт их рядом с исполняемым файлом)
    private static readonly string[] SrvInfoRoots =
    {
        @"C:\Program Files\1cv8\srvinfo",
        @"C:\Program Files (x86)\1cv8\srvinfo",
        @"C:\1cv8\srvinfo",
    };

    public List<ClusterEntry> Clusters { get; private set; } = new();

    public void Refresh()
    {
        var result = new List<ClusterEntry>();
        foreach (var root in SrvInfoRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var regDir in Directory.GetDirectories(root, "reg_*"))
                {
                    var cluster = ParseRegDir(regDir);
                    if (cluster != null) result.Add(cluster);
                }
            }
            catch (Exception ex) { Logger.Warn($"SrvInfo: {root} → {ex.Message}"); }
        }
        Clusters = result;
        Logger.Info($"SrvInfo: {Clusters.Count} кластер(ов), {Clusters.Sum(c => c.Bases.Count)} баз");
    }

    // ── Чтение reg_NNNN ──────────────────────────────────────────────────────

    private static ClusterEntry? ParseRegDir(string regDir)
    {
        var clstoPath = Path.Combine(regDir, "1CV8Clsto.lst");
        if (!File.Exists(clstoPath)) return null;

        string text;
        try
        {
            using var fs = new FileStream(clstoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            text = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            Logger.Warn($"SrvInfo: не удалось прочитать {clstoPath}: {ex.Message}");
            return null;
        }

        var cluster = ParseCluster(text, regDir);
        if (cluster == null) return null;

        ScanLogSizes(cluster);
        return cluster;
    }

    // ── Парсинг 1CV8Clsto.lst ────────────────────────────────────────────────

    // Кластер: {UUID,"Имя",port,"Сервер",...}
    private static readonly Regex RxCluster = new Regex(
        @"\{([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})" +
        @",""([^""]*)""\s*,\s*(\d+)\s*,\s*""([^""]*)""",
        RegexOptions.Singleline);

    // База: {UUID,"Имя","Описание","СУБД","СерверБД","ИмяБД","Польз","...","КонстрСтроки",...
    private static readonly Regex RxBase = new Regex(
        @"\{([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})" +
        @",""([^""]*)"",""([^""]*)"",""([^""]*)"",""([^""]*)"",""([^""]*)"",""[^""]*"",""[^""]*"",""([^""]*)""",
        RegexOptions.Singleline);

    // Блокировка: {0,yyyyMMddHHmmss,yyyyMMddHHmmss,"причина","тип","...",0}
    // Дата 00010101000000 = не установлена
    private static readonly Regex RxLock = new Regex(
        @"\{0,(\d{14}),\d{14},""([^""]*)"",""([^""]*)""",
        RegexOptions.Singleline);

    private static ClusterEntry? ParseCluster(string text, string regDir)
    {
        // Кластер — первое совпадение
        var cm = RxCluster.Match(text);
        if (!cm.Success) return null;

        var cluster = new ClusterEntry
        {
            Uuid       = cm.Groups[1].Value.ToLower(),
            Name       = cm.Groups[2].Value,
            Port       = int.TryParse(cm.Groups[3].Value, out var p) ? p : 0,
            ServerName = cm.Groups[4].Value,
            RegPath    = regDir,
        };

        // Базы — все совпадения + ищем блокировку после каждого
        foreach (Match bm in RxBase.Matches(text))
        {
            var uuid = bm.Groups[1].Value.ToLower();

            // Блокировка: ищем следующий RxLock после позиции этой базы
            bool isBlocked = false;
            string blockReason = "";
            DateTime? blockedSince = null;
            var lm = RxLock.Match(text, bm.Index + bm.Length);
            if (lm.Success)
            {
                var dateStr = lm.Groups[1].Value;
                if (dateStr != "00010101000000" &&
                    DateTime.TryParseExact(dateStr, "yyyyMMddHHmmss",
                        null, System.Globalization.DateTimeStyles.None, out var dt))
                {
                    isBlocked    = true;
                    blockedSince = dt;
                    // Берём первую непустую из причины/типа
                    blockReason = lm.Groups[2].Value.Length > 0
                        ? lm.Groups[2].Value
                        : lm.Groups[3].Value;
                    // Убираем длинный текст с инструкциями
                    var nl = blockReason.IndexOf('\n');
                    if (nl > 0) blockReason = blockReason.Substring(0, nl).Trim();
                }
            }

            cluster.Bases.Add(new BaseEntry
            {
                Uuid         = uuid,
                Name         = bm.Groups[2].Value,
                Description  = bm.Groups[3].Value,
                DbType       = bm.Groups[4].Value,
                DbServer     = bm.Groups[5].Value,
                DbName       = bm.Groups[6].Value,
                ConnStr      = bm.Groups[7].Value,
                IsBlocked    = isBlocked,
                BlockReason  = blockReason,
                BlockedSince = blockedSince,
            });
        }

        return cluster;
    }

    // ── Объёмы журнала регистрации ───────────────────────────────────────────

    private static void ScanLogSizes(ClusterEntry cluster)
    {
        foreach (var b in cluster.Bases)
        {
            // Путь: reg_NNNN\{base_uuid}\1Cv8Log\
            var logDir = Path.Combine(cluster.RegPath, b.Uuid, "1Cv8Log");
            if (!Directory.Exists(logDir)) continue;

            b.LogDir = logDir;
            var periods = new List<LogPeriod>();

            try
            {
                foreach (var f in Directory.GetFiles(logDir))
                {
                    var name = Path.GetFileName(f);
                    if (name.EndsWith(".lgx", StringComparison.OrdinalIgnoreCase)) continue;

                    var info = new FileInfo(f);
                    DateTime date = DateTime.MinValue;
                    if (name != "1Cv8")
                        DateTime.TryParseExact(name, "yyyyMMddHHmmss",
                            null, System.Globalization.DateTimeStyles.None, out date);

                    periods.Add(new LogPeriod
                    {
                        FileName = name,
                        Date     = date,
                        Size     = info.Length,
                    });
                }
            }
            catch (Exception ex) { Logger.Warn($"SrvInfo: LogSizes [{b.Name}]: {ex.Message}"); }

            // Текущий период первым, потом по дате убыв.
            b.LogPeriods = periods
                .OrderByDescending(p => p.IsCurrent)
                .ThenByDescending(p => p.Date)
                .ToList();
        }
    }

    // ── Форматирование для TUI ────────────────────────────────────────────────

    public static string FormatReport(IEnumerable<ClusterEntry> clusters)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in clusters)
        {
            sb.AppendLine($"Кластер: {c.Name}  [{c.ServerName}:{c.Port}]");
            sb.AppendLine(new string('─', 60));

            if (c.Bases.Count == 0)
            {
                sb.AppendLine("  (базы не найдены в 1CV8Clsto.lst)");
            }
            else
            {
                foreach (var b in c.Bases)
                {
                    string lock_ = b.IsBlocked
                        ? $"  [■ БЛОК с {b.BlockedSince:yyyy-MM-dd HH:mm}]"
                        : "";
                    sb.AppendLine($"  {b.Name}{lock_}");
                    sb.AppendLine($"    СУБД:   {(string.IsNullOrEmpty(b.DbType) ? "—" : b.DbType)}  {b.DbServer}\\{b.DbName}");

                    if (b.LogPeriods.Count > 0)
                    {
                        string logSize = FormatSize(b.LogTotalBytes);
                        string periods = $"{b.LogPeriods.Count} период(ов)";
                        sb.AppendLine($"    ЖР:     {logSize}  ({periods})");
                        foreach (var lp in b.LogPeriods)
                        {
                            string label = lp.IsCurrent
                                ? "  текущий"
                                : $"  {lp.Date:yyyy-MM-dd HH:mm}";
                            sb.AppendLine($"      {lp.FileName,-20}{label,22}  {FormatSize(lp.Size),8}");
                        }
                    }
                    else sb.AppendLine("    ЖР:     не найден");

                    if (b.IsBlocked && !string.IsNullOrEmpty(b.BlockReason))
                        sb.AppendLine($"    Причина: {b.BlockReason}");

                    sb.AppendLine();
                }
            }
        }
        return sb.Length == 0 ? "(кластеров не обнаружено)" : sb.ToString();
    }

    // ── Удаление старых периодов ЖР ──────────────────────────────────────────

    public static (int Periods, long Bytes, List<string> Errors) DeleteOldPeriods(BaseEntry b)
    {
        int periods = 0;
        long bytes  = 0;
        var errors  = new List<string>();

        if (string.IsNullOrEmpty(b.LogDir)) return (0, 0, errors);

        foreach (var p in b.LogPeriods)
        {
            if (p.IsCurrent) continue;   // текущий период никогда не трогаем

            var main = Path.Combine(b.LogDir, p.FileName);
            var lgx  = Path.Combine(b.LogDir, p.FileName + ".lgx");
            try
            {
                if (File.Exists(main))
                {
                    bytes += new FileInfo(main).Length;
                    File.Delete(main);
                    periods++;
                    Logger.Info($"SrvInfo: удалён период ЖР {b.Name}\\{p.FileName}");
                }
                if (File.Exists(lgx))
                {
                    bytes += new FileInfo(lgx).Length;
                    File.Delete(lgx);
                }
            }
            catch (Exception ex)
            {
                var msg = $"{b.Name}\\{p.FileName}: {ex.Message}";
                errors.Add(msg);
                Logger.Error($"SrvInfo: ошибка удаления ЖР — {msg}");
            }
        }
        return (periods, bytes, errors);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024} КБ";
        return $"{bytes / 1024 / 1024} МБ";
    }
}
