using System.Diagnostics;
using Clinkon1C.Core;
using Clinkon1C.Modules.Bases;

namespace Clinkon1C.Modules.DT;

public record FileBase(string Name, string Path);

public static class DtModule
{
    // ── Поиск файловых баз ───────────────────────────────────────────────────

    public static List<FileBase> GetFileBases(IReadOnlyList<InfoBaseEntry> entries)
    {
        var result = new List<FileBase>();
        foreach (var e in entries)
        {
            if (!e.Connect.StartsWith("File=", StringComparison.OrdinalIgnoreCase)) continue;
            var path = ExtractPath(e.Connect);
            if (!string.IsNullOrEmpty(path))
                result.Add(new FileBase(e.Name, path));
        }
        return result;
    }

    // "File=C:\path;" или "File=\"C:\path with spaces\";" → "C:\path..."
    public static string ExtractPath(string connect)
    {
        int eq = connect.IndexOf('=');
        if (eq < 0) return "";

        var rest = connect.Substring(eq + 1).TrimStart();

        // Берём до первой точки с запятой (остальные параметры не нужны)
        int semi = rest.IndexOf(';');
        if (semi >= 0) rest = rest.Substring(0, semi).Trim();

        // Снимаем кавычки
        if (rest.Length >= 2 && rest[0] == '"' && rest[rest.Length - 1] == '"')
            rest = rest.Substring(1, rest.Length - 2);

        return rest;
    }

    // ── Поиск 1cv8.exe ───────────────────────────────────────────────────────

    public static string? Find1cv8()
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
                var exe = Path.Combine(ver, "bin", "1cv8.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    // ── Backup (DumpIB) ──────────────────────────────────────────────────────

    public static string? Backup(string basePath, string outputDt,
        string? user, string? password)
    {
        var exe = Find1cv8();
        if (exe == null) return "1cv8.exe не найден в стандартных путях установки 1С.";

        try { Directory.CreateDirectory(Path.GetDirectoryName(outputDt) ?? "."); }
        catch (Exception ex) { return $"Не удалось создать папку: {ex.Message}"; }

        var args = BuildArgs("DumpIB", basePath, outputDt, user, password);
        Logger.Info($"DT Backup: {basePath} → {outputDt}");
        return RunDesigner(exe, args);
    }

    // ── Restore (RestoreIB) ──────────────────────────────────────────────────

    public static string? Restore(string basePath, string sourceDt,
        string? user, string? password)
    {
        var exe = Find1cv8();
        if (exe == null) return "1cv8.exe не найден в стандартных путях установки 1С.";

        if (!File.Exists(sourceDt))
            return $"Файл не найден: {sourceDt}";

        var args = BuildArgs("RestoreIB", basePath, sourceDt, user, password);
        Logger.Info($"DT Restore: {sourceDt} → {basePath}");
        return RunDesigner(exe, args);
    }

    // ── Вспомогательные ──────────────────────────────────────────────────────

    private static string BuildArgs(string cmd, string basePath, string dtPath,
        string? user, string? password)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"DESIGNER /F \"{basePath}\" /{cmd} \"{dtPath}\"");
        if (!string.IsNullOrWhiteSpace(user))     sb.Append($" /N \"{user}\"");
        if (!string.IsNullOrWhiteSpace(password)) sb.Append($" /P \"{password}\"");
        sb.Append(" /DisableStartupMessages");
        return sb.ToString();
    }

    private static string? RunDesigner(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi)!;
            // DT операции могут занимать долго — 30 минут таймаут
            p.WaitForExit(30 * 60 * 1000);

            if (!p.HasExited)
            {
                try { p.Kill(); } catch { }
                return "Превышен таймаут 30 мин. Процесс завершён принудительно.";
            }

            if (p.ExitCode == 0)
            {
                Logger.Info($"DT: завершено успешно (код 0)");
                return null;   // успех
            }

            var stdout = p.StandardOutput.ReadToEnd().Trim();
            var stderr = p.StandardError.ReadToEnd().Trim();
            var detail = (stdout.Length > 0 ? stdout : stderr);
            if (detail.Length > 300) detail = detail.Substring(0, 300) + "...";
            Logger.Error($"DT: код {p.ExitCode} — {detail}");
            return $"1cv8 завершился с кодом {p.ExitCode}.\n{detail}";
        }
        catch (Exception ex)
        {
            Logger.Error($"DT: исключение — {ex.Message}");
            return ex.Message;
        }
    }

    public static string DefaultBackupPath(string baseName)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // Убираем символы недопустимые в именах файлов
        var safe = string.Concat(baseName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(@"C:\Temp\Clinkon1C\DT", $"{safe}_{ts}.dt");
    }
}
