using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Principal;
using System.Text.Json.Serialization;
using Clinkon1C.Core;
using Clinkon1C.Modules.Agents;
using Clinkon1C.Modules.Bases;
using Clinkon1C.Modules.Cache;
using Clinkon1C.Modules.COM;
using Clinkon1C.Modules.Configs;
using Clinkon1C.Modules.Diagnostics;
using Clinkon1C.Modules.Emulators;
using Clinkon1C.Modules.Licenses;
using Clinkon1C.Modules.Processes;
using Clinkon1C.Modules.Templates;
using Clinkon1C.Modules.Web;
using Clinkon1C.UI;

namespace Clinkon1C;

class Program
{
    // CI выставляет AssemblyInformationalVersion из git-тега через -p:Version=X.Y.Z.
    // Локальная сборка без тега возвращает "1.0.0".
    public static readonly string VERSION =
        (typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)
            ?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
    private const string GithubApiUrl = "https://api.github.com/repos/iMironRU/Clinkon1C/releases/latest";

    /// <summary>Версия с номером сборки: "1.0.0 b26" или просто "1.0.0" если нет build number.</summary>
    public static string FullVersion
    {
        get
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return (ver != null && ver.Revision > 0) ? $"{VERSION} b{ver.Revision}" : VERSION;
        }
    }

#if NETFRAMEWORK
    static void CheckDotNetFramework()
    {
        const int net48Release = 528040;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            var release = key?.GetValue("Release") as int?;
            if (release >= net48Release) return;
        }
        catch { }

        if (ConsoleDialog.ShowDotNetRequiredDialog())
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48",
                    UseShellExecute = true
                });
            }
            catch { }
        }
        Environment.Exit(1);
    }
