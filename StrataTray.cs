// StrataTray v11: tray menu for the local Strata server.
// Start/Stop, Load now / Unload now, Open Browser, Settings, Exit. No console windows.
// v3: all state checks on a background thread; detection via TCP table + process names (instant).
// v5: menu always drawn from cached state.
// v6: Settings dialog for the idle unload. It writes Strata's own idle_unload_s into the run
//     config (Strata does the idle detection and the reload on the next request). A fourth
//     state, Sleeping (server up, model unloaded), shows as a blue dot.
// v7: Exit stops Strata (model and server) before the tray closes.
// v8: status badge readable at 16 px: bigger dot, thicker ring, sleeping is a moon shape.
// v9: menu labels say what they act on: WebUI (the server) vs Model.
// v10: Start WebUI uses Strata's --lazy (server up, model unloaded until Load Model or the first
//      request). The tray starts the WebUI on launch if it is not already up.
// v11: the server's own output (its [strata] diagnostics) goes to C:\Strata\server.log.
// Build: build.cmd (uses the C# compiler that ships with Windows).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("StrataTray")]
[assembly: AssemblyProduct("StrataTray")]
[assembly: AssemblyVersion("11.0.0.0")]
[assembly: AssemblyFileVersion("11.0.0.0")]

static class Program
{
    // ---- settings ----
    const string Root   = @"C:\Strata";
    const string Python = Root + @"\.venv\Scripts\python.exe";
    const string Server = Root + @"\serve\server.py";
    const string Config = Root + @"\strata-nvfp4-nvfp4-qad.json";
    const int    Port   = 8080;
    static readonly string Url = "http://127.0.0.1:" + Port + "/";
    static readonly string[] EngineNames = { "strata", "strata-vision" };
    static readonly string TraySettings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"StrataTray\settings.json");
    const int DefaultIdleMinutes = 30;
    // -------------------

    enum State { Stopped, Starting, Running, Sleeping, Stopping }

    static NotifyIcon tray;
    static ToolStripMenuItem toggleItem, loadItem, browserItem, settingsItem;
    static Icon iconRunning, iconStarting, iconStopped, iconSleeping;
    static Form syncForm;                       // hidden; owns the UI thread for BeginInvoke

    static volatile State state = State.Stopped;
    static volatile bool busy;                  // a start/stop/load/unload is in flight
    static readonly AutoResetEvent pollNow = new AutoResetEvent(false);
    static Process launched;                    // the python we started, if any
    static readonly string ServerLog = Root + @"\server.log";
    static StreamWriter serverLog;
    static readonly object logLock = new object();

    [STAThread]
    static void Main()
    {
        bool created;
        Mutex mutex = new Mutex(true, "StrataTray_SingleInstance", out created);
        if (!created) return;

        Application.EnableVisualStyles();
        syncForm = new Form();
        syncForm.ShowInTaskbar = false;
        syncForm.WindowState = FormWindowState.Minimized;
        syncForm.Opacity = 0;
        IntPtr h = syncForm.Handle;             // force handle creation for BeginInvoke
        GC.KeepAlive(h);

        Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        iconRunning  = MakeIcon(appIcon, Color.FromArgb(40, 190, 70), false);
        iconStarting = MakeIcon(appIcon, Color.FromArgb(255, 170, 0), false);
        iconSleeping = MakeIcon(appIcon, Color.FromArgb(40, 100, 255), true);
        iconStopped  = MakeIcon(appIcon, Color.FromArgb(120, 120, 120), false);

        ContextMenuStrip menu = new ContextMenuStrip();
        toggleItem = new ToolStripMenuItem("Start WebUI", null, OnToggle);
        loadItem = new ToolStripMenuItem("Unload Model", null, OnLoadToggle);
        browserItem = new ToolStripMenuItem("Open Browser", null, OnOpenBrowser);
        settingsItem = new ToolStripMenuItem("Settings...", null, OnSettings);
        menu.Items.Add(toggleItem);
        menu.Items.Add(loadItem);
        menu.Items.Add(browserItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));
        menu.Opening += delegate { UpdateUi(); pollNow.Set(); };   // draw from cache, refresh soon, never block

        tray = new NotifyIcon();
        tray.ContextMenuStrip = menu;
        tray.Icon = iconStopped;
        tray.Text = "Strata";
        tray.Visible = true;
        tray.MouseUp += OnTrayClick;
        UpdateUi();                              // apply initial state

        Thread poller = new Thread(PollLoop);
        poller.IsBackground = true;
        poller.Start();

        Application.Run();
        GC.KeepAlive(mutex);
    }

    // ---------- background ----------

    static void PollLoop()
    {
        // On launch: bring the WebUI up (lazy, no model) unless something already owns the port.
        if (Probe() == State.Stopped)
        {
            busy = true;
            state = State.Starting; OnUi(UpdateUi);
            try { StartServer(); } finally { busy = false; }
        }
        while (true)
        {
            if (!busy)
            {
                State s = Probe();
                if (s != state) { state = s; OnUi(UpdateUi); }
            }
            pollNow.WaitOne(5000);
        }
    }

    // Never called on the UI thread.
    static State Probe()
    {
        if (PortListening())
        {
            string body;
            int http = Http("GET", "v1/status", null, out body);
            if (http == 200)
                return Regex.IsMatch(body, "\"loaded\"\\s*:\\s*false") ? State.Sleeping : State.Running;
            return State.Starting;                  // listening but 503 / not answering yet
        }
        if (launched != null && !launched.HasExited) return State.Starting;
        if (EngineRunning()) return State.Starting;
        return State.Stopped;
    }

    // Instant: reads the kernel's TCP table, no sockets opened.
    static bool PortListening()
    {
        try
        {
            IPEndPoint[] eps = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (IPEndPoint ep in eps)
                if (ep.Port == Port) return true;
        }
        catch { }
        return false;
    }

    // Instant: by name only, no module/path lookups.
    static bool EngineRunning()
    {
        foreach (string n in EngineNames)
        {
            Process[] ps = Process.GetProcessesByName(n);
            foreach (Process p in ps) p.Dispose();
            if (ps.Length > 0) return true;
        }
        return false;
    }

    // 0 = no answer; otherwise the HTTP status code. Body is the response (or error) text.
    static int Http(string method, string path, string json, out string body)
    {
        body = "";
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url + path);
            req.Method = method;
            req.Timeout = method == "GET" ? 1500 : 10000;
            req.ReadWriteTimeout = req.Timeout;
            req.Proxy = null;
            req.KeepAlive = false;
            if (json != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                req.ContentType = "application/json";   // Strata only accepts JSON on /load and /unload
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader r = new StreamReader(resp.GetResponseStream()))
            {
                body = r.ReadToEnd();
                return (int)resp.StatusCode;
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse r = ex.Response as HttpWebResponse;
            if (r == null) return 0;
            try { using (StreamReader sr = new StreamReader(r.GetResponseStream())) body = sr.ReadToEnd(); } catch { }
            return (int)r.StatusCode;
        }
        catch { return 0; }
    }

    // WMI is slow on this box, so it is only used on Stop, only when we did not
    // launch the server ourselves, and always off the UI thread.
    static List<Process> FindServerPythons()
    {
        List<Process> found = new List<Process>();
        string serverLc = Server.ToLowerInvariant();
        try
        {
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'python%'"))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    string cmd = ((mo["CommandLine"] as string) ?? "").ToLowerInvariant();
                    if (cmd.Contains(serverLc))
                    {
                        try { found.Add(Process.GetProcessById(Convert.ToInt32(mo["ProcessId"]))); }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return found;
    }

    // ---------- actions (run on the thread pool) ----------

    static void StartServer()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Python;
            psi.Arguments = "\"" + Server + "\" --engine strata --config \"" + Config + "\" --port " + Port + " --lazy";
            psi.WorkingDirectory = Root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;               // hidden console, children share it
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            OpenServerLog();
            Process p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += OnServerLine;
            p.ErrorDataReceived += OnServerLine;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            launched = p;
            Say("WebUI starting. The model loads on first use or Load Model.");
        }
        catch (Exception ex) { Say("Start failed: " + ex.Message); }
    }

    static void StopServer()
    {
        int denied = 0;

        // 1. The python server: ours if we launched it, else look it up.
        List<Process> pythons = new List<Process>();
        if (launched != null && !launched.HasExited) pythons.Add(launched);
        else pythons = FindServerPythons();
        foreach (Process p in pythons) denied += Kill(p);
        launched = null;

        // 2. The engine exes, by name.
        foreach (string n in EngineNames)
            foreach (Process p in Process.GetProcessesByName(n)) denied += Kill(p);

        if (denied > 0)
            Say("Couldn't stop " + denied + " process(es): Strata was started as admin. Stop it from that window once; after that the tray owns it.");
    }

    // Keeps the previous run as server.log.1, appends with timestamps, flushes every line.
    static void OpenServerLog()
    {
        lock (logLock)
        {
            try
            {
                if (serverLog != null) { serverLog.Dispose(); serverLog = null; }
                if (File.Exists(ServerLog))
                {
                    string old = ServerLog + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(ServerLog, old);
                }
                serverLog = new StreamWriter(ServerLog, false, new UTF8Encoding(false));
                serverLog.AutoFlush = true;
                serverLog.WriteLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " StrataTray started the server");
            }
            catch { serverLog = null; }
        }
    }

    static void OnServerLine(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;
        lock (logLock)
        {
            if (serverLog == null) return;
            try { serverLog.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + e.Data); } catch { }
        }
    }

    static int Kill(Process p)
    {
        try { p.Kill(); p.WaitForExit(5000); return 0; }
        catch (System.ComponentModel.Win32Exception) { return 1; }
        catch (InvalidOperationException) { return 0; }   // already gone
        finally { p.Dispose(); }
    }

    static void UnloadNow()
    {
        string body;
        int code = Http("POST", "unload?wait=1", "{}", out body);
        if (code == 200) Say("Model unloaded. VRAM and RAM are free; the next request loads it again.");
        else if (code == 409) Say("Busy with a request, try again in a moment.");
        else Say("Unload failed (" + code + "): " + Trim(body));
    }

    static void LoadNow()
    {
        string body;
        int code = Http("POST", "load", "{}", out body);
        if (code == 200) Say("Model loaded.");
        else Say("Load failed (" + code + "): " + Trim(body));
    }

    static string Trim(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
    }

    // ---------- idle unload setting (Strata's own idle_unload_s) ----------

    // Seconds, 0 = off, -1 = config unreadable.
    static int ReadIdleSeconds()
    {
        try
        {
            Match m = Regex.Match(File.ReadAllText(Config), "\"idle_unload_s\"\\s*:\\s*([0-9.]+)");
            if (!m.Success) return 0;
            return (int)double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        catch { return -1; }
    }

    // Writes idle_unload_s into the run config, keeping a backup. Every other key stays as it is.
    static void WriteIdleSeconds(int seconds)
    {
        string text = File.ReadAllText(Config);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(Config, Config + ".bak-tray-" + stamp, true);
        Regex key = new Regex("(\"idle_unload_s\"\\s*:\\s*)[0-9.]+");
        string updated;
        if (key.IsMatch(text))
            updated = key.Replace(text, "${1}" + seconds, 1);
        else
        {
            int brace = text.IndexOf('{');
            if (brace < 0) throw new InvalidDataException("run config is not a JSON object");
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            updated = text.Substring(0, brace + 1) + nl + "  \"idle_unload_s\": " + seconds + "," + text.Substring(brace + 1);
        }
        File.WriteAllText(Config, updated, new UTF8Encoding(false));
    }

    static int LoadTrayMinutes()
    {
        try
        {
            Match m = Regex.Match(File.ReadAllText(TraySettings), "\"idleMinutes\"\\s*:\\s*([0-9]+)");
            if (m.Success) return Math.Max(1, int.Parse(m.Groups[1].Value));
        }
        catch { }
        return DefaultIdleMinutes;
    }

    static void SaveTrayMinutes(int minutes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TraySettings));
            File.WriteAllText(TraySettings, "{ \"idleMinutes\": " + minutes + " }\r\n", new UTF8Encoding(false));
        }
        catch { }
    }

    // ---------- ui ----------

    static void OnTrayClick(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        MethodInfo show = typeof(NotifyIcon).GetMethod("ShowContextMenu",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (show != null) show.Invoke(tray, null);
    }

    static void RunBusy(State shown, ThreadStart work)
    {
        if (busy) return;
        busy = true;
        state = shown;
        UpdateUi();
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { work(); }
            finally { busy = false; pollNow.Set(); }
        });
    }

    static void OnToggle(object sender, EventArgs e)
    {
        if (state == State.Stopped) RunBusy(State.Starting, StartServer);
        else RunBusy(State.Stopping, StopServer);
    }

    static void OnLoadToggle(object sender, EventArgs e)
    {
        if (state == State.Running) RunBusy(State.Running, UnloadNow);
        else if (state == State.Sleeping) RunBusy(State.Starting, LoadNow);
    }

    static void OnOpenBrowser(object sender, EventArgs e)
    {
        try { Process.Start(Url); }
        catch (Exception ex) { Balloon("Could not open browser: " + ex.Message); }
    }

    static void OnSettings(object sender, EventArgs e)
    {
        int current = ReadIdleSeconds();
        if (current < 0)
        {
            MessageBox.Show("Can't read " + Config, "Strata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        bool enabled = current > 0;
        int minutes = enabled ? Math.Max(1, current / 60) : LoadTrayMinutes();

        using (SettingsForm f = new SettingsForm(enabled, minutes))
        {
            if (f.ShowDialog() != DialogResult.OK) return;
            int seconds = f.IdleEnabled ? f.Minutes * 60 : 0;
            SaveTrayMinutes(f.Minutes);
            if (seconds == current) return;
            try { WriteIdleSeconds(seconds); }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't write the run config: " + ex.Message, "Strata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (state == State.Stopped || busy) return;
            DialogResult r = MessageBox.Show(
                "Saved. Strata reads this at startup. Restart it now to apply?",
                "Strata", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
                RunBusy(State.Stopping, delegate { StopServer(); Thread.Sleep(2000); StartServer(); });
        }
    }

    static void OnExit(object sender, EventArgs e)
    {
        if (busy) return;
        busy = true;
        if (state != State.Stopped) { state = State.Stopping; UpdateUi(); }
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { StopServer(); } catch { }
            OnUi(delegate { tray.Visible = false; Application.Exit(); });
        });
    }

    static void OnUi(MethodInvoker m)
    {
        try { syncForm.BeginInvoke(m); } catch { }
    }

    static void Say(string msg)
    {
        OnUi(delegate { Balloon(msg); });
    }

    static void UpdateUi()
    {
        bool serverUp = (state == State.Running || state == State.Sleeping);
        browserItem.Enabled = serverUp && !busy;
        loadItem.Enabled = serverUp && !busy;
        loadItem.Text = (state == State.Sleeping) ? "Load Model" : "Unload Model";
        settingsItem.Enabled = !busy;
        switch (state)
        {
            case State.Running:
                toggleItem.Text = "Stop WebUI"; toggleItem.Enabled = !busy;
                tray.Icon = iconRunning; tray.Text = busy ? "Strata: unloading model" : "Strata: WebUI up, model loaded";
                break;
            case State.Sleeping:
                toggleItem.Text = "Stop WebUI"; toggleItem.Enabled = !busy;
                tray.Icon = iconSleeping; tray.Text = "Strata: WebUI up, model unloaded";
                break;
            case State.Starting:
                toggleItem.Text = busy ? "Starting..." : "Stop WebUI"; toggleItem.Enabled = !busy;
                tray.Icon = iconStarting; tray.Text = "Strata: loading";
                break;
            case State.Stopping:
                toggleItem.Text = "Stopping..."; toggleItem.Enabled = false;
                tray.Icon = iconStarting; tray.Text = "Strata: stopping";
                break;
            default:
                toggleItem.Text = "Start WebUI"; toggleItem.Enabled = true;
                tray.Icon = iconStopped; tray.Text = "Strata: WebUI stopped";
                break;
        }
    }

    static void Balloon(string msg)
    {
        tray.BalloonTipTitle = "Strata";
        tray.BalloonTipText = msg;
        tray.ShowBalloonTip(4000);
    }

    // App icon with a status badge in the bottom-right corner. moon=true draws a crescent (sleeping).
    static Icon MakeIcon(Icon baseIcon, Color c, bool moon)
    {
        Bitmap bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            g.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));
            // badge: 18 px circle, 3 px white ring
            using (SolidBrush ring = new SolidBrush(Color.White)) g.FillEllipse(ring, 14, 14, 18, 18);
            using (SolidBrush dot = new SolidBrush(c)) g.FillEllipse(dot, 17, 17, 12, 12);
            if (moon)
                using (SolidBrush bite = new SolidBrush(Color.White)) g.FillEllipse(bite, 21, 14, 11, 11);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

