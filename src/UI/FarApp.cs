using System.Linq;
using Clinkon1C.Core;
using Clinkon1C.Modules.Agents;
using Clinkon1C.Modules.Bases;
using Clinkon1C.Modules.Cache;
using Clinkon1C.Modules.Configs;
using Clinkon1C.Modules.Diagnostics;
using Clinkon1C.Modules.Emulators;
using Clinkon1C.Modules.Licenses;
using Clinkon1C.Modules.Processes;
using Clinkon1C.Modules.Templates;
using Clinkon1C.Modules.COM;
using Clinkon1C.Modules.EventLog;
using Clinkon1C.Modules.Firewall;
using Clinkon1C.Modules.Web;

namespace Clinkon1C.UI;

// ── Модель навигации ─────────────────────────────────────────────────────────

internal enum NavLevelKind
{
    Home,            // главный экран: Кэш / Шаблоны
    CacheRoot,       // список пользователей или баз
    CacheUser,       // базы внутри пользователя
    CacheUnknown,    // неизвестные папки
    CachePaths,      // Local/Roaming пути одной базы
    TemplatesRoot,   // список пользователей с шаблонами
    TemplatesUser,   // шаблоны конкретного пользователя
    TemplatesGroup,  // содержимое одного шаблона (версии / подпапки)
    BasesRoot,       // список информационных баз
    LicensesRoot,    // список программных лицензий
    AgentsRoot,      // список служб ragent
    ProcessesRoot,   // список запущенных процессов 1С
    WebRoot,         // список веб-публикаций 1С (Apache)
    EmulatorsRoot,   // аудит эмуляторов HASP
    ConfigsRoot,     // конфигурационные файлы платформы
    ComRoot          // COM-коннектор 1С
}

internal class NavItem
{
    public string Name           { get; init; } = "";
    public long   SizeBytes      { get; init; }
    public bool   IsDead         { get; init; }
    public bool   IsExcluded     { get; init; }
    public bool   IsUp           { get; init; }  // строка [..]
    public bool   CanEnter       { get; init; }  // можно провалиться внутрь
    public bool   IsUnknownGroup { get; init; }  // группа [Неизвестные]
    public string? ModuleId      { get; init; }  // "cache" / "templates" / "bases"
    public string? Description   { get; init; }  // для Баз: Connect= строка; для Лицензий: тип
    public bool    ShowDescCol  { get; init; }  // двухколоночный режим без пометки
    // Физические пути для выделения/удаления
    public List<string> Paths { get; init; } = new List<string>();
    // Для drill-down
    public string? UserName { get; init; }
    public string? BaseName { get; init; }
    public string? PathType { get; init; }  // "Local" / "Roaming" / "Temp"
}

internal class NavLevel
{
    public NavLevelKind Kind  { get; init; } = NavLevelKind.Home;
    public string Title { get; init; } = "";
    public List<NavItem> Items { get; init; } = new List<NavItem>();
    public int     Cursor      { get; set; }
    public int     ScrollTop   { get; set; }
    // Контекст для drill-down
    public string? ContextUser { get; init; }  // для кэша — имя пользователя
    public string? ContextPath { get; init; }  // для TemplatesGroup — путь к папке шаблона
}

// ── Главное приложение ───────────────────────────────────────────────────────

public class FarApp
{
    private const string RepoUrl = "github.com/iMironRU/Clinkon1C";

    private readonly CacheModule     _cache;
    private readonly TemplatesModule _templates;
    private readonly BasesModule     _bases;
    private readonly LicensesModule  _licenses;
    private readonly RagentModule    _agents;
    private readonly ProcessesModule _processes;
    private readonly WebModule          _web;
    private readonly EmulatorModule     _emulators;
    private readonly ConfigsModule      _configs;
    private readonly DiagnosticsModule  _diagnostics;
    private readonly ComModule          _com;
    private readonly FirewallModule     _firewall  = new();
    private readonly EventLogModule     _eventLog  = new();
    private volatile string?          _updateNotice;
    private readonly Func<string?>?   _updateChecker;
    private readonly Action?          _updateAction;

    // Отмеченные базы (по Connect= строке как ключу)
    private readonly HashSet<string> _markedBases =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<NavLevel> _nav = new Stack<NavLevel>();
    private readonly HashSet<string> _sel =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly List<(string Lvl, string Txt)> _log =
        new List<(string, string)>();
    private readonly object _logLock = new object();
    private bool _running = true;

    // ── Раскладка экрана ─────────────────────────────────────────────────────
    private static int ItemTop    => 4;              // первая строка items
    private static int ItemBot    => R.H - 6;        // последняя строка items
    private static int ItemH      => Math.Max(1, R.H - 9); // кол-во видимых items
    private static int SepBot     => R.H - 5;        // ╠═══╣ нижний
    private static int InfoRow    => R.H - 4;        // info-строка внутри панели
    private static int BotBorder  => R.H - 3;        // ╚═══╝
    private static int MsgRow     => R.H - 2;        // сообщение лога
    private static int KeyRow     => R.H - 1;        // подсказки клавиш
    private static int SizeCW     => 12;             // ширина колонки размера
    private static int InnerW     => R.W - 2;        // ширина между ║...║
    private static int NameW      => InnerW - SizeCW; // ширина колонки имени

    // ── Запуск ───────────────────────────────────────────────────────────────
    public FarApp(CacheModule cache, TemplatesModule templates, BasesModule bases,
                  LicensesModule licenses, RagentModule agents, ProcessesModule processes,
                  WebModule web, EmulatorModule emulators, ConfigsModule configs,
                  DiagnosticsModule diagnostics, ComModule com,
                  string? updateNotice = null,
                  Func<string?>? updateChecker = null,
                  Action? updateAction = null)
    {
        _cache          = cache;
        _templates      = templates;
        _bases          = bases;
        _licenses       = licenses;
        _agents         = agents;
        _processes      = processes;
        _web            = web;
        _emulators      = emulators;
        _configs        = configs;
        _diagnostics    = diagnostics;
        _com            = com;
        _updateNotice   = updateNotice;
        _updateChecker  = updateChecker;
        _updateAction   = updateAction;
        Logger.MessageLogged += OnLog;
    }

    public void Run()
    {
        Console.CursorVisible = false;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        ConsoleInput.EnableMouse();
        try
        {
            R.Init();
            Rescan();
            StartUpdateWatcher();

            while (_running)
            {
                if (R.W < 40 || R.H < 14)
                {
                    Console.Clear();
                    Console.WriteLine("Терминал слишком маленький (мин. 40×14)");
                    Console.ReadKey(true);
                    continue;
                }
                Draw();

                // Читаем события до первого значимого (клавиша/клик/колесо)
                while (true)
                {
                    var rec = ConsoleInput.ReadOne();
                    if (rec.EventType == ConsoleInput.KEY_EVENT && rec.KeyEvent.bKeyDown != 0)
                    {
                        Handle(ConsoleInput.ToKeyInfo(rec.KeyEvent));
                        ConsoleInput.Flush(); // сбрасываем буфер мыши после диалогов
                        break;
                    }
                    if (rec.EventType == ConsoleInput.MOUSE_EVENT)
                    {
                        // Пропускаем чистые движения без кнопки/колеса
                        var m = rec.MouseEvent;
                        bool pureMove = (m.dwEventFlags == ConsoleInput.MOUSE_MOVED)
                                     && m.dwButtonState == 0;
                        if (!pureMove) { HandleMouse(m); break; }
                    }
                    // window-resize и прочее → сразу перерисовываем
                    else if (rec.EventType != ConsoleInput.MOUSE_EVENT) break;
                }
            }
        }
        finally
        {
            ConsoleInput.DisableMouse();
            Console.CursorVisible = true;
            Console.ResetColor();
            Console.Clear();
        }
    }

