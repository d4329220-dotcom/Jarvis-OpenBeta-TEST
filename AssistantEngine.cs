using Jarvis.Actions;
using Jarvis.AI;
using Jarvis.Data;
using Jarvis.Voice;

namespace Jarvis.Core;

/// <summary>Оркестратор: голос → намерение → проверка безопасности → (подтверждение) → разрешённое действие → ответ.</summary>
public sealed class AssistantEngine : IDisposable
{
    private readonly AppSettings _s;
    private readonly CommandParser _parser;
    private readonly SecurityManager _security;
    private readonly ApplicationManager _apps;
    private readonly BrowserManager _web;
    private readonly FileManager _files;
    private readonly SystemManager _sys;
    private readonly ScreenshotManager _shot;
    private readonly WindowManager _win;
    private readonly LlmClient _llm;
    private readonly HistoryRepository _history;
    private readonly JarvisSpeechRecognizer _stt;
    private readonly ITextToSpeech _tts;
    private readonly WakeWordDetector _wake;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Intent? _pending;
    private string _pendingText = "";
    private DateTime _pendingUntil;
    private bool _awaiting;
    private bool _held;
    private DateTime _awaitUntil;
    private string? _lastError;

    public AssistantState State { get; private set; } = AssistantState.Idle;
    public bool VoiceEnabled { get; private set; }

    public event Action<AssistantState>? StateChanged;
    public event Action<string>? CommandHeard;
    public event Action<string>? Responded;
    public event Action? HistoryChanged;
    public event Action<bool>? VoiceChanged;

    public AssistantEngine(AppSettings settings, CommandParser parser, SecurityManager security, ApplicationManager apps,
        BrowserManager web, FileManager files, SystemManager sys, ScreenshotManager shot, WindowManager win, LlmClient llm,
        HistoryRepository history, JarvisSpeechRecognizer stt, ITextToSpeech tts, WakeWordDetector wake)
    {
        _s = settings; _parser = parser; _security = security; _apps = apps; _web = web; _files = files; _sys = sys;
        _shot = shot; _win = win; _llm = llm; _history = history; _stt = stt; _tts = tts; _wake = wake;
        _stt.Recognized += t => _ = OnRecognizedAsync(t);
        _stt.Failed += m => _ = FailAsync(m);
        _sys.Notify += t => _ = Task.Run(async () => { await _gate.WaitAsync(); try { await SayAsync(t); } finally { _gate.Release(); } });
    }

    private void SetState(AssistantState s) { State = s; StateChanged?.Invoke(s); }

    // ---------- управление микрофоном ----------
    public void StartVoice()
    {
        if (VoiceEnabled) return;
        if (!_stt.Start(_s.MicDevice)) { SetState(AssistantState.Error); return; }
        VoiceEnabled = true;
        _lastError = null;
        SetState(AssistantState.Idle);
        VoiceChanged?.Invoke(true);
    }

    public void StopVoice()
    {
        if (!VoiceEnabled) return;
        VoiceEnabled = false;
        _awaiting = false;
        _stt.Stop();
        SetState(AssistantState.Idle);
        VoiceChanged?.Invoke(false);
    }

    public void ApplySettings()
    {
        _wake.Configure(_s);
        if (VoiceEnabled) { StopVoice(); StartVoice(); }
    }

