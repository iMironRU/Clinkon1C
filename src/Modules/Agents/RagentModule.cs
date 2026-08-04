using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Clinkon1C.Core;
using Microsoft.Win32;

namespace Clinkon1C.Modules.Agents;

public class RagentEntry
{
    public string ServiceKey    { get; set; } = "";
    public string DisplayName   { get; set; } = "";
    public string ImagePath     { get; set; } = "";
    public string RagentExe     { get; set; } = "";
    public string Version       { get; set; } = "";
    public int    Port          { get; set; } = 1540;
    public string DataDir       { get; set; } = "";
    public int    RegPort       { get; set; } = 1541;
    public string Range         { get; set; } = "";
    public bool   DebugEnabled  { get; set; }
    public string DebugProtocol { get; set; } = "tcp";
    public int    DebugPort     { get; set; } = 1550;
    public string Status        { get; set; } = "Unknown";

    public bool IsRunning => Status == "Running";
}

public class RasEntry
{
    public string ServiceKey  { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ImagePath   { get; set; } = "";
    public string RasExe      { get; set; } = "";
    public string Version     { get; set; } = "";
    public int    RasPort     { get; set; } = 1545;
    public string AgentAddr   { get; set; } = "localhost:1540";
    public string Status      { get; set; } = "Unknown";
    public bool   IsRunning   => Status == "Running";
}

public class RagentModule
{
    public List<RagentEntry> Entries    { get; } = new();
    public List<RasEntry>    RasEntries { get; } = new();

    // ── Сканирование ──────────────────────────────────────────────────────────

