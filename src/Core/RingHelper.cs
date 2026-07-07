using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;

namespace Clinkon1C.Core;

/// <summary>
/// Поиск, извлечение и запуск утилиты ring license.
/// Все файлы размещаются в %ProgramData%\Clinkon1C\.
/// </summary>
public static class RingHelper
{
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Clinkon1C");

    public static string RingDir => Path.Combine(DataRoot, "ring");
    public static string RingCmd => Path.Combine(RingDir, "ring.cmd");
    public static string LicDir  => Path.Combine(DataRoot, "license");
    public static string JreDir  => Path.Combine(DataRoot, "jre");
    public static string JavaExe => Path.Combine(JreDir, "bin", "java.exe");

    // Eclipse Temurin 8 JRE для Windows x64 (OpenJDK, открытая лицензия)
    private const string JreDownloadUrl =
        "https://github.com/adoptium/temurin8-binaries/releases/download/" +
        "jdk8u392-b08/OpenJDK8U-jre_x64_windows_hotspot_8u392b08.zip";

    public enum SetupState { Ready, NeedRing, NeedJava }

    public static SetupState CheckSetup()
    {
        if (FindRingCmd() == null) return SetupState.NeedRing;
        if (FindJava()    == null) return SetupState.NeedJava;
        return SetupState.Ready;
    }

    // ── Поиск ring ────────────────────────────────────────────────────────────

    private static readonly string[] KnownRingPaths =
    {
        // Наша извлечённая копия
        // (проверяется отдельно через RingCmd)

        // Стандартные пути 1С
        @"C:\Program Files\1cv8\ring\ring.cmd",
        @"C:\Program Files (x86)\1cv8\ring\ring.cmd",
        @"C:\Program Files\1cv8\common\ring\ring.cmd",
        @"C:\Program Files (x86)\1cv8\common\ring\ring.cmd",
    };

    public static string? FindRingCmd()
    {
        if (File.Exists(RingCmd)) return RingCmd;
        foreach (var p in KnownRingPaths)
            if (File.Exists(p)) return p;
        return null;
    }

    // ── Поиск Java ────────────────────────────────────────────────────────────

    public static string? FindJava()
    {
        // 1. Наш portable JRE
        if (File.Exists(JavaExe)) return JavaExe;

        // 2. JAVA_HOME
        var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(jh))
        {
            var p = Path.Combine(jh, "bin", "java.exe");
            if (File.Exists(p)) return p;
        }

