using System.Text;
using System.Text.RegularExpressions;
using Clinkon1C.Core;

namespace Clinkon1C.Modules.Licenses;

public class LicenseEntry
{
    public string Name               { get; set; } = "";
    public string FileName           { get; set; } = ""; // имя .lic файла от ring
    public string LicenseType        { get; set; } = "";
    public string AssociationType    { get; set; } = ""; // Computer / HardwareProtectionKey
    public string GenerationDate     { get; set; } = "";
    public string ProductCode        { get; set; } = "";
    public string RegistrationNumber { get; set; } = "";
}

public class ActivateParams
{
    public string Company     { get; set; } = "";
    public string FirstName   { get; set; } = "";
    public string MiddleName  { get; set; } = "";
    public string LastName    { get; set; } = "";
    public string Email       { get; set; } = "";
    public string Country     { get; set; } = "";
    public string ZipCode     { get; set; } = "";
    public string Town        { get; set; } = "";
    public string Street      { get; set; } = "";
    public string House       { get; set; } = "";
    public string Serial      { get; set; } = "";
    public string Pin         { get; set; } = "";
    public string PreviousPin { get; set; } = "";
}

public class LicensesModule
{
    public string Name => "Лицензии";

    private List<LicenseEntry> _entries = new List<LicenseEntry>();
    public IReadOnlyList<LicenseEntry> Entries => _entries;

