using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jarvis.Core;

/// <summary>Синонимы названий программ: встроенные + пользовательские (файл synonyms.json, правятся в настройках).
/// Ключ — ключ/название/имя процесса приложения (chrome, telegram, steam ...), значения — как его можно назвать голосом.</summary>
public sealed class SynonymStore
{
    public static readonly SynonymStore Instance = new();

    public static readonly Dictionary<string, string[]> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer"] = new[] { "проводник", "файлы", "файловый менеджер", "мои файлы", "explorer" },
        ["settings"] = new[] { "настройки", "параметры", "настройки windows", "параметры windows", "settings" },
        ["chrome"] = new[] { "хром", "гугл хром", "браузер", "chrome", "google chrome" },
        ["telegram"] = new[] { "телеграм", "телеграмм", "телега", "тг", "telegram" },
        ["discord"] = new[] { "дискорд", "дс", "discord" },
        ["steam"] = new[] { "стим", "steam" },
        ["notepad"] = new[] { "блокнот", "notepad" },
        ["calculator"] = new[] { "калькулятор", "calculator" },
    };

    private static string FilePath => Path.Combine(AppPaths.DataDir, "synonyms.json");
    private readonly object _lock = new();
    private Dictionary<string, List<string>> _user = new(StringComparer.OrdinalIgnoreCase);

    public SynonymStore() => Load();

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath));
            if (data == null) return;
            lock (_lock) _user = new Dictionary<string, List<string>>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Error(ex, "synonyms.json"); }
    }

    /// <summary>Встроенные и пользовательские синонимы вместе.</summary>
    public List<KeyValuePair<string, string[]>> All()
    {
        lock (_lock)
        {
            var keys = Defaults.Keys.Concat(_user.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
            var result = new List<KeyValuePair<string, string[]>>();
            foreach (var k in keys)
            {
                var list = new List<string>();
                if (Defaults.TryGetValue(k, out var d)) list.AddRange(d);
                if (_user.TryGetValue(k, out var u)) list.AddRange(u);
                result.Add(new KeyValuePair<string, string[]>(k, list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            }
            return result;
        }
    }

    /// <summary>Текст для окна настроек: по строке на приложение, «ключ: синоним1, синоним2».</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var kv in All()) sb.AppendLine($"{kv.Key}: {string.Join(", ", kv.Value)}");
        return sb.ToString();
    }

    public void SetFromText(string text)
    {
        var parsed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim().ToLowerInvariant();
            var values = line[(idx + 1)..].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (key.Length == 0 || values.Count == 0) continue;
            if (parsed.TryGetValue(key, out var existing)) existing.AddRange(values); else parsed[key] = values;
        }
        lock (_lock) _user = parsed;
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(parsed, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
        catch (Exception ex) { Log.Error(ex, "save synonyms"); }
    }
}
