using System.Diagnostics;
using System.IO.Compression;

namespace Clinkon1C.Core;

public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? ".") ?? ".",
        "clinkon1c.logs");
    private static readonly string LogFile = Path.Combine(LogDir, "clinkon.log");
    private static readonly object _lock = new();

    public static event Action<string, string>? MessageLogged;

    /// <summary>
    /// Вызывается один раз при запуске.
    /// Переименовывает текущий clinkon.log в clinkon_YYYYMMDD_HHMMSS.log,
    /// затем архивирует старые файлы в zip если их накопилось 10+.
    /// </summary>
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(LogDir);

            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 0)
            {
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Move(LogFile, Path.Combine(LogDir, $"clinkon_{ts}.log"));
            }

            ArchiveOld();
        }
        catch { }
    }

    public static void Info(string message)  => Write("INFO",  message);
    public static void Warn(string message)  => Write("WARN",  message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level,-7}  {"Clinkon1C",-16}  {message}";
            lock (_lock)
                File.AppendAllText(LogFile, line + Environment.NewLine);

            if (level == "ERROR")
                WriteEventLog(message);
        }
        catch { }

        try { MessageLogged?.Invoke(level, message); }
        catch { }
    }

    // Пакует ротированные логи по датам: все clinkon_YYYYMMDD_*.log не за сегодня
    // → archive_YYYYMMDD.zip (дополняет если уже существует)
    private static void ArchiveOld()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");

        var byDate = Directory.GetFiles(LogDir, "clinkon_????????_??????.log")
            .Select(f => (File: f, Date: ExtractDate(f)))
            .Where(x => x.Date != null && x.Date != today)
            .GroupBy(x => x.Date!);

        foreach (var group in byDate)
        {
            var zipPath = Path.Combine(LogDir, $"archive_{group.Key}.zip");
            try
            {
                var mode = File.Exists(zipPath) ? ZipArchiveMode.Update : ZipArchiveMode.Create;
                using var zip = ZipFile.Open(zipPath, mode);
                foreach (var (file, _) in group)
                {
                    var entry = Path.GetFileName(file);
                    if (zip.GetEntry(entry) == null)
                        zip.CreateEntryFromFile(file, entry);
                    File.Delete(file);
                }
            }
            catch { }
        }
    }

    // clinkon_20260625_143022.log → "20260625"
    private static string? ExtractDate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path); // clinkon_20260625_143022
        return name.Length >= 16 ? name.Substring(8, 8) : null;
    }

    private static void WriteEventLog(string message)
    {
        try
        {
            const string src = "Clinkon1C";
            if (!EventLog.SourceExists(src))
                EventLog.CreateEventSource(src, "Application");
            EventLog.WriteEntry(src, message, EventLogEntryType.Error);
        }
        catch { }
    }
}
