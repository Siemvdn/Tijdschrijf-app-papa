// Tijdschrijven.exe - kleine lokale server + app-venster (Edge) voor de tijdschrijf-app.
// Gebouwd met de ingebouwde .NET Framework-compiler (C# 5): geen SDK nodig. Zie tools\build_exe.cmd.
//
// De server heeft hetzelfde gedrag als serve.ps1: alleen tijdschrijven.json mag geschreven worden,
// body altijd UTF-8, validatie (400/409/413), back-ups (laatste 50), atomair schrijven, 500 bij fouten.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    const int DefaultPort = 8765;
    const int MaxBody = 20 * 1024 * 1024;
    const int MaxBackups = 50;
    const int MaxEntryDrop = 5;
    const string DataFile = "tijdschrijven.json";

    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);   // UTF-8 zonder BOM
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly object SaveLock = new object();
    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tijdschrijven");

    static HttpListener listener;
    static int port;
    static string dataPath;                // volledig pad naar tijdschrijven.json (mag overal staan)
    static string ConfigFile { get { return Path.Combine(AppData, "datapad.txt"); } }
    static bool keepAlive;                 // --no-browser: nooit uit zichzelf stoppen (voor tests)
    static DateTime started = DateTime.UtcNow;
    static DateTime lastPing = DateTime.MinValue;
    static DateTime byeAt = DateTime.MinValue;
    static readonly ManualResetEvent Quit = new ManualResetEvent(false);

    [STAThread]
    static int Main(string[] args)
    {
        int wantedPort = DefaultPort; bool noBrowser = false; string dataArg = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out wantedPort);
            else if (args[i] == "--no-browser") noBrowser = true;
            else if (args[i] == "--data" && i + 1 < args.Length) dataArg = Path.GetFullPath(args[++i]);
        }
        keepAlive = noBrowser;
        Directory.CreateDirectory(AppData);

        bool createdNew;
        string mutexName = "Local\\Tijdschrijven_" + wantedPort;
        using (Mutex mutex = new Mutex(true, mutexName, out createdNew))
        {
            if (!createdNew)
            {   // Draait al: alleen een extra venster openen op de bestaande server.
                int p = ReadPortFile();
                if (!noBrowser) OpenWindow(p > 0 ? p : wantedPort);
                return 0;
            }
            try
            {
                if (!Directory.Exists(Root) || !File.Exists(Path.Combine(Root, "index.html")))
                    throw new Exception("index.html staat niet naast Tijdschrijven.exe (" + Root + ").");
                dataPath = dataArg ?? ResolveDataPath();
                if (dataPath == null) return 0;   // gebruiker annuleerde de keuze
                StartServer(wantedPort);
                File.WriteAllText(Path.Combine(AppData, "port.txt"), port.ToString());
                new Thread(ServeLoop) { IsBackground = true }.Start();
                Process browser = noBrowser ? null : OpenWindow(port);
                Wait(browser);
            }
            catch (Exception ex)
            {
                Log("START " + ex);
                MessageBox.Show("Tijdschrijven kon niet starten:\n\n" + ex.Message, "Tijdschrijven", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                try { if (listener != null) listener.Close(); } catch { }
            }
        }
        return 0;
    }

    // ---------- plek van het databestand ----------

    // Bepaalt waar tijdschrijven.json staat: onthouden pad, anders het bestand naast de exe, anders vragen.
    static string ResolveDataPath()
    {
        try
        {
            string saved = File.Exists(ConfigFile) ? File.ReadAllText(ConfigFile, Utf8).Trim() : "";
            if (saved.Length > 0 && File.Exists(saved)) return saved;
            if (saved.Length > 0)
            {
                MessageBox.Show("Het gegevensbestand is niet gevonden op de vorige plek:\n\n" + saved +
                    "\n\n(Staat de map of schijf er nog? Kies nu opnieuw waar het bestand staat.)", "Tijdschrijven", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                string local = Path.Combine(Root, DataFile);
                if (File.Exists(local)) { SaveDataPath(local); return local; }
            }
        }
        catch (Exception ex) { Log("CONFIG " + ex); }
        string chosen = AskLocation(null);
        if (chosen != null) SaveDataPath(chosen);
        return chosen;
    }

    static void SaveDataPath(string p) { File.WriteAllText(ConfigFile, p, Utf8); }

    // Vraagt waar het gegevensbestand staat (kiezen) of moet komen (nieuwe/kopie). null = geannuleerd.
    // Er wordt nooit iets overschreven of verwijderd.
    static string AskLocation(string current)
    {
        using (Form owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-3000, -3000), Size = new System.Drawing.Size(1, 1) })
        {
            owner.Show();
            bool hasCurrent = current != null && File.Exists(current);
            string q = hasCurrent
                ? "Waar moet het gegevensbestand komen?\n\nJA = een bestaand tijdschrijven.json kiezen\nNEE = de huidige gegevens kopiëren naar een andere map (het huidige bestand blijft staan)"
                : "Waar staat je gegevensbestand?\n\nJA = ik heb al een tijdschrijven.json, die wil ik kiezen\nNEE = ik begin met een nieuw, leeg bestand in een map naar keuze";
            DialogResult r = MessageBox.Show(owner, q, "Tijdschrijven – plek van de gegevens", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return null;
            if (r == DialogResult.Yes)
            {
                using (OpenFileDialog dlg = new OpenFileDialog { Title = "Kies tijdschrijven.json", Filter = "Gegevensbestand (*.json)|*.json", CheckFileExists = true })
                    return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
            }
            using (FolderBrowserDialog fb = new FolderBrowserDialog { Description = "Kies de map waar tijdschrijven.json moet staan" })
            {
                if (fb.ShowDialog(owner) != DialogResult.OK) return null;
                string target = Path.Combine(fb.SelectedPath, DataFile);
                if (File.Exists(target))
                {
                    if (MessageBox.Show(owner, "In die map staat al een tijdschrijven.json. Dat bestand gebruiken (er wordt niets overschreven)?", "Tijdschrijven", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return null;
                    return target;
                }
                if (hasCurrent) File.Copy(current, target);
                else File.WriteAllText(target, "{\n  \"version\": 2,\n  \"entries\": []\n}", Utf8);
                return target;
            }
        }
    }

    // Voor de knop in de app: kiest op een STA-thread een nieuwe plek en schakelt de server erop om.
    static object ChangeLocation()
    {
        string chosen = null; Exception err = null;
        Thread t = new Thread(delegate () { try { chosen = AskLocation(dataPath); } catch (Exception ex) { err = ex; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (err != null) throw err;
        if (chosen == null) return Obj("ok", false, "geannuleerd", true);
        lock (SaveLock) { dataPath = chosen; SaveDataPath(chosen); }
        return Obj("ok", true, "pad", chosen);
    }

    // ---------- opstarten en afsluiten ----------

    static void StartServer(int wanted)
    {
        Exception last = null;
        for (int p = wanted; p < wanted + 11; p++)
        {
            HttpListener l = new HttpListener();
            l.Prefixes.Add("http://127.0.0.1:" + p + "/");
            try { l.Start(); listener = l; port = p; return; }
            catch (HttpListenerException ex) { last = ex; try { l.Close(); } catch { } }
        }
        throw new Exception("Geen vrije poort gevonden (" + wanted + "-" + (wanted + 10) + "). " + (last != null ? last.Message : ""));
    }

    static int ReadPortFile()
    {
        try { return int.Parse(File.ReadAllText(Path.Combine(AppData, "port.txt")).Trim()); } catch { return 0; }
    }

    static void Wait(Process browser)
    {
        DateTime browserStart = DateTime.UtcNow;
        while (!Quit.WaitOne(1000))
        {
            if (keepAlive) continue;
            DateTime now = DateTime.UtcNow;
            // 1. venster dicht: het browserproces stopt (alleen betrouwbaar als het niet meteen doorgaf aan een ander proces)
            if (browser != null && browser.HasExited && (now - browserStart).TotalSeconds > 10) return;
            // 2. pagina meldde bij sluiten "bye" en er kwam binnen enkele seconden geen nieuwe ping (herladen telt niet)
            if (byeAt != DateTime.MinValue && (now - byeAt).TotalSeconds > 6) return;
            // 3. al een tijd geen levensteken meer
            if (lastPing != DateTime.MinValue && (now - lastPing).TotalSeconds > 180) return;
            // 4. nooit iemand verbonden
            if (lastPing == DateTime.MinValue && (now - started).TotalSeconds > 120) return;
        }
    }

    static Process OpenWindow(int p)
    {
        string url = "http://127.0.0.1:" + p + "/";
        string exe = FindBrowser();
        try
        {
            if (exe != null)
            {
                string profile = Path.Combine(AppData, "browser");
                string args = "--app=" + url + " --user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check --window-size=1280,860";
                return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });   // laatste redmiddel: standaardbrowser
        }
        catch (Exception ex)
        {
            Log("BROWSER " + ex);
            MessageBox.Show("De app is gestart, maar het venster kon niet geopend worden.\nOpen zelf " + url + " in een browser.\n\n" + ex.Message,
                "Tijdschrijven", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return null;
    }

    static string FindBrowser()
    {
        string[] keys = { "msedge.exe", "chrome.exe" };
        foreach (string k in keys)
        {
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using (RegistryKey rk = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)
                        .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + k))
                    {
                        string v = rk == null ? null : rk.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
                catch { }
            }
        }
        string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)"), pf = Environment.GetEnvironmentVariable("ProgramFiles");
        string[] cands = {
            Path.Combine(pf86 ?? "", @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf ?? "", @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf ?? "", @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86 ?? "", @"Google\Chrome\Application\chrome.exe") };
        return cands.FirstOrDefault(File.Exists);
    }

    // ---------- server ----------

    static void ServeLoop()
    {
        while (listener != null && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
        }
    }

    static void Handle(HttpListenerContext ctx)
    {
        try
        {
            string method = ctx.Request.HttpMethod;
            string rel = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath.TrimStart('/'));
            if (string.IsNullOrWhiteSpace(rel)) rel = "index.html";

            if (rel == "ping") { lastPing = DateTime.UtcNow; byeAt = DateTime.MinValue; SendJson(ctx, 200, Obj("ok", true)); return; }
            if (rel == "bye") { byeAt = DateTime.UtcNow; ctx.Response.StatusCode = 204; return; }

            if (rel == "datalocatie" && method == "GET") { SendJson(ctx, 200, Obj("ok", true, "pad", dataPath)); return; }
            if (rel == "kies-locatie" && method == "POST") { SendJson(ctx, 200, ChangeLocation()); return; }

            if (method == "PUT" || method == "POST")
            {
                if (rel == DataFile) { lock (SaveLock) { SaveData(ctx, dataPath); } }
                else ctx.Response.StatusCode = 403;
                return;
            }

            string path = rel == DataFile ? dataPath : Path.GetFullPath(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (rel != DataFile && !path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }
            if (File.Exists(path))
            {
                byte[] bytes = File.ReadAllBytes(path);
                ctx.Response.ContentType = MimeOf(Path.GetExtension(path));
                if (rel == DataFile || rel == "index.html") ctx.Response.Headers.Add("Cache-Control", "no-store");
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            else ctx.Response.StatusCode = 404;
        }
        catch (Exception ex)
        {
            Log("REQUEST " + ex);
            try { SendJson(ctx, 500, Obj("ok", false, "error", "Serverfout: " + ex.Message)); } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    static string MimeOf(string ext)
    {
        switch ((ext ?? "").ToLowerInvariant())
        {
            case ".html": return "text/html; charset=utf-8";
            case ".js": return "text/javascript";
            case ".css": return "text/css";
            case ".json": return "application/json";
            case ".png": return "image/png";
            case ".svg": return "image/svg+xml";
            case ".ico": return "image/x-icon";
            default: return "application/octet-stream";
        }
    }

    static Dictionary<string, object> Obj(params object[] kv)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }

    static void SendJson(HttpListenerContext ctx, int code, object obj)
    {
        byte[] bytes = Utf8.GetBytes(Json.Serialize(obj));
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    // Leest de body (altijd als UTF-8). null = groter dan toegestaan.
    static string ReadBody(HttpListenerRequest req)
    {
        if (req.ContentLength64 > MaxBody) return null;
        using (MemoryStream ms = new MemoryStream())
        {
            byte[] buf = new byte[81920]; int n;
            while ((n = req.InputStream.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxBody) return null;
            }
            return Utf8.GetString(ms.ToArray()).TrimStart('﻿');
        }
    }

    // Aantal entries, of -1 als de tekst geen geldige JSON met een entries-lijst is.
    static int EntryCount(string text)
    {
        try
        {
            IDictionary obj = Json.DeserializeObject(text) as IDictionary;
            if (obj == null || !obj.Contains("entries")) return -1;
            IEnumerable e = obj["entries"] as IEnumerable;
            if (e == null || obj["entries"] is string) return -1;
            return e.Cast<object>().Count();
        }
        catch { return -1; }
    }

    static void SaveData(HttpListenerContext ctx, string target)
    {
        string body = ReadBody(ctx.Request);
        if (body == null) { SendJson(ctx, 413, Obj("ok", false, "error", "Bestand te groot (maximaal 20 MB). Er is niets opgeslagen.")); return; }
        int newCount = EntryCount(body);
        if (newCount < 0) { SendJson(ctx, 400, Obj("ok", false, "error", "Ongeldige data: geen geldige JSON met een 'entries'-lijst. Er is niets opgeslagen.")); return; }

        if (File.Exists(target))
        {
            int oldCount = -1;
            try { oldCount = EntryCount(File.ReadAllText(target, Utf8)); } catch { }
            if (oldCount >= 0 && newCount < oldCount - MaxEntryDrop)
            {
                SendJson(ctx, 409, Obj("ok", false, "error", "Opslaan geweigerd: de pagina heeft " + newCount + " regels, op schijf staan er " + oldCount +
                    ". Waarschijnlijk is de pagina verouderd. Herlaad de pagina (F5) en probeer opnieuw."));
                return;
            }
            // Back-up van de huidige versie; alleen de oudste automatische back-ups worden opgeruimd.
            string dir = Path.Combine(Path.GetDirectoryName(target), "backups");   // naast het databestand
            Directory.CreateDirectory(dir);
            string backup = Path.Combine(dir, "tijdschrijven_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            if (!File.Exists(backup)) File.Copy(target, backup);   // nooit een bestaande back-up overschrijven
            string[] auto = Directory.GetFiles(dir, "tijdschrijven_????????_??????.json");
            Array.Sort(auto, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < auto.Length - MaxBackups; i++) File.Delete(auto[i]);
        }
        // Atomair: eerst naar een tijdelijk bestand, dan vervangen.
        string tmp = target + ".tmp";
        File.WriteAllText(tmp, body, Utf8);
        if (File.Exists(target)) File.Replace(tmp, target, null);
        else File.Move(tmp, target);
        SendJson(ctx, 200, Obj("ok", true, "entries", newCount));
    }

    static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(AppData, "fouten.log"), DateTime.Now.ToString("s") + " " + msg + Environment.NewLine); } catch { }
    }
}
