using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.AI;

public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
}

/// <summary>Отправляет текст фразы в LLM и получает ТОЛЬКО намерение (JSON). Никакой код/команды не исполняются.</summary>
public sealed class LlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly AppSettings _settings;
    private readonly SecretStore _secrets;

    public LlmClient(AppSettings settings, SecretStore secrets) { _settings = settings; _secrets = secrets; }

    public bool IsConfigured => _settings.LlmEnabled && !string.IsNullOrEmpty(_secrets.GetApiKey());

    private const string Prompt = @"Ты превращаешь русскую голосовую команду в JSON-намерение. Отвечай ТОЛЬКО одним JSON-объектом.
Формат: {""action"": ""..."", ""application"": ""..."", ...} — плоский объект. Разрешённые action и параметры:
open_application{application}, close_application{application}, open_website{site}, open_folder{folder: downloads|documents|desktop|pictures|music|videos},
find_file{query, kind: any|image|video|music|document}, open_latest_download{kind}, take_screenshot, set_volume{level 0-100}, mute, unmute, lock_computer,
get_system_information{kind: time|date|cpu|ram|disk|os, drive}, set_timer{seconds}, set_alarm{time: HH:mm},
open_settings{page: main|bluetooth|wifi|network|sound|display|update|apps|printers|power|personalization|privacy|mic},
minimize_window{application}, maximize_window{application}, focus_window{application} (application может быть ""@active"" — текущее окно),
close_active_window, close_all_windows, close_all_apps, show_desktop, get_active_window, list_windows, next_window,
shutdown_computer, restart_computer.
Приложения пользователя: {APPS}. Сайты: {SITES}.
«Скучно, открой музыку» → open_application с музыкальным приложением. Если ни одно действие не подходит: {""action"":""unknown""}.
Никогда не возвращай команды shell, cmd, PowerShell или код.";

    public async Task<Intent?> ParseAsync(string text, IEnumerable<string> apps, IEnumerable<string> sites, CancellationToken ct = default)
    {
        var key = _secrets.GetApiKey();
        if (!_settings.LlmEnabled || string.IsNullOrEmpty(key)) return null;
        try
        {
            var prompt = Prompt.Replace("{APPS}", string.Join(", ", apps.Take(150))).Replace("{SITES}", string.Join(", ", sites));
            var body = JsonSerializer.Serialize(new
            {
                model = _settings.LlmModel,
                temperature = 0,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = prompt },
                    new { role = "user", content = text }
                }
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, _settings.LlmEndpoint.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Info($"LLM HTTP {(int)resp.StatusCode}");
                throw new LlmException("Не удалось подключиться к языковой модели.");
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return ToIntent(content.Trim().Trim('`').Replace("json\n", ""));
        }
        catch (LlmException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info("LLM unreachable: " + ex.GetType().Name);                       // ключ в лог не попадает
            throw new LlmException(NetworkInterface.GetIsNetworkAvailable()
                ? "Не удалось подключиться к языковой модели."
                : "Соединение с интернетом отсутствует.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Error(ex, "LLM parse");
            throw new LlmException("Не удалось подключиться к языковой модели.");
        }
    }

    private static Intent ToIntent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var action = root.GetProperty("action").GetString() ?? "unknown";
        var p = new Dictionary<string, string>();

        void Add(JsonElement obj)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.Name == "action") continue;
                if (prop.Name == "params" && prop.Value.ValueKind == JsonValueKind.Object) { Add(prop.Value); continue; }
                p[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                    _ => ""
                };
            }
        }
        Add(root);
        return new Intent(action, p, "llm");
    }
}
