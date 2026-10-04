using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Jarvis.Core;

namespace Jarvis.Actions;

public sealed record WinInfo(IntPtr Handle, string Title, string ClassName, string ProcessName, uint Pid);

/// <summary>Управление окнами Windows: поиск, переключение, сворачивание, закрытие. Системные процессы не трогаются.</summary>
public sealed class WindowManager
{
    private delegate bool EnumProc(IntPtr h, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    private const uint GW_OWNER = 4, GA_ROOTOWNER = 3, WM_CLOSE = 0x10;
    private const int GWL_EXSTYLE = -20, SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    private static readonly HashSet<string> IgnoredClasses = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private readonly ApplicationManager _apps;

    public WindowManager(ApplicationManager apps) => _apps = apps;

    // ---------- перечисление окон ----------
    private static string Text(IntPtr h)
    {
        var len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ProcName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return ""; }
    }

    private static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>У UWP-окон (Параметры, Калькулятор) настоящий процесс спрятан за ApplicationFrameHost — ищем его в дочернем окне.</summary>
    private static uint RealPid(IntPtr frame, uint framePid)
    {
        var found = framePid;
        EnumChildWindows(frame, (c, _) =>
        {
            if (ClassOf(c) == "Windows.UI.Core.CoreWindow")
            {
                GetWindowThreadProcessId(c, out var p);
                if (p != framePid) { found = p; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Видимые окна верхнего уровня в порядке Z (сверху вниз). Окно самого JARVIS и рабочий стол исключены.</summary>
    public List<WinInfo> GetWindows()
    {
        var result = new List<WinInfo>();
        var self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || IsCloaked(h)) return true;
                var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
                var title = Text(h);
                if (title.Length == 0) return true;
                var cls = ClassOf(h);
                if (IgnoredClasses.Contains(cls)) return true;
                GetWindowThreadProcessId(h, out var pid);
                if (pid == self) return true;
                var real = cls == "ApplicationFrameWindow" ? RealPid(h, pid) : pid;
                result.Add(new WinInfo(h, title, cls, ProcName(real), real));
            }
            catch { /* отдельное окно не должно ломать перебор */ }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsProtected(WinInfo w) =>
        w.ClassName != "CabinetWClass" && (w.ProcessName.Length == 0 || ApplicationManager.ProtectedProcesses.Contains(w.ProcessName));

    private static string Short(string title) => title.Length > 50 ? title[..50] : title;

    private string Friendly(WinInfo w) =>
        _apps.Entries.FirstOrDefault(e => string.Equals(e.Process, w.ProcessName, StringComparison.OrdinalIgnoreCase))?.Display
        ?? (w.ProcessName.Length > 0 ? w.ProcessName : Short(w.Title));

    private List<WinInfo> Match(AppEntry e, List<WinInfo> all)
    {
        var res = string.IsNullOrEmpty(e.Process)
            ? new List<WinInfo>()
            : all.Where(w => string.Equals(w.ProcessName, e.Process, StringComparison.OrdinalIgnoreCase)).ToList();
        if (res.Count > 0 || !string.IsNullOrEmpty(e.Process)) return res;
        var keys = _apps.AliasesOf(e).Select(TextUtil.Normalize).Where(k => k.Length >= 4).ToList();
        return all.Where(w => { var t = TextUtil.Normalize(w.Title); return keys.Any(k => t.Contains(k)); }).ToList();
    }

    private WinInfo? ActiveWindow()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var root = GetAncestor(fg, GA_ROOTOWNER);
        return GetWindows().FirstOrDefault(w => w.Handle == fg || w.Handle == root);
    }

    /// <summary>Окно по имени приложения или «@active» (текущее активное).</summary>
    private WinInfo? Find(string name, out string display, out string? error)
    {
        error = null; display = "";
        if (name == "@active")
        {
            var a = ActiveWindow();
            if (a == null) { error = "Активное окно — JARVIS или системное, с ним ничего не сделать."; return null; }
            display = Friendly(a);
            return a;
        }
        var e = _apps.Resolve(name);
        if (e == null) { error = "Приложение не найдено."; return null; }
        display = e.Display;
        var w = Match(e, GetWindows()).FirstOrDefault();
        if (w == null) error = $"{e.Display} не запущено.";
        return w;
    }

    // ---------- активация ----------
    private static bool TryForeground(IntPtr h)
    {
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var cur = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != cur && AttachThreadInput(cur, fgThread, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(cur, fgThread, false);
        return GetForegroundWindow() == h;
    }

    private static void Activate(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (TryForeground(h)) return;
        keybd_event(0x12, 0, 0, UIntPtr.Zero);      // короткое нажатие Alt снимает блокировку смены активного окна
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        TryForeground(h);
    }

    // ---------- команды ----------
    public ActionResult Focus(string name)
    {
        var w = Find(name, out var display, out var error);
        if (w == null) return new(false, error ?? "Не удалось выполнить команду.");
        Activate(w.Handle);
        return new(true, $"Переключаюсь на {display}.");
    }

    public ActionResult Minimize(string name)
    {
        var w = Find(name, out var display, out var error);
        if (w == null) return new(false, error ?? "Не удалось выполнить команду.");
        ShowWindow(w.Handle, SW_MINIMIZE);
        return new(true, $"Сворачиваю {display}.");
    }

    public ActionResult Maximize(string name)
    {
        var w = Find(name, out var display, out var error);
        if (w == null) return new(false, error ?? "Не удалось выполнить команду.");
        ShowWindow(w.Handle, IsIconic(w.Handle) ? SW_RESTORE : SW_MAXIMIZE);
        Activate(w.Handle);
        return new(true, $"Разворачиваю {display}.");
    }

    /// <summary>«Закрой X»: системные и UWP-окна — через WM_CLOSE, обычные программы — через процесс (окно → принудительно).</summary>
    public ActionResult CloseApplication(string name)
    {
        var e = _apps.Resolve(name);
        if (e == null) return new(false, "Приложение не найдено.");
        var wins = Match(e, GetWindows());
        var uwp = wins.Any(w => w.ClassName == "ApplicationFrameWindow");
        var system = e.Process != null && ApplicationManager.ProtectedProcesses.Contains(e.Process);
        if (uwp || system)
        {
            if (wins.Count == 0) return new(false, $"{e.Display} не запущено.");
            foreach (var w in wins) PostMessage(w.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return new(true, $"Закрываю {e.Display}.");
        }
        return _apps.Close(name);
    }

    public ActionResult CloseActive()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return new(false, "Не удалось определить активное окно.");
        GetWindowThreadProcessId(fg, out var pid);
        var cls = ClassOf(fg);
        if (pid == (uint)Environment.ProcessId) return new(false, "Активное окно — JARVIS, его я закрывать не буду.");
        if (IgnoredClasses.Contains(cls)) return new(false, "Сейчас активен рабочий стол, закрывать нечего.");
        var proc = ProcName(pid);
        if (ApplicationManager.ProtectedProcesses.Contains(proc) && cls != "CabinetWClass" && cls != "ApplicationFrameWindow")
            return new(false, "Это системное окно, закрывать его небезопасно.");
        var title = Text(fg);
        PostMessage(fg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return new(true, title.Length > 0 ? $"Закрываю окно {Short(title)}." : "Закрываю окно.");
    }

    /// <summary>Просит закрыться все окна (программы сами спросят про сохранение). Системное не трогается.</summary>
    public ActionResult CloseAllWindows()
    {
        var wins = GetWindows().Where(w => !IsProtected(w)).ToList();
        if (wins.Count == 0) return new(false, "Открытых окон нет.");
        foreach (var w in wins) PostMessage(w.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return new(true, $"Закрываю окна: {wins.Count}.");
    }

    /// <summary>Закрывает окна, затем завершает программы, у которых окон не осталось (свернулись в трей).
    /// Программы с открытым окном (например, спросили про сохранение) не завершаются принудительно.</summary>
    public ActionResult CloseAllApps()
    {
        var wins = GetWindows().Where(w => !IsProtected(w)).ToList();
        if (wins.Count == 0) return new(false, "Открытых приложений нет.");
        var names = wins.Select(w => w.ProcessName)
            .Where(n => n.Length > 0 && !ApplicationManager.ProtectedProcesses.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var w in wins) PostMessage(w.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(3500);

        var stillOpen = GetWindows().Where(w => !IsProtected(w)).Select(w => w.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (stillOpen.Contains(n)) continue;
            foreach (var p in Process.GetProcessesByName(n))
            {
                try { p.Kill(); }
                catch (Exception ex) { Log.Error(ex, "kill " + n); }
                finally { p.Dispose(); }
            }
        }
        return stillOpen.Count == 0
            ? new(true, "Все приложения закрыты.")
            : new(true, $"Приложения закрыты, кроме {stillOpen.Count}: возможно, они ждут сохранения.");
    }

    public ActionResult ShowDesktop()
    {
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t != null)
            {
                dynamic shell = Activator.CreateInstance(t)!;
                shell.MinimizeAll();
                return new(true, "Показываю рабочий стол.");
            }
        }
        catch (Exception ex) { Log.Error(ex, "MinimizeAll"); }
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);       // запасной вариант: Win+D
        keybd_event(0x44, 0, 0, UIntPtr.Zero);
        keybd_event(0x44, 0, 2, UIntPtr.Zero);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);
        return new(true, "Показываю рабочий стол.");
    }

    public ActionResult ActiveInfo()
    {
        var w = ActiveWindow();
        if (w == null) return new(false, "Активное окно — JARVIS или системное.");
        return new(true, $"Активное окно: {Short(w.Title)}, программа {Friendly(w)}.");
    }

    public ActionResult ListWindows()
    {
        var wins = GetWindows();
        if (wins.Count == 0) return new(false, "Открытых окон нет.");
        var names = wins.Select(Friendly).Distinct().Take(6).ToList();
        return new(true, $"Открыто окон: {wins.Count}. {string.Join(", ", names)}.");
    }

    /// <summary>Переключение на предыдущее окно в порядке Z (как Alt+Tab).</summary>
    public ActionResult NextWindow()
    {
        var fg = GetForegroundWindow();
        var root = fg == IntPtr.Zero ? IntPtr.Zero : GetAncestor(fg, GA_ROOTOWNER);
        var next = GetWindows().FirstOrDefault(w => w.Handle != fg && w.Handle != root);
        if (next == null) return new(false, "Других окон нет.");
        Activate(next.Handle);
        return new(true, $"Переключаюсь на {Friendly(next)}.");
    }
}