    /// <summary>Кнопка «Говорить»: следующая фраза — команда без ключевого слова.</summary>
    public void ListenOnce()
    {
        if (!VoiceEnabled) StartVoice();
        if (!VoiceEnabled) return;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { await SayAsync("Слушаю."); Arm(); } finally { _gate.Release(); }
        });
    }

    /// <summary>Клавиша «зажми и говори» нажата: фразы выполняются как команды без ключевого слова.</summary>
    public void PttDown()
    {
        if (_held) return;
        _held = true;
        _awaiting = true;
        _awaitUntil = DateTime.UtcNow.AddMinutes(10);
        SetState(AssistantState.Listening);
        if (!VoiceEnabled) _ = Task.Run(StartVoice);
    }

    /// <summary>Клавишу отпустили: ещё пару секунд дослушиваем фразу.</summary>
    public void PttUp()
    {
        _held = false;
        var until = DateTime.UtcNow.AddSeconds(2.5);
        _awaitUntil = until;
        _ = Task.Delay(TimeSpan.FromSeconds(2.5)).ContinueWith(_ =>
        {
            if (!_held && _awaiting && _awaitUntil == until)
            {
                _awaiting = false;
                if (State == AssistantState.Listening) SetState(AssistantState.Idle);
            }
        });
    }

    private void Arm()
    {
        _awaiting = true;
        var until = DateTime.UtcNow.AddSeconds(_s.CommandTimeoutSeconds);
        _awaitUntil = until;
        SetState(AssistantState.Listening);
        _ = Task.Delay(TimeSpan.FromSeconds(_s.CommandTimeoutSeconds)).ContinueWith(_ =>
        {
            if (_awaiting && _awaitUntil == until && !_held)
            {
                _awaiting = false;
                if (State == AssistantState.Listening) SetState(AssistantState.Idle);
            }
        });
    }

    // ---------- обработка речи ----------
    private async Task OnRecognizedAsync(string text)
    {
        await _gate.WaitAsync();
        try
        {
            if (_pending != null)
            {
                if (DateTime.UtcNow <= _pendingUntil) { await ConfirmAsync(text); return; }
                _pending = null;
            }
            if (_awaiting)
            {
                if (!_held) _awaiting = false;
                if (DateTime.UtcNow <= _awaitUntil) { await ProcessAsync(text); return; }
            }
            if (!_s.ContinuousListening) return;
            if (!_wake.TryExtract(text, out var rest)) return;      // посторонняя речь игнорируется и не логируется
            if (string.IsNullOrWhiteSpace(rest)) { await SayAsync("Слушаю."); Arm(); }
            else await ProcessAsync(rest);
        }
        catch (Exception ex) { Log.Error(ex, "recognized"); await FailAsync("Не удалось выполнить команду."); }
        finally { _gate.Release(); }
    }

    /// <summary>Команда, введённая текстом в окне.</summary>
    public async Task HandleTextAsync(string text)
    {
        await _gate.WaitAsync();
        try
        {
            if (_pending != null) await ConfirmAsync(text); else await ProcessAsync(text);
        }
        catch (Exception ex) { Log.Error(ex, "text"); await FailAsync("Не удалось выполнить команду."); }
        finally { _gate.Release(); }
    }

    private async Task ProcessAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        CommandHeard?.Invoke(text);
        SetState(AssistantState.Processing);

        var intent = _parser.Parse(text);
        if (intent.Action == "unknown" && _llm.IsConfigured)
        {
            try { intent = await _llm.ParseAsync(text, _apps.Entries.Select(a => a.Key), _web.Sites.Select(s => s.Key)) ?? intent; }
            catch (LlmException ex) { await Reply(text, ex.Message, "llm_error", true); return; }
        }
        var error = _security.Validate(intent);
        Log.Info($"command=\"{text}\" intent={intent} source={intent.Source} valid={error == null}");
        if (error != null) { await Reply(text, error, intent.Action, true); return; }

        if (_security.RequiresConfirmation(intent))
        {
            _pending = intent; _pendingText = text; _pendingUntil = DateTime.UtcNow.AddSeconds(Math.Max(10, _s.CommandTimeoutSeconds));
            await SayAsync(SecurityManager.ConfirmationPrompt(intent));
            return;
        }
        await ExecuteAsync(intent, text);
    }

    private async Task ConfirmAsync(string text)
    {
        CommandHeard?.Invoke(text);
        var decision = SecurityManager.ParseConfirmation(text);
        if (decision == null) { await SayAsync("Команда требует подтверждения."); return; }
        var intent = _pending!; var original = _pendingText;
        _pending = null;
        if (decision == true) await ExecuteAsync(intent, original);
        else await Reply(original, "Хорошо, отменяю.", intent.Action, false);
    }

    private async Task ExecuteAsync(Intent intent, string text)
    {
        ActionResult result;
        try { result = await Task.Run(() => Dispatch(intent)); }
        catch (Exception ex)
        {
            Log.Error(ex, "action " + intent.Action);
            result = new ActionResult(false, "Не удалось выполнить команду.");
        }
        Log.Info($"action={intent.Action} ok={result.Ok}");
        await Reply(text, string.IsNullOrEmpty(result.Message) ? "Готово." : result.Message, intent.Action, !result.Ok);
    }

    private ActionResult Dispatch(Intent i) => i.Action switch
    {
        "open_application" => _apps.Open(i.Get("application")),
        "close_application" => _win.CloseApplication(i.Get("application")),
        "minimize_window" => _win.Minimize(i.Get("application")),
        "maximize_window" => _win.Maximize(i.Get("application")),
        "focus_window" => _win.Focus(i.Get("application")),
        "close_active_window" => _win.CloseActive(),
        "close_all_windows" => _win.CloseAllWindows(),
        "close_all_apps" => _win.CloseAllApps(),
        "show_desktop" => _win.ShowDesktop(),
        "get_active_window" => _win.ActiveInfo(),
        "list_windows" => _win.ListWindows(),
        "next_window" => _win.NextWindow(),
        "open_website" => _web.Open(i.Get("site")),
        "open_folder" => _files.OpenFolder(i.Get("folder")),
        "find_file" => _files.FindAndOpen(i.Get("query"), i.Get("kind", "any")),
        "open_latest_download" => _files.OpenLatestDownload(i.Get("kind", "any")),
        "take_screenshot" => _shot.Capture(),
        "set_volume" => _sys.SetVolume(int.Parse(i.Get("level"))),
        "mute" => _sys.SetMute(true),
        "unmute" => _sys.SetMute(false),
        "lock_computer" => _sys.Lock(),
        "get_system_information" => _sys.GetInfo(i.Get("kind"), i.Get("drive")),
        "set_timer" => _sys.SetTimer(int.Parse(i.Get("seconds"))),
        "set_alarm" => _sys.SetAlarm(i.Get("time")),
        "open_settings" => _sys.OpenSettings(i.Get("page", "main")),
        "shutdown_computer" => _sys.Shutdown(),
        "restart_computer" => _sys.Restart(),
        _ => new ActionResult(false, "Я не понял команду.")
    };

    private async Task Reply(string command, string response, string action, bool error)
    {
        try { _history.Add(command, response, action); HistoryChanged?.Invoke(); }
        catch (Exception ex) { Log.Error(ex, "history"); }
        await SayAsync(response, error);
    }

    private async Task SayAsync(string text, bool error = false)
    {
        Responded?.Invoke(text);
        SetState(error ? AssistantState.Error : AssistantState.Speaking);
        _stt.Paused = true;                       // не слушаем собственный голос
        try { await _tts.SpeakAsync(text); }
        catch (Exception ex) { Log.Error(ex, "tts"); }
        finally { await Task.Delay(500); _stt.Paused = false; }
        SetState(_awaiting ? AssistantState.Listening : AssistantState.Idle);
    }

    private async Task FailAsync(string message)
    {
        Log.Info("error: " + message);
        if (message == _lastError) return;        // одну и ту же ошибку вслух не повторяем
        _lastError = message;
        if (message.Contains("микрофон")) { VoiceEnabled = false; VoiceChanged?.Invoke(false); }
        await SayAsync(message, true);
    }

    public void Dispose() { _stt.Dispose(); _sys.Dispose(); }
}
