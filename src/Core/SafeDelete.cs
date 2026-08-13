namespace Clinkon1C.Core;

public record DeleteResult(int DeletedDirs, int DeletedFiles, long FreedBytes, List<string> Skipped, List<string> Errors);

public static class SafeDelete
{
    // Полный набор — для модулей Логи, Шаблоны (Phase 2)
    public static readonly string[] DefaultProtectedMasks =
        { "*.lic", "*.pfl", "*.usr", "1CV8Clnt.flt", "*.1CD", "*.dbf" };

    // Облегчённый набор — для кэша: внутри UUID-папок кэша *.1CD, *.pfl, *.usr, *.dbf
    // являются служебными файлами платформы (не базы/профили), безопасны для удаления.
    // Защищаем только *.lic (лицензии могут быть где угодно).
    public static readonly string[] CacheProtectedMasks =
        { "*.lic" };

    public static bool IsProtected(string path, string[]? masks = null)
    {
        var name = Path.GetFileName(path);
        foreach (var mask in masks ?? DefaultProtectedMasks)
        {
            if (mask.StartsWith("*"))
            {
                var ext = mask.Substring(1);
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else
            {
                if (string.Equals(name, mask, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    public static DeleteResult Delete(IEnumerable<string> paths, bool backup = false,
        string? backupRoot = null, string[]? protectedMasks = null)
    {
        var skipped = new List<string>();
        var errors = new List<string>();
        int dirs = 0, files = 0;
        long freed = 0;
        var masks = protectedMasks ?? DefaultProtectedMasks;

        var backupDir = backup && backupRoot != null
            ? Path.Combine(backupRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"))
            : null;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var r = DeleteDirectory(path, skipped, errors, backupDir, masks);
                dirs += r.dirs;
                files += r.files;
                freed += r.freed;
            }
            else if (File.Exists(path))
            {
                var r = DeleteFile(path, skipped, errors, backupDir, masks);
                files += r.files;
                freed += r.freed;
            }
        }

        Logger.Info($"Удалено: {dirs} папок, {files} файлов, освобождено {FormatSize(freed)}. Пропущено: {skipped.Count}.");
        return new DeleteResult(dirs, files, freed, skipped, errors);
    }

    private static (int dirs, int files, long freed) DeleteDirectory(
        string dir, List<string> skipped, List<string> errors, string? backupDir, string[] masks)
    {
        int dirs = 0, files = 0;
        long freed = 0;

        var info = new DirectoryInfo(dir);

        // Символическая ссылка — удаляем только ссылку
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            try { Directory.Delete(dir); dirs++; }
            catch (Exception ex) { errors.Add($"{dir}: {ex.Message}"); }
            return (dirs, files, freed);
        }

        foreach (var f in info.GetFiles("*", SearchOption.AllDirectories))
        {
            var r = DeleteFile(f.FullName, skipped, errors, backupDir, masks);
            files += r.files;
            freed += r.freed;
        }

        // Удаляем папки снизу вверх (только пустые после удаления файлов)
        foreach (var sub in info.GetDirectories("*", SearchOption.AllDirectories)
                               .OrderByDescending(d => d.FullName.Length))
        {
            try { sub.Delete(); dirs++; }
            catch { }
        }

        try { info.Delete(); dirs++; }
        catch (Exception ex) { errors.Add($"{dir}: {ex.Message}"); }

        return (dirs, files, freed);
    }

    private static (int files, long freed) DeleteFile(
        string path, List<string> skipped, List<string> errors, string? backupDir, string[] masks)
    {
        if (IsProtected(path, masks))
        {
            skipped.Add(path);
            Logger.Warn($"Защищённый файл пропущен: {path}");
            return (0, 0);
        }

        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }

        if (backupDir != null)
            BackupFile(path, backupDir);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                File.Delete(path);
                return (1, size);
            }
            catch (IOException) when (attempt < 3)
            {
                var blockers = RestartManagerHelper.GetBlockingProcesses(path);
                if (blockers.Count > 0)
                    Logger.Warn($"Заблокирован {path}: {string.Join(", ", blockers.Select(b => $"{b.ProcessName} (PID:{b.Pid})"))}");
                Thread.Sleep(2000);
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
                Logger.Error($"Ошибка удаления {path}: {ex.Message}");
                return (0, 0);
            }
        }

        errors.Add(path);
        return (0, 0);
    }

    private static void BackupFile(string path, string backupDir)
    {
        try
        {
            var rel = Path.GetFileName(path);
            var dest = Path.Combine(backupDir, rel);
            Directory.CreateDirectory(backupDir);
            File.Copy(path, dest, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Backup не удался для {path}: {ex.Message}");
        }
    }

    public static (long size, int files, int dirs) Measure(string path)
    {
        long size = 0; int files = 0, dirs = 0;
        try
        {
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                if (!di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // EnumerateFiles — потоково, по одному FileInfo за раз, а не
                    // GetFiles (материализует МАССИВ всех FileInfo сразу). Для
                    // кэша 1С с десятками тысяч мелких файлов и суммарным
                    // объёмом за 100 ГБ разница в пиковой памяти — заметная.
                    foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
                    { size += f.Length; files++; }
                    foreach (var _ in di.EnumerateDirectories("*", SearchOption.AllDirectories))
                        dirs++;
                }
            }
            else if (File.Exists(path))
            {
                size = new FileInfo(path).Length; files = 1;
            }
        }
        catch { }
        return (size, files, dirs);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
