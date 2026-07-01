using System.Diagnostics;
using Clinkon1C.Core;
using Microsoft.Win32;

namespace Clinkon1C.Modules.Firewall;

public class FirewallRule
{
    public string Direction { get; set; } = "";  // "In" / "Out"
    public bool   Enabled   { get; set; }
    public string Ports     { get; set; } = "";
    public string Action    { get; set; } = "";  // "Allow" / "Block"
}

public class FirewallModule
{
    public const string RuleName = "1c";
    public const string PortSpec = "1540,1541,1545,1550,1560-1591";

    private static readonly string[] RegPaths =
    {
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules",
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\Mdm\FirewallRules",
    };

    public List<FirewallRule> Rules { get; private set; } = new();

    public void Refresh()
    {
        Rules = ReadRules();
        Logger.Info($"FirewallModule: правил «{RuleName}» = {Rules.Count}");
    }

    // ── Чтение ────────────────────────────────────────────────────────────────

    private static List<FirewallRule> ReadRules()
    {
        var result = new List<FirewallRule>();
        foreach (var path in RegPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key == null) continue;
                foreach (var valueName in key.GetValueNames())
                {
                    var raw    = key.GetValue(valueName) as string ?? "";
                    var parsed = ParseEntry(raw);
                    if (parsed != null) result.Add(parsed);
                }
            }
            catch { }
        }
        return result;
    }

    // Формат: v2.10|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=...|Name=1c|...
    private static FirewallRule? ParseEntry(string val)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in val.Split('|'))
        {
            var idx = part.IndexOf('=');
            if (idx > 0) dict[part.Substring(0, idx)] = part.Substring(idx + 1);
        }

        if (!dict.TryGetValue("Name", out var name)) return null;
        if (!string.Equals(name, RuleName, StringComparison.OrdinalIgnoreCase)) return null;

        dict.TryGetValue("Dir",    out var dir);
        dict.TryGetValue("Action", out var action);
        dict.TryGetValue("LPort",  out var lport);
        dict.TryGetValue("Active", out var active);

        return new FirewallRule
        {
            Direction = dir    ?? "",
            Action    = action ?? "",
            Ports     = lport  ?? "",
            Enabled   = !string.Equals(active, "FALSE", StringComparison.OrdinalIgnoreCase),
        };
    }

    // ── Создать / обновить ───────────────────────────────────────────────────

    public string? CreateOrUpdate()
    {
        try
        {
            // Удаляем старые (игнорируем ошибку "не найдено")
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\" dir=in");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\" dir=out");

            var errIn = RunNetsh(
                $"advfirewall firewall add rule name=\"{RuleName}\" " +
                $"dir=in action=allow protocol=TCP localport={PortSpec}");
            if (errIn != null) return errIn;

            var errOut = RunNetsh(
                $"advfirewall firewall add rule name=\"{RuleName}\" " +
                $"dir=out action=allow protocol=TCP localport={PortSpec}");
            if (errOut != null) return errOut;

            Refresh();
            Logger.Info($"FirewallModule: правило «{RuleName}» создано, порты {PortSpec}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"FirewallModule.CreateOrUpdate: {ex.Message}");
            return ex.Message;
        }
    }

    // ── Удалить ───────────────────────────────────────────────────────────────

    public string? Delete()
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\" dir=in");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\" dir=out");
            Refresh();
            Logger.Info($"FirewallModule: правило «{RuleName}» удалено");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"FirewallModule.Delete: {ex.Message}");
            return ex.Message;
        }
    }

    // ── Вспомогательное ──────────────────────────────────────────────────────

    private static string? RunNetsh(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName               = "netsh.exe",
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            })!;
            p.WaitForExit(10_000);
            // ExitCode != 0 при "delete rule" если правила нет — это нормально
            if (p.ExitCode != 0 && args.Contains("add rule"))
                return $"netsh: код {p.ExitCode} — {p.StandardOutput.ReadToEnd().Trim()}";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
