using System.Text.RegularExpressions;
using Jarvis.Actions;

namespace Jarvis.Core;

/// <summary>Белый список. Намерение (от правил или LLM) выполняется только если действие разрешено и параметры валидны.</summary>
public sealed class SecurityManager
{
    public static readonly HashSet<string> Allowed = new()
    {
        "open_application", "close_application", "open_website", "open_folder", "find_file", "open_latest_download",
        "take_screenshot", "set_volume", "mute", "unmute", "lock_computer", "get_system_information", "set_timer",
        "set_alarm", "open_settings", "shutdown_computer", "restart_computer",
        "minimize_window", "maximize_window", "focus_window", "close_active_window", "close_all_windows",
        "close_all_apps", "show_desktop", "get_active_window", "list_windows", "next_window"
    };
    // delete_file / bulk_modify_files в программе не реализованы вообще; при добавлении они обязаны подтверждаться.
    public static readonly HashSet<string> Dangerous = new()
    {
        "shutdown_computer", "restart_computer", "close_all_windows", "close_all_apps", "delete_file", "bulk_modify_files"
    };
    private static readonly HashSet<string> Kinds = new() { "any", "image", "video", "music", "document" };
    private static readonly HashSet<string> Info = new() { "time", "date", "cpu", "ram", "disk", "os" };
    private static readonly HashSet<string> Yes = new() { "да", "подтверждаю", "подтвердить", "конечно", "давай", "точно", "угу", "ага", "yes" };
    private static readonly HashSet<string> No = new() { "нет", "не", "отмена", "отменить", "стоп", "неа", "no" };

    private readonly ApplicationManager _apps;
    private readonly BrowserManager _web;
    private readonly FileManager _files;

    public SecurityManager(ApplicationManager apps, BrowserManager web, FileManager files)
    {
        _apps = apps; _web = web; _files = files;
    }

    /// <summary>Возвращает текст ошибки или null, если намерение допустимо. Параметры приводятся к каноническим ключам.</summary>
    public string? Validate(Intent i)
    {
        if (!Allowed.Contains(i.Action)) return "Я не понял команду.";
        var p = i.Params;
        switch (i.Action)
        {
            case "open_application":
            case "close_application":
                var a = _apps.Resolve(i.Get("application"));
                if (a == null) return "Приложение не найдено.";
                p["application"] = a.Key; break;
            case "minimize_window":
            case "maximize_window":
            case "focus_window":
                if (i.Get("application") == "@active") break;
                var wa = _apps.Resolve(i.Get("application"));
                if (wa == null) return "Приложение не найдено.";
                p["application"] = wa.Key; break;
            case "open_website":
                var s = _web.Resolve(i.Get("site"));
                if (s == null) return "Сайт не найден.";
                p["site"] = s.Key; break;
            case "open_folder":
                var f = _files.ResolveFolderKey(i.Get("folder"));
                if (f == null) return "Папка не найдена.";
                p["folder"] = f; break;
            case "find_file":
                if (i.Get("query").Trim().Length is 0 or > 100) return "Я не понял, какой файл искать.";
                if (!Kinds.Contains(i.Get("kind", "any"))) p["kind"] = "any";
                break;
            case "open_latest_download":
                if (!Kinds.Contains(i.Get("kind", "any"))) p["kind"] = "any";
                break;
            case "set_volume":
                if (!int.TryParse(i.Get("level"), out var lvl) || lvl is < 0 or > 100) return "Громкость должна быть от нуля до ста процентов.";
                p["level"] = lvl.ToString(); break;
            case "set_timer":
                if (!int.TryParse(i.Get("seconds"), out var sec) || sec is < 1 or > 86400) return "Не удалось понять время таймера.";
                break;
            case "set_alarm":
                var m = Regex.Match(i.Get("time"), @"^(\d{1,2}):(\d{2})$");
                if (!m.Success || int.Parse(m.Groups[1].Value) > 23 || int.Parse(m.Groups[2].Value) > 59) return "Не удалось понять время будильника.";
                break;
            case "open_settings":
                if (!SystemManager.SettingsPages.ContainsKey(i.Get("page", "main"))) p["page"] = "main";
                break;
            case "get_system_information":
                if (!Info.Contains(i.Get("kind"))) return "Я не понял команду.";
                var d = i.Get("drive").Trim().TrimEnd(':', '\\').ToUpperInvariant();
                if (d.Length > 0 && !Regex.IsMatch(d, "^[A-Z]$")) return "Я не понял, какой диск вам нужен.";
                p["drive"] = d; break;
        }
        return null;
    }

    public bool RequiresConfirmation(Intent i) => Dangerous.Contains(i.Action);

    public static string ConfirmationPrompt(Intent i) => i.Action switch
    {
        "shutdown_computer" => "Подтвердите выключение компьютера.",
        "restart_computer" => "Подтвердите перезагрузку компьютера.",
        "close_all_windows" => "Подтвердите закрытие всех окон.",
        "close_all_apps" => "Подтвердите закрытие всех приложений. Несохранённые данные могут быть потеряны.",
        _ => "Команда требует подтверждения."
    };

    public static bool? ParseConfirmation(string text)
    {
        var tokens = TextUtil.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(No.Contains)) return false;
        if (tokens.Any(Yes.Contains)) return true;
        return null;
    }
}
