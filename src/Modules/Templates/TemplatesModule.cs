using Clinkon1C.Core;

namespace Clinkon1C.Modules.Templates;

public class TemplateEntry
{
    public string UserName  { get; set; } = "";  // пользователь или "Общие"
    public string Name      { get; set; } = "";  // имя файла / папки шаблона
    public string Path      { get; set; } = "";  // полный путь
    public long   SizeBytes { get; set; }
    public bool   IsNetwork { get; set; }         // источник — сетевой путь (UNC или маппинг сетевого диска)
}

/// <summary>
/// Модуль шаблонов конфигурации 1С.
///
/// Пути берём из ConfigurationTemplatesLocation в 1CEStart.cfg:
///   %ALLUSERSPROFILE%\1C\1CEStart\1CEStart.cfg  — системный (для всех пользователей)
///   %APPDATA%\1C\1CEStart\1CEStart.cfg          — пользовательский
///
/// Если ни один файл не задал путь → фолбэк на %APPDATA%\1C\1cv8*\tmplts\
///
/// Каждый непосредственный элемент директории шаблонов = отдельный шаблон.
/// </summary>
public class TemplatesModule
{
    private readonly List<TemplateEntry> _entries = new List<TemplateEntry>();

    public IReadOnlyList<TemplateEntry> Entries => _entries;
    public long TotalSize { get; private set; }

    public void Refresh(Action<string>? progress = null)
    {
        _entries.Clear();
        TotalSize = 0;

        var profiles = ProfileFinder.FindProfiles();
        var dirs     = CollectTemplateDirs(profiles);

        Logger.Info($"TemplatesModule: найдено {dirs.Count} директорий шаблонов");

        int idx = 0;
        foreach (var (dir, label) in dirs)
        {
            idx++;
            progress?.Invoke($"[{idx}/{dirs.Count}] Шаблоны: {label}...");
            ScanDir(dir, label);
        }

        Logger.Info($"TemplatesModule: {_entries.Count} шаблонов, {SafeDelete.FormatSize(TotalSize)}");
    }

    // ── Сбор директорий ──────────────────────────────────────────────────────

    private static List<(string Dir, string Label)> CollectTemplateDirs(List<UserProfile> profiles)
    {
        var result = new List<(string, string)>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Кэш Directory.Exists() по полному пути. На терминальном сервере с
        // десятками профилей один и тот же сетевой путь ConfigurationTemplatesLocation
        // (например общая папка обмена) обычно прописан у КАЖДОГО профиля —
        // без кэша это N лишних сетевых обращений к одной и той же папке
        // (наблюдали 54 проверки на 4 уникальных пути).
        var existsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string rawPath, string label)
        {
            try
            {
                var full = Path.GetFullPath(rawPath);
                if (!existsCache.TryGetValue(full, out var exists))
                {
                    exists = Directory.Exists(full);
                    existsCache[full] = exists;
                }
                if (exists && seen.Add(full))
                    result.Add((full, label));
            }
            catch { }
        }

        // 1. Системный 1CEStart.cfg — ConfigurationTemplatesLocation для всех пользователей
        foreach (var p in CfgHelper.GetValues(CfgHelper.AllUsersPath, "ConfigurationTemplatesLocation"))
        {
            Logger.Info($"TemplatesModule: ConfigurationTemplatesLocation (системный): {p}");
            TryAdd(p, "Общие");
        }

        // 2. Пользовательские 1CEStart.cfg
        foreach (var profile in profiles)
        {
            var cfgPath = CfgHelper.UserPath(profile.AppData);
            foreach (var p in CfgHelper.GetValues(cfgPath, "ConfigurationTemplatesLocation"))
            {
                Logger.Info($"TemplatesModule: ConfigurationTemplatesLocation [{profile.UserName}]: {p}");
                TryAdd(p, profile.UserName);
            }
        }

        // 3. Фолбэк: стандартное расположение %APPDATA%\1C\1cv8*\tmplts\
        //    используется если ConfigurationTemplatesLocation нигде не задан
        if (result.Count == 0)
        {
            Logger.Info("TemplatesModule: ConfigurationTemplatesLocation не найден, используем фолбэк tmplts");
            foreach (var profile in profiles)
            {
                var appData1C = Path.Combine(profile.AppData, "1C");
                if (!Directory.Exists(appData1C)) continue;

                foreach (var v8dir in Directory.GetDirectories(appData1C, "1cv8*", SearchOption.TopDirectoryOnly))
                    TryAdd(Path.Combine(v8dir, "tmplts"), profile.UserName);
            }
        }

        return result;
    }

    // ── Сканирование директории ───────────────────────────────────────────────

    private void ScanDir(string dir, string label)
    {
        bool isNetwork = IsNetworkPath(dir);
        try
        {
            foreach (var item in Directory.GetFileSystemEntries(dir))
            {
                var measured = SafeDelete.Measure(item);
                _entries.Add(new TemplateEntry
                {
                    UserName  = label,
                    Name      = System.IO.Path.GetFileName(item),
                    Path      = item,
                    SizeBytes = measured.size,
                    IsNetwork = isNetwork
                });
                TotalSize += measured.size;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"TemplatesModule: не удалось прочитать {dir}: {ex.Message}");
        }
    }

    /// <summary>UNC-путь (\\server\share) или маппинг сетевого диска.</summary>
    public static bool IsNetworkPath(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }
}
