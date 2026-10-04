using System.Diagnostics;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.Actions;

public sealed record AppEntry(string Key, string Display, string Target, List<string> Aliases, string? Process, bool AllowMultiple = false);

/// <summary>Обнаружение установленных приложений (меню «Пуск» + applications.json + синонимы) и безопасный запуск/закрытие.</summary>
public sealed class ApplicationManager
{
    /// <summary>Системные и критически важные процессы: их JARVIS никогда не закрывает.</summary>
    public static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "winlogon", "csrss", "svchost", "lsass", "wininit", "services", "dwm", "smss", "system", "idle", "jarvis",
        "applicationframehost", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "textinputhost",
        "runtimebroker", "sihost", "taskhostw", "ctfmon", "fontdrvhost", "securityhealthsystray", "msmpeng", "lockapp"
    };

    // Русские названия известных программ: ключ — часть имени ярлыка.
    private static readonly (string Match, string Display, string[] Aliases, string? Process)[] Known =
    {
        ("chrome", "Chrome", new[] { "хром", "гугл хром", "google chrome", "браузер" }, "chrome"),
        ("firefox", "Firefox", new[] { "файрфокс", "фаерфокс", "мозилла" }, "firefox"),
        ("microsoft edge", "Edge", new[] { "эдж", "едж" }, "msedge"),
        ("telegram", "Telegram", new[] { "телеграм", "телеграмм", "телега" }, "Telegram"),
        ("discord", "Discord", new[] { "дискорд" }, "Discord"),
        ("steam", "Steam", new[] { "стим" }, "steam"),
        ("spotify", "Spotify", new[] { "спотифай", "музыка", "музыку", "музыкальное приложение" }, "Spotify"),
        ("viber", "Viber", new[] { "вайбер" }, "Viber"),
        ("whatsapp", "WhatsApp", new[] { "ватсап", "вотсап" }, "WhatsApp"),
        ("word", "Word", new[] { "ворд" }, "WINWORD"),
        ("excel", "Excel", new[] { "эксель", "иксель" }, "EXCEL"),
        ("obs studio", "OBS", new[] { "обс" }, "obs64"),
        ("vlc", "VLC", new[] { "влс", "плеер" }, "vlc"),
        ("roblox", "Roblox", new[] { "роблокс", "роблекс", "роблох", "roblox" }, "RobloxPlayerBeta"),
        ("epic games", "Epic Games", new[] { "эпик", "эпик геймс", "эпик игрс" }, "EpicGamesLauncher"),
        ("battle.net", "Battle.net", new[] { "батл нет", "близзард" }, "Battle.net"),
        ("opera", "Opera", new[] { "опера" }, "opera"),
    };

    private readonly Lazy<List<AppEntry>> _entries;
    private readonly SynonymStore _synonyms;

    public ApplicationManager(SynonymStore synonyms)
    {
        _synonyms = synonyms;
        _entries = new Lazy<List<AppEntry>>(Build, true);
        Task.Run(() => { _ = _entries.Value; });   // фоновое сканирование при старте
    }

    public IReadOnlyList<AppEntry> Entries => _entries.Value;
    private static string CustomPath => Path.Combine(AppPaths.DataDir, "applications.json");

    /// <summary>Все названия приложения: собственные + пользовательские и встроенные синонимы.</summary>
    public IEnumerable<string> AliasesOf(AppEntry e)
    {
        var list = new List<string> { e.Key, e.Display };
        list.AddRange(e.Aliases);
        foreach (var kv in _synonyms.All())
            if (EntryMatchesKey(e, kv.Key)) list.AddRange(kv.Value);
        return list;
    }

    private static bool EntryMatchesKey(AppEntry e, string key)
    {
        var k = TextUtil.Normalize(key);
        return TextUtil.Normalize(e.Key) == k
               || TextUtil.Normalize(e.Display) == k
               || (e.Process != null && TextUtil.Normalize(e.Process) == k);
    }

    public AppEntry? Resolve(string phrase) => AliasMatcher.Find(phrase, Entries, AliasesOf);

    private List<AppEntry> Build()
    {
        var list = new List<AppEntry>();
        try
        {
            foreach (var dir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
            })
            {
                var programs = Path.Combine(dir, "Programs");
                if (!Directory.Exists(programs)) continue;
                foreach (var lnk in Directory.EnumerateFiles(programs, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("удал", StringComparison.OrdinalIgnoreCase)) continue;
                    if (list.Any(e => e.Display.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                    var aliases = new List<string> { name };
                    string? proc = null;
                    foreach (var k in Known)
                        if (name.Contains(k.Match, StringComparison.OrdinalIgnoreCase)) { aliases.AddRange(k.Aliases); proc = k.Process; }
                    if (proc == null)
                    {
                        var target = ShortcutTarget(lnk);
                        var exe = target == null ? null : Path.GetFileNameWithoutExtension(target);
                        if (exe != null && !exe.Equals("update", StringComparison.OrdinalIgnoreCase)) proc = exe;
                    }
                    list.Add(new AppEntry(name.ToLowerInvariant(), name, lnk, aliases, proc));
                }
            }
        }
        catch (Exception ex) { Log.Error(ex, "scan start menu"); }

        void Builtin(string key, string display, string target, string[] aliases, string? proc, bool multi)
        {
            if (!list.Any(e => e.Key == key)) list.Add(new AppEntry(key, display, target, aliases.ToList(), proc, multi));
        }
        Builtin("notepad", "Блокнот", "notepad.exe", new[] { "блокнот", "notepad" }, "notepad", true);
        Builtin("calculator", "Калькулятор", "calc.exe", new[] { "калькулятор", "calculator" }, "CalculatorApp", true);
        Builtin("explorer", "Проводник", "explorer.exe", new[] { "проводник", "explorer" }, "explorer", true);
        Builtin("taskmgr", "Диспетчер задач", "taskmgr.exe", new[] { "диспетчер задач" }, "Taskmgr", false);
        Builtin("paint", "Paint", "mspaint.exe", new[] { "пейнт", "paint" }, "mspaint", true);
        Builtin("settings", "Параметры Windows", "ms-settings:", new[] { "параметры windows", "параметры", "настройки", "settings" }, "SystemSettings", true);
        Builtin("dota2", "Dota 2", "steam://rungameid/570", new[] { "дота", "дота 2", "дота два", "доту", "доту 2", "доту два", "dota", "dota 2" }, "dota2", false);
        Builtin("cs2", "Counter-Strike 2", "steam://rungameid/730", new[] { "кс", "кс 2", "кс два", "кс2", "кс го", "ксго", "cs", "cs2", "контр страйк" }, "cs2", false);

        if (!list.Any(e => e.Aliases.Contains("браузер")))        // «браузер» → любой найденный браузер
        {
            var b = list.FirstOrDefault(e => e.Process is "msedge" or "firefox");
            b?.Aliases.Add("браузер");
        }
        LoadCustom(list);
        return list;
    }

    private static void LoadCustom(List<AppEntry> list)
    {
        try
        {
            if (!File.Exists(CustomPath))
            {
                File.WriteAllText(CustomPath,
                    "{\n  \"_пример\": { \"path\": \"C:\\\\Program Files\\\\MyApp\\\\app.exe\", \"aliases\": [\"моя программа\"], \"process\": \"app\" }\n}\n");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(CustomPath));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.StartsWith('_')) continue;
                string? path; var aliases = new List<string> { p.Name }; string? proc = null; var display = p.Name;
                if (p.Value.ValueKind == JsonValueKind.String) path = p.Value.GetString();
                else
                {
                    path = p.Value.TryGetProperty("path", out var pp) ? pp.GetString() : null;
                    if (p.Value.TryGetProperty("aliases", out var al)) aliases.AddRange(al.EnumerateArray().Select(x => x.GetString() ?? ""));
                    if (p.Value.TryGetProperty("process", out var pr)) proc = pr.GetString();
                    if (p.Value.TryGetProperty("display", out var dn)) display = dn.GetString() ?? display;
                }
                if (string.IsNullOrWhiteSpace(path)) continue;
                path = Environment.ExpandEnvironmentVariables(path);
                proc ??= Path.GetFileNameWithoutExtension(path);
                list.RemoveAll(e => e.Key == p.Name.ToLowerInvariant());
                list.Insert(0, new AppEntry(p.Name.ToLowerInvariant(), display, path, aliases, proc));
            }
        }
        catch (Exception ex) { Log.Error(ex, "applications.json"); }
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }

    public ActionResult Open(string name)
    {
        var e = Resolve(name);
        if (e == null) return new(false, "Приложение не найдено.");
        try
        {
            if (!e.AllowMultiple && e.Process != null && Process.GetProcessesByName(e.Process).Length > 0)
                return new(true, $"{e.Display} уже запущен.");
            // Запускается только путь из каталога (не строка от пользователя или LLM), без cmd/PowerShell.
            Process.Start(new ProcessStartInfo(e.Target) { UseShellExecute = true });
            return new(true, $"Открываю {e.Display}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Log.Error(ex, "open " + e.Key);
            return new(false, "Приложение не найдено.");
        }
    }

    /// <summary>Закрытие по процессу (окно, потом принудительно). Системные процессы не закрываются.</summary>
    public ActionResult Close(string name)
    {
        var e = Resolve(name);
        if (e == null) return new(false, "Приложение не найдено.");
        if (e.Process == null) return new(false, $"Не удалось определить процесс {e.Display}.");
        if (ProtectedProcesses.Contains(e.Process)) return new(false, $"{e.Display} нельзя закрыть: это системный процесс.");
        var procs = Process.GetProcessesByName(e.Process);
        if (procs.Length == 0) return new(false, $"{e.Display} не запущен.");
        foreach (var p in procs)
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(2500)) p.Kill();
            }
            catch (Exception ex) { Log.Error(ex, "close " + e.Key); }
            finally { p.Dispose(); }
        }
        return new(true, $"Закрываю {e.Display}.");
    }
}