    // ── Сканирование ─────────────────────────────────────────────────────────
    private void StartUpdateWatcher()
    {
        if (_updateChecker == null || _updateNotice != null) return;

        var t = new Thread(() =>
        {
            int[] delays = { 5, 10, 30 }; // минуты: +5, потом ещё +10, потом ещё +30 = итого +5/+15/+45
            foreach (var min in delays)
            {
                Thread.Sleep(TimeSpan.FromMinutes(min));
                if (!_running) return;
                try
                {
                    var notice = _updateChecker();
                    if (notice != null)
                    {
                        _updateNotice = notice;
                        R.Invalidate();
                    }
                }
                catch { }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private void Rescan()
    {
        _nav.Clear();
        _sel.Clear();

        // Шаги: (метка прогресс-бара, действие)
        var steps = new (string Label, Action Run)[]
        {
            ("Кэш...",          () => _cache.Refresh()),
            ("Шаблоны...",      () => _templates.Refresh()),
            ("Базы...",         () => _bases.Refresh()),
            ("Лицензии...",     () => _licenses.Refresh()),
            ("Агенты...",       () => _agents.Refresh()),
            ("Процессы...",     () => _processes.Refresh()),
            ("Веб...",          () => _web.Refresh()),
            ("Эмуляторы...",    () => _emulators.Scan()),
            ("Конфиги...",      () => _configs.Refresh()),
            ("Диагностика...",  () => _diagnostics.ScanSync()),
            ("COM...",          () => _com.Scan()),
            ("Брандмауэр...",   () => _firewall.Refresh()),
            ("EventLog...",     () => _eventLog.Load()),
        };

        int total = steps.Length;
        for (int i = 0; i < total; i++)
        {
            ConsoleDialog.DrawProgressBar("Clinkon1C — Инициализация", steps[i].Label, i, total);
            try { steps[i].Run(); }
            catch (Exception ex) { Logger.Error($"Сканирование [{steps[i].Label}]: {ex.Message}"); }
        }
        ConsoleDialog.DrawProgressBar("Clinkon1C — Инициализация", "Готово", total, total);
        Thread.Sleep(120); // кратко показываем 100%

        R.Invalidate();
        _nav.Push(MakeHomeLevel());
    }


    /// <summary>
    /// Пересканирует только кэш и восстанавливает навигацию на прежнем уровне.
    /// Вызывается после удаления — чтобы не выбрасывало на главный экран.
    /// </summary>
    private void RescanCacheAndRestore()
    {
        // Запоминаем контекст (Stack перечисляется сверху вниз — самый глубокий уровень первым)
        string? savedUser = null;
        NavLevelKind targetKind = NavLevelKind.CacheRoot;

        foreach (var lvl in _nav)   // Stack<T> IEnumerable — top first
        {
            if (lvl.Kind == NavLevelKind.CacheUser || lvl.Kind == NavLevelKind.CacheUnknown)
            {
                if (savedUser == null) // берём самый глубокий cache-user уровень
                {
                    targetKind = lvl.Kind;
                    savedUser  = lvl.ContextUser;
                }
            }
        }

        // Пересканируем только кэш
        string status = "Обновление кэша...";
        bool   done   = false;
        int    spin   = 0;
        var    spinCh = new[] { '|', '/', '-', '\\' };

        var t = new Thread(() =>
        {
            try   { _cache.Refresh(msg => { status = msg; }); }
            catch (Exception ex) { Logger.Error($"Обновление: {ex.Message}"); }
            finally { done = true; }
        });
        t.IsBackground = true;
        t.Start();
        while (!done)
        {
            ConsoleDialog.DrawSpinner("Clinkon1C — Сканирование", status, spinCh[spin++ % spinCh.Length]);
            Thread.Sleep(100);
        }
        t.Join();

        R.Invalidate();

        // Восстанавливаем навигацию
        _nav.Clear();
        _nav.Push(MakeHomeLevel());
        _nav.Push(MakeCacheLevel());

        if (savedUser != null &&
            (targetKind == NavLevelKind.CacheUser || targetKind == NavLevelKind.CacheUnknown))
        {
            bool userHasEntries = _cache.Entries.Any(e =>
                string.Equals(e.UserName, savedUser, StringComparison.OrdinalIgnoreCase));
            if (userHasEntries)
                _nav.Push(MakeUserLevel(savedUser));
        }
    }


    // ── Построение уровней ───────────────────────────────────────────────────
    // ── Главный экран ────────────────────────────────────────────────────────

    private NavLevel MakeHomeLevel()
    {
        long total = _cache.TotalSize + _templates.TotalSize;

        var cachePaths = _cache.Entries
            .SelectMany(e => e.Paths.Select(p => p.Path)).ToList();
        var tmplPaths = _templates.Entries
            .Select(e => e.Path).ToList();

        return new NavLevel
        {
            Kind  = NavLevelKind.Home,
            Title = $"Clinkon1C  [{SafeDelete.FormatSize(total)}]",
            Items = new List<NavItem>
            {
                new NavItem
                {
                    Name     = "Кэш",
                    SizeBytes = _cache.TotalSize,
                    CanEnter = true,
                    ModuleId = "cache",
                    Paths    = cachePaths
                },
                new NavItem
                {
                    Name      = "Шаблоны",
                    SizeBytes = _templates.TotalSize,
                    CanEnter  = _templates.Entries.Count > 0,
                    ModuleId  = "templates",
                    Paths     = tmplPaths
                },
                new NavItem
                {
                    Name        = "Базы",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "bases",
                    Paths       = new List<string>(),
                    Description = $"{_bases.Entries.Count} записей"
                },
                new NavItem
                {
                    Name        = "Лицензии",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "licenses",
                    Paths       = new List<string>(),
                    Description = "ring license"
                },
                new NavItem
                {
                    Name        = "Агенты",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "agents",
                    Paths       = new List<string>(),
                    Description = "ragent.exe"
                },
                new NavItem
                {
                    Name        = "Процессы",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "processes",
                    Paths       = new List<string>(),
                    Description = "1С клиенты"
                },
                new NavItem
                {
                    Name        = "Веб",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "web",
                    Paths       = new List<string>(),
                    Description = "Apache / публикации"
                },
                new NavItem
                {
                    Name        = "Эмуляторы HASP",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "emulators",
                    Paths       = new List<string>(),
                    Description = "аудит лицензий"
                },
                new NavItem
                {
                    Name        = "Конфиги платформы",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "configs",
                    Paths       = new List<string>(),
                    Description = "conf.cfg, logcfg.xml ..."
                },
                new NavItem
                {
                    Name        = "COM Коннектор",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "com",
                    Paths       = new List<string>(),
                    Description = "comcntr.dll / COM+"
                },
                new NavItem
                {
                    Name        = "Брандмауэр",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "firewall",
                    Paths       = new List<string>(),
                    Description = "порты 1С (1540–1591)"
                },
                new NavItem
                {
                    Name        = "Журнал событий",
                    SizeBytes   = 0,
                    CanEnter    = true,
                    ModuleId    = "eventlog",
                    Paths       = new List<string>(),
                    Description = "Windows EventLog / 1C:Enterprise"
                }
            }
        };
    }

    private NavLevel MakeCacheLevel()
    {
        var items = new List<NavItem> { UpItem() };

        if (_cache.ViewMode == CacheViewMode.ByUser)
        {
            // ByUser: на корне — пользователи, неизвестных нет (они внутри каждого юзера)
            var groups = _cache.Entries
                .GroupBy(e => e.UserName, StringComparer.OrdinalIgnoreCase);
            var sorted = _cache.SortBy == SortMode.BySize
                ? groups.OrderByDescending(g => g.Sum(e => e.SizeBytes))
                : groups.OrderBy(g => g.Key);

            foreach (var g in sorted)
            {
                var paths = g.SelectMany(e => e.Paths.Select(p => p.Path)).ToList();
                items.Add(new NavItem
                {
                    Name      = g.Key,
                    SizeBytes = g.Sum(e => e.SizeBytes),
                    CanEnter  = true,
                    Paths     = paths,
                    UserName  = g.Key
                });
            }
        }
        else // ByBase
        {
            // Известные базы
            var knownGroups = _cache.Entries
                .Where(e => !e.IsDead)
                .GroupBy(e => e.BaseName, StringComparer.OrdinalIgnoreCase);
            var sorted = _cache.SortBy == SortMode.BySize
                ? knownGroups.OrderByDescending(g => g.Sum(e => e.SizeBytes))
                : knownGroups.OrderBy(g => g.Key);

            foreach (var g in sorted)
            {
                var paths = g.SelectMany(e => e.Paths.Select(p => p.Path)).ToList();
                items.Add(new NavItem
                {
                    Name      = g.Key,
                    SizeBytes = g.Sum(e => e.SizeBytes),
                    CanEnter  = paths.Count > 1,
                    Paths     = paths,
                    BaseName  = g.Key
                });
            }

            // Группа неизвестных (все пользователи)
            AddUnknownGroup(items, _cache.Entries.Where(e => e.IsDead).ToList(), null);
        }

        return new NavLevel
        {
            Kind  = NavLevelKind.CacheRoot,
            Title = $"Кэш [{SafeDelete.FormatSize(_cache.TotalSize)}]",
            Items = items
        };
    }

    private NavLevel MakeUserLevel(string user)
    {
        var entries = _cache.Entries
            .Where(e => string.Equals(e.UserName, user, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var known   = entries.Where(e => !e.IsDead && e.BaseName != "[Temp 1C]").ToList();
        var unknown = entries.Where(e =>  e.IsDead).ToList();
        var temp    = entries.Where(e => e.BaseName == "[Temp 1C]").ToList();

        // Сортируем известные
        var sortedKnown = _cache.SortBy == SortMode.BySize
            ? known.OrderByDescending(e => e.SizeBytes)
            : known.OrderBy(e => e.BaseName);

        var items = new List<NavItem> { UpItem() };

        foreach (var e in sortedKnown)
        {
            var paths = e.Paths.Select(p => p.Path).ToList();
            items.Add(new NavItem
            {
                Name      = e.BaseName,
                SizeBytes = e.SizeBytes,
                CanEnter  = e.Paths.Count > 1,
                Paths     = paths,
                UserName  = e.UserName,
                BaseName  = e.BaseName
            });
        }

        // Группа неизвестных — всегда в конце, перед Temp
        AddUnknownGroup(items, unknown, user);

        // Temp — всегда последний
        foreach (var e in temp)
        {
            var paths = e.Paths.Select(p => p.Path).ToList();
            items.Add(new NavItem
            {
                Name      = e.BaseName,
                SizeBytes = e.SizeBytes,
                Paths     = paths,
                UserName  = e.UserName,
                BaseName  = e.BaseName
            });
        }

        return new NavLevel
        {
            Kind        = NavLevelKind.CacheUser,
            Title       = $"{user}  →  {SafeDelete.FormatSize(entries.Sum(e => e.SizeBytes))}",
            Items       = items,
            ContextUser = user
        };
    }

    /// <summary>Добавляет группу [Неизвестные — N] если список не пуст.</summary>
    private static void AddUnknownGroup(
        List<NavItem> items, List<CacheEntry> unknowns, string? user)
    {
        if (unknowns.Count == 0) return;
        var paths     = unknowns.SelectMany(e => e.Paths.Select(p => p.Path)).ToList();
        long totalSz  = unknowns.Sum(e => e.SizeBytes);
        items.Add(new NavItem
        {
            Name           = $"[Неизвестные — {unknowns.Count}]",
            SizeBytes      = totalSz,
            CanEnter       = true,
            IsUnknownGroup = true,
            Paths          = paths,
            UserName       = user
        });
    }

    /// <summary>Уровень с неизвестными папками одного пользователя.</summary>
    private NavLevel MakeUnknownLevel(string user)
    {
        var unknowns = _cache.Entries
            .Where(e => string.Equals(e.UserName, user, StringComparison.OrdinalIgnoreCase)
                     && e.IsDead)
            .ToList();

        var sorted = _cache.SortBy == SortMode.BySize
            ? unknowns.OrderByDescending(e => e.SizeBytes)
            : unknowns.OrderBy(e => e.Uuid);

        var items = new List<NavItem> { UpItem() };
        foreach (var e in sorted)
        {
            var shortId = e.Uuid.Length >= 8 ? e.Uuid.Substring(0, 8) + "…" : e.Uuid;
            var paths   = e.Paths.Select(p => p.Path).ToList();
            items.Add(new NavItem
            {
                Name      = shortId,
                SizeBytes = e.SizeBytes,
                IsDead    = true,
                CanEnter  = e.Paths.Count > 1,
                Paths     = paths,
                UserName  = e.UserName,
                BaseName  = e.BaseName    // нужен для MakePathLevel lookup
            });
        }

        long total = unknowns.Sum(e => e.SizeBytes);
        return new NavLevel
        {
            Kind        = NavLevelKind.CacheUnknown,
            Title       = $"{user}  →  Неизвестные [{SafeDelete.FormatSize(total)}]",
            Items       = items,
            ContextUser = user
        };
    }

    /// <summary>Уровень с неизвестными папками всех пользователей (для ByBase).</summary>
    private NavLevel MakeUnknownFlatLevel()
    {
        var unknowns = _cache.Entries.Where(e => e.IsDead).ToList();

        var sorted = _cache.SortBy == SortMode.BySize
            ? unknowns.OrderByDescending(e => e.SizeBytes)
            : unknowns.OrderBy(e => e.Uuid);

        var items = new List<NavItem> { UpItem() };
        foreach (var e in sorted)
        {
            var shortId = e.Uuid.Length >= 8 ? e.Uuid.Substring(0, 8) + "…" : e.Uuid;
            var paths   = e.Paths.Select(p => p.Path).ToList();
            items.Add(new NavItem
            {
                Name      = $"{shortId}  ({e.UserName})",
                SizeBytes = e.SizeBytes,
                IsDead    = true,
                CanEnter  = e.Paths.Count > 1,
                Paths     = paths,
                UserName  = e.UserName,
                BaseName  = e.BaseName
            });
        }

        long total = unknowns.Sum(e => e.SizeBytes);
        return new NavLevel
        {
            Kind  = NavLevelKind.CacheUnknown,
            Title = $"Неизвестные — все пользователи [{SafeDelete.FormatSize(total)}]",
            Items = items
        };
    }

    private NavLevel MakePathLevel(CacheEntry entry)
    {
        var items = new List<NavItem> { UpItem() };
        foreach (var cp in entry.Paths)
        {
            var name = $"{cp.Type,-8}  {ShortenPath(cp.Path)}";
            items.Add(new NavItem
            {
                Name      = name,
                SizeBytes = cp.SizeBytes,
                Paths     = new List<string> { cp.Path },
                UserName  = entry.UserName,
                BaseName  = entry.BaseName,
                PathType  = cp.Type
            });
        }

        return new NavLevel
        {
            Kind        = NavLevelKind.CachePaths,
            Title       = $"{entry.UserName}  →  {entry.BaseName}" +
                          $"  [{SafeDelete.FormatSize(entry.SizeBytes)}]",
            Items       = items,
            ContextUser = entry.UserName
        };
    }

    private NavLevel MakeBaseFlatLevel(string baseName)
    {
        // ByBase: все пути этой базы из всех пользователей
        var entries = _cache.Entries
            .Where(e => string.Equals(e.BaseName, baseName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var items = new List<NavItem> { UpItem() };
        foreach (var e in entries)
        {
            foreach (var cp in e.Paths)
            {
                var name = $"{cp.Type,-8}  {e.UserName}  {ShortenPath(cp.Path)}";
                items.Add(new NavItem
                {
                    Name      = name,
                    SizeBytes = cp.SizeBytes,
                    Paths     = new List<string> { cp.Path },
                    UserName  = e.UserName,
                    BaseName  = e.BaseName,
                    PathType  = cp.Type
                });
            }
        }

        var total = entries.Sum(e => e.SizeBytes);
        return new NavLevel
        {
            Title = $"{baseName}  [{SafeDelete.FormatSize(total)}]",
            Items = items
        };
    }

    // ── Шаблоны ──────────────────────────────────────────────────────────────

    private NavLevel MakeTemplatesLevel()
    {
        var items = new List<NavItem> { UpItem() };

        var groups = _templates.Entries
            .GroupBy(e => e.UserName, StringComparer.OrdinalIgnoreCase);
        var sorted = _cache.SortBy == SortMode.BySize
            ? groups.OrderByDescending(g => g.Sum(e => e.SizeBytes))
            : groups.OrderBy(g => g.Key);

        foreach (var g in sorted)
        {
            long sz    = g.Sum(e => e.SizeBytes);
            var  paths = g.Select(e => e.Path).ToList();
            items.Add(new NavItem
            {
                Name      = g.Key,
                SizeBytes = sz,
                CanEnter  = true,
                Paths     = paths,
                UserName  = g.Key
            });
        }

        return new NavLevel
        {
            Kind  = NavLevelKind.TemplatesRoot,
            Title = $"Шаблоны [{SafeDelete.FormatSize(_templates.TotalSize)}]",
            Items = items
        };
    }

    private NavLevel MakeTemplatesUserLevel(string user)
    {
        var entries = _templates.Entries
            .Where(e => string.Equals(e.UserName, user, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sorted = _cache.SortBy == SortMode.BySize
            ? entries.OrderByDescending(e => e.SizeBytes)
            : entries.OrderBy(e => e.Name);

        var items = new List<NavItem> { UpItem() };
        foreach (var e in sorted)
        {
            // Директории можно открыть (внутри — версии/подпапки)
            bool isDir = Directory.Exists(e.Path);
            items.Add(new NavItem
            {
                Name      = e.Name,
                SizeBytes = e.SizeBytes,
                CanEnter  = isDir,
                Paths     = new List<string> { e.Path },
                UserName  = e.UserName
            });
        }

        long total = entries.Sum(e => e.SizeBytes);
        return new NavLevel
        {
            Kind        = NavLevelKind.TemplatesUser,
            Title       = $"{user}  →  Шаблоны [{SafeDelete.FormatSize(total)}]",
            Items       = items,
            ContextUser = user
        };
    }

    /// <summary>Содержимое одного шаблона: версии, подпапки, файлы.</summary>
    private NavLevel MakeTemplatesGroupLevel(string groupPath, string groupName)
    {
        var items = new List<NavItem> { UpItem() };

        try
        {
            var entries = Directory.GetFileSystemEntries(groupPath)
                .Select(p => (
                    Path: p,
                    Name: Path.GetFileName(p),
                    Size: SafeDelete.Measure(p).size,
                    IsDir: Directory.Exists(p)))
                .ToList();

            var sorted = _cache.SortBy == SortMode.BySize
                ? entries.OrderByDescending(e => e.Size)
                : entries.OrderBy(e => e.Name);

            foreach (var e in sorted)
            {
                items.Add(new NavItem
                {
                    Name      = e.Name,
                    SizeBytes = e.Size,
                    // Подпапки тоже можно открыть для просмотра/удаления отдельных файлов
                    CanEnter  = e.IsDir,
                    Paths     = new List<string> { e.Path }
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"TemplatesGroup: {groupPath}: {ex.Message}");
        }

        long total = items.Where(i => !i.IsUp).Sum(i => i.SizeBytes);
        return new NavLevel
        {
            Kind        = NavLevelKind.TemplatesGroup,
            Title       = $"{groupName}  [{SafeDelete.FormatSize(total)}]",
            Items       = items,
            ContextPath = groupPath
        };
    }

    // ── Базы ─────────────────────────────────────────────────────────────────

    private NavLevel MakeBasesLevel()
    {
        var items = new List<NavItem> { UpItem() };

        var sorted = _cache.SortBy == SortMode.BySize
            ? _bases.Entries.OrderByDescending(e => e.Name)
            : _bases.Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var e in sorted)
        {
            // Укорачиваем Connect= для отображения
            var conn = e.Connect;
            if (conn.Length > 45) conn = conn.Substring(0, 42) + "…";

            items.Add(new NavItem
            {
                Name        = e.Name,
                SizeBytes   = 0,
                BaseName    = e.Connect,    // ключ для _markedBases
                Description = conn          // строка подключения для отображения
            });
        }

        return new NavLevel
        {
            Kind  = NavLevelKind.BasesRoot,
            Title = $"Базы [{_bases.Entries.Count}]",
            Items = items
        };
    }

    // ── Лицензии ──────────────────────────────────────────────────────────────

    private NavLevel MakeLicensesLevel()
    {
        var items = new List<NavItem> { UpItem() };

        var sorted = _licenses.Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var e in sorted)
        {
            var assoc = e.AssociationType == "HardwareProtectionKey" ? "HASP" :
                        e.AssociationType == "Computer"              ? "ПК"   : e.AssociationType;
            var desc  = string.IsNullOrEmpty(assoc) ? e.LicenseType
                      : string.IsNullOrEmpty(e.LicenseType) ? assoc
                      : $"{assoc} / {e.LicenseType}";

            items.Add(new NavItem
            {
                Name         = e.Name,
                SizeBytes    = 0,
                Description  = desc,
                Paths        = new List<string>(),
                CanEnter     = true, // Enter → показать полный info
                ShowDescCol  = true
            });
        }

        return new NavLevel
        {
            Kind  = NavLevelKind.LicensesRoot,
            Title = $"Лицензии [{_licenses.Entries.Count}]",
            Items = items
        };
    }

    // ── Агенты ───────────────────────────────────────────────────────────────

    private NavLevel MakeAgentsLevel()
    {
        var items = new List<NavItem> { UpItem() };

        foreach (var e in _agents.Entries)
        {
            items.Add(new NavItem
            {
                Name        = e.DisplayName,
                BaseName    = e.ServiceKey,
                PathType    = e.Version,
                Description = StatusDisplay(e.Status),
                ShowDescCol = true,
                CanEnter    = true,
            });
        }

        // ── RAS ──────────────────────────────────────────────────────────────
        items.Add(new NavItem { Name = "── RAS (Remote Server) ─────────────────────", IsDead = true });

        foreach (var e in _agents.RasEntries)
        {
            items.Add(new NavItem
            {
                Name        = e.DisplayName,
                BaseName    = "RAS:" + e.ServiceKey,
                PathType    = e.Version,
                Description = StatusDisplay(e.Status),
                ShowDescCol = true,
                CanEnter    = true,
            });
        }

        var installedVers = _agents.RasEntries
            .Select(r => r.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (ver, exe) in RagentModule.FindRasVersions())
        {
            if (installedVers.Contains(ver)) continue;
            items.Add(new NavItem
            {
                Name        = $"+ Установить RAS {ver}",
                BaseName    = $"INSTALL_RAS:{ver}|{exe}",
                Description = "не зарегистрирован",
                ShowDescCol = true,
                CanEnter    = true,
            });
        }

        if (_agents.RasEntries.Count == 0 && RagentModule.FindRasVersions().Count == 0)
            items.Add(new NavItem { Name = "(ras.exe не найден)", IsDead = true });

        return new NavLevel
        {
            Kind  = NavLevelKind.AgentsRoot,
            Title = $"Агенты [{_agents.Entries.Count}]  RAS [{_agents.RasEntries.Count}]",
            Items = items
        };
    }

    private NavLevel MakeProcessesLevel()
    {
        var items = new List<NavItem> { UpItem() };

        foreach (var e in _processes.Entries)
        {
            var dbDisplay = string.IsNullOrEmpty(e.DbName)
                ? (string.IsNullOrEmpty(e.DbPath) ? e.ExeName : e.DbPath)
                : e.DbName;
            var typePrefix = e.DbType switch
            {
                "Файл"  => "[Ф] ",
                "Сервер"=> "[С] ",
                "Веб"   => "[В] ",
                _       => ""
            };
            dbDisplay = typePrefix + dbDisplay;

            items.Add(new NavItem
            {
                Name        = dbDisplay,
                BaseName    = e.Pid.ToString(),     // PID для операций
                PathType    = e.Mode,               // Предприятие / Конфигуратор
                UserName    = e.User1C,             // пользователь 1С
                Description = e.WinUser,            // Windows-пользователь
                CanEnter    = true,
            });
        }

        return new NavLevel
        {
            Kind  = NavLevelKind.ProcessesRoot,
            Title = $"Процессы 1С [{_processes.Entries.Count}]",
            Items = items
        };
    }

    private NavLevel MakeWebLevel()
    {
        var items = new List<NavItem> { UpItem() };

        if (!_web.ApacheFound)
        {
            items.Add(new NavItem
            {
                Name        = "(Apache не обнаружен)",
                Description = "Нет конфигурационного файла",
                IsDead      = true,
                CanEnter    = false,
            });
        }
        else
        {
            foreach (var e in _web.Entries)
            {
                items.Add(new NavItem
                {
                    Name        = e.Alias,
                    BaseName    = e.Alias,         // ключ для lookup
                    PathType    = e.DbType,        // Файл / Сервер
                    Description = e.DbName,        // имя базы
                    UserName    = e.Version,        // версия 1С
                    IsDead      = !e.Enabled,       // серый цвет для выключенных
                    CanEnter    = true,
                });
            }
        }

        var apacheSt = !_web.ApacheFound
            ? "не найден"
            : (_web.ApacheRunning ? "▶ Работает" : "■ Остановлен");

        return new NavLevel
        {
            Kind  = NavLevelKind.WebRoot,
            Title = $"Веб-публикации 1С  [{_web.Entries.Count}]  Apache: {apacheSt}",
            Items = items
        };
    }

    private NavLevel MakeEmulatorsLevel()
    {
        var items = new List<NavItem> { UpItem() };

        foreach (var e in _emulators.Entries)
        {
            items.Add(new NavItem
            {
                Name        = e.Name,
                Description = e.Found ? e.Summary() : "не обнаружен",
                ShowDescCol = true,
                CanEnter    = e.Found,
                IsDead      = !e.Found,
                BaseName    = e.Name
            });
        }

        int foundCount = _emulators.Found.Count;
        return new NavLevel
        {
            Kind  = NavLevelKind.EmulatorsRoot,
            Title = $"Эмуляторы HASP  [{foundCount} найдено из {EmulatorModule.KnownEmulators.Length}]",
            Items = items
        };
    }

    private NavLevel MakeConfigsLevel()
    {
        var items = new List<NavItem> { UpItem() };
        foreach (var f in _configs.Files)
        {
            items.Add(new NavItem
            {
                Name        = f.DisplayName,
                Description = f.Found ? f.Path : "не найден",
                ShowDescCol = true,
                CanEnter    = f.CanEdit,
                IsDead      = !f.Found,
                BaseName    = f.Id,
                Paths       = f.Found ? new List<string> { f.Path! } : new List<string>()
            });
        }
        int found = _configs.Files.Count(f => f.Found);
        return new NavLevel
        {
            Kind  = NavLevelKind.ConfigsRoot,
            Title = $"Конфиги платформы  [{found} найдено из {_configs.Files.Count}]",
            Items = items
        };
    }

    private static string StatusDisplay(string status) => status switch
    {
        "Running"      => "▶ Работает",
        "Stopped"      => "■ Остановлен",
        "StartPending" => "⏳ Запуск...",
        "StopPending"  => "⏳ Остановка...",
        "Paused"       => "⏸ Пауза",
        _              => $"? {status}",
    };

    private static string ShortenPath(string path)
    {
        const int max = 38;
        if (path.Length <= max) return path;
        return "…" + path.Substring(path.Length - (max - 1));
    }

    private static NavItem UpItem() =>
        new NavItem { Name = "[..]", IsUp = true };

    // ── Навигация ─────────────────────────────────────────────────────────────
    private void Enter()
    {
        var lvl  = _nav.Peek();
        var item = lvl.Items[lvl.Cursor];
        if (item.IsUp) { GoUp(); return; }
        if (!item.CanEnter) return;

        switch (lvl.Kind)
        {
            case NavLevelKind.Home:
                if (item.ModuleId == "cache")
                    _nav.Push(MakeCacheLevel());
                else if (item.ModuleId == "templates")
                    _nav.Push(MakeTemplatesLevel());
                else if (item.ModuleId == "bases")
                    _nav.Push(MakeBasesLevel());
                else if (item.ModuleId == "licenses")
                    EnterLicenses();
                else if (item.ModuleId == "agents")
                    EnterAgents();
                else if (item.ModuleId == "processes")
                    EnterProcesses();
                else if (item.ModuleId == "web")
                    EnterWeb();
                else if (item.ModuleId == "emulators")
                    EnterEmulators();
                else if (item.ModuleId == "configs")
                    EnterConfigs();
                else if (item.ModuleId == "com")
                    EnterCom();
                else if (item.ModuleId == "firewall")
                    DoFirewallInfo();
                else if (item.ModuleId == "eventlog")
                    DoEventLogInfo();
                break;

            case NavLevelKind.CacheRoot:
                if (item.IsUnknownGroup)
                    _nav.Push(MakeUnknownFlatLevel());
                else if (_cache.ViewMode == CacheViewMode.ByUser && item.UserName != null)
                    _nav.Push(MakeUserLevel(item.UserName));
                else if (_cache.ViewMode == CacheViewMode.ByBase && item.BaseName != null)
                    _nav.Push(MakeBaseFlatLevel(item.BaseName));
                break;

            case NavLevelKind.CacheUser:
                if (item.IsUnknownGroup && item.UserName != null)
                    _nav.Push(MakeUnknownLevel(item.UserName));
                else if (item.UserName != null && item.BaseName != null)
                {
                    var entry = _cache.Entries.FirstOrDefault(e =>
                        string.Equals(e.UserName, item.UserName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.BaseName, item.BaseName, StringComparison.OrdinalIgnoreCase));
                    if (entry != null && entry.Paths.Count > 1)
                        _nav.Push(MakePathLevel(entry));
                }
                break;

            case NavLevelKind.CacheUnknown:
                if (item.UserName != null && item.BaseName != null)
                {
                    var entry = _cache.Entries.FirstOrDefault(e =>
                        string.Equals(e.UserName, item.UserName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.BaseName, item.BaseName, StringComparison.OrdinalIgnoreCase));
                    if (entry != null && entry.Paths.Count > 1)
                        _nav.Push(MakePathLevel(entry));
                }
                break;

            case NavLevelKind.TemplatesRoot:
                if (item.UserName != null)
                    _nav.Push(MakeTemplatesUserLevel(item.UserName));
                break;

            case NavLevelKind.TemplatesUser:
                // Шаблон-директория → показываем версии/подпапки
                if (item.Paths.Count > 0)
                    _nav.Push(MakeTemplatesGroupLevel(item.Paths[0], item.Name));
                break;

            case NavLevelKind.TemplatesGroup:
                // Подпапка версии → ещё один уровень (рекурсивно)
                if (item.Paths.Count > 0)
                    _nav.Push(MakeTemplatesGroupLevel(item.Paths[0], item.Name));
                break;

            case NavLevelKind.LicensesRoot:
                DoShowLicenseInfo(item.Name);
                break;

            case NavLevelKind.AgentsRoot:
                if (item.BaseName?.StartsWith("RAS:") == true)
                    DoRasInfo(item.BaseName.Substring(4));
                else if (item.BaseName?.StartsWith("INSTALL_RAS:") == true)
                    DoRasInstall(item.BaseName.Substring(12));
                else
                    DoAgentInfo(item.BaseName);
                break;

            case NavLevelKind.ProcessesRoot:
                DoProcessInfo(item.BaseName);
                break;

            case NavLevelKind.WebRoot:
                if (!item.IsDead) DoWebInfo(item.BaseName);
                break;

            case NavLevelKind.EmulatorsRoot:
                DoEmulatorInfo(item.BaseName);
                break;

            case NavLevelKind.ConfigsRoot:
                DoEditConfig(item.BaseName);
                break;

            case NavLevelKind.ComRoot:
                DoComAction(item);
                break;
        }
    }

    private void GoUp()
    {
        if (_nav.Count > 1) _nav.Pop();
    }

    /// <summary>Пересобирает текущий уровень (после смены сортировки или вида).</summary>
    private void RebuildCurrentLevel()
    {
        if (_nav.Count == 0) return;
        var cur = _nav.Peek();
        NavLevel? rebuilt = null;

        switch (cur.Kind)
        {
            case NavLevelKind.Home:          rebuilt = MakeHomeLevel();  break;
            case NavLevelKind.CacheRoot:     rebuilt = MakeCacheLevel(); break;
            case NavLevelKind.CacheUser:
                if (cur.ContextUser != null) rebuilt = MakeUserLevel(cur.ContextUser);
                break;
            case NavLevelKind.CacheUnknown:
                if (cur.ContextUser != null) rebuilt = MakeUnknownLevel(cur.ContextUser);
                else                          rebuilt = MakeUnknownFlatLevel();
                break;
            case NavLevelKind.BasesRoot:     rebuilt = MakeBasesLevel();     break;
            case NavLevelKind.LicensesRoot:  rebuilt = MakeLicensesLevel();  break;
            case NavLevelKind.AgentsRoot:    rebuilt = MakeAgentsLevel();    break;
            case NavLevelKind.ProcessesRoot: rebuilt = MakeProcessesLevel(); break;
            case NavLevelKind.WebRoot:       rebuilt = MakeWebLevel();       break;
            case NavLevelKind.EmulatorsRoot: rebuilt = MakeEmulatorsLevel(); break;
            case NavLevelKind.ConfigsRoot:   rebuilt = MakeConfigsLevel();   break;
            case NavLevelKind.ComRoot:       rebuilt = MakeComLevel();       break;
            case NavLevelKind.TemplatesRoot: rebuilt = MakeTemplatesLevel(); break;
            case NavLevelKind.TemplatesUser:
                if (cur.ContextUser != null) rebuilt = MakeTemplatesUserLevel(cur.ContextUser);
                break;
            case NavLevelKind.TemplatesGroup:
                if (cur.ContextPath != null)
                    rebuilt = MakeTemplatesGroupLevel(
                        cur.ContextPath, Path.GetFileName(cur.ContextPath) ?? cur.Title);
                break;
        }

        if (rebuilt == null) return;
        // Сохраняем позицию курсора
        rebuilt.Cursor    = Math.Min(cur.Cursor,    Math.Max(0, rebuilt.Items.Count - 1));
        rebuilt.ScrollTop = Math.Min(cur.ScrollTop, Math.Max(0, rebuilt.Items.Count - 1));
        _nav.Pop();
        _nav.Push(rebuilt);
    }

    private void ToggleSel()
    {
        var lvl = _nav.Peek();

        // Отдельная логика для раздела Баз
        if (lvl.Kind == NavLevelKind.BasesRoot)
        {
            var bItem = lvl.Items[lvl.Cursor];
            if (!bItem.IsUp && bItem.BaseName != null)
            {
                if (!_markedBases.Remove(bItem.BaseName))
                    _markedBases.Add(bItem.BaseName);
                MoveCursor(1);
            }
            return;
        }

        var item = lvl.Items[lvl.Cursor];
        if (item.IsUp || item.IsExcluded || item.Paths.Count == 0) return;

        bool allSel = item.Paths.All(p => _sel.Contains(p));
        if (allSel) foreach (var p in item.Paths) _sel.Remove(p);
        else         foreach (var p in item.Paths) _sel.Add(p);

        // Сдвигаем курсор вниз
        MoveCursor(1);
    }

    private NavItem? CurrentItem()
    {
        if (_nav.Count == 0) return null;
        var lvl = _nav.Peek();
        if (lvl.Items.Count == 0) return null;
        return lvl.Items[lvl.Cursor];
    }

    private void MoveCursor(int delta)
    {
        var lvl = _nav.Peek();
        int n = lvl.Items.Count;
        lvl.Cursor = Math.Max(0, Math.Min(n - 1, lvl.Cursor + delta));
        if (lvl.Cursor < lvl.ScrollTop)
            lvl.ScrollTop = lvl.Cursor;
        else if (lvl.Cursor >= lvl.ScrollTop + ItemH)
            lvl.ScrollTop = lvl.Cursor - ItemH + 1;
    }

    // ── Обработка клавиш ─────────────────────────────────────────────────────
    private void Handle(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.UpArrow:   MoveCursor(-1); break;
            case ConsoleKey.DownArrow: MoveCursor(+1); break;
            case ConsoleKey.PageUp:    MoveCursor(-ItemH); break;
            case ConsoleKey.PageDown:  MoveCursor(+ItemH); break;

            case ConsoleKey.Home:
                var lh = _nav.Peek();
                lh.Cursor = 0; lh.ScrollTop = 0;
                break;
            case ConsoleKey.End:
                var le = _nav.Peek();
                le.Cursor = Math.Max(0, le.Items.Count - 1);
                le.ScrollTop = Math.Max(0, le.Cursor - ItemH + 1);
                break;

            case ConsoleKey.Enter:
            case ConsoleKey.RightArrow:
                Enter();
                break;

            case ConsoleKey.Backspace:
            case ConsoleKey.LeftArrow:
                GoUp();
                break;

            case ConsoleKey.Spacebar:
                ToggleSel();
                break;

            case ConsoleKey.Escape:
                _sel.Clear();
                _markedBases.Clear();
                break;

            case ConsoleKey.F8:
                if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.LicensesRoot)
                    DoRemoveLicense();
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.AgentsRoot)
                    DoAgentDelete();
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.ProcessesRoot)
                    DoProcessKill();
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.WebRoot)
                    DoWebUnpublish();
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.EmulatorsRoot)
                    DoEmulatorRemove();
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.ComRoot)
                    DoComDelete();
                else
                    DoDelete();
                break;

            // Dry Run — работает, но не показываем в подсказке (скрытая функция)
            case ConsoleKey.Delete when (k.Modifiers & ConsoleModifiers.Shift) != 0:
                DoDryRun();
                break;

            case ConsoleKey.F5:
                if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.AgentsRoot)
                {
                    ConsoleDialog.ShowProgress("Обновление...", _ => _agents.Refresh());
                    R.Invalidate();
                    RebuildCurrentLevel();
                }
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.ProcessesRoot)
                {
                    ConsoleDialog.ShowProgress("Обновление...", _ => _processes.Refresh());
                    R.Invalidate();
                    RebuildCurrentLevel();
                }
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.WebRoot)
                {
                    ConsoleDialog.ShowProgress("Обновление...", _ => _web.Refresh());
                    R.Invalidate();
                    RebuildCurrentLevel();
                }
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.EmulatorsRoot)
                {
                    ConsoleDialog.ShowProgress("Сканирование...", _ => _emulators.Scan());
                    R.Invalidate();
                    RebuildCurrentLevel();
                }
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.ConfigsRoot)
                {
                    ConsoleDialog.ShowProgress("Обновление...", _ => _configs.Refresh());
                    R.Invalidate();
                    RebuildCurrentLevel();
                }
                else if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.Home)
                {
                    ConsoleDialog.ShowProgress("Сканирование системы...", _ => _diagnostics.ScanSync());
                    R.Invalidate();
                }
                else
                    Rescan();
                break;

            case ConsoleKey.F1:
                ShowHelp();
                break;

            case ConsoleKey.F10:
                _running = false;
                break;

            case ConsoleKey.Tab:
                ConsoleDialog.ShowLog(() => { lock (_logLock) { return _log.ToArray(); } });
                break;

            case ConsoleKey.U when _updateNotice != null && _updateAction != null:
                _updateAction();
                break;

            default:
                var kind2 = _nav.Count > 0 ? _nav.Peek().Kind : NavLevelKind.Home;
                char ch = char.ToLower(k.KeyChar);
                if (kind2 == NavLevelKind.AgentsRoot)
                {
                    if      (ch == 's') DoAgentStart();
                    else if (ch == 't') DoAgentStop();
                    else if (ch == 'r') DoAgentRestart();
                    else if (ch == 'd') DoAgentToggleDebug();
                    else if (ch == 'n') DoAgentNew();
                }
                else if (kind2 == NavLevelKind.ProcessesRoot)
                {
                    if      (ch == 'k') DoProcessKill();
                    else if (ch == 'a') DoProcessKillAll();
                }
                else if (kind2 == NavLevelKind.WebRoot)
                {
                    if      (ch == 'p') DoWebPublish();
                    else if (ch == 'a') DoWebAnon(CurrentItem()?.BaseName);
                    else if (ch == 'e') DoWebEdit(CurrentItem()?.BaseName);
                    else if (ch == 'j') DoWebJwt(CurrentItem()?.BaseName);
                    else if (ch == 's') DoWebApacheOp("start");
                    else if (ch == 't') DoWebApacheOp("stop");
                    else if (ch == 'r') DoWebApacheOp("restart");
                }
                else if (kind2 == NavLevelKind.EmulatorsRoot)
                {
                    if (ch == 'd') DoEmulatorRemove();
                }
                else if (ch == 'v' &&
                    (kind2 == NavLevelKind.CacheRoot || kind2 == NavLevelKind.CacheUser))
                {
                    _cache.ViewMode = _cache.ViewMode == CacheViewMode.ByUser
                        ? CacheViewMode.ByBase : CacheViewMode.ByUser;
                    RebuildCurrentLevel();
                }
                else if (ch == 's')
                {
                    _cache.SortBy = _cache.SortBy == SortMode.ByName
                        ? SortMode.BySize : SortMode.ByName;
                    RebuildCurrentLevel();
                }
                else if (ch == 'c' && kind2 == NavLevelKind.BasesRoot)
                    DoCopyBases();
                else if (ch == 'e' && kind2 == NavLevelKind.BasesRoot)
                    DoExportBases();
                else if (ch == 'a' && kind2 == NavLevelKind.LicensesRoot)
                    DoActivateLicense();
                else if (ch == 'v' && kind2 == NavLevelKind.LicensesRoot)
                    DoValidateLicense();
                break;
        }
    }

    // ── Обработка мыши ───────────────────────────────────────────────────────
    private void HandleMouse(ConsoleInput.MOUSE_EVENT_RECORD m)
    {
        // Колесо мыши — прокрутка списка
        if ((m.dwEventFlags & ConsoleInput.MOUSE_WHEELED) != 0)
        {
            // Старший WORD dwButtonState — знаковый delta (положительный = вверх)
            int delta = (short)(m.dwButtonState >> 16) > 0 ? -3 : 3;
            MoveCursor(delta);
            return;
        }

        // Обрабатываем только нажатие левой кнопки
        if ((m.dwButtonState & ConsoleInput.LEFT_BUTTON) == 0) return;

        bool dbl = (m.dwEventFlags & ConsoleInput.DOUBLE_CLICK) != 0;
        int  y   = m.MouseY;

        // Клик по области элементов списка
        if (y >= ItemTop && y <= ItemBot)
        {
            var lvl = _nav.Peek();
            int idx = lvl.ScrollTop + (y - ItemTop);
            if (idx < 0 || idx >= lvl.Items.Count) return;

            if (lvl.Cursor == idx)
            {
                // Повторный клик / двойной клик → Enter
                if (dbl) Enter();
            }
            else
            {
                // Первый клик → переместить курсор
                lvl.Cursor = idx;
                if (dbl) Enter(); // сразу активировать при двойном
            }
        }

        // Клик по [..] в строке заголовка не обрабатываем — для этого есть Backspace/←
    }

    // ── Действия ─────────────────────────────────────────────────────────────
    private List<string> TargetPaths()
    {
        if (_sel.Count > 0) return _sel.ToList();
        var item = _nav.Peek().Items[_nav.Peek().Cursor];
        if (item.IsUp || item.Paths.Count == 0) return new List<string>();
        return item.Paths;
    }

    private void DoDryRun()
    {
        var paths = TargetPaths();
        if (paths.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ПРЕДПРОСМОТР — ничего не удаляется]");
        sb.AppendLine();
        long total = 0;
        foreach (var p in paths)
        {
            var (sz, f, d) = SafeDelete.Measure(p);
            total += sz;
            sb.AppendLine("  " + p);
            sb.AppendLine($"    {SafeDelete.FormatSize(sz)}, файлов: {f}, папок: {d}");
        }
        sb.AppendLine();
        sb.AppendLine("Итого: " + SafeDelete.FormatSize(total));
        ConsoleDialog.ShowText("Dry Run — предпросмотр", sb.ToString());
        R.Invalidate();
    }

    private void DoDelete()
    {
        var paths = TargetPaths();
        if (paths.Count == 0) return;

        long total = 0;
        foreach (var p in paths) { var (sz, _, _) = SafeDelete.Measure(p); total += sz; }

        bool ok;
        if (total > 10L * 1024 * 1024 * 1024)
            ok = ConsoleDialog.ConfirmWord("ПОДТВЕРЖДЕНИЕ",
                $"Удалить {paths.Count} объект(а)?\nОбъём: {SafeDelete.FormatSize(total)}",
                "УДАЛИТЬ");
        else
            ok = ConsoleDialog.Confirm("Подтверждение удаления",
                $"Удалить {paths.Count} объект(а)?\nОбъём: {SafeDelete.FormatSize(total)}");

        R.Invalidate(); // после диалога подтверждения — восстанавливаем панель
        if (!ok) return;

        if (ProcessHelper.AnyRunning1CProcesses())
        {
            if (!ConsoleDialog.Confirm("Предупреждение", "Запущены процессы 1С!\nПродолжить?"))
            { R.Invalidate(); return; }
            R.Invalidate();
        }

        Logger.Info($"Удаление: {paths.Count} путей");

        // Удаляем в фоне — показываем спиннер (иначе экран зависает)
        DeleteResult? res = null;
        bool delDone = false;
        var delThread = new Thread(() =>
        {
            try
            {
                res = SafeDelete.Delete(paths,
                    RegistryHelper.BackupEnabled,
                    RegistryHelper.BackupEnabled ? RegistryHelper.BackupPath : null,
                    SafeDelete.CacheProtectedMasks);
            }
            catch (Exception ex) { Logger.Error($"Ошибка удаления: {ex.Message}"); }
            finally { delDone = true; }
        });
        delThread.IsBackground = true;
        delThread.Start();

        int spin = 0;
        var spinCh = new[] { '|', '/', '-', '\\' };
        while (!delDone)
        {
            ConsoleDialog.DrawSpinner("Clinkon1C — Удаление", $"Удаление {paths.Count} объект(а)...", spinCh[spin++ % spinCh.Length]);
            Thread.Sleep(80);
        }
        delThread.Join();

        if (res != null)
            Logger.Info($"Итог: {res.DeletedDirs} папок, {res.DeletedFiles} файлов, " +
                        $"{SafeDelete.FormatSize(res.FreedBytes)}, ошибок: {res.Errors.Count}");

        _sel.Clear();
        RescanCacheAndRestore(); // остаёмся в том же месте навигации
    }

    // ── Действия с базами ─────────────────────────────────────────────────────

    private List<InfoBaseEntry> GetMarkedBases()
    {
        if (_markedBases.Count > 0)
            return _bases.Entries.Where(e => _markedBases.Contains(e.Connect)).ToList();
        // Ничего не отмечено — предлагаем текущий под курсором
        var lvl  = _nav.Peek();
        var item = lvl.Items[lvl.Cursor];
        if (!item.IsUp && item.BaseName != null)
        {
            var e = _bases.Entries.FirstOrDefault(b =>
                string.Equals(b.Connect, item.BaseName, StringComparison.OrdinalIgnoreCase));
            if (e != null) return new List<InfoBaseEntry> { e };
        }
        return new List<InfoBaseEntry>();
    }

    private void DoCopyBases()
    {
        var entries = GetMarkedBases();
        if (entries.Count == 0) return;

        var profiles = ProfileFinder.FindProfiles();
        if (profiles.Count == 0) { ConsoleDialog.ShowOk("Базы", "Профили не найдены."); R.Invalidate(); return; }

        var names    = profiles.Select(p => p.UserName).ToArray();
        var selected = ConsoleDialog.MultiSelect(
            $"Копировать {entries.Count} баз(ы) пользователям", names);
        R.Invalidate();
        if (selected.Count == 0) return;

        int totalAdded = 0, totalSkipped = 0;
        foreach (var idx in selected)
        {
            var (added, skipped) = _bases.CopyToUser(entries, profiles[idx]);
            totalAdded   += added;
            totalSkipped += skipped;
        }

        ConsoleDialog.ShowOk("Копирование завершено",
            $"Профилей: {selected.Count}\n" +
            $"Добавлено записей: {totalAdded}\n" +
            $"Уже существовало: {totalSkipped}");
        R.Invalidate();
        _markedBases.Clear();
    }

    private void DoExportBases()
    {
        var entries = GetMarkedBases();
        if (entries.Count == 0) return;

        var fileName = ConsoleDialog.InputText(
            "Экспорт в .v8i",
            $"Будет сохранено {entries.Count} баз.\nФайл появится на рабочем столе.\n\nИмя файла (без расширения):",
            "bases");
        R.Invalidate();

        if (string.IsNullOrWhiteSpace(fileName)) return;

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var safeName = new string(fileName.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "bases";

        var desktop   = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var filePath  = Path.Combine(desktop, safeName + ".v8i");

        _bases.ExportToV8i(entries, filePath);

        ConsoleDialog.ShowOk("Сохранено",
            $"Файл:\n{filePath}\n\nЗаписей: {entries.Count}");
        R.Invalidate();
        _markedBases.Clear();
    }

    private void ShowHelp()
    {
        ConsoleDialog.ShowText("Помощь — Clinkon1C",
@"  ↑ ↓ PgUp PgDn Home End   Навигация
  Enter  /  →               Войти в папку/базу
  Backspace  /  ←           Выйти на уровень выше
  Пробел                     Выделить (курсор сдвигается вниз)
  Esc                        Снять всё выделение
  S                          Сортировка: Имя ▲ / Размер ▼
  Tab                        Лог операций (полный экран)
  V                          Кэш — вид: по пользователю / по базе
  Shift+Del                  Dry Run — предпросмотр удаления
  Del                        Удалить выделенное (или под курсором)
  F5                         Пересканировать
  F1                         Помощь
  F10                        Выход");
        R.Invalidate();
    }

    // ── Действия с лицензиями ─────────────────────────────────────────────────

    private void EnterLicenses()
    {
        var state = RingHelper.CheckSetup();

        if (state == RingHelper.SetupState.NeedRing)
        {
            string? err = null;
            ConsoleDialog.ShowProgress("Установка ring license", msgs =>
            {
                err = RingHelper.ExtractFromResources(msgs);
            });
            R.Invalidate();

            if (err != null)
            {
                ConsoleDialog.ShowOk("Ошибка установки ring", err);
                R.Invalidate();
                return;
            }
            state = RingHelper.CheckSetup();
        }

        if (state == RingHelper.SetupState.NeedJava)
        {
            bool ok = ConsoleDialog.Confirm(
                "Java не найдена",
                "Для работы модуля лицензий требуется Java.\n\n" +
                "Скачать Eclipse Temurin 8 JRE (~55 МБ)?\n" +
                "Файл будет сохранён в %ProgramData%\\Clinkon1C\\jre\\");
            R.Invalidate();
            if (!ok) return;

            string? err = null;
            ConsoleDialog.ShowProgress("Скачивание Java JRE", msgs =>
            {
                err = RingHelper.DownloadJre(msgs);
            });
            R.Invalidate();

            if (err != null)
            {
                ConsoleDialog.ShowOk("Ошибка загрузки JRE", err);
                R.Invalidate();
                return;
            }
        }

        ConsoleDialog.ShowProgress("Загрузка лицензий", _ => _licenses.Refresh());
        R.Invalidate();
        _nav.Push(MakeLicensesLevel());
    }

    private void DoShowLicenseInfo(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "..") return;

        var entry      = _licenses.Entries.FirstOrDefault(x => x.Name == name);
        var licFileName = entry?.FileName ?? "";

        string? licPath    = null;
        Dictionary<string, string>? licData = null;
        string ringInfo    = "";

        ConsoleDialog.ShowProgress("Загрузка информации", upd =>
        {
            upd("Поиск файла лицензии...");
            licPath = LicensesModule.FindLicFileByName(name, licFileName);
            if (licPath != null)
                licData = LicensesModule.ReadLicFile(licPath);

            upd("ring license info...");
            ringInfo = _licenses.GetFullInfo(name);
        });
        R.Invalidate();

        int innerW = Math.Min(Console.WindowWidth - 4, 78) - 4;
        var displaySb = new System.Text.StringBuilder();
        var saveSb    = new System.Text.StringBuilder();

        // ── .lic файл ────────────────────────────────────────────────────────
        if (licPath != null && licData != null && licData.Count > 0)
        {
            var header = $"Файл: {System.IO.Path.GetFileName(licPath)}";
            var path   = $"Путь: {licPath}";
            displaySb.AppendLine(header);
            displaySb.AppendLine(path);
            displaySb.AppendLine();
            saveSb.AppendLine(header);
            saveSb.AppendLine(path);
            saveSb.AppendLine();

            var table = FormatLicTable(licData, innerW);
            displaySb.Append(table);
            saveSb.Append(table);
        }
        else
        {
            displaySb.AppendLine("(файл .lic не найден в стандартных директориях 1С)");
            saveSb.AppendLine("(файл .lic не найден)");
        }

        // ── ring license info (регистрационные данные) ────────────────────────
        if (!string.IsNullOrWhiteSpace(ringInfo))
        {
            displaySb.AppendLine();
            displaySb.AppendLine(new string('─', Math.Min(innerW, 40)));
            displaySb.AppendLine(ringInfo);
            saveSb.AppendLine();
            saveSb.AppendLine(new string('─', 40));
            saveSb.AppendLine(ringInfo);
        }

        var fullSaveText = $"Лицензия: {name}\n\n{saveSb}";
        var safeName     = new string(name.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)).ToArray());

        ConsoleDialog.ShowText($"Лицензия: {name}", displaySb.ToString(), () =>
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var fpath   = System.IO.Path.Combine(desktop, $"license_{safeName}.txt");
                System.IO.File.WriteAllText(fpath, fullSaveText, System.Text.Encoding.UTF8);
                ConsoleDialog.ShowOk("Сохранено", fpath);
            }
            catch (Exception ex)
            {
                ConsoleDialog.ShowOk("Ошибка сохранения", ex.Message);
            }
        });
        R.Invalidate();
    }

    private static readonly string[] LicFieldOrder = new[]
    {
        "Регистрационный номер", "Тип лицензии", "Номер продукта",
        "Наименование продукта", "Дата производства", "Срок действия",
        "Количество пользователей", "Количество пинкодов в группе", "Привязка"
    };

    private static string FormatLicTable(Dictionary<string, string> data, int totalWidth)
    {
        var pairs = new List<(string Key, string Val)>();
        foreach (var k in LicFieldOrder)
            if (data.TryGetValue(k, out var v)) pairs.Add((k, v));
        foreach (var kvp in data)
            if (!LicFieldOrder.Contains(kvp.Key)) pairs.Add((kvp.Key, kvp.Value));
        if (pairs.Count == 0) return "";

        int keyW = Math.Min(pairs.Max(p => p.Key.Length), 30);
        int valW = Math.Max(8, totalWidth - keyW - 3); // " : " = 3

        var sb = new System.Text.StringBuilder();
        foreach (var (key, val) in pairs)
        {
            var paddedKey = key.PadRight(keyW);
            var lines     = WordWrapVal(val, valW);
            for (int i = 0; i < lines.Length; i++)
            {
                sb.AppendLine(i == 0
                    ? $"{paddedKey} : {lines[i]}"
                    : $"{new string(' ', keyW + 3)}{lines[i]}");
            }
        }
        return sb.ToString();
    }

    private static string[] WordWrapVal(string text, int width)
    {
        if (text.Length <= width) return new[] { text };
        var result  = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (current.Length == 0)
                current.Append(word.Length <= width ? word : word.Substring(0, width));
            else if (current.Length + 1 + word.Length <= width)
                current.Append(' ').Append(word);
            else
            {
                result.Add(current.ToString());
                current.Clear();
                current.Append(word.Length <= width ? word : word.Substring(0, width));
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    private void DoValidateLicense()
    {
        var item = CurrentItem();
        if (item == null || item.IsUp) return;
        var name = item.Name;
        bool ok = ConsoleDialog.Confirm("Проверить лицензию",
            $"Проверить соответствие оборудования для:\n{name}");
        R.Invalidate();
        if (!ok) return;

        (bool res, string msg) result = (false, "");
        ConsoleDialog.ShowProgress("Проверка лицензии", _ =>
        {
            result = _licenses.Validate(name);
        });
        R.Invalidate();

        ConsoleDialog.ShowOk(
            result.res ? "Лицензия действительна" : "Ошибка проверки",
            string.IsNullOrWhiteSpace(result.msg) ? (result.res ? "OK" : "Ошибка") : result.msg);
        R.Invalidate();
    }

    private void DoRemoveLicense()
    {
        var item = CurrentItem();
        if (item == null || item.IsUp) return;
        var name = item.Name;

        bool ok = ConsoleDialog.Confirm("Удалить лицензию",
            $"Удалить лицензию из хранилища?\n\n{name}\n\nЭто действие необратимо.");
        R.Invalidate();
        if (!ok) return;

        (bool res, string msg) result = (false, "");
        ConsoleDialog.ShowProgress("Удаление лицензии", _ =>
        {
            result = _licenses.Remove(name);
        });
        R.Invalidate();

        ConsoleDialog.ShowOk(
            result.res ? "Удалено" : "Ошибка удаления",
            string.IsNullOrWhiteSpace(result.msg) ? (result.res ? "Успешно" : "Ошибка") : result.msg);
        R.Invalidate();

        if (result.res)
        {
            _licenses.Refresh();
            RebuildCurrentLevel();
        }
    }

    private void DoActivateLicense()
    {
        var fields = new (string Key, string Label)[]
        {
            ("serial",  "Серийный номер"),
            ("pin",     "Пин-код"),
            ("prevpin", "Пред. пин (если есть)"),
            ("company", "Организация"),
            ("country", "Страна"),
            ("zip",     "Индекс"),
            ("town",    "Город"),
            ("street",  "Улица"),
            ("house",   "Дом"),
            ("email",   "E-mail (необяз.)"),
        };

        var vals = ConsoleDialog.Form("Активация лицензии 1С", fields);
        R.Invalidate();
        if (vals == null) return;

        var p = new Modules.Licenses.ActivateParams
        {
            Serial      = vals["serial"],
            Pin         = vals["pin"],
            PreviousPin = vals["prevpin"],
            Company     = vals["company"],
            Country     = vals["country"],
            ZipCode     = vals["zip"],
            Town        = vals["town"],
            Street      = vals["street"],
            House       = vals["house"],
            Email       = vals["email"],
        };

        if (string.IsNullOrWhiteSpace(p.Serial) || string.IsNullOrWhiteSpace(p.Pin))
        {
            ConsoleDialog.ShowOk("Ошибка", "Серийный номер и пин-код обязательны.");
            R.Invalidate();
            return;
        }

        (bool res, string msg) result = (false, "");
        ConsoleDialog.ShowProgress("Активация лицензии", _ =>
        {
            result = _licenses.Activate(p);
        });
        R.Invalidate();

        ConsoleDialog.ShowOk(
            result.res ? "Активация выполнена" : "Ошибка активации",
            string.IsNullOrWhiteSpace(result.msg) ? (result.res ? "Успешно" : "Ошибка") : result.msg);
        R.Invalidate();

        if (result.res)
        {
            _licenses.Refresh();
            RebuildCurrentLevel();
        }
    }

    // ── Действия с агентами ───────────────────────────────────────────────────

    private void EnterAgents()
    {
        ConsoleDialog.ShowProgress("Сканирование агентов...", _ => _agents.Refresh());
        R.Invalidate();
        _nav.Push(MakeAgentsLevel());
    }

    private void EnterProcesses()
    {
        ConsoleDialog.ShowProgress("Сканирование процессов 1С...", _ => _processes.Refresh());
        R.Invalidate();
        _nav.Push(MakeProcessesLevel());
    }

    // ── Действия с веб-публикациями ───────────────────────────────────────────

    private void EnterWeb()
    {
        ConsoleDialog.ShowProgress("Поиск Apache и публикаций...", _ => _web.Refresh());
        R.Invalidate();
        _nav.Push(MakeWebLevel());
    }

    private void EnterEmulators()
    {
        ConsoleDialog.ShowProgress("Сканирование эмуляторов HASP...", _ => _emulators.Scan());
        R.Invalidate();
        _nav.Push(MakeEmulatorsLevel());
    }

    private void EnterConfigs()
    {
        ConsoleDialog.ShowProgress("Поиск конфигурационных файлов...", _ => _configs.Refresh());
        R.Invalidate();
        _nav.Push(MakeConfigsLevel());
    }

    private void EnterCom()
    {
        ConsoleDialog.ShowProgress("Сканирование COM-коннекторов...", _ => _com.Scan());
        R.Invalidate();
        _nav.Push(MakeComLevel());
    }

    private NavLevel MakeComLevel()
    {
        var items = new List<NavItem> { UpItem() };

        var reg = _com.Registered;
        if (reg.Count > 0)
        {
            items.Add(new NavItem { Name = "── Зарегистрированные ──────────────────────────────────", CanEnter = false, ShowDescCol = false });
            foreach (var e in reg)
            {
                string src  = e.Source == "COM+" ? "COM+" : "regsvr32";
                string warn = e.DllExists ? "" : "  [! dll не найдена]";
                items.Add(new NavItem
                {
                    Name        = e.ProgId,
                    Description = $"{src}{warn}",
                    ShowDescCol = true,
                    CanEnter    = true,
                    IsDead      = !e.DllExists,
                    BaseName    = e.ProgId
                });
            }
        }

        var avail = _com.Available;
        if (avail.Count > 0)
        {
            items.Add(new NavItem { Name = "── Доступные для регистрации ───────────────────────────", CanEnter = false, ShowDescCol = false });
            foreach (var e in avail)
            {
                items.Add(new NavItem
                {
                    Name        = ComVersionFromPath(e.DllPath),
                    Description = e.DllPath,
                    ShowDescCol = true,
                    CanEnter    = true,
                    IsDead      = false,
                    BaseName    = e.DllPath  // lookup key
                });
            }
        }

        if (reg.Count == 0 && avail.Count == 0)
            items.Add(new NavItem { Name = "(comcntr.dll не найден)", CanEnter = false, ShowDescCol = false });

        return new NavLevel
        {
            Kind  = NavLevelKind.ComRoot,
            Title = $"COM Коннектор  [{reg.Count} зарег. / {avail.Count} доступно]",
            Items = items
        };
    }

    private static string ComVersionFromPath(string dllPath)
    {
        var parts = dllPath.Replace('\\', '/').Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals("1cv8", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
            {
                var v = parts[i + 1];
                if (v.Split('.').Length == 4) return v;
            }
        return Path.GetFileName(dllPath);
    }

    private void DoComAction(NavItem item)
    {
        if (item.BaseName == null) return;

        // Зарегистрированная запись — инфо-диалог
        var registered = _com.Registered.FirstOrDefault(e => e.ProgId == item.BaseName);
        if (registered != null)
        {
            DoComInfo(registered);
            return;
        }

        // Доступная запись — регистрация
        var available = _com.Available.FirstOrDefault(e => e.DllPath == item.BaseName);
        if (available == null) return;

        string defaultProgId = ComEntry.DefaultProgId(available.DllPath);
        var progId = ConsoleDialog.InputText(
            "Регистрация COM-коннектора",
            $"DLL: {available.DllPath}\n\nProgID для регистрации:",
            defaultProgId);
        if (string.IsNullOrWhiteSpace(progId)) return;

        string? err = null;
        ConsoleDialog.ShowProgress("Регистрация COM-коннектора...", msgs =>
        {
            try
            {
                msgs($"Регистрируем {progId}...");
                _com.Register(available.DllPath, progId.Trim());
                msgs("Готово.");
                Logger.Info($"COM: зарегистрован {progId} → {available.DllPath}");
            }
            catch (Exception ex)
            {
                err = ex.Message;
                Logger.Error($"COM регистрация: {ex.Message}");
            }
        });
        if (err != null)
            ConsoleDialog.ShowOk("Ошибка регистрации COM", err);
        R.Invalidate();
        _nav.Pop();
        _nav.Push(MakeComLevel());
    }

    private void DoComInfo(ComEntry e)
    {
        var lines = new List<string>
        {
            $"ProgID:    {e.ProgId}",
            $"DLL:       {e.DllPath}",
        };
        if (!string.IsNullOrEmpty(e.Clsid))
            lines.Add($"CLSID:     {{{e.Clsid}}}");
        lines.Add($"Источник:  {e.Source}");
        lines.Add($"Файл DLL:  {(e.DllExists ? "✓ найдена" : "✗ не найдена")}");

        int btn = ConsoleDialog.ShowInfo(
            $"COM-коннектор: {e.ProgId}",
            lines.ToArray(),
            "  Изменить ProgID  ", "  Удалить  ", "  Закрыть  ");

        if (btn == 0) // Изменить ProgID
        {
            var newId = ConsoleDialog.InputText("Изменить ProgID",
                $"DLL: {e.DllPath}\n\nНовый ProgID:", e.ProgId);
            if (!string.IsNullOrWhiteSpace(newId) && newId.Trim() != e.ProgId)
            {
                string? err = null;
                ConsoleDialog.ShowProgress("Перерегистрация...", msgs =>
                {
                    try
                    {
                        msgs($"Удаляем {e.ProgId}...");
                        _com.Unregister(e);
                        msgs($"Регистрируем {newId.Trim()}...");
                        _com.Register(e.DllPath, newId.Trim());
                        Logger.Info($"COM: переименован {e.ProgId} → {newId.Trim()}");
                    }
                    catch (Exception ex) { err = ex.Message; Logger.Error($"COM: {ex.Message}"); }
                });
                if (err != null) ConsoleDialog.ShowOk("Ошибка", err);
            }
        }
        else if (btn == 1) // Удалить
        {
            if (ConsoleDialog.Confirm("Удалить регистрацию",
                $"Удалить COM-коннектор?\n\nProgID:  {e.ProgId}\nDLL:     {e.DllPath}\nТип:     {e.Source}",
                defaultYes: false, "  Удалить  ", "  Отмена  "))
            {
                ConsoleDialog.ShowProgress("Удаление...", _ => _com.Unregister(e));
                Logger.Info($"COM: удалена регистрация {e.ProgId}");
            }
        }
        // btn == 2 или -1 (Закрыть / Esc) — ничего не делаем

        R.Invalidate();
        _nav.Pop();
        _nav.Push(MakeComLevel());
    }

    private void DoComDelete()
    {
        if (_nav.Count == 0) return;
        var lvl  = _nav.Peek();
        var item = lvl.Items[lvl.Cursor];
        if (item.IsUp || item.BaseName == null) return;
        var e = _com.Registered.FirstOrDefault(r => r.ProgId == item.BaseName);
        if (e == null) return;
        if (!ConsoleDialog.Confirm("Удалить регистрацию",
            $"Удалить COM-коннектор?\n\nProgID:  {e.ProgId}\nDLL:     {e.DllPath}\nТип:     {e.Source}",
            defaultYes: false, "  Удалить  ", "  Отмена  "))
            return;
        ConsoleDialog.ShowProgress("Удаление...", _ => _com.Unregister(e));
        Logger.Info($"COM: удалена регистрация {e.ProgId}");
        R.Invalidate();
        _nav.Pop();
        _nav.Push(MakeComLevel());
    }

    private void DoWebInfo(string? alias)
    {
        if (string.IsNullOrEmpty(alias)) return;
        var e = _web.Entries.FirstOrDefault(x => x.Alias == alias);
        if (e == null) return;

        int innerW = Math.Min(Console.WindowWidth - 4, 78) - 4;

        (string title, string content) GetInfo()
        {
            var sb = new System.Text.StringBuilder();
            var pairs = new List<(string K, string V)>
            {
                ("Псевдоним",     e.Alias),
                ("Тип базы",      e.DbType),
                ("База",          e.DbName),
                ("Строка подкл.", e.IbString),
                ("Статус",        e.Enabled ? "Включено" : "Выключено"),
                ("Анон. вход",    !string.IsNullOrEmpty(e.AnonUser) ? $"да ({e.AnonUser})" : "нет"),
                ("Отладка",       e.DebugEnabled ? $"да ({e.DebugProtocol})" : "нет"),
                ("JWT блок",      e.JwtBlockXml != null ? "задан" : "нет"),
                ("Версия 1С",     e.Version),
                ("VRD файл",      e.VrdPath),
                ("Конфиг Apache", e.ConfFile),
            };
            pairs.RemoveAll(p => string.IsNullOrEmpty(p.V));

            int keyW = Math.Min(pairs.Max(p => p.K.Length), 20);
            int valW = Math.Max(8, innerW - keyW - 3);
            foreach (var (k, v) in pairs)
            {
                var lines = WordWrapVal(v, valW);
                for (int i = 0; i < lines.Length; i++)
                    sb.AppendLine(i == 0
                        ? $"{k.PadRight(keyW)} : {lines[i]}"
                        : $"{new string(' ', keyW + 3)}{lines[i]}");
            }

            var svcName = string.IsNullOrEmpty(_web.ApacheService) ? "" : $" ({_web.ApacheService})";
            var apacheSt = !_web.ApacheFound ? "не найден"
                : (_web.ApacheRunning ? "▶ Работает" : "■ Остановлен");
            sb.AppendLine();
            sb.AppendLine(new string('─', Math.Min(innerW, 40)));
            sb.AppendLine($"Apache{svcName}: {apacheSt}");

            return ($"Публикация: {e.Alias}", sb.ToString());
        }

        ConsoleDialog.ShowTextWithKeys(
            GetInfo,
            "[A] Анон. вход  [E] Редактировать  [J] JWT  [S] Старт  [T] Стоп  [R] Рестарт  Esc — закрыть",
            (key, _) =>
            {
                char ch = char.ToLower((char)key);
                if (ch == 's') { DoWebApacheOp("start");   return true; }
                if (ch == 't') { DoWebApacheOp("stop");    return true; }
                if (ch == 'r') { DoWebApacheOp("restart"); return true; }
                if (ch == 'a') { DoWebAnon(alias);         return false; }
                if (ch == 'e') { DoWebEdit(alias);         return false; }
                if (ch == 'j') { DoWebJwt(alias);          return false; }
                return true;
            });
        R.Invalidate();
    }

    private void DoWebPublish()
    {
        if (!_web.ApacheFound)
        {
            ConsoleDialog.ShowOk("Ошибка", "Apache не обнаружен на этом компьютере.");
            R.Invalidate();
            return;
        }

        var fields = ConsoleDialog.Form("Новая веб-публикация 1С",
            new[] { ("alias", "Псевдоним (без /)"), ("ib", "Строка подключения") });
        R.Invalidate();
        if (fields == null) return;

        var alias = fields["alias"].Trim();
        var ib    = fields["ib"].Trim();
        if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(ib)) return;

        string? err = null;
        ConsoleDialog.ShowProgress("Публикация...", _ => { err = _web.Publish(alias, ib); });
        R.Invalidate();

        if (err != null)
        {
            ConsoleDialog.ShowOk("Ошибка публикации", err);
            R.Invalidate();
            return;
        }

        bool restart = ConsoleDialog.Confirm("Публикация добавлена",
            $"/{alias.TrimStart('/')} успешно опубликована.\n\nПерезапустить Apache сейчас?");
        R.Invalidate();
        if (restart) DoWebApacheOp("restart");

        ConsoleDialog.ShowProgress("Обновление...", _ => _web.Refresh());
        R.Invalidate();
        RebuildCurrentLevel();
    }

    private void DoWebUnpublish()
    {
        var item = CurrentItem();
        if (item == null || item.IsUp || string.IsNullOrEmpty(item.BaseName)) return;

        var entry = _web.Entries.FirstOrDefault(e => e.Alias == item.BaseName);
        if (entry == null) return;

        if (!ConsoleDialog.Confirm("Снять с публикации",
            $"Снять публикацию {entry.Alias}?\n\nБудут удалены:\n• {entry.ConfFile}\n• {entry.VrdPath}"))
        {
            R.Invalidate();
            return;
        }
        R.Invalidate();

        string? err = null;
        ConsoleDialog.ShowProgress("Снятие публикации...", _ => { err = _web.Unpublish(entry); });
        R.Invalidate();

        if (err != null)
        {
            ConsoleDialog.ShowOk("Ошибка", err);
            R.Invalidate();
            return;
        }

        bool restart = ConsoleDialog.Confirm("Готово",
            $"{entry.Alias} снята с публикации.\n\nПерезапустить Apache сейчас?");
        R.Invalidate();
        if (restart) DoWebApacheOp("restart");

        ConsoleDialog.ShowProgress("Обновление...", _ => _web.Refresh());
        R.Invalidate();
        RebuildCurrentLevel();
    }

    private void DoWebApacheOp(string op)
    {
        if (!_web.ApacheFound) return;

        var label = op switch
        {
            "start"   => "Запуск Apache...",
            "stop"    => "Остановка Apache...",
            "restart" => "Перезапуск Apache...",
            _         => op
        };

        string? err = null;
        ConsoleDialog.ShowProgress(label, _ =>
        {
            err = op switch
            {
                "start"   => _web.StartApache(),
                "stop"    => _web.StopApache(),
                "restart" => _web.RestartApache(),
                _         => null
            };
        });
        R.Invalidate();

        var okMsg = op switch
        {
            "start"   => "✓ Apache запущен",
            "stop"    => "✓ Apache остановлен",
            "restart" => "✓ Apache перезапущен",
            _         => "✓ Готово"
        };

        if (err != null)
            ConsoleDialog.ShowOk("Ошибка Apache", err);
        else
            ConsoleDialog.ShowOk("Apache", okMsg);
        R.Invalidate();

        if (_nav.Count > 0 && _nav.Peek().Kind == NavLevelKind.WebRoot)
            RebuildCurrentLevel();
    }

    private void DoWebEdit(string? alias)
    {
        if (string.IsNullOrEmpty(alias)) return;
        var e = _web.Entries.FirstOrDefault(x => x.Alias == alias);
        if (e == null) return;

        var defaults = new Dictionary<string, string>
        {
            ["anon"]  = string.IsNullOrEmpty(e.AnonUser) ? "нет" : "да",
            ["user"]  = e.AnonUser,
            ["pwd"]   = e.AnonPwd,
            ["debug"] = e.DebugEnabled ? "да" : "нет",
            ["proto"] = e.DebugProtocol,
            ["url"]   = e.DebugUrl,
        };

        var vals = ConsoleDialog.Form($"Редактирование: {e.Alias}", new (string, string)[]
        {
            ("anon",  "Анон. вход (да/нет)"),
            ("user",  "Пользователь 1С"),
            ("pwd",   "Пароль"),
            ("debug", "Отладка (да/нет)"),
            ("proto", "Протокол (tcp/http)"),
            ("url",   "URL отладчика"),
        }, defaults);
        R.Invalidate();
        if (vals == null) return;

        bool anonEnabled  = IsYes(vals["anon"]);
        bool debugEnabled = IsYes(vals["debug"]);
        string? anonUser  = anonEnabled && !string.IsNullOrEmpty(vals["user"]) ? vals["user"].Trim() : null;
        string? anonPwd   = anonEnabled && !string.IsNullOrEmpty(vals["pwd"])  ? vals["pwd"].Trim()  : null;
        string  debugProto = string.IsNullOrWhiteSpace(vals["proto"]) ? "tcp" : vals["proto"].Trim();
        string  debugUrl   = vals["url"].Trim();

        string? err = null;
        ConsoleDialog.ShowProgress("Сохранение...", _ =>
            err = _web.UpdateVrd(e, anonUser, anonPwd, debugEnabled, debugProto, debugUrl, e.JwtBlockXml));
        R.Invalidate();

        if (err != null) { ConsoleDialog.ShowOk("Ошибка", err); R.Invalidate(); return; }

        bool restart = ConsoleDialog.Confirm("Сохранено",
            $"{e.Alias} обновлена.\n\nПерезапустить Apache сейчас?");
        R.Invalidate();
        if (restart) DoWebApacheOp("restart");

        ConsoleDialog.ShowProgress("Обновление...", _ => _web.Refresh());
        R.Invalidate();
        RebuildCurrentLevel();
    }

    private void DoWebAnon(string? alias)
    {
        if (string.IsNullOrEmpty(alias)) return;
        var e = _web.Entries.FirstOrDefault(x => x.Alias == alias);
        if (e == null) return;

        bool wasEnabled = !string.IsNullOrEmpty(e.AnonUser);
        string header   = wasEnabled
            ? $"Анон. вход: ВКЛЮЧЁН ({e.AnonUser})"
            : "Анон. вход: выключен";

        var defaults = new Dictionary<string, string>
        {
            ["on"]   = wasEnabled ? "да" : "нет",
            ["user"] = e.AnonUser,
            ["pwd"]  = e.AnonPwd,
        };

        var vals = ConsoleDialog.Form(
            $"{header}  •  {e.Alias}",
            new (string, string)[]
            {
                ("on",   "Включить (да/нет)"),
                ("user", "Пользователь 1С (для авто-входа)"),
                ("pwd",  "Пароль"),
            }, defaults);
        R.Invalidate();
        if (vals == null) return;

        bool enable   = IsYes(vals["on"]);
        string? user  = enable && !string.IsNullOrWhiteSpace(vals["user"]) ? vals["user"].Trim() : null;
        string? pwd   = enable && !string.IsNullOrWhiteSpace(vals["pwd"])  ? vals["pwd"].Trim()  : null;

        string? err = null;
        ConsoleDialog.ShowProgress("Сохранение VRD...", _ =>
            err = _web.UpdateVrd(e, user, pwd, e.DebugEnabled, e.DebugProtocol, e.DebugUrl, e.JwtBlockXml));
        R.Invalidate();

        if (err != null) { ConsoleDialog.ShowOk("Ошибка", err); R.Invalidate(); return; }

        var status = enable
            ? (user != null ? $"включён (пользователь: {user})" : "включён без авто-входа")
            : "отключён";
        bool restart = ConsoleDialog.Confirm("Сохранено",
            $"{e.Alias}: анонимный вход {status}.\n\nПерезапустить Apache сейчас?");
        R.Invalidate();
        if (restart) DoWebApacheOp("restart");

        ConsoleDialog.ShowProgress("Обновление...", _ => _web.Refresh());
        R.Invalidate();
        RebuildCurrentLevel();
    }

    private void DoWebJwt(string? alias)
    {
        if (string.IsNullOrEmpty(alias)) return;
        var e = _web.Entries.FirstOrDefault(x => x.Alias == alias);
        if (e == null) return;

        var raw = ConsoleDialog.PasteBlock("JWT — accessTokenAuthentication");
        R.Invalidate();
        if (raw == null) return;

        raw = raw.Trim();
        try
        {
            const string ns = "http://v8.1c.ru/8.2/virtual-resource-system";
            var tempDoc = new System.Xml.XmlDocument();
            tempDoc.LoadXml($"<root xmlns=\"{ns}\">{raw}</root>");
            var first = tempDoc.DocumentElement?.FirstChild as System.Xml.XmlElement;
            if (first?.LocalName != "accessTokenAuthentication")
            {
                ConsoleDialog.ShowOk("Ошибка XML",
                    $"Ожидается <accessTokenAuthentication>.\nПолучен: <{first?.LocalName ?? "?"}>");
                R.Invalidate();
                return;
            }
        }
        catch (Exception ex)
        {
            ConsoleDialog.ShowOk("Ошибка XML", ex.Message);
            R.Invalidate();
            return;
        }

        string? anonUser = string.IsNullOrEmpty(e.AnonUser) ? null : e.AnonUser;
        string? anonPwd  = string.IsNullOrEmpty(e.AnonPwd)  ? null : e.AnonPwd;

        string? err = null;
        ConsoleDialog.ShowProgress("Сохранение JWT...", _ =>
            err = _web.UpdateVrd(e, anonUser, anonPwd, e.DebugEnabled, e.DebugProtocol, e.DebugUrl, raw));
        R.Invalidate();

        if (err != null) { ConsoleDialog.ShowOk("Ошибка", err); R.Invalidate(); return; }

        bool restart = ConsoleDialog.Confirm("JWT сохранён",
            $"{e.Alias} обновлена.\n\nПерезапустить Apache сейчас?");
        R.Invalidate();
        if (restart) DoWebApacheOp("restart");

        ConsoleDialog.ShowProgress("Обновление...", _ => _web.Refresh());
        R.Invalidate();
        RebuildCurrentLevel();
    }

    // ── Брандмауэр ───────────────────────────────────────────────────────────

    private void DoFirewallInfo()
    {
        while (true)
        {
            _firewall.Refresh();
            var inRule  = _firewall.Rules.FirstOrDefault(r =>
                string.Equals(r.Direction, "In", StringComparison.OrdinalIgnoreCase));
            var outRule = _firewall.Rules.FirstOrDefault(r =>
                string.Equals(r.Direction, "Out", StringComparison.OrdinalIgnoreCase));

            string RuleStatus(FirewallRule? r) => r == null
                ? "[ ] отсутствует"
                : (r.Enabled
                    ? $"[✓] {r.Action}  {r.Ports}"
                    : $"[■] отключено  {r.Ports}");

            var lines = new[]
            {
                $"Правило «{FirewallModule.RuleName}» — порты 1С",
                "",
                $"  Входящее:    {RuleStatus(inRule)}",
                $"  Исходящее:   {RuleStatus(outRule)}",
                "",
                $"  Эталон:  {FirewallModule.PortSpec}",
                $"           1540 Агент  1541 Менеджер  1545 RAS",
                $"           1550 Доп.   1560-1591 Рабочие процессы",
            };

            int btn = ConsoleDialog.ShowInfo(
                "Брандмауэр Windows — порты 1С",
                lines,
                "Создать/Обновить", "Удалить", "Закрыть");
            R.Invalidate();

            if (btn == 2 || btn < 0) break;

            if (btn == 0)
            {
                string? err = null;
                ConsoleDialog.ShowProgress("Настройка брандмауэра...",
                    _ => { err = _firewall.CreateOrUpdate(); });
                R.Invalidate();
                if (err != null) { ConsoleDialog.ShowOk("Ошибка брандмауэра", err); R.Invalidate(); }
                // цикл — показываем обновлённый статус
            }
            else if (btn == 1)
            {
                if (_firewall.Rules.Count == 0)
                {
                    ConsoleDialog.ShowOk("Брандмауэр", $"Правил «{FirewallModule.RuleName}» нет.");
                    R.Invalidate();
                }
                else if (ConsoleDialog.Confirm("Удалить правила",
                    $"Удалить правила брандмауэра «{FirewallModule.RuleName}» (in + out)?"))
                {
                    string? err = null;
                    ConsoleDialog.ShowProgress("Удаление...", _ => { err = _firewall.Delete(); });
                    R.Invalidate();
                    if (err != null) { ConsoleDialog.ShowOk("Ошибка", err); R.Invalidate(); }
                }
                else R.Invalidate();
            }
        }
    }

    // ── Журнал событий Windows ───────────────────────────────────────────────

    private void DoEventLogInfo()
    {
        var filter = EventLogFilter.ErrorsWarnings;

        (string title, string content) GetInfo()
        {
            var visible  = EventLogModule.Apply(_eventLog.Entries, filter).ToList();
            var fLabel   = EventLogModule.FilterLabel(filter);
            var title    = $"Журнал событий 1С  [{fLabel}]  — {visible.Count} записей";
            var content  = EventLogModule.FormatEntries(visible);
            return (title, content);
        }

        string hint = "[F] Фильтр  [R] Обновить  ↑↓ PgUp/PgDn — скролл  Esc — закрыть";

        ConsoleDialog.ShowTextWithKeys(GetInfo, hint, (key, ch) =>
        {
            if (key == ConsoleKey.F || char.ToLower(ch) == 'f')
            {
                filter = EventLogModule.NextFilter(filter);
                return true;
            }
            if (key == ConsoleKey.R || char.ToLower(ch) == 'r')
            {
                ConsoleDialog.ShowProgress("Загрузка событий...",
                    _ => _eventLog.Load(EventLogModule.DefaultMax));
                return true;
            }
            return true;
        });
    }

    // ── Эмуляторы HASP ───────────────────────────────────────────────────────

    private void DoEmulatorInfo(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var e = _emulators.Found.FirstOrDefault(x => x.Name == name);
        if (e == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Эмулятор: {e.Name}");
        sb.AppendLine();
        sb.AppendLine($"Сервис:   {(e.SvcFound ? (e.SvcRunning ? "запущен" : "остановлен") : "не найден")}");
        if (e.SysPaths.Length > 0)
        {
            sb.AppendLine($".sys файлы ({e.SysPaths.Length}):");
            foreach (var p in e.SysPaths) sb.AppendLine($"  {p}");
        }
        else
        {
            sb.AppendLine(".sys файлы: не найдены");
        }
        sb.AppendLine($"Дамп реестра: {(e.DumpFound ? "найден" : "нет")}");
        if (e.DumpFound)
            sb.AppendLine($"  HKLM\\SYSTEM\\CurrentControlSet\\{e.Name}");

        var eLines = sb.ToString().TrimEnd().Replace("\r\n", "\n").Split('\n')
                        .Select(l => l.TrimEnd('\r')).ToArray();
        ConsoleDialog.ShowInfo($"Эмулятор — {e.Name}", eLines, "  Закрыть  ");
        R.Invalidate();
    }

    private void DoEmulatorRemove()
    {
        var lvl = _nav.Peek();
        if (lvl.Kind != NavLevelKind.EmulatorsRoot) return;
        var item = lvl.Items[lvl.Cursor];
        if (item.IsUp || string.IsNullOrEmpty(item.BaseName)) return;

        var e = _emulators.Found.FirstOrDefault(x => x.Name == item.BaseName);
        if (e == null) return;

        bool ok = ConsoleDialog.Confirm("Удаление эмулятора",
            $"Удалить эмулятор {e.Name}?\n\n{e.Summary()}\n\nПосле удаления потребуется перезагрузка ПК.");
        R.Invalidate();
        if (!ok) return;

        bool done = false;
        string msg = "";
        ConsoleDialog.ShowProgress($"Удаление {e.Name}...", _ =>
        {
            var (res, text) = _emulators.Remove(e);
            done = res;
            msg  = text;
        });
        R.Invalidate();

        ConsoleDialog.ShowOk(done ? "Готово" : "Ошибка", msg);
        R.Invalidate();

        ConsoleDialog.ShowProgress("Повторное сканирование...", _ => _emulators.Scan());
        R.Invalidate();
        RebuildCurrentLevel();
    }

    // ── Конфигурационные файлы ────────────────────────────────────────────────

    private void DoEditConfig(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var cf = _configs.Files.FirstOrDefault(f => f.Id == id);
        if (cf == null || !cf.CanEdit) return;

        switch (id)
        {
            case "conf.cfg":    DoEditConfCfg(cf);  break;
            case "logcfg.xml":  DoEditLogcfg(cf);   break;
        }
    }

    private void DoEditConfCfg(ConfigFile cf)
    {
        // Если файл не найден — предлагаем создать
        string path = cf.Path ?? ConfigsModule.DefaultConfCfgPath();
        if (cf.Path == null)
        {
            bool create = ConsoleDialog.Confirm("conf.cfg не найден",
                $"Файл не найден. Создать?\n\n{path}");
            R.Invalidate();
            if (!create) return;
        }

        // Читаем текущие значения
        var current = ConfigsModule.ReadKeyValue(path);
        string G(string key, string def = "") =>
            current.TryGetValue(key, out var v) ? v : def;

        var fields = new (string Key, string Label)[]
        {
            ("SystemLanguage",                    "Язык (RU/EN/System)"),
            ("DBFormatVersion",                   "Формат файловой БД"),
            ("UseHwLicenses",                     "HASP-ключи (0/1)"),
            ("DisableUnsafeActionProtection",     "Откл. защиты (regex)"),
            ("PublishDistributiveLocationWindows32", "Дистрибутив Win32"),
            ("PublishDistributiveLocationWindows64", "Дистрибутив Win64"),
        };

        var defaults = new Dictionary<string, string>
        {
            ["SystemLanguage"]                        = G("SystemLanguage", "RU"),
            ["DBFormatVersion"]                       = G("DBFormatVersion", "8.3.8"),
            ["UseHwLicenses"]                         = G("UseHwLicenses", "1"),
            ["DisableUnsafeActionProtection"]         = G("DisableUnsafeActionProtection"),
            ["PublishDistributiveLocationWindows32"]  = G("PublishDistributiveLocationWindows32"),
            ["PublishDistributiveLocationWindows64"]  = G("PublishDistributiveLocationWindows64"),
        };

        var result = ConsoleDialog.Form("conf.cfg", fields, defaults);
        R.Invalidate();
        if (result == null) return;

        // Сохраняем только заполненные поля (или очищаем если пустые)
        string? err = null;
        ConsoleDialog.ShowProgress("Сохранение conf.cfg...", _ =>
            err = ConfigsModule.WriteKeyValue(path, result));
        R.Invalidate();

        if (err != null)
        {
            ConsoleDialog.ShowOk("Ошибка", err);
            R.Invalidate();
            return;
        }

        ConsoleDialog.ShowOk("Готово", $"Сохранено:\n{path}\n\nИзменения вступят в силу при следующем запуске 1С.");
        R.Invalidate();

        // Обновляем путь если файл был создан
        ConsoleDialog.ShowProgress("Обновление...", _ => _configs.Refresh());
        R.Invalidate();
        RebuildCurrentLevel();
    }

    private void DoEditLogcfg(ConfigFile cf)
    {
        string path = cf.Path ?? ConfigsModule.DefaultLogcfgPath();
        if (cf.Path == null)
        {
            bool create = ConsoleDialog.Confirm("logcfg.xml не найден",
                $"Технологический журнал не настроен.\nСоздать конфигурацию?\n\n{path}");
            R.Invalidate();
            if (!create) return;
        }

        // Читаем существующий файл или создаём дефолт
        var s = cf.Path != null
            ? ConfigsModule.ParseLogcfg(cf.Path)
            : new ConfigsModule.LogcfgSettings
              {
                  LogPath   = @"C:\v8logs",
                  History   = "24",
                  Format    = "text",
                  DumpPath  = @"C:\v8dumps",
                  DumpType  = "3",
                  SystemLevel = "ERROR"
              };

        // ── Шаг 1: Основные параметры ────────────────────────────────────────
        var fields = new (string Key, string Label)[]
        {
            ("LogPath",       "Путь к логам"),
            ("History",       "Хранить (часов)"),
            ("Format",        "Формат (text/json)"),
            ("MinDurationMs", "Мин. длит. (мс, 0=все)"),
            ("DumpPath",      "Путь к дампам"),
            ("DumpType",      "Тип дампа (1/2/3)"),
            ("SystemLevel",   "Уровень системы"),
        };
        var defaults = new Dictionary<string, string>
        {
            ["LogPath"]        = s.LogPath,
            ["History"]        = s.History,
            ["Format"]         = s.Format,
            ["MinDurationMs"]  = s.MinDurationMs,
            ["DumpPath"]       = s.DumpPath,
            ["DumpType"]       = s.DumpType,
            ["SystemLevel"]    = s.SystemLevel,
        };

        var formResult = ConsoleDialog.Form("logcfg.xml — Основные параметры", fields, defaults);
        R.Invalidate();
        if (formResult == null) return;

        s.LogPath       = formResult["LogPath"];
        s.History       = string.IsNullOrWhiteSpace(formResult["History"]) ? "24" : formResult["History"];
        s.Format        = string.IsNullOrWhiteSpace(formResult["Format"])  ? "text" : formResult["Format"].ToLower();
        s.MinDurationMs = formResult["MinDurationMs"];
        s.DumpPath      = formResult["DumpPath"];
        s.DumpType      = string.IsNullOrWhiteSpace(formResult["DumpType"]) ? "3" : formResult["DumpType"];
        s.SystemLevel   = string.IsNullOrWhiteSpace(formResult["SystemLevel"]) ? "ERROR" : formResult["SystemLevel"].ToUpper();

        // ── Шаг 2: Выбор событий ─────────────────────────────────────────────
        var evtItems = ConfigsModule.KnownEvents
            .Select(e => $"{e.Name,-12} — {e.Desc}")
            .ToArray();
        var preselected = Enumerable.Range(0, ConfigsModule.KnownEvents.Length)
            .Where(i => s.Events.Contains(ConfigsModule.KnownEvents[i].Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Временно выставляем preselect через MultiSelect с отмеченными по умолчанию
        var selectedIdx = ConsoleDialog.MultiSelect(
            "logcfg.xml — События ТЖ (Пробел=выбрать, A=все)", evtItems, preselected);
        R.Invalidate();

        s.Events = selectedIdx
            .Select(i => ConfigsModule.KnownEvents[i].Name)
            .ToArray();

        // ── Сохранение ────────────────────────────────────────────────────────
        string? err = null;
        ConsoleDialog.ShowProgress("Сохранение logcfg.xml...", _ =>
            err = ConfigsModule.SaveLogcfg(path, s));
        R.Invalidate();

        if (err != null)
        {
            ConsoleDialog.ShowOk("Ошибка", err);
            R.Invalidate();
            return;
        }

        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"Сохранено: {path}");
        summary.AppendLine();
        if (!string.IsNullOrEmpty(s.LogPath))
            summary.AppendLine($"Логи:    {s.LogPath}  (хранить {s.History}ч, {s.Format})");
        if (s.Events.Length > 0)
            summary.AppendLine($"События: {string.Join(", ", s.Events)}");
        if (!string.IsNullOrWhiteSpace(s.MinDurationMs) && s.MinDurationMs != "0")
            summary.AppendLine($"Длит.:   >{s.MinDurationMs} мс");
        if (!string.IsNullOrEmpty(s.DumpPath))
            summary.AppendLine($"Дампы:   {s.DumpPath}  (тип {s.DumpType})");
        summary.AppendLine();
        summary.AppendLine("Перезапуск 1С не требуется — файл читается при старте каждого процесса.");

        ConsoleDialog.ShowOk("Готово", summary.ToString().TrimEnd());
        R.Invalidate();

        ConsoleDialog.ShowProgress("Обновление...", _ => _configs.Refresh());
        R.Invalidate();
        RebuildCurrentLevel();
    }

    private static bool IsYes(string s)
    {
        s = s.Trim().ToLowerInvariant();
        return s is "да" or "yes" or "y" or "1" or "true";
    }

    private void DoProcessInfo(string? pidStr)
    {
        if (pidStr == null || !int.TryParse(pidStr, out var pid)) return;
        var e = _processes.Entries.FirstOrDefault(x => x.Pid == pid);
        if (e == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PID:         {e.Pid}");
        sb.AppendLine($"Файл:        {e.ExeName}");
        if (!string.IsNullOrEmpty(e.Version))
            sb.AppendLine($"Версия 1С:   {e.Version}");
        sb.AppendLine($"Режим:       {e.Mode}");
        if (!string.IsNullOrEmpty(e.DbType))
            sb.AppendLine($"Тип базы:    {e.DbType}");
        if (!string.IsNullOrEmpty(e.DbPath))
            sb.AppendLine($"База:        {e.DbPath}");
        if (!string.IsNullOrEmpty(e.User1C))
            sb.AppendLine($"Польз. 1С:   {e.User1C}");
        if (!string.IsNullOrEmpty(e.WinUser))
            sb.AppendLine($"Windows:     {e.WinUser}");
        if (!string.IsNullOrEmpty(e.CmdLine))
        {
            sb.AppendLine();
            sb.AppendLine("Командная строка:");
            sb.AppendLine($"  {e.CmdLine}");
        }

        var pLines = sb.ToString().TrimEnd().Replace("\r\n", "\n").Split('\n')
                       .Select(l => l.TrimEnd('\r')).ToArray();
        ConsoleDialog.ShowInfo($"Процесс PID {e.Pid}", pLines, "  Закрыть  ");
        R.Invalidate();
    }

    private void DoProcessKill()
    {
        var item = CurrentItem();
        if (item == null || item.IsUp || item.BaseName == null) return;
        if (!int.TryParse(item.BaseName, out var pid)) return;
        var e = _processes.Entries.FirstOrDefault(x => x.Pid == pid);
        if (e == null) return;

        var name = string.IsNullOrEmpty(e.DbName) ? e.ExeName : e.DbName;
        if (!ConsoleDialog.Confirm("Завершить процесс", $"Завершить 1С:\n{name} (PID {pid})?"))
        {
            R.Invalidate(); return;
        }

        var err = _processes.Kill(pid);
        R.Invalidate();
        if (err != null) { ConsoleDialog.ShowOk("Ошибка", err); R.Invalidate(); }
        _processes.Refresh();
        RebuildCurrentLevel();
    }

    private void DoProcessKillAll()
    {
        int cnt = _processes.Entries.Count;
        if (cnt == 0) return;
        if (!ConsoleDialog.Confirm("Завершить все процессы 1С",
            $"Будут завершены все {cnt} процесс(а) 1С.\nПродолжить?"))
        {
            R.Invalidate(); return;
        }

        List<string> errors = null!;
        ConsoleDialog.ShowProgress("Завершение процессов...", _ => errors = _processes.KillAll());
        R.Invalidate();

        if (errors?.Count > 0)
        {
            ConsoleDialog.ShowOk("Ошибки", string.Join("\n", errors));
            R.Invalidate();
        }

        _processes.Refresh();
        RebuildCurrentLevel();
    }

    private RagentEntry? AgentUnderCursor()
    {
        var item = CurrentItem();
        if (item == null || item.IsUp || item.BaseName == null) return null;
        return _agents.Entries.FirstOrDefault(e => e.ServiceKey == item.BaseName);
    }

    private void DoAgentInfo(string? key)
    {
        if (key == null) return;

        RagentEntry? GetE() => _agents.Entries.FirstOrDefault(x => x.ServiceKey == key);
        if (GetE() == null) return;

        const string keyHint = "[S] Старт  [T] Стоп  [R] Рестарт  [D] Отладка  Esc — закрыть";

        ConsoleDialog.ShowTextWithKeys(
            getInfo: () =>
            {
                var e = GetE();
                return e == null
                    ? ("Агент", "(не найден)")
                    : ($"Агент: {e.DisplayName}", BuildAgentInfoText(e));
            },
            keyHint: keyHint,
            onKey: (k, ch) =>
            {
                var e = GetE();
                if (e == null) return false;

                char c = char.ToLower(ch);
                if (k == ConsoleKey.S || c == 's') { AgentServiceOp(e, "start"); _agents.Refresh(); return true; }
                if (k == ConsoleKey.T || c == 't') { AgentServiceOp(e, "stop");  _agents.Refresh(); return true; }
                if (k == ConsoleKey.R || c == 'r') { AgentServiceOp(e, "restart"); _agents.Refresh(); return true; }
                if (k == ConsoleKey.D || c == 'd') { DoAgentToggleDebugFor(e); _agents.Refresh(); return true; }
                return true; // прочие клавиши — остаться
            }
        );

        R.Invalidate();
        _agents.Refresh();
        RebuildCurrentLevel();
    }

    private static string BuildAgentInfoText(RagentEntry e)
    {
        int w = Math.Min(Console.WindowWidth - 4, 78) - 4;

        var pairs = new List<(string k, string v)>
        {
            ("Служба",    e.ServiceKey),
            ("Имя",       e.DisplayName),
            ("Версия 1С", e.Version),
            ("Порт",      e.Port.ToString()),
        };
        if (e.RegPort > 0) pairs.Add(("Порт rmngr", e.RegPort.ToString()));
        if (!string.IsNullOrEmpty(e.Range))   pairs.Add(("Диапазон", e.Range));
        if (!string.IsNullOrEmpty(e.DataDir)) pairs.Add(("Каталог",  e.DataDir));
        pairs.Add(("Отладка", e.DebugEnabled
            ? $"{e.DebugProtocol.ToUpper()} порт {e.DebugPort}"
            : "выключена"));
        pairs.Add(("Статус", StatusDisplay(e.Status)));

        int keyW = pairs.Max(p => p.k.Length);
        int valW = Math.Max(8, w - keyW - 3);

        var sb = new System.Text.StringBuilder();
        foreach (var (k, v) in pairs)
        {
            var lines = WordWrapVal(v, valW);
            for (int i = 0; i < lines.Length; i++)
                sb.AppendLine(i == 0
                    ? $"{k.PadRight(keyW)} : {lines[i]}"
                    : $"{new string(' ', keyW + 3)}{lines[i]}");
        }

        if (!string.IsNullOrEmpty(e.ImagePath))
        {
            sb.AppendLine();
            sb.AppendLine("Команда:");
            foreach (var cl in WordWrapVal(e.ImagePath, w - 2))
                sb.AppendLine("  " + cl);
        }

        return sb.ToString();
    }

    private void AgentServiceOp(RagentEntry e, string op)
    {
        if (op == "stop" && !e.IsRunning)
        {
            ConsoleDialog.ShowOk("Стоп", $"Служба уже остановлена:\n{e.DisplayName}");
            R.Invalidate(); return;
        }
        if (op == "start" && e.IsRunning)
        {
            ConsoleDialog.ShowOk("Запуск", $"Служба уже запущена:\n{e.DisplayName}");
            R.Invalidate(); return;
        }
        if (op is "stop" or "restart")
        {
            var q = op == "stop" ? "Остановить" : "Перезапустить";
            if (!ConsoleDialog.Confirm($"{q} службу", $"{q} агент?\n\n{e.DisplayName}"))
            { R.Invalidate(); return; }
        }

        var title = op switch { "start" => "Запуск", "stop" => "Остановка", _ => "Перезапуск" };
        string? err = null;
        ConsoleDialog.ShowProgress($"{title}: {e.DisplayName}", _ =>
        {
            err = op switch
            {
                "start"   => _agents.StartService(e.ServiceKey),
                "stop"    => _agents.StopService(e.ServiceKey),
                "restart" => _agents.RestartService(e.ServiceKey),
                _         => null
            };
        });
        R.Invalidate();

        var doneTitle = op switch { "start" => "Запущено", "stop" => "Остановлено", _ => "Перезапущено" };
        if (err != null)
            ConsoleDialog.ShowOk($"Ошибка: {title.ToLower()}", err);
        else
            ConsoleDialog.ShowOk(doneTitle, $"✓ {doneTitle}:\n{e.DisplayName}");
        R.Invalidate();
    }

    private void DoAgentStart()
    {
        var e = AgentUnderCursor();
        if (e == null) return;
        AgentServiceOp(e, "start");
        _agents.Refresh();
        RebuildCurrentLevel();
    }

    private void DoAgentStop()
    {
        var e = AgentUnderCursor();
        if (e == null) return;
        AgentServiceOp(e, "stop");
        _agents.Refresh();
        RebuildCurrentLevel();
    }

    private void DoAgentRestart()
    {
        var e = AgentUnderCursor();
        if (e == null) return;
        AgentServiceOp(e, "restart");
        _agents.Refresh();
        RebuildCurrentLevel();
    }

    private void DoAgentToggleDebug()
    {
        var e = AgentUnderCursor();
        if (e == null) return;
        DoAgentToggleDebugFor(e);
        _agents.Refresh();
        RebuildCurrentLevel();
    }

    private void DoAgentToggleDebugFor(RagentEntry e)
    {

        string? newProto;

        if (e.DebugEnabled)
        {
            var choices = new[]
            {
                $"Выключить отладку (текущая: {e.DebugProtocol.ToUpper()})",
                $"Переключить на {(e.DebugProtocol == "tcp" ? "HTTP" : "TCP")}",
            };
            var sel = ConsoleDialog.MultiSelect(e.DisplayName, choices);
            R.Invalidate();
            if (sel.Count == 0) return;
            newProto = sel[0] == 0 ? null
                     : (e.DebugProtocol == "tcp" ? "http" : "tcp");
        }
        else
        {
            var choices = new[] { "TCP (/debug -tcp)", "HTTP (/debug -http)" };
            var sel = ConsoleDialog.MultiSelect($"Включить отладку: {e.DisplayName}", choices);
            R.Invalidate();
            if (sel.Count == 0) return;
            newProto = sel[0] == 0 ? "tcp" : "http";
        }

        var err = _agents.SetDebug(e, newProto);
        R.Invalidate();

        if (err != null)
        {
            ConsoleDialog.ShowOk("Ошибка", err);
            R.Invalidate();
            _agents.Refresh();
            RebuildCurrentLevel();
            return;
        }

        if (e.IsRunning)
        {
            var action = newProto == null ? "отключения" : "включения";
            if (ConsoleDialog.Confirm("Перезапуск",
                    $"Для {action} отладки нужен перезапуск.\n\nПерезапустить {e.DisplayName}?"))
            {
                string? re = null;
                ConsoleDialog.ShowProgress("Перезапуск...",
                    _ => re = _agents.RestartService(e.ServiceKey));
                R.Invalidate();
                if (re != null) { ConsoleDialog.ShowOk("Ошибка перезапуска", re); R.Invalidate(); }
            }
            else R.Invalidate();
        }
    }

    private void DoAgentNew()
    {
        var versions = RagentModule.FindVersions();
        if (versions.Count == 0)
        {
            ConsoleDialog.ShowOk("Агенты",
                "Установки 1С с ragent.exe не найдены.\n\nПуть поиска:\n  %ProgramFiles%\\1cv8\\*\\bin\\ragent.exe");
            R.Invalidate(); return;
        }

        // Выбор версии 1С
        int chosenIdx;
        if (versions.Count == 1)
        {
            chosenIdx = 0;
        }
        else
        {
            var verNames = versions.Select(v => v.Version).ToArray();
            var selVer = ConsoleDialog.MultiSelect("Выберите версию 1С для нового агента", verNames);
            R.Invalidate();
            if (selVer.Count == 0) return;
            chosenIdx = selVer[0];
        }
        var selExe     = versions[chosenIdx].RagentExe;
        var selVersion = versions[chosenIdx].Version;

        // Параметры агента
        var fields = new (string Key, string Label)[]
        {
            ("port",    "Порт агента"),
            ("regport", "Порт rmngr"),
            ("range",   "Диапазон портов"),
            ("datadir", "Каталог данных /d"),
        };
        var defaults = new Dictionary<string, string>
        {
            ["port"]    = "1540",
            ["regport"] = "1541",
            ["range"]   = "1560:1591",
            ["datadir"] = "",
        };
        var vals = ConsoleDialog.Form($"Новый агент 1С {selVersion}", fields, defaults);
        R.Invalidate();
        if (vals == null) return;

        // Режим отладки
        string? proto = null;
        if (ConsoleDialog.Confirm("Отладка", "Включить отладку для нового агента?"))
        {
            var dbgChoices = new[] { "TCP (/debug -tcp)", "HTTP (/debug -http)" };
            var selDbg = ConsoleDialog.MultiSelect("Выберите протокол отладки", dbgChoices);
            R.Invalidate();
            proto = selDbg.Count > 0 && selDbg[0] == 1 ? "http" : "tcp";
        }
        else R.Invalidate();

        if (!int.TryParse(vals["port"], out int port) || port < 1 || port > 65535)
        {
            ConsoleDialog.ShowOk("Ошибка", "Некорректный порт.");
            R.Invalidate(); return;
        }

        var cp = new RagentModule.CreateParams
        {
            RagentExe = selExe,
            Port      = port,
            RegPort   = int.TryParse(vals["regport"], out int rp) ? rp : port + 1,
            Range     = vals.TryGetValue("range", out var rng) ? rng : "1560:1591",
            DataDir   = vals.TryGetValue("datadir", out var dd) ? dd : "",
            Protocol  = proto,
        };

        string? err = null;
        ConsoleDialog.ShowProgress("Создание службы...", _ => err = _agents.CreateAgent(cp));
        R.Invalidate();

        if (err != null) { ConsoleDialog.ShowOk("Ошибка создания", err); R.Invalidate(); return; }

        ConsoleDialog.ShowOk("Готово",
            $"Агент на порту {port} зарегистрирован.\n\nВ списке нажмите [S] для запуска.");
        R.Invalidate();

        _agents.Refresh();
        RebuildCurrentLevel();
    }

    private void DoAgentDelete()
    {
        var e = AgentUnderCursor();
        if (e == null) return;

        if (!ConsoleDialog.Confirm("Удалить службу агента",
            $"Снять регистрацию и удалить службу?\n\n{e.DisplayName}\n\nСлужба будет остановлена."))
        { R.Invalidate(); return; }

        string? err = null;
        ConsoleDialog.ShowProgress($"Удаление: {e.DisplayName}",
            _ => err = _agents.DeleteAgent(e.ServiceKey));
        R.Invalidate();

        if (err != null) { ConsoleDialog.ShowOk("Ошибка удаления", err); R.Invalidate(); }
        _agents.Refresh();
        RebuildCurrentLevel();
    }

    // ── RAS ───────────────────────────────────────────────────────────────────

    private void DoRasInfo(string key)
    {
        RasEntry? GetE() => _agents.RasEntries.FirstOrDefault(x => x.ServiceKey == key);
        if (GetE() == null) return;

        const string keyHint = "[S] Старт  [T] Стоп  [R] Рестарт  [Del] Удалить службу  Esc — закрыть";

        ConsoleDialog.ShowTextWithKeys(
            getInfo: () =>
            {
                var e = GetE();
                return e == null
                    ? ("RAS", "(не найден)")
                    : ($"RAS: {e.DisplayName}", BuildRasInfoText(e));
            },
            keyHint: keyHint,
            onKey: (k, _) =>
            {
                var e = GetE();
                if (e == null) return false;

                if (k == ConsoleKey.S) { AgentServiceOp2(e.ServiceKey, e.DisplayName, "start"); _agents.Refresh(); return true; }
                if (k == ConsoleKey.T) { AgentServiceOp2(e.ServiceKey, e.DisplayName, "stop");  _agents.Refresh(); return true; }
                if (k == ConsoleKey.R) { AgentServiceOp2(e.ServiceKey, e.DisplayName, "restart"); _agents.Refresh(); return true; }
                if (k == ConsoleKey.Delete)
                {
                    if (!ConsoleDialog.Confirm("Удалить RAS",
                        $"Снять с регистрации и удалить службу?\n\n{e.DisplayName}"))
                        return true;
                    string? err = null;
                    ConsoleDialog.ShowProgress($"Удаление: {e.DisplayName}", _ => err = _agents.DeleteRas(e.ServiceKey));
                    if (err != null) ConsoleDialog.ShowOk("Ошибка удаления", err);
                    else ConsoleDialog.ShowOk("Готово", $"✓ Служба RAS удалена:\n{e.DisplayName}");
                    _agents.Refresh();
                    RebuildCurrentLevel();
                    return false;
                }
                return true;
            }
        );

        _agents.Refresh();
        RebuildCurrentLevel();
    }

    private void DoRasInstall(string verAndExe)
    {
        var parts = verAndExe.Split('|');
        if (parts.Length < 2) return;
        var version = parts[0];
        var rasExe  = parts[1];

        var fields = new (string Key, string Label)[]
        {
            ("port",   "RAS порт"),
            ("agent",  "Адрес агента"),
            ("user",   "Пользователь (пусто = LocalSystem)"),
            ("pwd",    "Пароль"),
        };
        var defaults = new Dictionary<string, string>
        {
            ["port"]  = "1545",
            ["agent"] = "localhost:1540",
            ["user"]  = @".\USR1CV8",
            ["pwd"]   = "",
        };

        var vals = ConsoleDialog.Form($"Установить RAS {version}", fields, defaults);
        if (vals == null) return;

        if (!int.TryParse(vals["port"], out int rasPort) || rasPort < 1 || rasPort > 65535)
        {
            ConsoleDialog.ShowOk("Ошибка", "Некорректный порт.");
            return;
        }

        string? err = null;
        ConsoleDialog.ShowProgress($"Регистрация RAS {version}...", _ =>
            err = _agents.CreateRas(rasExe, rasPort, vals["agent"], vals["user"], vals["pwd"]));

        if (err != null) { ConsoleDialog.ShowOk("Ошибка", err); return; }

        ConsoleDialog.ShowOk("Готово",
            $"✓ RAS зарегистрирован.\n\nВерсия: {version}\nПорт: {rasPort}\n\nВ списке нажмите [S] для запуска.");

        _agents.Refresh();
        RebuildCurrentLevel();
    }

    private static string BuildRasInfoText(RasEntry e)
    {
        int w = Math.Min(Console.WindowWidth - 4, 78) - 4;

        var pairs = new List<(string k, string v)>
        {
            ("Служба",   e.ServiceKey),
            ("Имя",      e.DisplayName),
            ("Версия 1С",e.Version),
            ("RAS порт", e.RasPort.ToString()),
            ("Агент",    e.AgentAddr),
            ("Статус",   StatusDisplay(e.Status)),
        };

        int keyW = pairs.Max(p => p.k.Length);
        int valW = Math.Max(8, w - keyW - 3);

        var sb = new System.Text.StringBuilder();
        foreach (var (k, v) in pairs)
        {
            var lines = WordWrapVal(v, valW);
            for (int i = 0; i < lines.Length; i++)
                sb.AppendLine(i == 0
                    ? $"{k.PadRight(keyW)} : {lines[i]}"
                    : $"{new string(' ', keyW + 3)}{lines[i]}");
        }

        if (!string.IsNullOrEmpty(e.ImagePath))
        {
            sb.AppendLine();
            sb.AppendLine("Команда:");
            foreach (var cl in WordWrapVal(e.ImagePath, w - 2))
                sb.AppendLine("  " + cl);
        }

        return sb.ToString();
    }

    private void AgentServiceOp2(string serviceKey, string displayName, string op)
    {
        if (op is "stop" or "restart")
        {
            var q = op == "stop" ? "Остановить" : "Перезапустить";
            if (!ConsoleDialog.Confirm($"{q} службу", $"{q}?\n\n{displayName}")) return;
        }

        var title = op switch { "start" => "Запуск", "stop" => "Остановка", _ => "Перезапуск" };
        string? err = null;
        ConsoleDialog.ShowProgress($"{title}: {displayName}", _ =>
        {
            err = op switch
            {
                "start"   => _agents.StartService(serviceKey),
                "stop"    => _agents.StopService(serviceKey),
                "restart" => _agents.RestartService(serviceKey),
                _         => null
            };
        });

        var doneTitle = op switch { "start" => "Запущено", "stop" => "Остановлено", _ => "Перезапущено" };
        if (err != null) ConsoleDialog.ShowOk($"Ошибка: {title.ToLower()}", err);
        else ConsoleDialog.ShowOk(doneTitle, $"✓ {doneTitle}:\n{displayName}");
    }

    // ── Лог ───────────────────────────────────────────────────────────────────
    private void OnLog(string lvl, string txt)
    {
        lock (_logLock)
        {
            _log.Add((lvl, txt));
            if (_log.Count > 300) _log.RemoveAt(0);
        }
    }

    private (string Lvl, string Txt) LastLogMsg()
    {
        lock (_logLock)
        {
            return _log.Count > 0 ? _log[_log.Count - 1] : ("", "");
        }
    }



    // ── Рисование ─────────────────────────────────────────────────────────────
    private void Draw()
    {
        R.CheckResize();

        var lvl = _nav.Peek();
        if (lvl.Kind == NavLevelKind.Home)
        {
            DrawHome(lvl);
            return;
        }

        DrawHeader();
        R.BoxTop(1, lvl.Title);
        DrawColHeader();
        R.BoxSep(3);
        DrawItems(lvl);
        R.BoxSep(SepBot);
        DrawPanelInfo(lvl);
        R.BoxBottom(BotBorder);
        DrawMsg();
        DrawKeyBar();
        R.Flush();
    }

    private void DrawHome(NavLevel lvl)
    {
        DrawHeader();
        R.SplitTop(1, "Меню  ·  " + lvl.Title, "Сводка");
        R.SplitRow(2, "", R.PanelFg, R.PanelBg, "", R.PanelFg, R.PanelBg);
        R.SplitSep(3);

        var diagLines = BuildDiagLines();
        const int badgeW = 10; // [xxxxxxxx] — 10 символов

        for (int row = ItemTop; row <= ItemBot; row++)
        {
            int idx = lvl.ScrollTop + (row - ItemTop);

            // ── левая панель: пункт меню ──────────────────────────────────
            string leftContent = "";
            ConsoleColor lfg = R.PanelFg, lbg = R.PanelBg;
            if (idx < lvl.Items.Count)
            {
                var item      = lvl.Items[idx];
                bool isCursor = idx == lvl.Cursor;
                string arrow  = item.CanEnter ? "►" : " ";
                string badge  = HomeBadge(item);             // 10 chars: [xxxxxxxx]
                int nameAvail = R.LeftInnerW - 3 - badgeW;  // " ► " + badge
                string name   = R.Fit(item.Name, nameAvail);
                leftContent   = $" {arrow} {name}{badge}";
                lfg = isCursor ? R.CurFg : R.PanelFg;
                lbg = isCursor ? R.CurBg : R.PanelBg;
            }

            // ── правая панель: строка диагностики ─────────────────────────
            string rightContent = "";
            ConsoleColor rfg = R.PanelFg;
            int dIdx = row - ItemTop;
            if (dIdx < diagLines.Count)
            {
                rightContent = diagLines[dIdx].Text;
                rfg          = diagLines[dIdx].Fg;
            }

            R.SplitRow(row, leftContent, lfg, lbg, rightContent, rfg, R.PanelBg);
        }

        R.SplitSep(SepBot);
        R.SplitRow(InfoRow,
            "  [Enter] Открыть",
            R.HdrFg, R.HdrBg,
            "  [F5] Обновить сводку",
            R.HdrFg, R.HdrBg);
        R.SplitBottom(BotBorder);
        DrawMsg();
        DrawKeyBar();
        R.Flush();
    }

    private string HomeBadge(NavItem item)
    {
        string inner;
        switch (item.ModuleId)
        {
            case "cache":
            case "templates":
                inner = item.SizeBytes > 0 ? SafeDelete.FormatSize(item.SizeBytes) : "0 Б";
                break;
            case "bases":
                inner = _bases.Entries.Count.ToString();
                break;
            case "licenses":
                inner = _licenses.Entries.Count > 0 ? "✓" : " ";
                break;
            case "agents":
                int runAgents = _agents.Entries.Count(a => a.Status == "Running");
                inner = runAgents > 0 ? $"▶ {runAgents}"
                      : (_agents.Entries.Count > 0 ? "■" : " ");
                break;
            case "processes":
                inner = _processes.Entries.Count > 0
                    ? _processes.Entries.Count.ToString() : " ";
                break;
            case "web":
                inner = _web.Entries.Count > 0 ? "✓" : " ";
                break;
            case "emulators":
                int eFound = _emulators.Found.Count;
                inner = eFound > 0 ? $"! {eFound}" : "✓";
                break;
            case "configs":
                int cfFound = _configs.Files.Count(f => f.Found);
                int cfTotal = _configs.Files.Count;
                inner = $"{cfFound}/{cfTotal}";
                break;
            case "com":
                int comReg = _com.Registered.Count;
                inner = comReg > 0 ? comReg.ToString() : " ";
                break;
            default:
                inner = " ";
                break;
        }
        return ("[" + inner + "]").PadLeft(10);
    }

    private List<(string Text, ConsoleColor Fg)> BuildDiagLines()
    {
        var lines = new List<(string, ConsoleColor)>();
        var d = _diagnostics.Data;

        if (d.IsScanning)
        {
            lines.Add(("  Сканирование системы...", ConsoleColor.Gray));
            return lines;
        }
        if (d.ScanError != null)
        {
            lines.Add(("  Ошибка: " + d.ScanError, ConsoleColor.Red));
            return lines;
        }

        // ── Версии 1С ────────────────────────────────────────────────────
        lines.Add((" Версии 1С платформы", ConsoleColor.Cyan));
        if (d.Versions.Count == 0)
        {
            lines.Add(("  [ ]  не обнаружено", ConsoleColor.DarkGray));
        }
        else
        {
            // Единый форматный шаблон — заголовок и строки данных используют одну строку форматирования.
            // CW=6 покрывает самые длинные значения: "Толст"=5, "[V83]"=5, "ibcmd"=5.
            const string FMT = "  {0,-13} {1,-6}{2,-6}{3,-6}{4,-6}{5,-6}{6}";
            static string Chk(bool f) => f ? "[✓]" : "[ ]";

            lines.Add((string.Format(FMT, "Версия", "Серв", "Толст", "Тонк", "COM", "Веб", "ibcmd"), ConsoleColor.Gray));
            foreach (var v in d.Versions)
            {
                string com = v.HasCom && v.ComVer != null ? $"[{v.ComVer}]" : "[ ]";
                string row = string.Format(FMT,
                    v.Version,
                    Chk(v.HasServer), Chk(v.HasThick), Chk(v.HasThin),
                    com,
                    Chk(v.HasWeb), Chk(v.HasIbcmd));
                lines.Add((row, ConsoleColor.White));
            }
        }
        lines.Add(("", R.PanelFg));

        // ── Веб-серверы ──────────────────────────────────────────────────
        lines.Add((" Веб-серверы", ConsoleColor.Cyan));
        foreach (var ws in d.WebServers)
        {
            if (!ws.IsInstalled)
            {
                lines.Add(($"  [ ]  {ws.Name,-14} не установлен", ConsoleColor.DarkGray));
            }
            else
            {
                string ver = ws.Version != null ? $" {ws.Version}" : "";
                string st  = ws.IsRunning ? "работает" : "остановлен";
                string ind = ws.IsRunning ? "[✓]" : "[■]";
                ConsoleColor c = ws.IsRunning ? ConsoleColor.Green : ConsoleColor.Yellow;
                lines.Add(($"  {ind}  {ws.Name + ver,-14} {st}", c));
            }
        }
        lines.Add(("", R.PanelFg));

        // ── СУБД ─────────────────────────────────────────────────────────
        lines.Add((" СУБД", ConsoleColor.Cyan));
        foreach (var db in d.Databases)
        {
            if (!db.IsInstalled)
            {
                lines.Add(($"  [ ]  {db.Name,-20} не установлен", ConsoleColor.DarkGray));
            }
            else
            {
                string ver   = db.Version != null ? $" {db.Version}" : "";
                string st    = db.IsRunning ? "работает" : "остановлен";
                string admin = db.HasAdminTool ? "  [SSMS]" : "";
                string ind   = db.IsRunning ? "[✓]" : "[■]";
                ConsoleColor c = db.IsRunning ? ConsoleColor.Green : ConsoleColor.Yellow;
                lines.Add(($"  {ind}  {db.Name + ver,-20} {st}{admin}", c));
            }
        }
        lines.Add(("", R.PanelFg));

        // ── Сервисы 1С ───────────────────────────────────────────────────
        lines.Add((" Сервисы 1С", ConsoleColor.Cyan));
        if (_agents.Entries.Count == 0)
        {
            lines.Add(("  [ ]  сервисы не обнаружены", ConsoleColor.DarkGray));
        }
        else
        {
            foreach (var svc in _agents.Entries)
            {
                bool running = svc.Status == "Running";
                string st  = StatusDisplay(svc.Status);
                string ind = running ? "[✓]" : "[■]";
                ConsoleColor c = running ? ConsoleColor.Green : ConsoleColor.Yellow;
                string name = svc.DisplayName.Length > 28
                    ? svc.DisplayName.Substring(0, 27) + "…"
                    : svc.DisplayName;
                lines.Add(($"  {ind}  {name,-28} {st}", c));
            }
        }
        lines.Add(("", R.PanelFg));

        // ── Порты ────────────────────────────────────────────────────────
        if (d.Ports.Count > 0)
        {
            lines.Add((" Порты", ConsoleColor.Cyan));
            var portLine = new System.Text.StringBuilder("  ");
            foreach (var (port, label, open) in d.Ports)
                portLine.Append($"[{(open ? "✓" : " ")}] {port}  ");
            lines.Add((portLine.ToString().TrimEnd(), ConsoleColor.White));
        }

        // ── Брандмауэр ───────────────────────────────────────────────────
        lines.Add(("", R.PanelFg));
        lines.Add((" Брандмауэр — правило «1c»", ConsoleColor.Cyan));
        var fwIn  = _firewall.Rules.FirstOrDefault(r =>
            string.Equals(r.Direction, "In",  StringComparison.OrdinalIgnoreCase));
        var fwOut = _firewall.Rules.FirstOrDefault(r =>
            string.Equals(r.Direction, "Out", StringComparison.OrdinalIgnoreCase));
        string FwLine(string dir, FirewallRule? r)
        {
            if (r == null) return $"  [ ]  {dir,-12} нет правила";
            string ind  = r.Enabled ? "[✓]" : "[■]";
            string info = string.IsNullOrEmpty(r.Ports) ? r.Action : $"{r.Action}  {r.Ports}";
            return $"  {ind}  {dir,-12} {info}";
        }
        var fwColor = (fwIn != null && fwIn.Enabled && fwOut != null && fwOut.Enabled)
            ? ConsoleColor.Green : ConsoleColor.Yellow;
        lines.Add((FwLine("Входящее",   fwIn),  fwIn  == null ? ConsoleColor.DarkGray : fwColor));
        lines.Add((FwLine("Исходящее",  fwOut), fwOut == null ? ConsoleColor.DarkGray : fwColor));

        return lines;
    }

    private void DrawHeader()
    {
        R.FillRow(0, R.HdrFg, R.HdrBg);
        R.Put(0, 0, $" Clinkon1C {Program.FullVersion}  │  {RepoUrl}", R.HdrFg, R.HdrBg);
        if (!string.IsNullOrEmpty(_updateNotice))
        {
            var n = $" ★ {_updateNotice} — [U] обновить ";
            R.Put(R.W - n.Length, 0, n, ConsoleColor.Green, R.HdrBg);
        }
    }

    private void DrawColHeader()
    {
        var kind = _nav.Count > 0 ? _nav.Peek().Kind : NavLevelKind.Home;

        if (kind == NavLevelKind.BasesRoot)
        {
            const int namePartW = 32;
            var content = R.Fit(" Имя", namePartW) + R.Fit("Подключение", InnerW - namePartW);
            R.BoxRow(2, content, R.HdrFg, R.HdrBg);
            return;
        }

        if (kind == NavLevelKind.LicensesRoot)
        {
            const int namePartW = 34;
            var content = R.Fit(" Имя лицензии", namePartW) + R.Fit("Тип / Привязка", InnerW - namePartW);
            R.BoxRow(2, content, R.HdrFg, R.HdrBg);
            return;
        }

        if (kind == NavLevelKind.AgentsRoot)
        {
            const int nameW = 40;
            const int verW  = 12;
            var content = R.Fit(" Служба", nameW)
                        + R.Fit("Версия 1С", verW)
                        + R.Fit("Статус", InnerW - nameW - verW);
            R.BoxRow(2, content, R.HdrFg, R.HdrBg);
            return;
        }

        if (kind == NavLevelKind.ProcessesRoot)
        {
            const int pidW  = 7;
            const int modeW = 14;
            const int dbW   = 32;
            const int u1cW  = 16;
            var content = R.Fit(" PID",       pidW)
                        + R.Fit("Режим",      modeW)
                        + R.Fit("База",       dbW)
                        + R.Fit("Польз. 1С",  u1cW)
                        + R.Fit("Windows",    InnerW - pidW - modeW - dbW - u1cW);
            R.BoxRow(2, content, R.HdrFg, R.HdrBg);
            return;
        }

        if (kind == NavLevelKind.WebRoot)
        {
            const int aliasW = 22;
            const int typeW  = 9;
            const int verW   = 14;
            var content = R.Fit(" Псевдоним",   aliasW)
                        + R.Fit("Тип",          typeW)
                        + R.Fit("База",         InnerW - aliasW - typeW - verW)
                        + R.Fit("Версия 1С",    verW);
            R.BoxRow(2, content, R.HdrFg, R.HdrBg);
            return;
        }

        bool bySize = _cache.SortBy == SortMode.BySize;
        var nameLabel = bySize ? " Имя" : " Имя ▲";
        var sizeLabel = bySize ? "Размер ▼ " : "Размер   ";
        var colContent = R.Fit(nameLabel, NameW) + sizeLabel.PadLeft(SizeCW);
        R.BoxRow(2, colContent, R.HdrFg, R.HdrBg);
    }

    private void DrawItems(NavLevel lvl)
    {
        for (int row = ItemTop; row <= ItemBot; row++)
        {
            int idx = lvl.ScrollTop + (row - ItemTop);
            if (idx < lvl.Items.Count)
                DrawItem(row, lvl.Items[idx], idx == lvl.Cursor, lvl.Kind);
            else
                R.BoxRow(row, "", R.PanelFg, R.PanelBg);
        }
    }

    private void DrawItem(int row, NavItem item, bool isCursor, NavLevelKind kind = NavLevelKind.Home)
    {
        // Процессы — пятиколоночная раскладка
        if (kind == NavLevelKind.ProcessesRoot && !item.IsUp)
        {
            const int pidW  = 7;
            const int modeW = 14;
            const int dbW   = 32;
            const int u1cW  = 16;
            var pfg = isCursor ? R.CurFg : ConsoleColor.White;
            var pbg = isCursor ? R.CurBg : R.PanelBg;
            var pidStr  = item.BaseName ?? "";
            var modeStr = item.PathType ?? "";
            var dbStr   = item.Name;
            var u1cStr  = item.UserName ?? "";
            var winStr  = item.Description ?? "";
            R.BoxRow(row,
                R.Fit($" {pidStr}",  pidW)
                + R.Fit(modeStr,     modeW)
                + R.Fit(dbStr,       dbW)
                + R.Fit(u1cStr,      u1cW)
                + R.Fit(winStr,      InnerW - pidW - modeW - dbW - u1cW),
                pfg, pbg);
            return;
        }

        // Веб-публикации — четырёхколоночная раскладка
        if (kind == NavLevelKind.WebRoot && !item.IsUp)
        {
            const int aliasW = 22;
            const int typeW  = 9;
            const int verW   = 14;
            var wfg = isCursor ? R.CurFg : (item.IsDead ? ConsoleColor.DarkGray : ConsoleColor.Green);
            var wbg = isCursor ? R.CurBg : R.PanelBg;
            R.BoxRow(row,
                R.Fit($" ► {item.Name}", aliasW)
                + R.Fit(item.PathType ?? "", typeW)
                + R.Fit(item.Description ?? "", InnerW - aliasW - typeW - verW)
                + R.Fit(item.UserName ?? "", verW),
                wfg, wbg);
            return;
        }

        // Конфиги — двухколоночная раскладка: имя файла + путь
        if (kind == NavLevelKind.ConfigsRoot && !item.IsUp)
        {
            var cfgFg = isCursor ? R.CurFg
                : (item.IsDead ? ConsoleColor.DarkGray
                : item.CanEnter ? ConsoleColor.Cyan
                : ConsoleColor.DarkCyan);
            var cfgBg = isCursor ? R.CurBg : R.PanelBg;
            const int nameW = 22;
            var pathStr = item.Description ?? "";
            // для найденных — сокращаем путь
            if (!item.IsDead && pathStr.Length > InnerW - nameW - 1)
                pathStr = "…" + pathStr.Substring(pathStr.Length - (InnerW - nameW - 2));
            R.BoxRow(row,
                R.Fit($" {(item.CanEnter ? "►" : " ")} {item.Name}", nameW)
                + R.Fit(pathStr, InnerW - nameW),
                cfgFg, cfgBg);
            return;
        }

        // Эмуляторы HASP — двухколоночная раскладка с красной подсветкой
        if (kind == NavLevelKind.EmulatorsRoot && !item.IsUp && item.BaseName != null)
        {
            var efg = isCursor ? R.CurFg : ConsoleColor.Red;
            var ebg = isCursor ? R.CurBg : R.PanelBg;
            const int nameW = 20;
            R.BoxRow(row,
                R.Fit($" ⚠ {item.Name}", nameW)
                + R.Fit(item.Description ?? "", InnerW - nameW),
                efg, ebg);
            return;
        }

        // Агенты — специальная трёхколоночная раскладка
        if (kind == NavLevelKind.AgentsRoot && !item.IsUp)
        {
            bool running = item.Description?.StartsWith("▶") == true;
            var afg = isCursor ? R.CurFg : (running ? ConsoleColor.Green : ConsoleColor.DarkGray);
            var abg = isCursor ? R.CurBg : R.PanelBg;
            const int nameW = 40;
            const int verW  = 12;
            R.BoxRow(row,
                R.Fit($" ► {item.Name}", nameW)
                + R.Fit(item.PathType ?? "", verW)
                + R.Fit(item.Description ?? "", InnerW - nameW - verW),
                afg, abg);
            return;
        }

        ConsoleColor fg, bg;

        if (isCursor)
        {
            fg = R.CurFg; bg = R.CurBg;
        }
        else if (!item.IsUp && item.Paths.Count > 0 && item.Paths.All(p => _sel.Contains(p)))
        {
            fg = R.SelFg; bg = R.PanelBg;
        }
        else if (item.IsUnknownGroup)
        {
            fg = ConsoleColor.DarkCyan; bg = R.PanelBg;  // заметно, но не кричаще
        }
        else if (item.IsDead || item.IsExcluded)
        {
            fg = R.DeadFg; bg = R.PanelBg;
        }
        else
        {
            fg = R.PanelFg; bg = R.PanelBg;
        }

        string content;
        if (item.IsUp)
        {
            content = R.Fit(" [..]", InnerW);
        }
        else if (item.ShowDescCol && item.Description != null)
        {
            // Элемент с двухколоночным описанием (Лицензии)
            const int namePartW = 34;
            var name = R.Fit($" ► {item.Name}", namePartW);
            var desc = R.Fit(item.Description, InnerW - namePartW);
            content  = name + desc;
        }
        else if (item.Description != null && item.BaseName != null)
        {
            // Элемент раздела "Базы": [*] Имя    Connect=...
            bool isMarkedBase = _markedBases.Contains(item.BaseName);
            var  mark = isMarkedBase ? "*" : " ";
            if (!isCursor)
                fg = isMarkedBase ? R.SelFg : R.PanelFg;
            const int namePartW = 32;
            var name    = R.Fit($" {mark} {item.Name}", namePartW);
            var connect = R.Fit(item.Description, InnerW - namePartW);
            content = name + connect;
        }
        else
        {
            var arrow   = item.CanEnter ? "►" : " ";
            var nameStr = R.Fit($" {arrow} {item.Name}", NameW);
            var sizeStr = SafeDelete.FormatSize(item.SizeBytes).PadLeft(SizeCW);
            content = nameStr + sizeStr;
        }

        R.BoxRow(row, content, fg, bg);
    }

    private void DrawPanelInfo(NavLevel lvl)
    {
        if (lvl.Kind == NavLevelKind.BasesRoot)
        {
            var baseItems = lvl.Items.Where(i => !i.IsUp).ToList();
            int marked    = baseItems.Count(i => i.BaseName != null && _markedBases.Contains(i.BaseName));
            var basesInfo = marked > 0
                ? $"  {baseItems.Count} баз  │  Отмечено: {marked}  │  [C] Копировать  [E] Экспорт .v8i"
                : $"  {baseItems.Count} баз  │  [C] Копировать  [E] Экспорт .v8i";
            R.BoxRow(InfoRow, basesInfo, R.HdrFg, R.HdrBg);
            return;
        }

        if (lvl.Kind == NavLevelKind.LicensesRoot)
        {
            var licInfo = $"  {lvl.Items.Count(i => !i.IsUp)} лицензий  │  [Enter] Инфо  [A] Активация  [V] Проверить  [F8] Удалить";
            R.BoxRow(InfoRow, licInfo, R.HdrFg, R.HdrBg);
            return;
        }

        if (lvl.Kind == NavLevelKind.AgentsRoot)
        {
            int cnt     = _agents.Entries.Count;
            int running = _agents.Entries.Count(e => e.IsRunning);
            var agentInfo = $"  {cnt} агент(а)  │  Запущено: {running}  │  [S] Старт  [T] Стоп  [R] Рестарт  [D] Отладка  [N] Новый  [F8] Удалить";
            R.BoxRow(InfoRow, agentInfo, R.HdrFg, R.HdrBg);
            return;
        }

        if (lvl.Kind == NavLevelKind.ProcessesRoot)
        {
            int cnt = _processes.Entries.Count;
            var procInfo = $"  {cnt} процесс(а)  │  [Enter] Инфо  [K]/[F8] Завершить  [A] Завершить все  [F5] Обновить";
            R.BoxRow(InfoRow, procInfo, R.HdrFg, R.HdrBg);
            return;
        }

        if (lvl.Kind == NavLevelKind.WebRoot)
        {
            var apacheSt = !_web.ApacheFound ? "не найден"
                : (_web.ApacheRunning ? "▶ Работает" : "■ Остановлен");
            var webInfo  = $"  {_web.Entries.Count} публик.  │  Apache: {apacheSt}  │  [Enter] Инфо  [A] Анон.  [E] Редакт.  [J] JWT  [P] Публик.  [F8] Снять  [S] Старт  [T] Стоп  [R] Рестарт  [F5] Обновить";
            R.BoxRow(InfoRow, webInfo, R.HdrFg, R.HdrBg);
            return;
        }

        if (lvl.Kind == NavLevelKind.EmulatorsRoot)
        {
            int found = _emulators.Found.Count;
            var emulInfo = found == 0
                ? $"  Проверено {EmulatorModule.KnownEmulators.Length} эмуляторов  │  Система чиста  │  [F5] Повторить скан"
                : $"  Найдено: {found} из {EmulatorModule.KnownEmulators.Length}  │  [Enter] Детали  [D]/[F8] Удалить  [F5] Повторить скан";
            R.BoxRow(InfoRow, emulInfo, found > 0 ? ConsoleColor.Red : ConsoleColor.Green, R.HdrBg);
            return;
        }

        if (lvl.Kind == NavLevelKind.ConfigsRoot)
        {
            int found      = _configs.Files.Count(f => f.Found);
            int totalFiles = _configs.Files.Count;
            var cfgInfo = $"  {found} из {totalFiles} файлов найдено  │  [Enter] Редактировать  │  [F5] Обновить";
            R.BoxRow(InfoRow, cfgInfo, R.HdrFg, R.HdrBg);
            return;
        }

        var items   = lvl.Items.Where(i => !i.IsUp).ToList();
        long total  = items.Sum(i => (long)i.SizeBytes);
        int selCnt  = items.Count(i => i.Paths.Any(p => _sel.Contains(p)));

        string info;
        if (selCnt > 0)
        {
            long selSz = _cache.Entries
                .SelectMany(e => e.Paths)
                .Where(p => _sel.Contains(p.Path))
                .Sum(p => p.SizeBytes);
            info = $"  {items.Count} объект(а)  │  Выделено: {selCnt}  [{SafeDelete.FormatSize(selSz)}]  │  {SafeDelete.FormatSize(total)}";
        }
        else
        {
            info = $"  {items.Count} объект(а)  │  {SafeDelete.FormatSize(total)}";
        }

        R.BoxRow(InfoRow, info, R.HdrFg, R.HdrBg);
    }

    private void DrawMsg()
    {
        var (lvl, txt) = LastLogMsg();
        R.FillRow(MsgRow, ConsoleColor.Black, ConsoleColor.Black);
        if (!string.IsNullOrEmpty(txt))
        {
            ConsoleColor fg = lvl == "ERROR" ? R.ErrFg : lvl == "WARN" ? R.WarnFg : R.InfoFg;
            var prefix = lvl == "ERROR" ? "✗ " : lvl == "WARN" ? "! " : "  ";
            R.Put(0, MsgRow, prefix + txt, fg, ConsoleColor.Black);
        }
    }

    private void DrawKeyBar()
    {
        var kind = _nav.Count > 0 ? _nav.Peek().Kind : NavLevelKind.Home;
        var view = _cache.ViewMode == CacheViewMode.ByUser ? "По базе" : "По польз.";

        // Секционные подсказки (только буквенные клавиши, без F-клавиш)
        var left = kind switch
        {
            NavLevelKind.BasesRoot     => "[Пробел] Отметить  [C] Польз.  [E] .v8i",
            NavLevelKind.LicensesRoot  => "[Enter] Инфо  [A] Активация  [V] Проверить",
            NavLevelKind.AgentsRoot    => "[Enter] Инфо  [S] Старт  [T] Стоп  [R] Рестарт  [D] Отладка  [N] Новый",
            NavLevelKind.ProcessesRoot => "[Enter] Инфо  [K]/[F8] Завершить  [A] Завершить все",
            NavLevelKind.WebRoot       => "[Enter] Инфо  [A] Анон.  [E] Редакт.  [J] JWT  [P] Публик.  [F8] Снять  [S] Старт  [T] Стоп  [R] Рестарт",
            NavLevelKind.EmulatorsRoot => "[Enter] Детали  [D] Удалить",
            NavLevelKind.ConfigsRoot   => "[Enter] Редактировать",
            NavLevelKind.ComRoot       => "[Enter] Инфо/Рег.  [E] ProgID",
            NavLevelKind.CacheRoot or NavLevelKind.CacheUser
                                       => $"[Пробел] Выделить  [V] {view}",
            _                          => "[Пробел] Выделить",
        };

        // F8 в фиксированном хвосте: 1=активна, -1=серая (неактивна), 0=не показывать (уже в left)
        var (f8State, f8Label) = kind switch
        {
            NavLevelKind.BasesRoot     => (-1, "Удалить"),
            NavLevelKind.ConfigsRoot   => (-1, "Удалить"),
            NavLevelKind.ProcessesRoot => (0,  ""),        // [K]/[F8] уже в left
            NavLevelKind.WebRoot       => (0,  ""),        // [F8] Снять уже в left
            _                          => (1,  "Удалить"),
        };

        // Фиксированный хвост в порядке F1→F5→F8→Tab(F9)→F10
        const string TF1  = "  [F1] ?";
        const string TF5  = "  [F5] Обновить";
        const string TTab = "  [Tab] Лог";
        const string TF10 = "  [F10] Выход";
        var f8Part = f8State != 0 ? $"  [F8] {f8Label}" : "";
        int tailLen = TF1.Length + TF5.Length + f8Part.Length + TTab.Length + TF10.Length;

        // Версия прижата к правому краю
        var ver  = $"  {Program.FullVersion}  ";
        int verX = R.W - ver.Length;
        int tailX = verX - tailLen;
        int leftW = Math.Max(0, tailX);

        R.FillRow(KeyRow, R.HdrFg, R.HdrBg);
        R.Put(0, KeyRow, R.Fit(left, leftW), R.HdrFg, R.HdrBg);

        int tx = tailX;
        R.Put(tx, KeyRow, TF1,  R.HdrFg, R.HdrBg); tx += TF1.Length;
        R.Put(tx, KeyRow, TF5,  R.HdrFg, R.HdrBg); tx += TF5.Length;
        if (f8State != 0)
        {
            R.Put(tx, KeyRow, f8Part,
                f8State > 0 ? R.HdrFg : R.DeadFg, R.HdrBg);
            tx += f8Part.Length;
        }
        R.Put(tx, KeyRow, TTab, R.HdrFg, R.HdrBg); tx += TTab.Length;
        R.Put(tx, KeyRow, TF10, R.HdrFg, R.HdrBg);
        R.Put(verX, KeyRow, ver, R.HdrFg, R.HdrBg);
    }
}
