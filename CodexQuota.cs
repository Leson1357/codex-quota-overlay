using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyTitle("Codex Quota Overlay")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Windows desktop overlay. No changes to the Codex client or login files.
public sealed class CodexQuota : Form
{
    private readonly Label five = new Label(), week = new Label();
    private readonly Button more = new Button(), refresh = new Button(), close = new Button();
    private readonly ToolTip tips = new ToolTip { ShowAlways = true, InitialDelay = 150, ReshowDelay = 100, AutoPopDelay = 32767 };
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private readonly Font overlayFont = new Font("Microsoft YaHei UI", 9.5f);
    private Process server;
    private AutomationElement foregroundRoot;
    private AutomationElement editor;
    private IntPtr target;
    private IntPtr lastForeground;
    private bool foregroundIsCodex;
    private bool questionCardVisible;
    private AutomationElement composer;
    private System.Windows.Rect composerRectangle;
    private NativeRectangle measuredWindow;
    private DateTime nextLocate, nextMeasure, nextQuestionCheck, nextRead, requestedAt, lastUpdate;
    private DateTime nextCountdown;
    private DateTimeOffset? fiveReset, weekReset;
    private int sequence = 1, waitingId;
    private bool ready;
    private string codexPath;
    private int measuredDpi;

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; }
    }

    [STAThread]
    public static void Main()
    {
        using (var singleton = new Mutex(false, "Local\\CodexQuotaOverlay"))
        {
            if (!singleton.WaitOne(0)) return;
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new CodexQuota());
            }
            catch (Exception e) { MessageBox.Show(e.Message, "Codex 额度条"); }
            finally { singleton.ReleaseMutex(); }
        }
    }

    public CodexQuota(string executable = null)
    {
        Text = "Codex 额度条";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        BackColor = Color.White;
        ForeColor = Color.Black;
        Font = overlayFont;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-10000, -10000, 600, 38);
        foreach (var label in new[] { five, week })
        {
            label.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(label);
        }
        five.Text = "5h 剩余：读取中…";
        week.Text = "周额度剩余：读取中…";
        refresh.Text = "↻";
        close.Text = "×";
        more.Text = "⋯";
        more.AccessibleName = "额度重置倒计时";
        foreach (var button in new[] { more, refresh, close })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.ForeColor = ForeColor;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 244, 246);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(232, 234, 237);
            button.TabStop = false;
            Controls.Add(button);
        }
        tips.SetToolTip(refresh, "立即刷新（自动刷新间隔 30 秒）");
        tips.SetToolTip(close, "退出额度条");
        UpdateCountdownTooltip();
        refresh.Click += delegate { nextRead = DateTime.MinValue; };
        close.Click += delegate { Close(); };
        foreach (var surface in new Control[] { this, five, week })
            surface.MouseEnter += delegate { UpdateHoverVisibility(Cursor.Position); };
        Resize += delegate
        {
            LayoutLabels();
            using (var path = RoundedOutline())
            {
                var previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
            Invalidate();
        };
        codexPath = executable ?? FindCodex();
        if (codexPath == null) throw new InvalidOperationException("未找到 Codex 命令行程序。请先打开 Codex 桌面版，再启动额度条。");
        timer.Interval = 50;
        timer.Tick += delegate { Tick(); };
        timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop();
            StopServer();
            timer.Dispose();
            tips.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) overlayFont.Dispose();
    }

    private void LayoutLabels()
    {
        int padding = Math.Max(5, Height / 7);
        int unit = Math.Max(26, Height - padding * 2);
        int column = Math.Max(1, (ClientSize.Width - unit * 3 - padding * 2) / 2);
        five.SetBounds(padding, 1, Math.Max(1, column - 4), Height - 2);
        week.SetBounds(padding + column, 1, Math.Max(1, column - 4), Height - 2);
        more.SetBounds(ClientSize.Width - padding - unit * 3, padding, unit, Height - padding * 2);
        refresh.SetBounds(ClientSize.Width - padding - unit * 2, padding, unit, Height - padding * 2);
        close.SetBounds(ClientSize.Width - padding - unit, padding, unit, Height - padding * 2);
    }

    private GraphicsPath RoundedOutline()
    {
        float diameter = Math.Min(16 * DeviceDpi / 96f, Math.Min(Width - 1, Height - 1));
        var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(Width - 1 - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(Width - 1 - diameter, Height - 1 - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, Height - 1 - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundedOutline())
        using (var pen = new Pen(Color.FromArgb(220, 223, 228)))
        {
            e.Graphics.DrawPath(pen, path);
            e.Graphics.DrawLine(pen, week.Left - 2, Height / 3, week.Left - 2, Height * 2 / 3);
        }
    }

    private static string FindCodex()
    {
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            using (process)
            {
                try { if (File.Exists(process.MainModule.FileName)) return process.MainModule.FileName; }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(bin))
        {
            var file = Directory.GetFiles(bin, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file != null) return file;
        }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var file = Path.Combine(folder.Trim('"'), "codex.exe");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    private static bool IsCodexWindow(IntPtr window)
    {
        uint id;
        GetWindowThreadProcessId(window, out id);
        try
        {
            using (var process = Process.GetProcessById((int)id))
                return process.MainModule.FileName.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void PositionOverlay()
    {
        var foreground = GetForegroundWindow();
        if (foreground != lastForeground)
        {
            lastForeground = foreground;
            foregroundIsCodex = IsCodexWindow(foreground);
            foregroundRoot = null;
            editor = null;
            nextMeasure = DateTime.MinValue;
            nextQuestionCheck = DateTime.MinValue;
        }
        if (!foregroundIsCodex || !IsWindowVisible(foreground) || IsIconic(foreground)) { Hide(); return; }
        if (foregroundRoot == null) foregroundRoot = AutomationElement.FromHandle(foreground);
        if (DateTime.UtcNow >= nextQuestionCheck)
        {
            questionCardVisible = HasVisibleQuestionCard(foregroundRoot);
            nextQuestionCheck = DateTime.UtcNow.AddMilliseconds(250);
        }
        if (questionCardVisible) { Hide(); return; }
        if (foreground != target || DateTime.UtcNow >= nextLocate)
        {
            editor = null;
            target = foreground;
            // Inspect only the foreground window, rather than enumerating every desktop window.
            var edits = foregroundRoot.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            foreach (AutomationElement element in edits)
            {
                var current = element.Current;
                if (!current.IsOffscreen && current.ClassName == "ProseMirror" && current.BoundingRectangle.Width >= 180)
                {
                    editor = element;
                    target = foreground;
                    break;
                }
            }
            nextLocate = DateTime.UtcNow.AddMilliseconds(editor == null ? 250 : 3000);
            composer = editor == null ? null : TreeWalker.RawViewWalker.GetParent(editor);
            nextMeasure = DateTime.MinValue;
        }
        if (editor == null || IsIconic(target)) { Hide(); return; }
        NativeRectangle currentWindow;
        if (!GetWindowRect(target, out currentWindow)) { Hide(); return; }
        int dpi = (int)GetDpiForWindow(target);
        if (dpi == 0) dpi = 96;
        if (DateTime.UtcNow >= nextMeasure || currentWindow.Right - currentWindow.Left != measuredWindow.Right - measuredWindow.Left ||
            currentWindow.Bottom - currentWindow.Top != measuredWindow.Bottom - measuredWindow.Top || dpi != measuredDpi)
        {
            if (editor.Current.IsOffscreen) { Hide(); return; }
            composerRectangle = editor.Current.BoundingRectangle;
            if (composer != null)
            {
                var outer = composer.Current.BoundingRectangle;
                if (!outer.IsEmpty && outer.Width >= composerRectangle.Width && outer.Width <= composerRectangle.Width + 100 * dpi / 96.0)
                    composerRectangle = outer;
            }
            measuredWindow = currentWindow;
            measuredDpi = dpi;
            nextMeasure = DateTime.UtcNow.AddMilliseconds(250);
        }
        // Native window coordinates track dragging at 50 ms; UI Automation measures layout at 250 ms.
        var rectangle = composerRectangle;
        rectangle.Offset(currentWindow.Left - measuredWindow.Left, currentWindow.Top - measuredWindow.Top);
        if (rectangle.IsEmpty || rectangle.Width < 180) { Hide(); return; }
        Bounds = OverlayBounds(rectangle, dpi);
        UpdateHoverVisibility(Cursor.Position);
    }

    private static Rectangle OverlayBounds(System.Windows.Rect composerBounds, int dpi)
    {
        int height = (int)Math.Round(38 * dpi / 96.0);
        int clearance = (int)Math.Round(48 * dpi / 96.0);
        return new Rectangle((int)Math.Round(composerBounds.Left),
            (int)Math.Round(composerBounds.Top) - height - clearance,
            (int)Math.Round(composerBounds.Width), height);
    }

    private void UpdateHoverVisibility(Point pointer)
    {
        if (questionCardVisible || (Bounds.Contains(pointer) && pointer.X - Left < more.Left))
            Hide();
        else if (!Visible)
            Show();
    }

    private static bool HasVisibleQuestionCard(AutomationElement root)
    {
        var cache = new CacheRequest();
        cache.AutomationElementMode = AutomationElementMode.None;
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        using (cache.Activate())
        {
            var containers = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane)));
            foreach (AutomationElement container in containers)
            {
                var current = container.Cached;
                if (!current.IsOffscreen && current.ClassName.Contains("@container/request-card"))
                    return true;
            }
        }
        return false;
    }

    private void StartServer()
    {
        server = new Process();
        server.StartInfo = new ProcessStartInfo(codexPath, "app-server --stdio")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        server.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
        {
            if (Object.ReferenceEquals(sender, server) && e.Data != null) messages.Enqueue(e.Data);
        };
        server.ErrorDataReceived += delegate { }; // Drain logs without saving credentials or diagnostics.
        server.Start();
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        ready = false;
        Send("initialize", new { clientInfo = new { name = "quota_overlay", version = "1.0.0" }, capabilities = new { experimentalApi = true } });
    }

    private void Send(string method, object parameters)
    {
        waitingId = sequence++;
        requestedAt = DateTime.UtcNow;
        server.StandardInput.WriteLine(json.Serialize(new { id = waitingId, method = method, @params = parameters }));
        server.StandardInput.Flush();
    }

    private static IDictionary<string, object> Map(object value) { return value as IDictionary<string, object>; }
    private static object Get(IDictionary<string, object> map, string key)
    {
        object value;
        return map != null && map.TryGetValue(key, out value) ? value : null;
    }

    private void DisplayLimits(IDictionary<string, object> response)
    {
        var limits = Map(Get(Map(Get(response, "rateLimitsByLimitId")), "codex")) ?? Map(Get(response, "rateLimits"));
        if (limits == null) throw new InvalidOperationException("额度数据暂不可用");
        var windows = new[] { Map(Get(limits, "primary")), Map(Get(limits, "secondary")) };
        DisplayWindow(five, "5h", windows.FirstOrDefault(w => Convert.ToInt32(Get(w, "windowDurationMins")) == 300));
        DisplayWindow(week, "周额度", windows.FirstOrDefault(w => Convert.ToInt32(Get(w, "windowDurationMins")) == 10080));
        lastUpdate = DateTime.Now;
        nextRead = requestedAt.AddSeconds(30);
        nextCountdown = DateTime.MinValue;
        UpdateCountdownTooltip();
    }

    private void DisplayWindow(Label label, string name, IDictionary<string, object> window)
    {
        DateTimeOffset? reset = Get(window, "resetsAt") == null ? (DateTimeOffset?)null :
            DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(Get(window, "resetsAt")));
        if (label == five) fiveReset = reset;
        else weekReset = reset;
        if (Get(window, "usedPercent") == null)
        {
            label.Text = name + "：暂无数据";
            label.ForeColor = ForeColor;
            tips.SetToolTip(label, "账户未返回该额度窗口");
            return;
        }
        double remaining = Math.Max(0, Math.Min(100, 100 - Convert.ToDouble(Get(window, "usedPercent"))));
        label.Text = name + " 剩余：" + remaining.ToString("0.#") + "%";
        label.ForeColor = ForeColor;
        string resetText = reset.HasValue ? reset.Value.ToLocalTime().ToString("MM-dd HH:mm") : "未知";
        tips.SetToolTip(label, "更新时间：" + DateTime.Now.ToString("HH:mm:ss") + "\n额度重置：" + resetText);
    }

    private void UpdateCountdownTooltip()
    {
        var now = DateTimeOffset.UtcNow;
        if (now.UtcDateTime < nextCountdown) return;
        nextCountdown = now.UtcDateTime.AddSeconds(1);
        tips.SetToolTip(more, "5h 重置倒计时：" + FormatCountdown(fiveReset, now) +
            "\n周额度重置倒计时：" + FormatCountdown(weekReset, now));
    }

    private static string FormatCountdown(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (!reset.HasValue) return "暂无数据";
        var remaining = reset.Value - now;
        if (remaining.TotalSeconds <= 0) return "等待额度更新";
        var duration = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        return (duration.Days > 0 ? duration.Days + "天 " : "") +
            duration.Hours.ToString("00") + "小时 " + duration.Minutes.ToString("00") + "分 " + duration.Seconds.ToString("00") + "秒";
    }

    private void StopServer()
    {
        var process = server;
        server = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.StandardInput.Close(); }
                    catch (IOException) { } // A reader may have exited while its pipe was being closed.
                    if (!process.WaitForExit(250)) process.Kill();
                }
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        ready = false;
        waitingId = 0;
        string discarded;
        while (messages.TryDequeue(out discarded)) { }
    }

    private void Tick()
    {
        UpdateCountdownTooltip();
        try { PositionOverlay(); }
        catch (ElementNotAvailableException) { foregroundRoot = editor = null; nextLocate = DateTime.MinValue; Hide(); }
        catch (System.Runtime.InteropServices.COMException) { foregroundRoot = editor = null; nextLocate = DateTime.MinValue; Hide(); }
        try
        {
            if (server != null && server.HasExited) throw new InvalidOperationException("额度连接已断开");
            string line;
            while (messages.TryDequeue(out line))
            {
                var message = Map(json.DeserializeObject(line));
                var id = Get(message, "id");
                int responseId;
                if (id == null || !Int32.TryParse(Convert.ToString(id), out responseId) || responseId != waitingId) continue;
                waitingId = 0;
                if (Get(message, "error") != null) throw new InvalidOperationException("额度读取失败，请确认 Codex 已登录 ChatGPT 账户");
                if (!ready)
                {
                    server.StandardInput.WriteLine("{\"method\":\"initialized\"}");
                    ready = true;
                    Send("account/rateLimits/read", null);
                }
                else
                {
                    DisplayLimits(Map(Get(message, "result")));
                    // The reader is needed only during a refresh, not for the entire session.
                    StopServer();
                }
            }
            if (waitingId != 0 && DateTime.UtcNow - requestedAt > TimeSpan.FromSeconds(20))
                throw new TimeoutException("额度读取超时，将在 30 秒后重试");
            if (waitingId == 0 && DateTime.UtcNow >= nextRead)
            {
                if (server == null) StartServer();
                else Send("account/rateLimits/read", null);
            }
        }
        catch (Exception e)
        {
            StopServer();
            nextRead = DateTime.UtcNow.AddSeconds(30);
            if (lastUpdate == DateTime.MinValue)
            {
                five.Text = "5h：读取失败";
                week.Text = "周额度：读取失败";
            }
            else
            {
                five.Text = five.Text.Replace("（未更新）", "") + "（未更新）";
                week.Text = week.Text.Replace("（未更新）", "") + "（未更新）";
            }
            five.ForeColor = week.ForeColor = ForeColor;
            tips.SetToolTip(five, e.Message);
            tips.SetToolTip(week, e.Message);
        }
    }
}