#endif

    static void Main(string[] args)
    {
#if NETFRAMEWORK
        CheckDotNetFramework();
#endif
        // Mutex — защита от двойного запуска
        using var mutex = new System.Threading.Mutex(true, "Global\\Clinkon1C_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Console.WriteLine("Clinkon1C уже запущен.");
            return;
        }

        Console.Title = $"Clinkon1C v{VERSION}";
        Console.OutputEncoding = System.Text.Encoding.UTF8;
#if !NETFRAMEWORK
        // В .NET 8+ CP1251 недоступен без явной регистрации; в net48 встроен
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
#endif
        Logger.Init();
        Logger.Info($"Clinkon1C v{VERSION} запущен пользователем {Environment.UserName}");

        // 1. Сначала — проверка и запрос прав администратора
        bool isAdmin = IsAdministrator();
        if (!isAdmin)
        {
            Logger.Warn("Запущен без прав администратора");
            var choice = ConsoleDialog.ShowElevationMenu();
            if (choice == ElevationChoice.Elevate)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName,
                        Verb            = "runas",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    ConsoleDialog.Confirm("Ошибка", $"Не удалось запустить с повышением прав:\n{ex.Message}");
                }
                return;
            }
            if (choice == ElevationChoice.Exit) return;
            Logger.Warn("Продолжение без прав администратора");
        }

        // 2. После прав — предупреждение в FAR-стиле (пропускается при авто-обновлении)
        bool skipWarning = Array.IndexOf(args, "--skip-admin-warning") >= 0;
        if (!skipWarning && !ConsoleDialog.ShowWarningDialog()) return;

        // 3. Проверка обновлений — интерактивная, предлагаем обновиться
        string? updateNotice = null;
        try
        {
            var upd = CheckForUpdate();
            if (upd != null)
            {
                updateNotice = $"v{upd.Version}";
                if (ConsoleDialog.ShowUpdateDialog(FullVersion, upd.Version))
                {
                    SelfUpdate(upd);
                    return; // SelfUpdate запускает новый процесс и вызывает Environment.Exit(0)
                }
            }
        }
        catch { /* сетевые ошибки не останавливают запуск */ }

        // Запуск приложения
        Console.Title = $"Clinkon1C v{VERSION}";
        Logger.Info($"Clinkon1C v{VERSION} ({FullVersion}) запуск TUI");
        try
        {
            var cache     = new CacheModule();
            var templates = new TemplatesModule();
            var bases     = new BasesModule();
            var licenses  = new LicensesModule();
            var agents    = new RagentModule();
            var processes = new ProcessesModule();
            var web       = new WebModule();
            var emulators    = new EmulatorModule();
            var configs      = new ConfigsModule();
            var diagnostics  = new DiagnosticsModule();
            var com          = new ComModule();
            static string? CheckSilent()
            {
                try { var u = CheckForUpdate(); return u != null ? $"v{u.Version}" : null; }
                catch { return null; }
            }
            static void DoUpdate()
            {
                try
                {
                    var upd = CheckForUpdate();
                    if (upd != null && ConsoleDialog.ShowUpdateDialog(FullVersion, upd.Version))
                        SelfUpdate(upd);
                }
                catch { }
            }
            new FarApp(cache, templates, bases, licenses, agents, processes, web, emulators, configs, diagnostics, com, updateNotice, CheckSilent, DoUpdate).Run();
        }
        finally
        {
            Logger.Info("Clinkon1C завершён");
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ── Обновление ────────────────────────────────────────────────────────────

    private record UpdateInfo(string Version, string DownloadUrl, string AssetName);

    private static UpdateInfo? CheckForUpdate()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", $"Clinkon1C/{VERSION}");
        client.Timeout = TimeSpan.FromSeconds(6);

        var release = client.GetFromJsonAsync<GithubRelease>(GithubApiUrl)
            .GetAwaiter().GetResult();
        if (release?.TagName == null) return null;

        var latest = release.TagName.TrimStart('v');
        if (!IsNewerVersion(VERSION, latest)) return null;

        // Ищем нужный артефакт для текущей платформы
#if NETFRAMEWORK
        const string keyword = "legacy";
#else
        const string keyword = "x64";
#endif
        var asset = release.Assets?.FirstOrDefault(a =>
            a.Name?.Contains(keyword) == true && a.Name.EndsWith(".exe"));

        return new UpdateInfo(latest, asset?.BrowserDownloadUrl ?? "", asset?.Name ?? "");
    }

    private static bool IsNewerVersion(string current, string latest)
    {
        var split = (string s) => s.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        var cur = split(current);
        var lat = split(latest);
        int len = Math.Max(cur.Length, lat.Length);
        for (int i = 0; i < len; i++)
        {
            int a = i < cur.Length ? cur[i] : 0;
            int b = i < lat.Length ? lat[i] : 0;
            if (b > a) return true;
            if (b < a) return false;
        }
        return false;
    }

    private static void SelfUpdate(UpdateInfo upd)
    {
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        Console.ForegroundColor = ConsoleColor.White;
        Console.Clear();
        Console.SetCursorPosition(2, 2);
        Console.Write($"Загрузка v{upd.Version}...");

        try
        {
            if (string.IsNullOrEmpty(upd.DownloadUrl))
            {
                // Нет прямой ссылки — открываем страницу Releases в браузере
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "https://github.com/iMironRU/Clinkon1C/releases/latest",
                    UseShellExecute = true
                });
                return;
            }

            var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
            var currentDir = Path.GetDirectoryName(currentExe)!;
            var assetName  = !string.IsNullOrEmpty(upd.AssetName) ? upd.AssetName : Path.GetFileName(currentExe);
            var newExePath = Path.Combine(currentDir, assetName);
            var tempFile   = Path.Combine(Path.GetTempPath(), "Clinkon1C_update.exe");

            Logger.Info($"Обновление: скачиваем {upd.AssetName} v{upd.Version} из {upd.DownloadUrl}");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", $"Clinkon1C/{VERSION}");
            client.Timeout = TimeSpan.FromMinutes(5);

            using var response = client.GetAsync(upd.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            long total   = response.Content.Headers.ContentLength ?? 0;
            long received = 0;
            var tempStream = File.OpenWrite(tempFile);
            using (tempStream)
            using (var netStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            {
                var buf = new byte[81920];
                int read;
                while ((read = netStream.Read(buf, 0, buf.Length)) > 0)
                {
                    tempStream.Write(buf, 0, read);
                    received += read;
                    if (total > 0)
                    {
                        int pct = (int)(received * 100 / total);
                        string bar = new string('█', pct / 5).PadRight(20);
                        Console.SetCursorPosition(2, 3);
                        Console.Write($"[{bar}] {pct,3}%  {received / 1024} / {total / 1024} КБ  ");
                    }
                }
            }
            Logger.Info($"Обновление: скачано {received / 1024} КБ → {tempFile}");

            Console.SetCursorPosition(2, 4);
            Console.Write("Применение обновления...");

            // Bat-скрипт: ждёт выхода текущего процесса, кладёт новый exe рядом под правильным именем
            var scriptPath = Path.Combine(Path.GetTempPath(), "clinkon1c_upd.bat");
            // Удаляем старый exe только если он отличается от нового (разные имена версий)
            string deleteOld = !string.Equals(currentExe, newExePath, StringComparison.OrdinalIgnoreCase)
                ? $"del /F /Q \"{currentExe}\" >nul 2>&1\r\n"
                : "";

            File.WriteAllText(scriptPath,
                $"@echo off\r\n" +
                $"timeout /t 2 /nobreak >nul\r\n" +
                $"copy /y \"{tempFile}\" \"{newExePath}\" >nul\r\n" +
                $"del \"{tempFile}\" >nul\r\n" +
                $"start \"\" \"{newExePath}\" --skip-admin-warning\r\n" +
                deleteOld +
                $"del \"%~f0\"\r\n",
                System.Text.Encoding.ASCII);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = scriptPath,
                UseShellExecute = true,
                WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden
            });

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.SetCursorPosition(2, 4);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"Ошибка: {ex.Message}");
            Console.ForegroundColor = ConsoleColor.White;
            Console.SetCursorPosition(2, 5);
            Console.Write("Нажмите любую клавишу...");
            Console.ReadKey(true);
        }
    }

    private class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GithubAsset>? Assets { get; set; }
    }

    private class GithubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
