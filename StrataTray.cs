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
// v12: Model submenu. Every strata-*.json in C:\Strata is a model; picking one switches the
//      run config (stopping and starting the server when it is up) and is remembered in
//      %APPDATA%\StrataTray\settings.json.
// v13: a control port on 127.0.0.1:8081 so a client can ask for a model by name:
//        GET  /model            -> {"model":...,"state":...,"models":[...]}
//        POST /model {"model":"<name>"}  -> switches the run config and returns when the
//                                            server is listening again (Strata loads lazily,
//                                            so the weights load inside the caller's own
//                                            first request, as they always did).
// v14: the control port answers only local programs, never web pages. Any website in a browser on
//      this PC can send requests to 127.0.0.1, so a request is refused when it carries a browser's
//      marks (an Origin or Sec-Fetch-* header: cross-site request forgery), when its Host is not
//      this port on loopback (DNS rebinding), and a POST must be application/json (a page cannot
//      send that cross-site without a CORS preflight, which the port never grants).
// v15: when Strata has an API key (STRATA_API_KEY, else "api_key" in the run config), the tray sends
//      it with its own requests (status, load, unload), which Strata then refuses without it.
// v16: model_name and api_key are read from the config's top level only. An "images" section has
//      its own model_name, and the first match anywhere in the file named the wrong model.
// v17: Image submenu. Every image model (.gguf) next to the run config's images.diffusion_model is
//      listed; picking one points every run config's "images" section at it (and at the matching
//      Qwen3-VL text encoder: BF16 for F16, else the same quant), then restarts Strata if it is up.
//      The Model submenu no longer lists Strata's *.shared-settings.json (not a run config).
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
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("StrataTray")]
[assembly: AssemblyProduct("StrataTray")]
[assembly: AssemblyVersion("17.0.0.0")]
[assembly: AssemblyFileVersion("14.0.0.0")]