    // Стандартные пути хранения .lic файлов 1С
    private static readonly string[] LicDirs = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1C", "licenses"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1C", "1cv8", "lic"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1C", "1cv8", "lic"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1C", "1cv8", "lic"),
        @"C:\Program Files\1cv8\bin",
        @"C:\Program Files (x86)\1cv8\bin",
    };

    // ── Кэш деталей лицензий ────────────────────────────────────────────────
    // Ключ: имя лицензии (ring license list). Живёт всё время процесса.
    // Инвалидируется только для имён, которых не было в прошлом вызове (новые лицензии).
    // ring license info вызывается ровно один раз на лицензию за время жизни процесса.

    private static readonly Dictionary<string, LicenseEntry> _detailsCache
        = new(StringComparer.OrdinalIgnoreCase);

    // ── Загрузка ──────────────────────────────────────────────────────────────

    public void Refresh()
    {
        _entries.Clear();

        var (code, output) = RingHelper.RunLicense("list");
        if (code != 0 || string.IsNullOrWhiteSpace(output))
        {
            Logger.Warn($"LicensesModule: list → exit {code}: {output}");
            return;
        }

        int fromCache = 0, scanned = 0;

        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;

            string name;
            string fileName = "";

            // Ищем блок (имя файла: "FILE.lic") в любом месте строки
            var fm = Regex.Match(t,
                @"\((?:имя файла|file name)\s*:\s*[""']?([^""')]+)[""']?\)",
                RegexOptions.IgnoreCase);
            if (fm.Success)
            {
                fileName = fm.Groups[1].Value.Trim();
                // Имя — всё до блока с файлом
                name = t.Substring(0, fm.Index).Trim();
            }
            else
            {
                name = t;
            }

            // Убираем возможный префикс "Лицензия: " или "License: "
            var lp = Regex.Match(name, @"^(?:Лицензия|License)\s*:\s*", RegexOptions.IgnoreCase);
            if (lp.Success) name = name.Substring(lp.Length).Trim();

            if (string.IsNullOrEmpty(name)) continue;

            // Берём детали из кэша — ring license info вызывается ровно раз за процесс
            if (_detailsCache.TryGetValue(name, out var cached))
            {
                cached.FileName = fileName; // имя файла могло обновиться
                _entries.Add(cached);
                fromCache++;
            }
            else
            {
                var entry = new LicenseEntry { Name = name, FileName = fileName };
                FillDetails(entry);
                _detailsCache[name] = entry;
                _entries.Add(entry);
                scanned++;
            }
        }

        Logger.Info($"LicensesModule: {_entries.Count} лицензий (кэш: {fromCache}, ring info: {scanned})");
    }

    private static void FillDetails(LicenseEntry entry)
    {
        try
        {
            var (code, info) = RingHelper.RunLicense($"info --name \"{entry.Name}\"");
            if (code != 0) return;

            bool inTech = false;
            foreach (var line in info.Split('\n'))
            {
                var t = line.Trim();
                if (t == "TechnicalInfo:") { inTech = true; continue; }
                if (!inTech) continue;

                var sep = t.IndexOf(':');
                if (sep < 0) continue;
                var key = t.Substring(0, sep).Trim();
                var val = t.Substring(sep + 1).Trim();

                switch (key)
                {
                    case "LicenseType":                       entry.LicenseType        = val; break;
                    case "LicenseAssociationType":            entry.AssociationType    = val; break;
                    case "LicenseGenerationDate":             entry.GenerationDate     = val; break;
                    case "ProductCode":                       entry.ProductCode        = val; break;
                    case "DistributionKitRegistrationNumber": entry.RegistrationNumber = val; break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"LicensesModule.FillDetails [{entry.Name}]: {ex.Message}");
        }
    }

    // ── Чтение .lic файла напрямую ────────────────────────────────────────────

    /// <summary>Ищет .lic файл по имени файла в стандартных директориях 1С.</summary>
    public static string? FindLicFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        foreach (var dir in LicDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var direct = Path.Combine(dir, fileName);
                if (File.Exists(direct)) return direct;

                var found = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories)
                                     .FirstOrDefault();
                if (found != null) return found;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Ищет .lic файл: сначала по имени файла, потом — по рег. номеру из licenseName (суффикс после '-').
    /// </summary>
    public static string? FindLicFileByName(string licenseName, string fileName)
    {
        if (!string.IsNullOrEmpty(fileName))
        {
            var byFile = FindLicFile(fileName);
            if (byFile != null) return byFile;
        }

        // Суффикс имени лицензии = часть рег. номера
        var parts = licenseName.Split('-');
        var regSuffix = parts.Length > 0 ? parts[parts.Length - 1] : "";
        if (string.IsNullOrEmpty(regSuffix)) return null;

        foreach (var dir in LicDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var path in Directory.GetFiles(dir, "*.lic", SearchOption.AllDirectories))
                {
                    try
                    {
                        var text = File.ReadAllText(path, Encoding.GetEncoding(1251));
                        if (text.Contains(regSuffix)) return path;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>Парсит .lic файл (CP1251) в словарь "Ключ → Значение".</summary>
    public static Dictionary<string, string> ReadLicFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Пробуем UTF-8 (современные .lic); если первая строка не читается — CP1251
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, new UTF8Encoding(false, true)); // throwOnInvalid=true
            }
            catch (DecoderFallbackException)
            {
                lines = File.ReadAllLines(path, Encoding.GetEncoding(1251));
            }

            foreach (var line in lines)
            {
                var sep = line.IndexOf(':');
                if (sep <= 0) continue;
                var key = line.Substring(0, sep).Trim();
                var val = line.Substring(sep + 1).Trim();
                if (string.IsNullOrEmpty(key)) continue;
                // Пропускаем строки цифровой подписи: Base64-хэш с пустым значением
                if (string.IsNullOrEmpty(val) && Regex.IsMatch(key, @"^[A-Za-z0-9+/]+=*$"))
                    continue;
                result[key] = val;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"LicensesModule.ReadLicFile [{path}]: {ex.Message}");
        }
        return result;
    }

    // ── Операции ──────────────────────────────────────────────────────────────

    public string GetFullInfo(string name)
    {
        var (_, output) = RingHelper.RunLicense($"info --name \"{name}\"");
        return string.IsNullOrWhiteSpace(output) ? "(нет данных)" : output;
    }

    public (bool Ok, string Message) Validate(string name)
    {
        var (code, output) = RingHelper.RunLicense($"validate --name \"{name}\"");
        return (code == 0, output);
    }

    public (bool Ok, string Message) Remove(string name)
    {
        var (code, output) = RingHelper.RunLicense($"remove -name \"{name}\"");
        return (code == 0, output);
    }

    public (bool Ok, string Message) Activate(ActivateParams p)
    {
        var args = new StringBuilder("activate");

        if (!string.IsNullOrEmpty(p.Company))
            args.Append($" --company \"{Esc(p.Company)}\"");
        if (!string.IsNullOrEmpty(p.FirstName))
            args.Append($" --first-name \"{Esc(p.FirstName)}\"");
        if (!string.IsNullOrEmpty(p.MiddleName))
            args.Append($" --middle-name \"{Esc(p.MiddleName)}\"");
        if (!string.IsNullOrEmpty(p.LastName))
            args.Append($" --last-name \"{Esc(p.LastName)}\"");
        if (!string.IsNullOrEmpty(p.Email))
            args.Append($" --email \"{Esc(p.Email)}\"");

        args.Append($" --country \"{Esc(p.Country)}\"");
        args.Append($" --zip-code \"{Esc(p.ZipCode)}\"");
        args.Append($" --town \"{Esc(p.Town)}\"");
        args.Append($" --street \"{Esc(p.Street)}\"");
        args.Append($" --house \"{Esc(p.House)}\"");
        args.Append($" --serial \"{Esc(p.Serial)}\"");
        args.Append($" --pin \"{Esc(p.Pin)}\"");
        if (!string.IsNullOrEmpty(p.PreviousPin))
            args.Append($" --previous-pin \"{Esc(p.PreviousPin)}\"");

        var (code, output) = RingHelper.RunLicense(args.ToString());
        return (code == 0, string.IsNullOrWhiteSpace(output) ? (code == 0 ? "Успешно" : "Ошибка") : output);
    }

    private static string Esc(string s) => s.Replace("\"", "\\\"");
}
