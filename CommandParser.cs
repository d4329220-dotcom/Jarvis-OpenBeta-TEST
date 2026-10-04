using System.Text.RegularExpressions;
using Jarvis.Actions;

namespace Jarvis.Core;

/// <summary>Быстрый разбор фраз правилами (без интернета). Неизвестное уходит в LLM, если она настроена.</summary>
public sealed class CommandParser
{
    private static readonly HashSet<string> Wake = new() { "джарвис", "джарвиз", "жарвис", "джарви", "jarvis" };
    private static readonly HashSet<string> Fillers = new() { "пожалуйста", "программу", "приложение", "сайт", "мне", "окно", "на" };
    private static readonly HashSet<string> FindStop = new()
    {
        "файл", "файлы", "мне", "фотографию", "фото", "фотография", "картинку", "изображение", "документ", "видео",
        "песню", "музыку", "с", "названием", "под", "называется", "по", "имени", "название", "пожалуйста"
    };
    private static readonly Dictionary<string, string> Drives = new()
    {
        ["с"] = "C", ["си"] = "C", ["це"] = "C", ["цэ"] = "C", ["д"] = "D", ["ди"] = "D", ["е"] = "E", ["эф"] = "F"
    };
    private static readonly (string Pattern, string Page)[] Pages =
    {
        (@"bluetooth|блютуз|блютус", "bluetooth"), (@"wi ?fi|вай ?фай|вайфай", "wifi"), (@"сеть|сети|интернет", "network"),
        (@"звук", "sound"), (@"экран|дисплей", "display"), (@"обновлен", "update"), (@"приложен", "apps"),
        (@"принтер", "printers"), (@"питани|батаре", "power"), (@"персонализ|обои|тема", "personalization"),
        (@"микрофон", "mic"), (@"конфиденц", "privacy")
    };

    private readonly ApplicationManager _apps;
    private readonly BrowserManager _web;
    private readonly FileManager _files;
    private readonly AppSettings _settings;

    public CommandParser(ApplicationManager apps, BrowserManager web, FileManager files, AppSettings settings)
    {
        _apps = apps; _web = web; _files = files; _settings = settings;
    }

    private static bool R(string t, string pattern) => Regex.IsMatch(t, pattern);

    public Intent Parse(string text)
    {
        var words = TextUtil.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var own = TextUtil.Normalize(_settings.WakeWord);
        while (words.Count > 0 && (Wake.Contains(words[0]) || words[0] == own)) words.RemoveAt(0);
        return Rules(string.Join(' ', words));
    }

    private Intent Rules(string t)
    {
        if (t.Length == 0) return Intent.Unknown();
        if (R(t, @"(выключ|выруб|отключ)\w*\s+(компьютер|пк|ноутбук|систем)|заверш\w*\s+работ")) return new("shutdown_computer");
        if (R(t, @"перезагруз|рестарт")) return new("restart_computer");
        if (R(t, @"заблокир|блокиров")) return new("lock_computer");
        if (R(t, @"скриншот|скрин\b|снимок\s+экрана|сфотографир\w*\s+экран")) return new("take_screenshot");
        if (R(t, @"таймер"))
        {
            var sec = ParseSeconds(t);
            return sec > 0 ? new("set_timer", new() { ["seconds"] = sec.ToString() }) : Intent.Unknown();
        }
        if (R(t, @"будильник")) return ParseAlarm(t);
        if (R(t, @"(выключ\w*|отключ\w*|выруб\w*|убери|заглуши)\s+звук|без\s+звука")) return new("mute");
        if (R(t, @"(включ\w*|верни|верните)\s+звук")) return new("unmute");
        if (R(t, @"громкост|звук"))
        {
            int? level = R(t, @"максимум|полную") ? 100 : t.Contains("половин") ? 50 : t.Contains("минимум") ? 0
                : (TextUtil.ExtractNumbers(t) is { Count: > 0 } n ? (int?)n[0] : null);
            if (level != null) return new("set_volume", new() { ["level"] = level.Value.ToString() });
        }

        // ---------- управление окнами и приложениями ----------
        var win = WindowRules(t);
        if (win != null) return win;

        if (R(t, @"настройк|параметры"))
        {
            var page = Pages.FirstOrDefault(p => R(t, p.Pattern)).Page ?? "main";
            return new("open_settings", new() { ["page"] = page });
        }
        var info = SysInfo(t);
        if (info != null) return info;
        if (R(t, @"последн\w*\s+(загруж\w*|скачан\w*)"))
            return new("open_latest_download", new() { ["kind"] = Kind(t) });
        var m = Regex.Match(t, @"^(?:найди|найти|поищи|поиск)\s+(.+)$");
        if (m.Success && !R(t, @"в\s+интернете"))
        {
            var query = string.Join(' ', m.Groups[1].Value.Split(' ').Where(w => !FindStop.Contains(w)));
            return query.Length == 0 ? Intent.Unknown() : new("find_file", new() { ["query"] = query, ["kind"] = Kind(t) });
        }
        m = Regex.Match(t, @"папк\w*\s*(.*)$");
        if (m.Success) return new("open_folder", new() { ["folder"] = _files.ResolveFolderKey(m.Groups[1].Value) ?? m.Groups[1].Value });
        m = Regex.Match(t, @"^(?:откр\w*|запус\w*|включ\w*|зайд\w*|зайти|покаж\w*)\s+(.+)$");
        if (m.Success) return Open(Clean(m.Groups[1].Value));
        return Intent.Unknown();
    }