static class Program
{
    // ---- settings ----
    const string Root   = @"C:\Strata";
    const string Python = Root + @"\.venv\Scripts\python.exe";
    const string Server = Root + @"\serve\server.py";
    const string FallbackConfig = Root + @"\strata-nvfp4-nvfp4-qad.json";
    static string Config;                                    // the run config in use
    static readonly List<string> Configs = new List<string>();   // every strata-*.json in Root
    const int    Port   = 8080;
    const int    ControlPort = 8081;        // where a client asks for a model by name
    static readonly string Url = "http://127.0.0.1:" + Port + "/";
    static readonly string[] EngineNames = { "strata", "strata-vision" };
    static readonly string TraySettings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"StrataTray\settings.json");
    const int DefaultIdleMinutes = 30;
    // -------------------

    enum State { Stopped, Starting, Running, Sleeping, Stopping }

    static NotifyIcon tray;
    static ToolStripMenuItem toggleItem, loadItem, browserItem, settingsItem, modelMenu, imageMenu;
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

        DiscoverConfigs();
        Config = LoadTrayConfig();
        if (Config == null) Config = (Configs.Count > 0) ? Configs[0] : FallbackConfig;

        ContextMenuStrip menu = new ContextMenuStrip();
        toggleItem = new ToolStripMenuItem("Start WebUI", null, OnToggle);
        loadItem = new ToolStripMenuItem("Unload Model", null, OnLoadToggle);
        browserItem = new ToolStripMenuItem("Open Browser", null, OnOpenBrowser);
        settingsItem = new ToolStripMenuItem("Settings...", null, OnSettings);
        modelMenu = new ToolStripMenuItem("Model");
        BuildModelMenu();
        imageMenu = new ToolStripMenuItem("Image");
        BuildImageMenu();
        menu.Items.Add(toggleItem);
        menu.Items.Add(loadItem);
        menu.Items.Add(browserItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(modelMenu);
        menu.Items.Add(imageMenu);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));
        menu.Opening += delegate { RefreshModelMenu(); BuildImageMenu(); UpdateUi(); pollNow.Set(); };   // draw from cache, refresh soon, never block

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

        Thread control = new Thread(ControlLoop);
        control.IsBackground = true;
        control.Start();

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

    // The key Strata answers to, as Strata reads it: STRATA_API_KEY first, else the run config's
    // "api_key" (a string; several keys are comma separated, the first is used). null = no key,
    // also for "none" (served without a key on purpose). Read per request, so an edit is seen at once.
    static string ApiKey()
    {
        string raw = Environment.GetEnvironmentVariable("STRATA_API_KEY");
        if (string.IsNullOrEmpty(raw))
        {
            try
            {
                raw = TopLevelString(File.ReadAllText(Config), "api_key");
            }
            catch { }
        }
        if (string.IsNullOrEmpty(raw)) return null;
        string first = raw.Split(',')[0].Trim();
        if (first.Length == 0 || first.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return first;
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
            string key = ApiKey();
            if (key != null) req.Headers["Authorization"] = "Bearer " + key;
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
        SaveTraySettings(minutes, Config);
    }

    // ---------- the model (which run config the server is started with) ----------

    // Every strata-*.json in Root, sorted. Backups (.bak-tray-*) are not .json and never match.
    static void DiscoverConfigs()
    {
        Configs.Clear();
        try
        {
            foreach (string p in Directory.GetFiles(Root, "strata-*.json"))
                if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && !p.EndsWith(".shared-settings.json", StringComparison.OrdinalIgnoreCase)) Configs.Add(p);
        }
        catch { }
        Configs.Sort(StringComparer.OrdinalIgnoreCase);
    }

    // The string value of `key` in the JSON object's top level (not inside a nested section), or null.
    static string TopLevelString(string json, string key)
    {
        int v = ValueAt(json, json.IndexOf('{'), key);
        if (v < 0 || json[v] != '"') return null;              // missing, or not a string (a list of keys, a number)
        return ReadJsonString(json, ref v);
    }

    // The string value of section.key (e.g. images.diffusion_model), or null.
    static string SectionString(string json, string section, string key)
    {
        int o = ValueAt(json, json.IndexOf('{'), section);
        if (o < 0 || json[o] != '{') return null;
        int v = ValueAt(json, o, key);
        if (v < 0 || json[v] != '"') return null;
        return ReadJsonString(json, ref v);
    }

    // Replaces the string value of section.key, keeping every other byte. false = no such string value.
    static bool SetSectionString(ref string json, string section, string key, string value)
    {
        int o = ValueAt(json, json.IndexOf('{'), section);
        if (o < 0 || json[o] != '{') return false;
        int v = ValueAt(json, o, key);
        if (v < 0 || json[v] != '"') return false;
        int end = v;
        if (ReadJsonString(json, ref end) == null) return false;
        json = json.Substring(0, v) + JsonQuote(value) + json.Substring(end);
        return true;
    }

    // Index where the value of `key` starts, for the key directly inside the object that opens at json[open]
    // ('{'), never one deeper down; -1 = not there. Strings are skipped whole, so braces inside them never count.
    static int ValueAt(string json, int open, string key)
    {
        if (open < 0 || open >= json.Length || json[open] != '{') return -1;
        int depth = 0;
        int i = open;
        while (i < json.Length)
        {
            char c = json[i];
            if (c == '{' || c == '[') { depth++; i++; continue; }
            if (c == '}' || c == ']') { depth--; i++; if (depth == 0) return -1; continue; }
            if (c != '"') { i++; continue; }
            string str = ReadJsonString(json, ref i);         // i is now just past the closing quote
            if (str == null) return -1;
            if (depth != 1 || str != key) continue;
            int j = i;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j >= json.Length || json[j] != ':') continue;   // a value that equals the key, not the key
            j++;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            return (j < json.Length) ? j : -1;
        }
        return -1;
    }

    static string JsonQuote(string x)
    {
        StringBuilder sb = new StringBuilder("\"");
        foreach (char c in x)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // Reads the JSON string starting at json[i] == '"', unescaping it; leaves i past its end. null = unterminated.
    static string ReadJsonString(string json, ref int i)
    {
        StringBuilder sb = new StringBuilder();
        i++;
        while (i < json.Length)
        {
            char c = json[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= json.Length) return null;
            char e = json[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 > json.Length) return null;
                    sb.Append((char)Convert.ToInt32(json.Substring(i, 4), 16)); i += 4; break;
                default: sb.Append(e); break;                  // \" \\ \/
            }
        }
        return null;
    }

    // The config's own model_name, else its file name.
    static string ModelLabel(string path)
    {
        try
        {
            string name = TopLevelString(File.ReadAllText(path), "model_name");
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    // NotifyIcon.Text is capped at 63 characters, so the menu carries the name, not the tooltip.
    static string ShortLabel(string name)
    {
        const string prefix = "qwen3.8-flash-next-";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name.Substring(prefix.Length);
        return (name.Length > 32) ? name.Substring(0, 31) + "\u2026" : name;
    }

    static void BuildModelMenu()
    {
        modelMenu.DropDownItems.Clear();
        modelMenu.Text = "Model: " + ShortLabel(ModelLabel(Config));
        foreach (string path in Configs)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(ModelLabel(path), null, OnPickModel);
            it.Tag = path;
            it.Checked = string.Equals(path, Config, StringComparison.OrdinalIgnoreCase);
            modelMenu.DropDownItems.Add(it);
        }
        if (modelMenu.DropDownItems.Count == 0)
        {
            ToolStripMenuItem none = new ToolStripMenuItem("(no strata-*.json in " + Root + ")");
            none.Enabled = false;
            modelMenu.DropDownItems.Add(none);
        }
    }

    // On menu open: a directory listing only, and the files are re-read just when the set changed.
    static void RefreshModelMenu()
    {
        List<string> before = new List<string>(Configs);
        DiscoverConfigs();
        bool same = (before.Count == Configs.Count);
        for (int i = 0; same && i < before.Count; i++)
            same = string.Equals(before[i], Configs[i], StringComparison.OrdinalIgnoreCase);
        if (!same) BuildModelMenu();
    }

    static void OnPickModel(object sender, EventArgs e)
    {
        ToolStripMenuItem it = sender as ToolStripMenuItem;
        if (it == null || busy) return;
        string path = it.Tag as string;
        if (path == null || string.Equals(path, Config, StringComparison.OrdinalIgnoreCase)) return;

        bool wasUp = (state == State.Running || state == State.Sleeping);
        if (wasUp && MessageBox.Show("Switch to " + it.Text + "?\r\n\r\nStrata stops and starts again on the new model.",
                                     "Strata", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        Config = path;
        SaveTraySettings(LoadTrayMinutes(), Config);
        BuildModelMenu();
        if (!wasUp)
        {
            UpdateUi();
            Balloon("Model set to " + it.Text + ". Start WebUI to load it.");
            return;
        }
        string name = it.Text;
        RunBusy(State.Stopping, delegate
        {
            StopServer();
            for (int i = 0; i < 50 && PortListening(); i++) Thread.Sleep(100);   // let the port go
            StartServer();
            Say("Switched to " + name + ".");
        });
    }

    // ---------- the image model (the run configs' "images" section) ----------

    // The text encoder that goes with an image model: same folder, Qwen3-VL-*-<quant>.gguf, where an F16
    // image model takes the BF16 encoder (then F16). null = none found (the current one is kept).
    static string EncoderFor(string imageModel)
    {
        string dir = Path.GetDirectoryName(imageModel);
        string stem = Path.GetFileNameWithoutExtension(imageModel);
        int dash = stem.LastIndexOf('-');
        if (dash < 0) return null;
        string quant = stem.Substring(dash + 1);
        string[] wanted = quant.Equals("F16", StringComparison.OrdinalIgnoreCase)
            ? new string[] { "BF16", "F16" } : new string[] { quant };
        foreach (string q in wanted)
            foreach (string f in Directory.GetFiles(dir, "Qwen3-VL-*-" + q + ".gguf"))
                return f;
        return null;
    }

    static string QuantOf(string imageModel)
    {
        string stem = Path.GetFileNameWithoutExtension(imageModel);
        int dash = stem.LastIndexOf('-');
        return dash < 0 ? stem : stem.Substring(dash + 1);
    }

    // Rebuilt on every menu open: one directory listing and one config read.
    static void BuildImageMenu()
    {
        imageMenu.DropDownItems.Clear();
        string current = null;
        try { current = SectionString(File.ReadAllText(Config), "images", "diffusion_model"); } catch { }
        if (current == null)
        {
            imageMenu.Text = "Image: off";
            ToolStripMenuItem none = new ToolStripMenuItem("(this run config has no \"images\" section)");
            none.Enabled = false;
            imageMenu.DropDownItems.Add(none);
            return;
        }
        imageMenu.Text = "Image: " + Path.GetFileNameWithoutExtension(current);
        List<string> models = new List<string>();
        try
        {
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(current), "*.gguf"))
                if (!Path.GetFileName(f).StartsWith("Qwen3-VL", StringComparison.OrdinalIgnoreCase)) models.Add(f);
        }
        catch { }
        models.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string f in models)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(Path.GetFileNameWithoutExtension(f), null, OnPickImage);
            it.Tag = f;
            it.Checked = string.Equals(f, current, StringComparison.OrdinalIgnoreCase);
            imageMenu.DropDownItems.Add(it);
        }
        if (models.Count == 0)
        {
            ToolStripMenuItem none = new ToolStripMenuItem("(no .gguf next to " + current + ")");
            none.Enabled = false;
            imageMenu.DropDownItems.Add(none);
        }
    }

    // Points every run config that has an "images" section at `model` (and its encoder). Returns why not, or null.
    static string SetImageModel(string model)
    {
        string encoder = EncoderFor(model);
        string stem = Path.GetFileNameWithoutExtension(model);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        int done = 0;
        foreach (string path in Configs)
        {
            string text = File.ReadAllText(path);
            if (SectionString(text, "images", "diffusion_model") == null) continue;
            string updated = text;
            SetSectionString(ref updated, "images", "diffusion_model", model);
            if (encoder != null) SetSectionString(ref updated, "images", "llm", encoder);
            SetSectionString(ref updated, "images", "quant", QuantOf(model));       // only when the key is there
            SetSectionString(ref updated, "images", "model_name", stem);
            if (updated == text) { done++; continue; }
            File.Copy(path, path + ".bak-tray-" + stamp, true);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            done++;
        }
        return done == 0 ? "no run config in " + Root + " has an \"images\" section" : null;
    }

    static void OnPickImage(object sender, EventArgs e)
    {
        ToolStripMenuItem it = sender as ToolStripMenuItem;
        if (it == null || busy || it.Checked) return;
        string model = it.Tag as string;
        if (model == null) return;

        bool wasUp = (state == State.Running || state == State.Sleeping);
        if (wasUp && MessageBox.Show("Switch the image model to " + it.Text + "?\r\n\r\nStrata stops and starts again to use it.",
                                     "Strata", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        string why;
        try { why = SetImageModel(model); } catch (Exception ex) { why = ex.Message; }
        if (why != null) { Balloon("Image model not switched: " + why); return; }
        BuildImageMenu();
        if (!wasUp)
        {
            UpdateUi();
            Balloon("Image model set to " + it.Text + ". Start WebUI to use it.");
            return;
        }
        string name = it.Text;
        RunBusy(State.Stopping, delegate
        {
            StopServer();
            for (int i = 0; i < 50 && PortListening(); i++) Thread.Sleep(100);   // let the port go
            StartServer();
            Say("Image model: " + name + ".");
        });
    }

    static string LoadTrayConfig()
    {
        try
        {
            Match m = Regex.Match(File.ReadAllText(TraySettings), "\"config\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success)
            {
                string p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    static void SaveTraySettings(int minutes, string config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TraySettings));
            File.WriteAllText(TraySettings,
                "{ \"idleMinutes\": " + minutes + ", \"config\": \"" + config.Replace("\\", "\\\\") + "\" }\r\n",
                new UTF8Encoding(false));
        }
        catch { }
    }

    // ---------- control port: a client asks for a model by name ----------

    // Loopback only, and a hand-rolled HTTP/1.1 reply: HttpListener would need a URL reservation.
    static void ControlLoop()
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, ControlPort);
            listener.Start();
        }
        catch (Exception ex)
        {
            Say("Model switching is off: port " + ControlPort + " (" + ex.Message + ")");
            return;
        }
        while (true)
        {
            try
            {
                using (TcpClient c = listener.AcceptTcpClient()) Serve(c);
            }
            catch { }
        }
    }

    static void Serve(TcpClient c)
    {
        c.ReceiveTimeout = 5000;
        c.SendTimeout = 300000;                 // a switch can keep the caller waiting
        NetworkStream s = c.GetStream();

        StringBuilder head = new StringBuilder();
        byte[] one = new byte[1];
        while (head.Length < 8192)
        {
            int n = s.Read(one, 0, 1);
            if (n <= 0) return;
            head.Append((char)one[0]);
            if (head.Length >= 4 && head[head.Length - 1] == 10 && head[head.Length - 3] == 10) break;
        }
        string text = head.ToString();
        string[] lines = text.Split(new string[] { "\r\n" }, StringSplitOptions.None);
        string[] first = lines[0].Split(' ');
        if (first.Length < 2) { Reply(s, 400, "{\"error\":\"bad request\"}"); return; }
        string method = first[0].ToUpperInvariant(), path = first[1];

        int length = 0;
        foreach (string line in lines)
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line.Substring(15).Trim(), out length);
        string body = "";
        if (length > 0 && length < 65536)
        {
            byte[] buf = new byte[length];
            int got = 0;
            while (got < length)
            {
                int n = s.Read(buf, got, length - got);
                if (n <= 0) break;
                got += n;
            }
            body = Encoding.UTF8.GetString(buf, 0, got);
        }

        string refusal = RefuseNonLocal(lines, method);
        if (refusal != null) { Reply(s, 403, "{\"error\":\"" + Escape(refusal) + "\"}"); return; }

        if (path != "/model") { Reply(s, 404, "{\"error\":\"unknown path\"}"); return; }

        if (method == "GET")
        {
            DiscoverConfigs();
            StringBuilder all = new StringBuilder();
            foreach (string p in Configs)
            {
                if (all.Length > 0) all.Append(",");
                all.Append("\"").Append(Escape(ModelLabel(p))).Append("\"");
            }
            Reply(s, 200, "{\"model\":\"" + Escape(ModelLabel(Config)) + "\",\"state\":\"" + state.ToString().ToLowerInvariant()
                          + "\",\"models\":[" + all + "]}");
            return;
        }
        if (method != "POST") { Reply(s, 405, "{\"error\":\"use GET or POST\"}"); return; }

        Match m = Regex.Match(body, "\"(?:model|name)\"\\s*:\\s*\"([^\"]+)\"");
        if (!m.Success) { Reply(s, 400, "{\"error\":\"send {\\\"model\\\": \\\"<name>\\\"}\"}"); return; }
        string wanted = m.Groups[1].Value;

        string err = SwitchTo(wanted);
        if (err == null) Reply(s, 200, "{\"model\":\"" + Escape(ModelLabel(Config)) + "\",\"switched\":true}");
        else Reply(s, 409, "{\"error\":\"" + Escape(err) + "\"}");
    }

    static string HeaderValue(string[] lines, string name)
    {
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0 && string.Equals(lines[i].Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase))
                return lines[i].Substring(colon + 1).Trim();
        }
        return null;
    }

    // Why a request may not use the control port, or null. Programs on this PC (Goose) send none of a
    // browser's marks; a web page cannot avoid them.
    static string RefuseNonLocal(string[] lines, string method)
    {
        if (HeaderValue(lines, "Origin") != null || HeaderValue(lines, "Sec-Fetch-Site") != null
            || HeaderValue(lines, "Sec-Fetch-Mode") != null)
            return "web pages may not use the tray's control port";

        string host = (HeaderValue(lines, "Host") ?? "").ToLowerInvariant();
        string port = ":" + ControlPort;
        if (host != "127.0.0.1" + port && host != "localhost" + port && host != "[::1]" + port)
            return "Host '" + host + "' is not this port on loopback (DNS rebinding protection)";

        if (method == "POST")
        {
            string type = (HeaderValue(lines, "Content-Type") ?? "").ToLowerInvariant();
            if (!type.StartsWith("application/json"))
                return "POST needs Content-Type: application/json";
        }
        return null;
    }

    static string Escape(string x)
    {
        return (x ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    static void Reply(NetworkStream s, int code, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        string head = "HTTP/1.1 " + code + " \r\nContent-Type: application/json\r\nContent-Length: "
                      + payload.Length + "\r\nConnection: close\r\n\r\n";
        byte[] h = Encoding.UTF8.GetBytes(head);
        try { s.Write(h, 0, h.Length); s.Write(payload, 0, payload.Length); s.Flush(); }
        catch { }
    }

    // Puts the server on the config whose model_name is `wanted`. Returns null, or why it could not.
    // Only the server is brought up: Strata is started with --lazy, so the weights load on the first
    // request, which is the caller's own.
    static string SwitchTo(string wanted)
    {
        DiscoverConfigs();
        string target = null;
        foreach (string p in Configs)
            if (string.Equals(ModelLabel(p), wanted, StringComparison.OrdinalIgnoreCase)) { target = p; break; }
        if (target == null) return "no config in " + Root + " has model_name \"" + wanted + "\"";

        for (int i = 0; i < 600 && busy; i++) Thread.Sleep(100);     // let a menu action finish
        if (busy) return "the tray is busy with another action";

        bool same = string.Equals(target, Config, StringComparison.OrdinalIgnoreCase);
        if (same && PortListening()) return null;                     // already serving it

        busy = true;
        try
        {
            if (PortListening())
            {
                StopServer();
                for (int i = 0; i < 100 && PortListening(); i++) Thread.Sleep(100);
            }
            Config = target;
            SaveTraySettings(LoadTrayMinutes(), Config);
            OnUi(delegate { BuildModelMenu(); UpdateUi(); });
            StartServer();
            for (int i = 0; i < 600 && !PortListening(); i++) Thread.Sleep(100);
            if (!PortListening()) return "Strata did not start listening on port " + Port;
        }
        finally { busy = false; pollNow.Set(); }
        Say("Switched to " + wanted + " on request.");
        return null;
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
        modelMenu.Enabled = !busy;
        imageMenu.Enabled = !busy;
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
