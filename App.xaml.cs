using System.Diagnostics;
using System.Windows;
using Jarvis.Actions;
using Jarvis.AI;
using Jarvis.Core;
using Jarvis.Data;
using Jarvis.UI;
using Jarvis.Voice;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private ServiceProvider? _sp;
    private TrayManager? _tray;
    private MainWindow? _main;
    private string[] _args = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;
        _mutex = new Mutex(true, @"Local\JARVIS_SingleInstance", out var created);
        if (!created) { Shutdown(); return; }

        DispatcherUnhandledException += (_, a) => { Log.Error(a.Exception, "ui"); a.Handled = true; };   // ошибка не закрывает программу
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error((Exception)a.ExceptionObject, "domain");
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error(a.Exception, "task"); a.SetObserved(); };

        var services = new ServiceCollection();
        services.AddSingleton(AppSettings.Load());
        services.AddSingleton(SynonymStore.Instance);
        services.AddSingleton<SecretStore>();
        services.AddSingleton<Database>();
        services.AddSingleton<HistoryRepository>();
        services.AddSingleton<ApplicationManager>();
        services.AddSingleton<BrowserManager>();
        services.AddSingleton<FileManager>();
        services.AddSingleton<SystemManager>();
        services.AddSingleton<ScreenshotManager>();
        services.AddSingleton<WindowManager>();
        services.AddSingleton<CommandParser>();
        services.AddSingleton<SecurityManager>();
        services.AddSingleton<LlmClient>();
        services.AddSingleton<WakeWordDetector>();
        services.AddSingleton<HotkeyListener>();
        services.AddSingleton<JarvisSpeechRecognizer>();
        services.AddSingleton<ITextToSpeech, JarvisSpeechSynthesizer>();   // замена голоса: поменяйте реализацию здесь
        services.AddSingleton<AssistantEngine>();
        services.AddSingleton<TrayManager>();
        services.AddSingleton<MainWindow>();
        _sp = services.BuildServiceProvider();

        var settings = _sp.GetRequiredService<AppSettings>();
        var engine = _sp.GetRequiredService<AssistantEngine>();
        _main = _sp.GetRequiredService<MainWindow>();
        MainWindow = _main;

        _tray = _sp.GetRequiredService<TrayManager>();
        _tray.Init(_main.ShowFromTray, engine.StartVoice, engine.StopVoice, _main.OpenSettings, RestartApp, ExitApp);

        if (!(settings.StartInTray || _args.Contains("--tray"))) _main.Show();

        var hotkey = _sp.GetRequiredService<HotkeyListener>();
        hotkey.KeyDown += engine.PttDown;
        hotkey.KeyUp += engine.PttUp;
        hotkey.Start();

        engine.StartVoice();
    }

    private void RestartApp()
    {
        ReleaseMutex();
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', _args)) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, "restart"); }
        ExitApp();
    }

    private void ReleaseMutex()
    {
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    private void ExitApp()
    {
        if (_main != null) _main.AllowClose = true;
        _tray?.Dispose();
        _sp?.Dispose();
        ReleaseMutex();
        Shutdown();
    }
}