    private Intent? WindowRules(string t)
    {
        static Intent W(string action, string target) => new(action, new() { ["application"] = target });

        if (R(t, @"^(?:закр\w*|заверш\w*)\s+все\s+(?:приложени\w*|программ\w*)")) return new("close_all_apps");
        if (R(t, @"^(?:закр\w*|заверш\w*)\s+все\s+окн\w*")) return new("close_all_windows");
        if (R(t, @"^закр\w*\s+(?:(?:текущ\w*|актив\w*|это)\s+)?окн\w*$")) return new("close_active_window");
        if (R(t, @"^покаж\w*\s+(?:мне\s+)?рабочий\s+стол|^сверн\w*\s+все(?:\s+окна)?$|^свернуть\s+все")) return new("show_desktop");
        if (R(t, @"следующ\w*\s+окн\w*|^(?:переключи\w*|смени\w*)\s+(?:на\s+)?(?:другое\s+)?окн\w*|^другое\s+окно$")) return new("next_window");
        if (R(t, @"какие\s+(?:окна|приложения|программы)\s+(?:открыты|запущены)|список\s+окон|открытые\s+окна")) return new("list_windows");
        if (R(t, @"какое\s+(?:сейчас\s+)?(?:окно|приложение)|в\s+каком\s+я\s+окне|^актив\w*\s+окно$")) return new("get_active_window");

        var m = Regex.Match(t, @"^(?:сверн\w*|минимизир\w*)\s+(.+)$");
        if (m.Success) return W("minimize_window", Target(m.Groups[1].Value));
        m = Regex.Match(t, @"^(?:разверн\w*|максимизир\w*|восстанов\w*)\s+(.+)$");
        if (m.Success) return W("maximize_window", Target(m.Groups[1].Value));
        m = Regex.Match(t, @"^(?:переключ\w*|перейди|перейти|активируй\w*|сфокусируй\w*)\s+(?:на\s+)?(.+)$");
        if (m.Success) return W("focus_window", Target(m.Groups[1].Value));
        m = Regex.Match(t, @"^сдела\w*\s+(.+?)\s+(?:основн\w*|актив\w*|главн\w*)(?:\s+окн\w*)?$");
        if (m.Success) return W("focus_window", Target(m.Groups[1].Value));
        m = Regex.Match(t, @"^(?:закр\w*|заверш\w*|останов\w*|убей|выруби)\s+(.+)$");
        if (m.Success) return W("close_application", Clean(m.Groups[1].Value));
        return null;
    }

    /// <summary>«текущее окно» → активное окно; иначе — название приложения.</summary>
    private static string Target(string rest) =>
        R(rest, @"^(?:(?:текущ\w*|актив\w*|это)\s+)?окн\w*$|^(?:текущ\w*|актив\w*|это)\b") ? "@active" : Clean(rest);

    private Intent Open(string rest)
    {
        var app = _apps.Resolve(rest);
        if (app != null) return new("open_application", new() { ["application"] = app.Key });
        var site = _web.Resolve(rest);
        if (site != null) return new("open_website", new() { ["site"] = site.Key });
        var folder = _files.ResolveFolderKey(rest);
        if (folder != null) return new("open_folder", new() { ["folder"] = folder });
        return new("open_application", new() { ["application"] = rest });   // SecurityManager вернёт «Приложение не найдено.»
    }

    private static string Clean(string s) => string.Join(' ', s.Split(' ').Where(w => !Fillers.Contains(w)));

    private static string Kind(string t) =>
        R(t, @"фото|картинк|изображен") ? "image" : R(t, @"видео|фильм") ? "video"
        : R(t, @"песн|музык|трек") ? "music" : R(t, @"документ") ? "document" : "any";

    private static int ParseSeconds(string t)
    {
        if (t.Contains("полчаса")) return 1800;
        var tokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var total = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            var unit = tokens[i].StartsWith("секунд") ? 1 : tokens[i].StartsWith("минут") ? 60 : tokens[i].StartsWith("час") ? 3600 : 0;
            if (unit == 0) continue;
            var window = string.Join(' ', tokens.Skip(Math.Max(0, i - 3)).Take(Math.Min(3, i)));
            var nums = TextUtil.ExtractNumbers(window);
            total += (nums.Count > 0 ? nums[^1] : 1) * unit;
        }
        return total;
    }

    private static Intent ParseAlarm(string t)
    {
        var nums = TextUtil.ExtractNumbers(t[t.IndexOf("будильник", StringComparison.Ordinal)..]);
        if (nums.Count == 0) return Intent.Unknown();
        var h = nums[0]; var min = nums.Count > 1 ? nums[1] : 0;
        if (h < 12 && R(t, @"вечера|дня")) h += 12;
        return new("set_alarm", new() { ["time"] = $"{h:00}:{min:00}" });
    }

    private static Intent? SysInfo(string t)
    {
        static Intent I(string kind, string drive = "") => new("get_system_information", new() { ["kind"] = kind, ["drive"] = drive });
        if (R(t, @"верси\w*.*(windows|винд\w*)|(windows|винд\w*).*верси\w*|информаци\w*\s+о\s+систем")) return I("os");
        if (R(t, @"свободн\w*|диск"))
        {
            var m = Regex.Match(t, @"диск\w*\s+(?:([a-z])|(с|си|це|цэ|д|ди|е|эф))\b");
            var drive = m.Groups[1].Success ? m.Groups[1].Value.ToUpperInvariant() : m.Groups[2].Success ? Drives[m.Groups[2].Value] : "";
            return I("disk", drive);
        }
        if (R(t, @"оператив\w*|\bозу\b|\bram\b|памят\w*")) return I("ram");
        if (R(t, @"процессор\w*|\bcpu\b|\bцп\b")) return I("cpu");
        if (R(t, @"\bдата\b|какое\s+сегодня\s+число")) return I("date");
        if (R(t, @"\bчас\b|\bчасов\b|врем(?:я|ени)\b")) return I("time");
        return null;
    }
}