    public void Refresh()
    {
        Entries.Clear();
        RasEntries.Clear();
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services");
            if (services == null) return;

            foreach (var name in services.GetSubKeyNames())
            {
                using var svc = services.OpenSubKey(name);
                var img = svc?.GetValue("ImagePath") as string;
                if (img == null) continue;

                if (img.IndexOf("ragent", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var entry = Parse(name, svc!, img);
                    if (entry != null) Entries.Add(entry);
                }
                else if (img.IndexOf("ras.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var entry = ParseRas(name, svc!, img);
                    if (entry != null) RasEntries.Add(entry);
                }
            }

            Entries.Sort((a, b) => a.Port.CompareTo(b.Port));
            RasEntries.Sort((a, b) => a.RasPort.CompareTo(b.RasPort));

            foreach (var e in Entries)    e.Status = QueryStatus(e.ServiceKey);
            foreach (var e in RasEntries) e.Status = QueryStatus(e.ServiceKey);
        }
        catch (Exception ex) { Logger.Error($"RagentModule.Refresh: {ex.Message}"); }
    }

    private static RasEntry? ParseRas(string key, RegistryKey svc, string img)
    {
        var e = new RasEntry
        {
            ServiceKey  = key,
            DisplayName = svc.GetValue("DisplayName") as string ?? key,
            ImagePath   = img,
        };

        string args;
        if (img.StartsWith("\""))
        {
            int end = img.IndexOf('"', 1);
            if (end < 0) return null;
            e.RasExe = img.Substring(1, end - 1);
            args = img.Substring(end + 1).Trim();
        }
        else
        {
            int sp = img.IndexOf(' ');
            if (sp < 0) { e.RasExe = img; args = ""; }
            else { e.RasExe = img.Substring(0, sp); args = img.Substring(sp + 1).Trim(); }
        }

        foreach (var part in e.RasExe.Replace('/', '\\').Split('\\'))
            if (Regex.IsMatch(part, @"^\d+\.\d+\.\d+\.\d+$")) { e.Version = part; break; }

        var portM = Regex.Match(args, @"--port[=\s]+(\d+)", RegexOptions.IgnoreCase);
        if (portM.Success && int.TryParse(portM.Groups[1].Value, out int p)) e.RasPort = p;

        // адрес агента — последний токен вида host:port
        var addrM = Regex.Match(args, @"(\S+:\d+)\s*$");
        if (addrM.Success) e.AgentAddr = addrM.Groups[1].Value;

        return e;
    }

    private static RagentEntry? Parse(string key, RegistryKey svc, string img)
    {
        var e = new RagentEntry
        {
            ServiceKey  = key,
            DisplayName = svc.GetValue("DisplayName") as string ?? key,
            ImagePath   = img,
        };

        string args;
        if (img.StartsWith("\""))
        {
            int end = img.IndexOf('"', 1);
            if (end < 0) return null;
            e.RagentExe = img.Substring(1, end - 1);
            args = img.Substring(end + 1).Trim();
        }
        else
        {
            int sp = img.IndexOf(' ');
            if (sp < 0) { e.RagentExe = img; args = ""; }
            else { e.RagentExe = img.Substring(0, sp); args = img.Substring(sp + 1).Trim(); }
        }

        // Версия 1С из пути (…\8.3.25.1234\bin\ragent.exe)
        foreach (var part in e.RagentExe.Replace('/', '\\').Split('\\'))
            if (Regex.IsMatch(part, @"^\d+\.\d+\.\d+\.\d+$")) { e.Version = part; break; }

        e.Port      = GetInt(args, "port",            1540);
        e.RegPort   = GetInt(args, "regport",         1541);
        e.DebugPort = GetInt(args, "debugServerPort", 1550);
        e.DataDir   = GetStr(args, "d",     "");
        e.Range     = GetStr(args, "range", "");

        var dm = Regex.Match(args, @"[/-]debug\s+-(tcp|http)", RegexOptions.IgnoreCase);
        if (dm.Success)
        {
            e.DebugEnabled  = true;
            e.DebugProtocol = dm.Groups[1].Value.ToLower();
        }

        return e;
    }

    private static int GetInt(string args, string param, int def)
    {
        // Принимаем как /param так и -param (оба формата используются в 1С)
        var m = Regex.Match(args, $@"[/-]{Regex.Escape(param)}\s+(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : def;
    }

    private static string GetStr(string args, string param, string def)
    {
        var pat = $@"[/-]{Regex.Escape(param)}\s+";
        var m = Regex.Match(args, pat + @"""([^""]+)""", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(args, pat + @"(\S+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : def;
    }

    // ── Поиск установленных версий 1С ─────────────────────────────────────────

    public static List<(string Version, string RasExe)> FindRasVersions()
    {
        var result = new List<(string Version, string RasExe)>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(r => !string.IsNullOrEmpty(r)).Distinct();

        foreach (var root in roots)
        {
            var dir1cv8 = Path.Combine(root, "1cv8");
            if (!Directory.Exists(dir1cv8)) continue;
            foreach (var ver in Directory.GetDirectories(dir1cv8))
            {
                var exe = Path.Combine(ver, "bin", "ras.exe");
                if (File.Exists(exe))
                    result.Add((Path.GetFileName(ver), exe));
            }
        }

        result.Sort((a, b) => string.Compare(b.Version, a.Version, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static List<(string Version, string RagentExe)> FindVersions()
    {
        var result = new List<(string Version, string RagentExe)>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(r => !string.IsNullOrEmpty(r)).Distinct();

        foreach (var root in roots)
        {
            var dir1cv8 = Path.Combine(root, "1cv8");
            if (!Directory.Exists(dir1cv8)) continue;
            foreach (var ver in Directory.GetDirectories(dir1cv8))
            {
                var exe = Path.Combine(ver, "bin", "ragent.exe");
                if (File.Exists(exe))
                    result.Add((Path.GetFileName(ver), exe));
            }
        }

        result.Sort((a, b) =>
            string.Compare(b.Version, a.Version, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // ── Управление службой (Win32, без System.ServiceProcess) ─────────────────

    public string? StartService(string key)
    {
        var hScm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (hScm == IntPtr.Zero) return $"OpenSCManager: {Marshal.GetLastWin32Error()}";
        try
        {
            var hSvc = OpenService(hScm, key, SERVICE_START | SERVICE_QUERY_STATUS);
            if (hSvc == IntPtr.Zero) return $"Служба не найдена: {key}";
            try
            {
                if (!QueryServiceStatus(hSvc, out var st)) return null;
                if (st.dwCurrentState == SERVICE_RUNNING) return null;
                if (!Win32StartService(hSvc, 0, null))
                    return $"StartService: {Marshal.GetLastWin32Error()}";
                return WaitForState(hSvc, SERVICE_RUNNING, 20_000);
            }
            finally { CloseServiceHandle(hSvc); }
        }
        finally { CloseServiceHandle(hScm); }
    }

    public string? StopService(string key)
    {
        var hScm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (hScm == IntPtr.Zero) return $"OpenSCManager: {Marshal.GetLastWin32Error()}";
        try
        {
            var hSvc = OpenService(hScm, key, SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (hSvc == IntPtr.Zero) return null; // уже удалена или не найдена
            try
            {
                if (!QueryServiceStatus(hSvc, out var st)) return null;
                if (st.dwCurrentState == SERVICE_STOPPED) return null;
                if (!ControlService(hSvc, SERVICE_CONTROL_STOP, out _))
                    return $"ControlService: {Marshal.GetLastWin32Error()}";
                return WaitForState(hSvc, SERVICE_STOPPED, 20_000);
            }
            finally { CloseServiceHandle(hSvc); }
        }
        finally { CloseServiceHandle(hScm); }
    }

    public string? RestartService(string key)
    {
        var err = StopService(key);
        if (err != null) return err;
        return StartService(key);
    }

    private static string QueryStatus(string key)
    {
        // SC_MANAGER_CONNECT (не ALL_ACCESS) — чтение статуса работает без прав администратора
        var hScm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (hScm == IntPtr.Zero) return "Unknown";
        try
        {
            var hSvc = OpenService(hScm, key, SERVICE_QUERY_STATUS);
            if (hSvc == IntPtr.Zero) return "Unknown";
            try
            {
                if (!QueryServiceStatus(hSvc, out var st)) return "Unknown";
                return st.dwCurrentState switch
                {
                    SERVICE_RUNNING       => "Running",
                    SERVICE_STOPPED       => "Stopped",
                    SERVICE_START_PENDING => "StartPending",
                    SERVICE_STOP_PENDING  => "StopPending",
                    _                     => $"State_{st.dwCurrentState}",
                };
            }
            finally { CloseServiceHandle(hSvc); }
        }
        finally { CloseServiceHandle(hScm); }
    }

    private static string? WaitForState(IntPtr hSvc, uint target, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!QueryServiceStatus(hSvc, out var st)) break;
            if (st.dwCurrentState == target) return null;
            Thread.Sleep(250);
        }
        return "Таймаут ожидания состояния службы";
    }

    // ── Переключение отладки ──────────────────────────────────────────────────

    public string? SetDebug(RagentEntry entry, string? protocol)
    {
        try
        {
            var newImg = Regex.Replace(
                entry.ImagePath, @"\s*/debug\s+-\w+", "", RegexOptions.IgnoreCase).TrimEnd();
            if (protocol != null)
                newImg += $" /debug -{protocol}";

            var baseName   = Regex.Replace(entry.DisplayName, @"\s*\[DEBUG:[^\]]*\]", "").Trim();
            var newDisplay = protocol != null
                ? $"{baseName} [DEBUG:{protocol.ToUpper()}]"
                : baseName;

            using var svcKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{entry.ServiceKey}", writable: true);
            if (svcKey == null)
                return "Не удалось открыть ключ службы в реестре (недостаточно прав)";

            svcKey.SetValue("ImagePath",   newImg);
            svcKey.SetValue("DisplayName", newDisplay);

            entry.ImagePath     = newImg;
            entry.DisplayName   = newDisplay;
            entry.DebugEnabled  = protocol != null;
            entry.DebugProtocol = protocol ?? entry.DebugProtocol;

            Logger.Info($"ragent {entry.ServiceKey}: отладка → {(protocol ?? "выкл")}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"RagentModule.SetDebug: {ex.Message}");
            return ex.Message;
        }
    }

    // ── Создание / удаление ───────────────────────────────────────────────────

    public class CreateParams
    {
        public string  RagentExe { get; set; } = "";
        public int     Port      { get; set; } = 1540;
        public int     RegPort   { get; set; } = 1541;
        public string  Range     { get; set; } = "1560:1591";
        public string  DataDir   { get; set; } = "";
        public string? Protocol  { get; set; }
    }

    public string? CreateAgent(CreateParams p)
    {
        try
        {
            var key     = $"1C_Agent_{p.Port}";
            var display = $"1C Агент :{p.Port}"
                + (p.Protocol != null ? $" [DEBUG:{p.Protocol.ToUpper()}]" : "");

            var sb = new StringBuilder();
            sb.Append($"\"{p.RagentExe}\"");
            sb.Append($" /port {p.Port}");
            if (p.RegPort > 0)               sb.Append($" /regport {p.RegPort}");
            if (!string.IsNullOrEmpty(p.Range))   sb.Append($" /range {p.Range}");
            if (!string.IsNullOrEmpty(p.DataDir)) sb.Append($" /d \"{p.DataDir.TrimEnd('\\')}\"");
            if (p.Protocol != null)          sb.Append($" /debug -{p.Protocol}");

            var err = ScmCreate(key, display, sb.ToString());
            if (err != null) return err;

            Logger.Info($"ragent: создан агент {key} порт {p.Port}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"RagentModule.CreateAgent: {ex.Message}");
            return ex.Message;
        }
    }

    public string? DeleteAgent(string key)
    {
        try
        {
            StopService(key);
            var err = ScmDelete(key);
            if (err != null) return err;
            Logger.Info($"ragent: удалён агент {key}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"RagentModule.DeleteAgent: {ex.Message}");
            return ex.Message;
        }
    }

    // ── RAS: создание / удаление ──────────────────────────────────────────────

    public string? CreateRas(string rasExe, int rasPort, string agentAddr,
        string? account, string? password)
    {
        try
        {
            string version = "";
            foreach (var part in rasExe.Replace('/', '\\').Split('\\'))
                if (Regex.IsMatch(part, @"^\d+\.\d+\.\d+\.\d+$")) { version = part; break; }

            var key     = $"1C_RAS_{rasPort}";
            var display = string.IsNullOrEmpty(version)
                ? $"1C:Enterprise RAS :{rasPort}"
                : $"1C:Enterprise RAS {version} :{rasPort}";
            var binPath = $"\"{rasExe}\" cluster --service --port={rasPort} {agentAddr}";

            var err = ScmCreate(key, display, binPath,
                string.IsNullOrEmpty(account)  ? null : account,
                string.IsNullOrEmpty(password) ? null : password);
            if (err != null) return err;

            Logger.Info($"RAS: создана служба {key} порт {rasPort}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"RagentModule.CreateRas: {ex.Message}");
            return ex.Message;
        }
    }

    public string? DeleteRas(string key)
    {
        try
        {
            StopService(key);
            var err = ScmDelete(key);
            if (err != null) return err;
            Logger.Info($"RAS: удалена служба {key}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"RagentModule.DeleteRas: {ex.Message}");
            return ex.Message;
        }
    }

    // ── Win32 SCM P/Invoke ────────────────────────────────────────────────────

    private const uint SC_MANAGER_ALL_ACCESS     = 0xF003F;
    private const uint SC_MANAGER_CONNECT        = 0x0001; // достаточно для чтения статуса, не требует прав администратора
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_AUTO_START        = 0x00000002;
    private const uint SERVICE_ERROR_NORMAL      = 0x00000001;
    private const uint SERVICE_ALL_ACCESS        = 0xF01FF;
    private const uint DELETE                    = 0x00010000;
    private const uint SERVICE_QUERY_STATUS      = 0x0004;
    private const uint SERVICE_START             = 0x0010;
    private const uint SERVICE_STOP              = 0x0020;
    private const uint SERVICE_CONTROL_STOP      = 0x00000001;
    private const uint SERVICE_STOPPED           = 0x00000001;
    private const uint SERVICE_START_PENDING     = 0x00000002;
    private const uint SERVICE_STOP_PENDING      = 0x00000003;
    private const uint SERVICE_RUNNING           = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(IntPtr hScm,
        string name, string display, uint access,
        uint type, uint start, uint error,
        string binPath, string? group, IntPtr tag,
        string? deps, string? account, string? pwd);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr hScm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hSvc, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "StartServiceW", CharSet = CharSet.Unicode)]
    private static extern bool Win32StartService(IntPtr hSvc, int argc, string[]? argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hSvc, uint control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr hSvc);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr h);

    private static string? ScmCreate(string key, string display, string binPath,
        string? account = null, string? password = null)
    {
        var hScm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (hScm == IntPtr.Zero)
            return $"OpenSCManager: {Marshal.GetLastWin32Error()}";
        try
        {
            var hSvc = CreateService(hScm, key, display, SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL,
                binPath, null, IntPtr.Zero, null, account, password);
            if (hSvc == IntPtr.Zero)
                return $"CreateService: {Marshal.GetLastWin32Error()}";
            CloseServiceHandle(hSvc);
            return null;
        }
        finally { CloseServiceHandle(hScm); }
    }

    private static string? ScmDelete(string key)
    {
        var hScm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (hScm == IntPtr.Zero)
            return $"OpenSCManager: {Marshal.GetLastWin32Error()}";
        try
        {
            var hSvc = OpenService(hScm, key, DELETE);
            if (hSvc == IntPtr.Zero)
                return $"OpenService: {Marshal.GetLastWin32Error()}";
            try
            {
                return DeleteService(hSvc) ? null
                    : $"DeleteService: {Marshal.GetLastWin32Error()}";
            }
            finally { CloseServiceHandle(hSvc); }
        }
        finally { CloseServiceHandle(hScm); }
    }
}