        // 3. PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "java.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        return null;
    }

    // ── Извлечение из встроенных ресурсов ────────────────────────────────────

    /// <summary>
    /// Извлекает ring и license-tools из встроенных в exe ресурсов (ring.car, license-tools.car).
    /// Вызывается автоматически при первом входе в модуль Лицензии.
    /// </summary>
    /// <returns>null при успехе, иначе сообщение об ошибке.</returns>
    public static string? ExtractFromResources(Action<string>? progress = null)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();

            progress?.Invoke("Извлечение ring...");
            Directory.CreateDirectory(RingDir);
            using (var stream = asm.GetManifestResourceStream("ring.car"))
            {
                if (stream == null) return "Ресурс ring.car не найден в сборке";
                ExtractCarStream(stream, RingDir, "data/", keepStructure: true);
            }

            progress?.Invoke("Извлечение license-tools...");
            Directory.CreateDirectory(LicDir);
            using (var stream = asm.GetManifestResourceStream("license-tools.car"))
            {
                if (stream == null) return "Ресурс license-tools.car не найден в сборке";
                ExtractCarStream(stream, LicDir, "data/", keepStructure: false);
            }

            progress?.Invoke("Создание ring-commands.cfg...");
            WriteRingCommandsCfg();

            Logger.Info($"RingHelper: извлечено из ресурсов в {DataRoot}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"RingHelper.ExtractFromResources: {ex.Message}");
            return ex.Message;
        }
    }

    private static void ExtractCarStream(Stream carStream, string destDir,
        string stripPrefix, bool keepStructure)
    {
        using var zip = new ZipArchive(carStream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith("/")) continue;
            if (!entry.FullName.StartsWith(stripPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string dest;
            if (keepStructure)
            {
                var rel = entry.FullName.Substring(stripPrefix.Length);
                dest = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                var fileName = Path.GetFileName(entry.FullName);
                if (string.IsNullOrEmpty(fileName)) continue;
                dest = Path.Combine(destDir, fileName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = entry.Open();
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }

    /// <summary>
    /// Создаёт ring-commands.cfg — файл регистрации модуля license для утилиты ring.
    /// Формат YAML: outer key = имя модуля, value = список {version, arch, file}.
    /// Пишем в два места: наш RingDir (для E1C_RING_COMMANDS) и стандартный путь 1C.
    /// </summary>
    public static void WriteRingCommandsCfg()
    {
        // Основной JAR модуля (com._1c.license.activator.ring-*.jar)
        var mainJar = Directory.GetFiles(LicDir, "com._1c.license.activator.ring-*.jar")
                               .FirstOrDefault()?.Replace('\\', '/') ?? "";

        // Читаем версию из имени JAR (формат: com._1c.license.activator.ring-0.15.0-2.jar)
        var jarName = Path.GetFileNameWithoutExtension(mainJar.Replace('/', Path.DirectorySeparatorChar));
        var version = "0.15.0"; // fallback
        if (!string.IsNullOrEmpty(jarName))
        {
            // Вида: com._1c.license.activator.ring-0.15.0-2 → версия 0.15.0-2
            //       com._1c.license.activator.ring-0.15    → нормализуем до 0.15.0
            var dashIdx = jarName.IndexOf('-');
            if (dashIdx >= 0)
            {
                version = jarName.Substring(dashIdx + 1);
                // ring принимает только X.Y.Z — стрипим суффиксы вида "-2" или "+2"
                var qualSep   = version.IndexOfAny(new[] { '-', '+' });
                var numeric   = qualSep < 0 ? version : version.Substring(0, qualSep);
                var qualifier = qualSep < 0 ? "" : version.Substring(qualSep);
                // Чисто числовой суффикс (например -2) ring отвергает — убираем
                if (System.Text.RegularExpressions.Regex.IsMatch(qualifier, @"^[-+]\d+$"))
                    qualifier = "";
                var dots = numeric.Count(c => c == '.');
                while (dots < 2) { numeric += ".0"; dots++; }
                version = numeric + qualifier;
            }
        }

        var content =
            "---\n" +
            "license:\n" +
            $"- version: \"{version}\"\n" +
            $"  arch: x86_64\n" +
            $"  file: \"{mainJar}\"\n";

        Logger.Info($"RingHelper: ring-commands.cfg version={version}, jar={mainJar}");

        void TryWrite(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content, Encoding.UTF8);
                Logger.Info($"RingHelper: cfg → {path}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"RingHelper: cfg FAIL → {path}: {ex.Message}");
            }
        }

        // 1. Наш RingDir (используется через E1C_RING_COMMANDS)
        TryWrite(Path.Combine(RingDir, "ring-commands.cfg"));

        // 2. Директория самого ring.cmd (системный 1С хранит там ring-commands.cfg с 0.15)
        var ringCmd = FindRingCmd();
        if (ringCmd != null)
        {
            var ringCmdDir = Path.GetDirectoryName(ringCmd);
            if (ringCmdDir != null)
                TryWrite(Path.Combine(ringCmdDir, "ring-commands.cfg"));
        }

        // 3. Известные каталоги системного ring 1С
        var knownRingDirs = new[]
        {
            @"C:\Program Files\1cv8\ring",
            @"C:\Program Files (x86)\1cv8\ring",
            @"C:\Program Files\1cv8\common\ring",
            @"C:\Program Files (x86)\1cv8\common\ring",
        };
        foreach (var dir in knownRingDirs)
            if (Directory.Exists(dir))
                TryWrite(Path.Combine(dir, "ring-commands.cfg"));

        // 4. %ProgramData%\1C\1CE\ — стандартный путь 1С
        TryWrite(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "1C", "1CE", "ring-commands.cfg"));

        // 5. %AppData%\1C\1CE\ — per-user путь (ring также читает отсюда)
        TryWrite(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "1C", "1CE", "ring-commands.cfg"));
    }

    // ── Скачивание JRE ────────────────────────────────────────────────────────

    /// <summary>Скачивает Eclipse Temurin 8 JRE и распаковывает в JreDir.</summary>
    /// <returns>null при успехе, иначе сообщение об ошибке.</returns>
    public static string? DownloadJre(Action<string>? progress = null)
    {
        try
        {
            var tempZip     = Path.Combine(Path.GetTempPath(), "clinkon1c_jre.zip");
            var tempExtract = Path.Combine(Path.GetTempPath(), "clinkon1c_jre_tmp");

            progress?.Invoke("Скачивание Java JRE (~55 МБ)...");
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Clinkon1C");
            client.Timeout = TimeSpan.FromMinutes(15);
            var bytes = client.GetByteArrayAsync(JreDownloadUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(tempZip, bytes);

            progress?.Invoke("Распаковка Java JRE...");
            if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true);
            Directory.CreateDirectory(tempExtract);

            using (var zip = new ZipArchive(File.OpenRead(tempZip), ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/")) continue;
                    var dest = Path.Combine(tempExtract,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using var src = entry.Open();
                    using var dst = File.Create(dest);
                    src.CopyTo(dst);
                }
            }

            // Ищем java.exe внутри распакованного архива
            var jreSub = Directory.GetDirectories(tempExtract)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", "java.exe")));
            if (jreSub == null)
                return "Не найден java.exe в скачанном архиве";

            if (Directory.Exists(JreDir)) Directory.Delete(JreDir, true);
            Directory.Move(jreSub, JreDir);
            Directory.Delete(tempExtract, true);
            try { File.Delete(tempZip); } catch { }

            Logger.Info($"RingHelper: JRE установлен в {JreDir}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"RingHelper.DownloadJre: {ex.Message}");
            return ex.Message;
        }
    }

    // ── Запуск ring ───────────────────────────────────────────────────────────

    /// <summary>Выполняет команду ring license и возвращает (exitCode, stdout+stderr).</summary>
    public static (int ExitCode, string Output) RunLicense(string args)
    {
        var ringCmd = FindRingCmd();
        if (ringCmd == null) return (-1, "ring не найден");

        var javaExe = FindJava();

        // chcp 65001 переключает codepage cmd на UTF-8 до запуска ring
        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/c chcp 65001 >nul && \"{ringCmd}\" license {args} --send-statistics false",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        // Пересоздаём ring-commands.cfg перед каждым запуском — исправляет битые файлы от старых версий
        if (Directory.Exists(LicDir) &&
            Directory.GetFiles(LicDir, "com._1c.license.activator.ring-*.jar").Length > 0)
        {
            try { WriteRingCommandsCfg(); } catch { }
        }

        // Путь к нашему ring-commands.cfg — ring читает его по этой переменной (E1C_RING_COMMANDS)
        var ourCfg = Path.Combine(RingDir, "ring-commands.cfg");
        if (File.Exists(ourCfg))
            psi.EnvironmentVariables["E1C_RING_COMMANDS"] = ourCfg;

        // Дополнительно — на случай если ring.cmd пробрасывает эти переменные в java
        psi.EnvironmentVariables["RING_OPTS"]        = "-Dfile.encoding=UTF-8";
        psi.EnvironmentVariables["JAVA_TOOL_OPTIONS"] = "-Dfile.encoding=UTF-8";

        if (javaExe != null)
        {
            // Устанавливаем JAVA_HOME на наш portable JRE (или системный)
            var jreHome = Path.GetDirectoryName(Path.GetDirectoryName(javaExe));
            if (jreHome != null)
                psi.EnvironmentVariables["JAVA_HOME"] = jreHome;
        }

        try
        {
            using var proc = Process.Start(psi)!;
            var stdout = StripJavaNoise(proc.StandardOutput.ReadToEnd());
            var stderr = StripJavaNoise(proc.StandardError.ReadToEnd());
            proc.WaitForExit(60_000); // таймаут 60 сек

            var output = !string.IsNullOrWhiteSpace(stdout) ? stdout
                       : !string.IsNullOrWhiteSpace(stderr) ? stderr
                       : "";
            Logger.Info($"ring license {args.Split(' ')[0]} → exit {proc.ExitCode}");
            return (proc.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            Logger.Error($"RingHelper.RunLicense: {ex.Message}");
            return (-1, ex.Message);
        }
    }

    // JVM обязательно печатает эту строку в stderr, когда установлена JAVA_TOOL_OPTIONS
    // (мы сами её выставляем выше для UTF-8) — не отключается, только фильтрация.
    // Без неё: при пустом stdout (нет лицензий) код падает в fallback на stderr,
    // и эта строка ошибочно парсится как единственная "лицензия".
    private static string StripJavaNoise(string s)
    {
        var lines = s.Split('\n').Where(l =>
            !l.TrimStart().StartsWith("Picked up JAVA_TOOL_OPTIONS", StringComparison.OrdinalIgnoreCase) &&
            !l.TrimStart().StartsWith("Picked up _JAVA_OPTIONS",     StringComparison.OrdinalIgnoreCase));
        return string.Join("\n", lines);
    }
}