class SettingsForm : Form
{
    CheckBox enable;
    NumericUpDown minutes;

    public bool IdleEnabled { get { return enable.Checked; } }
    public int Minutes { get { return (int)minutes.Value; } }

    public SettingsForm(bool enabled, int mins)
    {
        Text = "Strata settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        ClientSize = new Size(340, 130);
        Font = SystemFonts.MessageBoxFont;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        enable = new CheckBox();
        enable.Text = "Unload the model when idle";
        enable.AutoSize = true;
        enable.Location = new Point(16, 16);
        enable.Checked = enabled;
        enable.CheckedChanged += delegate { minutes.Enabled = enable.Checked; };

        Label after = new Label();
        after.Text = "after";
        after.AutoSize = true;
        after.Location = new Point(36, 48);

        minutes = new NumericUpDown();
        minutes.Minimum = 1; minutes.Maximum = 1440;
        minutes.Value = Math.Min(1440, Math.Max(1, mins));
        minutes.Location = new Point(76, 44);
        minutes.Width = 64;
        minutes.Enabled = enabled;

        Label unit = new Label();
        unit.Text = "minutes without requests";
        unit.AutoSize = true;
        unit.Location = new Point(146, 48);

        Button ok = new Button();
        ok.Text = "Save"; ok.DialogResult = DialogResult.OK;
        ok.Location = new Point(166, 92); ok.Width = 80;
        Button cancel = new Button();
        cancel.Text = "Cancel"; cancel.DialogResult = DialogResult.Cancel;
        cancel.Location = new Point(252, 92); cancel.Width = 80;
        AcceptButton = ok; CancelButton = cancel;

        Controls.AddRange(new Control[] { enable, after, minutes, unit, ok, cancel });
    }
}
