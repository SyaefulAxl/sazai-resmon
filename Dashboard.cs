// =====================================================================
//  Resource Monitoring v1.1 — native Windows 11 (C# WinForms, .NET Framework)
//  Fitur: pemakaian internet real-time (unduh/unggah/total/grafik),
//         status baterai (watt mA x V, estimasi, kesehatan via powercfg),
//         cuaca (Open-Meteo + geolokasi IP), info sistem (CPU/RAM/uptime).
//  Kompilasi: lihat build.ps1 di folder yang sama.
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ResourceMonitoring
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // mode kolektor suhu: `ResourceMonitoring.exe --tempsvc` diluncurkan sendiri oleh kartu SUHU
            // (sekali UAC). Tanpa UI dan tanpa mutex — boleh berjalan bersama instans dashboard normal.
            if (args != null && args.Length > 0 && Array.Exists(args, a => StringComparer.OrdinalIgnoreCase.Equals(a, "--tempsvc")))
            {
                SessionTempCollectorSvc.Run();
                return;
            }
            // satu instans saja: peluncuran kedua cukup meminta instans pertama menampilkan dashboard
            // (tanpa ini dua widget taskbar saling dorong dan ikon tray ganda)
            bool created;
            System.Threading.Mutex single = new System.Threading.Mutex(true, @"Local\ResourceMonitoring.Single", out created);
            System.Threading.EventWaitHandle showEv = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\ResourceMonitoring.Show");
            if (!created)
            {
                try { showEv.Set(); } catch { }
                return;
            }
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | (SecurityProtocolType)768 | (SecurityProtocolType)3072; }
            catch { }
            // tangkap semua exception -> dbg.log (mendiagnosis crash diam-diam)
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try { Dbg.Log("UI-EX " + e.Exception); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { Dbg.Log("FATAL-EX " + e.ExceptionObject); } catch { }
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Lang.Load();
            DashboardForm form = new DashboardForm();
            System.Threading.Thread waiter = new System.Threading.Thread(delegate()
            {
                while (true) { showEv.WaitOne(); form.RequestShow(); }
            });
            waiter.IsBackground = true;
            waiter.Start();
            Application.Run(form);
            GC.KeepAlive(single);
        }
    }

    static class Dbg
    {
        static readonly bool On = Environment.GetEnvironmentVariable("RESOURCEMON_DEBUG") == "1";

        public static void Log(string m)
        {
            if (!On && !m.StartsWith("UI-EX") && !m.StartsWith("FATAL-EX") && !m.StartsWith("TEMP-")) return;
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dbg.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + "\n"); } catch { }
        }
    }

    /// ---------------------------------------------------------------------------
    /// Kolektor suhu (mode `--tempsvc`, dijalankan sebagai Administrator lewat sekali UAC).
    /// Zona termal ACPI (root\WMI) dan suhu SSD/NVMe (layanan Storage) DITOLAK Windows untuk
    /// proses biasa di laptop ini (terverifikasi: Access denied / CIM denied) — jadi bagian ini
    /// ditinggikan. Menulis temps.json tiap 5 detik; kartu SUHU dashboard hanya membaca file itu
    /// dan tidak menyandera hak admin. Berhenti sendiri bila dashboard tidak terlihat 90 detik.
    /// ---------------------------------------------------------------------------
    static class SessionTempCollectorSvc
    {
        sealed class Ts { public string Kind, Name; public double C; }

        /// Lokasi temps.json: di folder aplikasi (salinan Tools), fallback %LOCALAPPDATA%.
        public static string TempFile()
        {
            try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temps.json"); } catch { }
            string d;
            try
            {
                d = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(d))
                {
                    d = Path.Combine(d, "ResourceMonitoring");
                    Directory.CreateDirectory(d);
                    return Path.Combine(d, "temps.json");
                }
            }
            catch { }
            return Path.Combine(Path.GetTempPath(), "ResourceMonitoring.temps.json");
        }

        /// Peluncuran kolektor dengan satu prompt UAC dari kartu SUHU (dipanggil proses dashboard).
        public static bool TryStart(out string err)
        {
            err = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--tempsvc";
                psi.UseShellExecute = true;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch (Exception e)
            {
                err = e.Message;
                return false;
            }
        }

        static string PrettyAcpi(string raw)
        {
            string s = raw ?? "";
            s = s.Trim('\\');                       // "\_TZ.CST0 (DRVC\)" dsb
            int i = s.IndexOf("_TZ.");
            if (i >= 0) s = s.Substring(i + 4);
            string[] parts = s.Split(new char[] { '.', '\\' });
            if (parts.Length > 2) s = parts[parts.Length - 2] + "." + parts[parts.Length - 1];
            else if (parts.Length == 2) s = parts[parts.Length - 2] + "." + parts[parts.Length - 1];
            return s.Replace("__", "").Trim();
        }

        static LibreHardwareMonitor.Hardware.Computer lhw;

        /// Satu putaran pengambilan seluruh sensor yang tersedia.
        static List<Ts> ProbeOnce()
        {
            List<Ts> viaLhw = ProbeLhw();
            if (viaLhw.Count > 0) return viaLhw;
            Dbg.Log("TEMP-lhk fallback-ke-WMI");
            return ProbeWmi();
        }

        static void AddTemp(List<Ts> into, string kindOrder, string name, double c)
        {
            if (double.IsNaN(c)) return;
            if (c <= -40 || c >= 130) return;
            into.Add(new Ts { Kind = kindOrder, Name = name, C = c });
        }

        /// Baca seluruh sensor suhu lewat LibreHardwareMonitorLib (ring-0 WinRing0/IGCL).
        /// Ini jalur utama: WMI/MsaCpi laptop ini tidak mengekspos suhu sama sekali (row ACPI bernilai 0).
        static List<Ts> ProbeLhw()
        {
            List<Ts> cpu = new List<Ts>(), gpu = new List<Ts>(), ssd = new List<Ts>(), mb = new List<Ts>(), other = new List<Ts>();
            List<Ts> fans = new List<Ts>(), watts = new List<Ts>();
            try
            {
                if (lhw == null)
                {
                    lhw = new LibreHardwareMonitor.Hardware.Computer();
                    lhw.IsCpuEnabled = true;
                    lhw.IsGpuEnabled = true;
                    lhw.IsMemoryEnabled = true;
                    lhw.IsMotherboardEnabled = true;
                    lhw.IsStorageEnabled = true;
                    lhw.IsBatteryEnabled = true;
                    lhw.IsControllerEnabled = false;
                    lhw.IsNetworkEnabled = false;
                    lhw.IsPsuEnabled = false;
                    lhw.Open();
                    Dbg.Log("TEMP-lhw Open ok");
                }
                foreach (LibreHardwareMonitor.Hardware.IHardware hw in lhw.Hardware)
                {
                    hw.Update();
                    CollectInto(cpu, gpu, ssd, mb, other, fans, watts, hw);
                    foreach (LibreHardwareMonitor.Hardware.IHardware sub in hw.SubHardware)
                    {
                        sub.Update();
                        CollectInto(cpu, gpu, ssd, mb, other, fans, watts, sub);
                    }
                }
            }
            catch (Exception ex)
            {
                Dbg.Log("TEMP-lhwEX " + ex.GetType().Name + " " + ex.Message);
            }
            List<Ts> all = new List<Ts>();
            all.AddRange(cpu); all.AddRange(gpu); all.AddRange(ssd); all.AddRange(mb); all.AddRange(other);
            all.AddRange(fans); all.AddRange(watts);   // kipas/daya di belakang: rata-rata hanya dari suhu
            Dbg.Log("TEMP-lhw suhu=" + (all.Count - fans.Count - watts.Count) + " kipas=" + fans.Count + " daya=" + watts.Count);
            return all;
        }

        static void CollectInto(List<Ts> cpu, List<Ts> gpu, List<Ts> ssd, List<Ts> mb, List<Ts> other, List<Ts> fans, List<Ts> watts, LibreHardwareMonitor.Hardware.IHardware hw)
        {
            string kind = hw.HardwareType.ToString().ToLowerInvariant();
            foreach (LibreHardwareMonitor.Hardware.ISensor sen in hw.Sensors)
            {
                // kipas (RPM) dan daya (W) ikut — untuk menjawab "kipas kenapa ngebut" dengan angka
                if (sen.SensorType == LibreHardwareMonitor.Hardware.SensorType.Fan && sen.Value.HasValue)
                {
                    double rpm = (double)sen.Value;
                    if (rpm > 0) fans.Add(new Ts { Kind = "kipas", Name = hw.Name + " \u00B7 " + sen.Name, C = rpm });
                    continue;
                }
                if (sen.SensorType == LibreHardwareMonitor.Hardware.SensorType.Power && sen.Value.HasValue)
                {
                    double w = (double)sen.Value;
                    if (w > 0.05 && w < 500) watts.Add(new Ts { Kind = "daya", Name = hw.Name + " \u00B7 " + sen.Name, C = w });
                    continue;
                }
                if (sen.SensorType != LibreHardwareMonitor.Hardware.SensorType.Temperature || !sen.Value.HasValue) continue;
                // sensor batas (atau peringatan/kritis) bukan pengukuran real; buang dari rata-rata
                string sn = sen.Name ?? "";
                if (sn.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0
                    || sn.IndexOf("critical", StringComparison.OrdinalIgnoreCase) >= 0
                    || sn.IndexOf("limit", StringComparison.OrdinalIgnoreCase) >= 0
                    || sn.IndexOf("margin", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                // baris teratas kartu: dari CPU dulu — "Package"/"Total" sebelum per-core
                bool top = sen.Name.IndexOf("package", StringComparison.OrdinalIgnoreCase) >= 0
                        || sen.Name.IndexOf("tctl", StringComparison.OrdinalIgnoreCase) >= 0
                        || sen.Name.IndexOf("ccd", StringComparison.OrdinalIgnoreCase) >= 0;
                string label = hw.Name + " \u00B7 " + sen.Name + (top ? " \u2691" : "");
                List<Ts> bucket = other;
                if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu) bucket = cpu;
                else if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.GpuIntel
                      || hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia
                      || hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.GpuAmd) bucket = gpu;
                else if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Storage) bucket = ssd;
                else if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Motherboard) bucket = mb;
                else if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Memory) bucket = other;
                else if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Battery) bucket = other;
                if (top && bucket.Count > 0) bucket.Insert(0, new Ts { Kind = kind, Name = label, C = (double)sen.Value });
                else bucket.Add(new Ts { Kind = kind, Name = label, C = (double)sen.Value });
            }
        }

        /// Fallback WMI (lama) bila LHM gagal dimuat: ACPI zone + baterai + Storage WMI.
        static List<Ts> ProbeWmi()
        {
            List<Ts> s = new List<Ts>();
            // 1) zona termal ACPI — pada laptop ini yang meng-cover CPU (CST) dan panas papan
            int nZ = 0; double zSum = 0;
            try
            {
                foreach (Dictionary<string, object> o in Util.WmiRows("root\\WMI",
                    "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    nZ++;
                    double k = Util.ToLong(o["CurrentTemperature"]);
                    if (k > 0)
                    {
                        double c = k / 10.0 - 273.15;
                        if (c > -20 && c < 125)
                        {
                            s.Add(new Ts { Kind = "zona", Name = PrettyAcpi(o["InstanceName"] as string), C = c });
                            zSum += c;
                        }
                    }
                    else Dbg.Log("TEMP-zona0 " + (o["InstanceName"] as string) + " k=" + k);
                }
            }
            catch (Exception ex) { Dbg.Log("TEMP-zonaEX " + ex.GetType().Name + " " + ex.Message); }
            Dbg.Log("TEMP-zona rows=" + nZ + " valid=" + s.Count + (s.Count > 0 ? " avg=" + (zSum / s.Count).ToString("0.0", CultureInfo.InvariantCulture) : ""));
            // 2) baterai (jarang diisi vendor, tapi ikut bila ada)
            int nB = 0; double bt = 0;
            foreach (Dictionary<string, object> o in Util.WmiRows("root\\WMI",
                "SELECT Temperature FROM BatteryStatus"))
            {
                nB++;
                double k = Util.ToLong(o["Temperature"]);
                if (k > 0)
                {
                    double c = k / 10.0 - 273.15;
                    if (c > 0 && c < 90) { s.Add(new Ts { Kind = "baterai", Name = "Baterai", C = c }); bt = c; }
                }
            }
            Dbg.Log("TEMP-battery rows=" + nB + (nB > 0 ? " temp=" + bt.ToString("0.0", CultureInfo.InvariantCulture) : ""));
            // 3) SSD NVMe lewat layanan Storage (Temperature = suhu komposit NVMe, kelvin)
            List<Ts> ssd = SsdTempsViaStorage();
            s.AddRange(ssd);
            Dbg.Log("TEMP-ssd n=" + ssd.Count);
            return s;
        }

        static List<Ts> SsdTempsViaStorage()
        {
            List<Ts> r = new List<Ts>();
            try
            {
                // jalur cepat: baris penghitung langsung dari nama tabel Storage
                foreach (Dictionary<string, object> o in Util.WmiRows("root\\Microsoft\\Windows\\Storage",
                    "SELECT ObjectId, Temperature FROM MSFT_StorageReliabilityCounter WHERE Temperature > 0"))
                {
                    double c = Util.ToLong(o["Temperature"]);
                    if (c > 0 && c < 100) r.Add(new Ts { Kind = "ssd", Name = "SSD", C = c });
                }
                if (r.Count > 0) return r;
            }
            catch { }
            // fallback: kueri asosiasi per disk (barisan Get-StorageReliabilityCounter pada Win11)
            try
            {
                int n = 0;
                foreach (Dictionary<string, object> d in Util.WmiRows("root\\Microsoft\\Windows\\Storage",
                    "SELECT ObjectId, FriendlyName FROM MSFT_PhysicalDisk"))
                {
                    try
                    {
                        string oid = (d["ObjectId"] as string) ?? "";
                        if (oid.IndexOf('\'') >= 0) continue;
                        foreach (Dictionary<string, object> o in Util.WmiRows("root\\Microsoft\\Windows\\Storage",
                            "Associators of {MSFT_PhysicalDisk.ObjectId='" + oid + "'} WHERE ResultClass = MSFT_StorageReliabilityCounter"))
                        {
                            double cc = Util.ToLong(o["Temperature"]);
                            if (cc > 0 && cc < 100)
                            {
                                string nm = d["FriendlyName"] as string;
                                if (string.IsNullOrEmpty(nm)) nm = "SSD " + (++n);
                                r.Add(new Ts { Kind = "ssd", Name = nm, C = cc });
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return r;
        }

        public static void Run()
        {
            // satu kolektor saja (instans kedua langsung keluar)
            bool mxNew;
            using (System.Threading.Mutex mx = new System.Threading.Mutex(true, @"Local\ResourceMonitoring.TempSvc", out mxNew))
            {
                if (!mxNew) return;
                GC.KeepAlive(mx);
                RunLoop();
            }
        }

        static void RunLoop()
        {
            string save = TempFile();
            string stopAt = Path.Combine(Path.GetDirectoryName(save) ?? ".", "temps.stop");
            byte quiet = 0;                      // putaran tanpa dashboard yang terlihat
            while (true)
            {
                List<Ts> s = null;
                try
                {
                    s = ProbeOnce();
                    double avg = 0; int ok = 0;
                    foreach (Ts t in s)
                    {
                        // rata-rata HANYA dari suhu; kipas (RPM) dan daya (W) bukan derajat
                        if (t.Kind == "kipas" || t.Kind == "daya") continue;
                        if (t.C > -40 && t.C < 130) { ok++; avg += t.C; }
                    }
                    System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer();
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["epoch"] = DateTime.UtcNow.Ticks;
                    payload["pembaruan"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    payload["avg"] = ok > 0 ? Math.Round(avg / ok, 1) : 0d;
                    payload["sensor"] = s.Select(t => (object)new Dictionary<string, object>
                    {
                        { "kind", t.Kind }, { "name", t.Name ?? "" }, { "c", Math.Round(t.C, 1) }
                    }).ToList();
                    string json = js.Serialize(payload);
                    string tmp = save + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(save)) File.Delete(save);
                    File.Move(tmp, save);
                }
                catch { /* tidak boleh mati kemana pun; dashboard hanya melihat file usang */ }
                // berhenti: dashboard tidak RUN (90 detik beruntun) — atau temps.stop dibuat pengguna
                try
                {
                    bool stop = false;
                    if (File.Exists(stopAt)) { stop = true; try { File.Delete(stopAt); } catch { } }
                    Process[] found = Process.GetProcessesByName("ResourceMonitoring");
                    if (found == null || found.Length == 0) quiet++; else quiet = 0;
                    if (found != null) foreach (Process p in found) try { p.Dispose(); } catch { }
                    if (stop || quiet >= 18) return;
                }
                catch { quiet++; if (quiet >= 18) return; }
                Thread.Sleep(5000);
            }
        }
    }

    /// -----------------------------------------------------------------------
    /// i18n dua bahasa: Bahasa Indonesia (default) dan English.
    /// Semua teks antarmuka diambil lewat Lang.T("kunci"); tabelnya di bawah.
    /// lang.txt (satu kata: "id"/"en") di folder aplikasi; bisa diganti dari menu.
    /// -----------------------------------------------------------------------
    static class Lang
    {
        static Dictionary<string, string> id = new Dictionary<string, string>();
        static Dictionary<string, string> en = new Dictionary<string, string>();
        static Dictionary<string, string> cur;
        static string mode = null;

        public static void Load()
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang.txt");
                if (File.Exists(p))
                {
                    string v = File.ReadAllText(p).Trim().ToLowerInvariant();
                    if (v == "en") mode = "en";
                    else if (v == "id") mode = "id";
                }
            }
            catch { }
            if (mode == null) mode = "id";
            cur = mode == "en" ? en : id;
        }

        /// Bahasa aktif ("id"/"en").
        public static string Mode { get { return mode == null ? "id" : mode; } }

        /// Ganti bahasa lalu simpan lang.txt. Pemanggil harus membangun ulang UI.
        public static void Set(string m)
        {
            mode = (m == "en") ? "en" : "id";
            cur = mode == "en" ? en : id;
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang.txt"), mode); } catch { }
        }

        public static string T(string k)
        {
            string v;
            if (cur != null && cur.TryGetValue(k, out v)) return v;
            return k;
        }

        static void P(string k, string i, string e) { id[k] = i; en[k] = e; }

        static Lang()
        {
            // ---- judul & kerangka ----
            P("hdr.sub", "Pemantau native Windows 11 \u00B7 pemakaian internet \u00B7 baterai \u00B7 cuaca \u00B7 sistem",
                        "Native Windows 11 monitor \u00B7 internet usage \u00B7 battery \u00B7 weather \u00B7 system");
            P("tray.hint", Lang.T("tray.hint"),
                        "Runs in the notification area. Click the icon for a summary.");
            P("card.batt", "\u26A1 BATERAI", "\u26A1 BATTERY");
            P("card.wx", "\U0001F327 CUACA", "\U0001F327 WEATHER");
            P("card.net", "\U0001F310 INTERNET \u2014 PEMAKAIAN REAL-TIME", "\U0001F310 INTERNET \u2014 REAL-TIME USAGE");
            P("card.sys", "\U0001F4BB SISTEM", "\U0001F4BB SYSTEM");
            P("card.stor", "\U0001F4BE PENYIMPANAN", "\U0001F4BE STORAGE");
            P("card.gpu", "\U0001F3AE GPU \u00B7 NPU \u00B7 CPU", "\U0001F3AE GPU \u00B7 NPU \u00B7 CPU");
            P("card.temp", "\U0001F321 SUHU", "\U0001F321 TEMPERATURE");
            // ---- menu (tray + widget) ----
            P("menu.full", "Dashboard penuh", "Full dashboard");
            P("menu.bar", "Mode bar (di atas taskbar)", "Bar mode (above the taskbar)");
            P("menu.taskbar", "Mode taskbar (di dalam taskbar)", "Taskbar mode (inside the taskbar)");
            P("menu.exit", "Keluar", "Exit");
            P("menu.ontop", "Selalu di atas", "Always on top");
            P("menu.open", "Buka dashboard", "Open dashboard");
            P("menu.close", "Tutup", "Close");
            P("menu.lang.toen", "Switch to English", "Switch to English");
            P("menu.lang.toid", "Ganti ke Bahasa Indonesia", "Ganti ke Bahasa Indonesia");
            // ---- baterai ----
            P("batt.remain", "SISA DAYA", "REMAINING");
            P("batt.pnow", "DAYA SAAT INI", "POWER NOW");
            P("batt.charging", "Mengisi daya (AC tersambung)", "Charging (AC connected)");
            P("batt.full", "Penuh (AC tersambung)", "Full (AC connected)");
            P("batt.ac", "AC tersambung", "AC connected");
            P("batt.ondis", "Menggunakan baterai", "On battery");
            P("batt.ext", "Daya eksternal", "External power");
            P("batt.status", "Status: ", "Status: ");
            P("batt.acdown", "Status: AC tersambung, tapi baterai turun", "Status: AC connected, but battery is draining");
            P("batt.chargerate", "Laju pengisian", "Charging rate");
            P("batt.rate.zero", "Laju: 0 mW (idle / tidak terbaca)", "Rate: 0 mW (idle / unreadable)");
            P("batt.netflow", "Aliran bersih: \u2212{0:0.0} W (EC melapor mengisi {1} mW)", "Net flow: \u2212{0:0.0} W (EC reports charging {1} mW)");
            P("batt.fullin", "Penuh dalam: ", "Full in: ");
            P("batt.eston", "Estimasi menyala: ", "On-battery estimate: ");
            P("batt.err", "Error baterai: ", "Battery error: ");
            // ---- internet ----
            P("net.adapter.init", "Adaptor: \u2026", "Adapter: \u2026");
            P("net.none", "Adaptor: tidak ada jaringan aktif", "Adapter: no active network");
            P("net.adapter", "Adaptor: {0} \u00B7 {1} \u00B7 {2}", "Adapter: {0} \u00B7 {1} \u00B7 {2}");
            P("net.ip.init", "IP publik: \u2026", "Public IP: \u2026");
            P("net.ip", "IP publik: ", "Public IP: ");
            P("net.isp", "ISP: ", "ISP: ");
            P("net.session.init", "Sesi ini: \u2014", "Session: \u2014");
            P("net.session", "Sesi ini: \u2193 {0} \u00B7 \u2191 {1}", "Session: \u2193 {0} \u00B7 \u2191 {1}");
            P("net.peak.init", "Puncak: \u2014", "Peak: \u2014");
            P("net.peak", "Puncak: \u2193 {0} \u00B7 \u2191 {1}", "Peak: \u2193 {0} \u00B7 \u2191 {1}");
            P("net.boot.init", "Sejak dinyalakan: \u2014", "Since boot: \u2014");
            P("net.boot", "Sejak dinyalakan: \u2193 {0} \u00B7 \u2191 {1}", "Since boot: \u2193 {0} \u00B7 \u2191 {1}");
            P("net.graph.hint", "Grafik 3 menit terakhir \u00B7 hijau = unduh, biru = unggah", "Last 3 minutes \u00B7 green = download, blue = upload");
            // ---- cuaca ----
            P("wx.loading", "Memuat lokasi\u2026", "Loading location\u2026");
            P("wx.reload", "\u21BB Muat ulang", "\u21BB Reload");
            P("wx.feels", "Terasa seperti: {0:0}\u00B0", "Feels like: {0:0}\u00B0");
            P("wx.feels.init", "Terasa seperti: \u2014", "Feels like: \u2014");
            P("wx.hum", "Kelembapan: {0:0}%", "Humidity: {0:0}%");
            P("wx.hum.init", "Kelembapan: \u2014", "Humidity: \u2014");
            P("wx.wind", "Angin: {0:0.#} km/j", "Wind: {0:0.#} km/h");
            P("wx.wind.init", "Angin: \u2014", "Wind: \u2014");
            P("wx.today.init", "Hari ini: \u2014", "Today: \u2014");
            P("wx.today", "Hari ini: {0:0}\u00B0 / {1:0}\u00B0", "Today: {0:0}\u00B0 / {1:0}\u00B0");
            P("wx.updated", "Diperbarui: ", "Updated: ");
            P("wx.device", "Perangkat ini", "This device");
            P("wx.unknown", "tidak diketahui", "unknown");
            P("wx.fail", "Gagal: ", "Failed: ");
            P("wx.c1", "Cerah", "Clear sky");
            P("wx.c2", "Berawan sebagian", "Partly cloudy");
            P("wx.c3", "Berkabut", "Foggy");
            P("wx.c4", "Gerimis ringan", "Light drizzle");
            P("wx.c5", "Gerimis membeku", "Freezing drizzle");
            P("wx.c6", "Hujan ringan", "Light rain");
            P("wx.c7", "Hujan membeku", "Freezing rain");
            P("wx.c8", "Salju ringan", "Light snow");
            P("wx.c9", "Butir salju", "Snow grains");
            P("wx.c10", "Hujan lokal ringan", "Rain showers");
            P("wx.c11", "Salju lokal", "Snow showers");
            P("wx.c12", "Badai petir", "Thunderstorm");
            // ---- suhu ----
            P("temp.activate", "\u26A1 Aktifkan (UAC)", "\u26A1 Enable (UAC)");
            P("temp.needadmin", "Suhu lengkap butuh izin Administrator sekali\n(tombol di bawah).", "Full temperatures need one Administrator consent\n(button below).");
            P("temp.needadmin2", "Data suhu lengkap perlu kolektor Administrator\n(klik sekali, berjalan diam di latar).", "Full temperatures need an Administrator collector\n(click once, it runs quietly in the background).");
            P("temp.already", "Kolektor suhu sudah aktif dan menulis data.", "The temperature collector is already active and writing data.");
            P("temp.cancelled", "Batal: ", "Cancelled: ");
            P("temp.started", "Kolektor suhu dimulai; menunggu pengukuran kedua (5 dtk).", "Temperature collector started; waiting for the second reading (5 s).");
            P("temp.stat.init", "Sensor termal: \u2014", "Thermal sensors: \u2014");
            P("temp.stat.sep", " sensor \u00B7 ", " sensors \u00B7 ");
            P("temp.more", " sensor lain", " more sensors");
            P("temp.fan", "Kipas: ", "Fan: ");
            P("temp.cpw", "Daya CPU: ", "CPU power: ");
            // ---- sistem & storage ----
            P("sys.up.init", "Aktif: \u2014", "Up: \u2014");
            P("sys.up", "Aktif: ", "Up: ");
            P("sys.cfree", "C: {0:0} dari {1:0} GB bebas", "C: {0:0} of {1:0} GB free");
            P("stor.io.init", "Aktivitas disk: \u2026", "Disk activity: \u2026");
            P("stor.io", "Aktivitas disk: baca {0} \u00B7 tulis {1}", "Disk activity: read {0} \u00B7 write {1}");
            P("stor.io.none", "Aktivitas disk: \u2014", "Disk activity: \u2014");
            P("stor.drive", "{0} \u2014 {1:0} GB bebas dari {2:0} GB ({3:0}% terpakai)", "{0} \u2014 {1:0} GB free of {2:0} GB ({3:0}% used)");
            // ---- gpu/npu/cpu ---- (label fixed)-

P("gpu.loading", "GPU: memuat\u2026", "GPU: loading\u2026");
            P("gpu.none", "GPU: tidak terdeteksi", "GPU: not detected");
            P("gpu.vram", "VRAM dedikasi {0:0.#} GB", "Dedicated VRAM {0:0.#} GB");
            P("gpu.shared", "memori bersama (adapter {0:0.#} GB \u00B7 sistem {1:0.#} GB)", "shared memory (adapter {0:0.#} GB \u00B7 system {1:0.#} GB)");
            P("gpu.shared2", "memori bersama (adapter {0:0.#} GB)", "shared memory (adapter {0:0.#} GB)");
            P("gpu.sharedsys", "memori bersama sistem", "system shared memory");
            P("fly.batt", "BATERAI", "BATTERY");
            P("fly.sys", "SISTEM", "SYSTEM");
            P("fly.wx", "CUACA", "WEATHER");
            P("fly.temp", "SUHU", "TEMPERATURE");
            P("fly.proc", "PROSES TERATAS", "TOP PROCESSES");
            P("fly.histcap", "Suhu 60 menit terakhir", "Temperature, last 60 min");
            P("menu.history", "Buka riwayat (CSV)", "Open history (CSV)");
            P("proc.cpu", "CPU: {0}", "CPU: {0}");
            P("proc.ram", "RAM: {0}", "RAM: {0}");
            P("alert.temp.title", "Suhu tinggi", "High temperature");
            P("alert.temp.body", "Suhu rata-rata {0:0.0} \u00B0C. Periksa Proses Teratas di tray.", "Average temperature {0:0.0} \u00B0C. Check Top Processes in the tray.");
            P("alert.ram.title", "RAM hampir penuh", "Memory almost full");
            P("alert.ram.body", "RAM terpakai {0:0}%. Tutup aplikasi yang tidak dipakai.", "Memory usage {0:0}%. Close unused apps.");
            P("alert.bat.title", "Baterai lemah", "Battery low");
            P("alert.bat.body", "Baterai {0:0.#}% \u2014 colok charger.", "Battery {0:0.#}% \u2014 plug in the charger.");
            P("batt.health2", "Kesehatan: {0:0.0}% (desain {1} mWh) \u00B7 {2} siklus", "Health: {0:0.0}% (design {1} mWh) \u00B7 {2} cycles");
            P("npu.loading", "NPU: memuat\u2026", "NPU: loading\u2026");
            P("batt.rate.init", "Laju: \u2014", "Rate: \u2014");
            P("batt.volt", "Tegangan: {0:0.00} V", "Voltage: {0:0.00} V");
            P("batt.volt.init", "Tegangan: \u2014", "Voltage: \u2014");
            P("batt.energy", "Energi: {0} / {1} mWh ({2:0.#}%)", "Energy: {0} / {1} mWh ({2:0.#}%)");
            P("batt.energy.init", "Energi: \u2014", "Energy: \u2014");
            P("batt.health", "Kesehatan: {0:0.0}% (desain {1} mWh)", "Health: {0:0.0}% (design {1} mWh)");
            P("batt.health.init", "Kesehatan: \u2014", "Health: \u2014");
            P("batt.est.init", "Estimasi: \u2014", "Est.: \u2014");
            P("batt.use", "Laju pemakaian", "Usage rate");
            P("fly.dash", "\u25B6 dashboard", "\u25B6 dashboard");
            P("npu.none", "NPU: tidak terdeteksi", "NPU: not detected");
            P("wx.reloading", " \u00B7 memuat ulang\u2026", " \u00B7 reloading\u2026");
            P("wx.loading2", "Memuat\u2026", "Loading\u2026");
            P("wx.err.empty", "cuaca kosong", "empty weather");
            P("wx.err.invalid", "data cuaca tidak valid", "invalid weather data");

        }
    }

    static class SharedHttp
    {
        public readonly static HttpClient Client;
        public readonly static HttpClient Plain;
        static SharedHttp()
        {
            HttpClientHandler h = new HttpClientHandler();
            h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            Client = new HttpClient(h);
            Client.Timeout = TimeSpan.FromSeconds(20);

            // klien tanpa gzip (fallback bila dekompresi bermasalah di jalur jaringan)
            HttpClientHandler h2 = new HttpClientHandler();
            h2.AutomaticDecompression = DecompressionMethods.None;
            Plain = new HttpClient(h2);
            Plain.Timeout = TimeSpan.FromSeconds(20);
        }
    }

    static class Util
    {
        static float scale = 0f;

        // faktor penyesuaian tambahan agar jendela desain tidak menghabiskan hampir seluruh
        // layar pada DPI tinggi (mis. 200%); 1.0 = ukuran DPI penuh
        public static Font Fnt(float pt, FontStyle st)
        {
            return new Font("Segoe UI", S(pt) * 4f / 3f, st, GraphicsUnit.Pixel);
        }

        // faktor DPI sistem (1.0 = 96 DPI, 2.0 = 192 DPI); aplikasi DPI-aware lewat app.manifest
        static float primaryScale = 0f;

        // skala untuk sebuah monitor: DPI-nya, dibatasi agar dashboard (980x806 unit desain) tidak menghabiskan layar
        // (DPI tinggi: maks 72% tinggi work area; DPI rendah: 85%)
        public static float ScaleFor(int dpi, System.Drawing.Rectangle wa)
        {
            float sc = dpi / 96f;
            if (sc < 1f) sc = 1f;
            float hf = dpi >= 144 ? 0.72f : 0.85f;
            float cap = Math.Min(wa.Width * 0.85f / 1330f, wa.Height * hf / 806f);
            if (sc > cap) sc = Math.Max(0.55f, cap);
            return sc;
        }

        // skala monitor utama (dipakai flyout dan bar yang selalu di monitor utama)
        public static float PrimaryScale
        {
            get
            {
                if (primaryScale <= 0f)
                {
                    int dpi = 96;
                    try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = (int)g.DpiX; } catch { }
                    try { primaryScale = ScaleFor(dpi, System.Windows.Forms.Screen.PrimaryScreen.WorkingArea); }
                    catch { primaryScale = Math.Max(1f, dpi / 96f); }
                }
                return primaryScale;
            }
        }

        // skala yang sedang berlaku untuk dashboard (berubah saat jendela pindah ke monitor lain)
        public static float Scale
        {
            get { if (scale <= 0f) scale = PrimaryScale; return scale; }
            set { scale = value; }
        }

        public static int S(int v) { return (int)Math.Round(v * Scale); }
        public static float S(float v) { return v * Scale; }

        public static long ToLong(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt64(o); } catch { return 0; }
        }

        // Query WMI dengan pembersihan penuh (searcher + collection + tiap objek).
        // Tanpa ini, polling per-detik membocorkan handle WMI (966 -> 1781 dalam 5 menit).
        static readonly Dictionary<string, ManagementScope> wmiScopes = new Dictionary<string, ManagementScope>();

        static ManagementScope WmiScope(string ns, bool fresh)
        {
            lock (wmiScopes)
            {
                ManagementScope sc;
                if (!fresh && wmiScopes.TryGetValue(ns, out sc)) return sc;
                sc = new ManagementScope(ns);
                sc.Connect();
                wmiScopes[ns] = sc;
                return sc;
            }
        }

        public static List<Dictionary<string, object>> WmiRows(string ns, string query)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { return WmiRowsOnce(WmiScope(ns, attempt > 0), query); }
                catch { if (attempt > 0) break; }
            }
            return new List<Dictionary<string, object>>();
        }

        static List<Dictionary<string, object>> WmiRowsOnce(ManagementScope scope, string query)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery(query)))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementBaseObject o in col)
                    {
                        Dictionary<string, object> row = new Dictionary<string, object>();
                        foreach (PropertyData p in o.Properties)
                        {
                            if (!row.ContainsKey(p.Name)) row[p.Name] = p.Value;
                        }
                        rows.Add(row);
                        o.Dispose();
                    }
                }
            }
            return rows;
        }

        public static object GetDict(Dictionary<string, object> d, string key)
        {
            if (d != null && d.ContainsKey(key)) return d[key];
            return null;
        }

        public static string GetStr(Dictionary<string, object> d, string key)
        {
            object v = GetDict(d, key);
            return v == null ? "" : v.ToString();
        }

        public static double GetNumD(Dictionary<string, object> d, string key)
        {
            object v = GetDict(d, key);
            if (v == null) return double.NaN;
            double r;
            if (double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return double.NaN;
        }

        public static object[] GetArr(Dictionary<string, object> d, string key)
        {
            return GetDict(d, key) as object[];
        }

        public static string FmtDur(TimeSpan t)
        {
            if (t.TotalDays >= 1) return string.Format(CultureInfo.CurrentCulture, "{0:0} h {1:0} j", t.TotalDays, t.Hours);
            if (t.TotalHours >= 1) return string.Format(CultureInfo.CurrentCulture, "{0:0} j {1:00} mnt", t.Hours, t.Minutes);
            return string.Format(CultureInfo.CurrentCulture, "{0:0} mnt", t.TotalMinutes);
        }

        public static string Num(long v)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:N0}", v);
        }

        public static string FmtRate(double bps)
        {
            if (bps < 0) bps = 0;
            if (bps < 1024) return string.Format(CultureInfo.CurrentCulture, "{0:0} B/s", bps);
            if (bps < 1048576) return string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB/s", bps / 1024.0);
            return string.Format(CultureInfo.CurrentCulture, "{0:0.##} MB/s", bps / 1048576.0);
        }

        public static string FmtBytes(double b)
        {
            if (b < 0) b = 0;
            if (b < 1024) return string.Format(CultureInfo.CurrentCulture, "{0:0} B", b);
            if (b < 1048576) return string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", b / 1024.0);
            if (b < 1073741824) return string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", b / 1048576.0);
            return string.Format(CultureInfo.CurrentCulture, "{0:0.##} GB", b / 1073741824.0);
        }
    }

    public class CardPanel : Panel
    {
        public Color BorderColor = Color.FromArgb(72, 78, 100);
        public Color FillColor = Color.FromArgb(44, 47, 62);

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = Rectangle.Inflate(ClientRectangle, -1, -1);
            using (GraphicsPath p = Rounded(r, Util.S(14)))
            {
                using (SolidBrush b = new SolidBrush(FillColor)) g.FillPath(b, p);
                using (Pen pen = new Pen(BorderColor, Util.S(1f))) g.DrawPath(pen, p);
            }
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            int d = rad * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    public class GraphPanel : Panel
    {
        public List<double> HistD = new List<double>();
        public List<double> HistU = new List<double>();

        public GraphPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(32, 34, 48);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen gp = new Pen(Color.FromArgb(48, 53, 72)))
            {
                for (int i = 1; i < 4; i++)
                {
                    int y = ClientSize.Height * i / 4;
                    g.DrawLine(gp, Util.S(2), y, ClientSize.Width - Util.S(2), y);
                }
            }
            double max = 2048;
            foreach (double v in HistD) if (v > max) max = v;
            foreach (double v in HistU) if (v > max) max = v;
            DrawSeries(g, HistU, max, Color.FromArgb(110, 190, 255));
            DrawSeries(g, HistD, max, Color.FromArgb(115, 222, 160));
            using (Pen border = new Pen(Color.FromArgb(60, 66, 88)))
            {
                g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        void DrawSeries(Graphics g, List<double> h, double max, Color c)
        {
            if (h == null || h.Count < 2) return;
            int n = h.Count;
            int pad = Util.S(5);
            int w = ClientSize.Width - 2 * pad;
            int hh = ClientSize.Height - 2 * pad;
            if (w < 10 || hh < 10) return;
            double step = (double)w / (n - 1);
            using (Pen p = new Pen(c, Util.S(1.6f)))
            {
                for (int i = 1; i < n; i++)
                {
                    float x1 = pad + (float)((i - 1) * step);
                    float y1 = pad + hh - (float)(h[i - 1] / max * hh);
                    float x2 = pad + (float)(i * step);
                    float y2 = pad + hh - (float)(h[i] / max * hh);
                    g.DrawLine(p, x1, y1, x2, y2);
                }
            }
        }
    }

    // bar pemakaian drive
    public class BarPanel : Panel
    {
        double val = 0;
        public double Value
        {
            get { return val; }
            set { double v = value; if (v < 0) v = 0; if (v > 1) v = 1; val = v; Invalidate(); }
        }

        public BarPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Rectangle r = new Rectangle(0, Util.S(2), ClientSize.Width, Util.S(10));
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(30, 32, 46))) g.FillRectangle(bg, r);
            Color c = val < 0.7 ? Color.FromArgb(115, 222, 160) : (val < 0.9 ? Color.FromArgb(255, 176, 92) : Color.FromArgb(255, 118, 118));
            int w = (int)(r.Width * val);
            if (w > 1)
            {
                using (SolidBrush f = new SolidBrush(c)) g.FillRectangle(f, r.X, r.Y, w, r.Height);
            }
        }
    }

    public class DriveUi
    {
        public Label L;
        public BarPanel Bar;
        public DriveUi(Label l, BarPanel b) { L = l; Bar = b; }
    }

    // bar ramping nempel di atas taskbar
    /// Grafik mini riwayat suhu untuk flyout (digambar ulang murah; data = referensi list dashboard).
    public class SparkPanel : Panel
    {
        public List<double> Values;
        public Color LineColor = Color.FromArgb(115, 222, 160);

        public SparkPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(20, 22, 32);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Width - 2, Height - 2);
            using (Pen b = new Pen(Color.FromArgb(64, 70, 94), 1f)) g.DrawRectangle(b, r);
            if (Values == null || Values.Count < 2) return;
            double mn = double.MaxValue, mx = double.MinValue;
            foreach (double v in Values)
            {
                if (double.IsNaN(v)) continue;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            if (mn == double.MaxValue) return;
            if (mx - mn < 6.0) { double c = (mn + mx) / 2.0; mn = c - 3; mx = c + 3; }
            float pad = 3f;
            float w = Width - 2 * pad - 2, h = Height - 2 * pad - 2;
            PointF[] pts = new PointF[Values.Count];
            int k = 0;
            for (int i = 0; i < Values.Count; i++)
            {
                double v = Values[i];
                if (double.IsNaN(v)) v = mx;
                float x = pad + (Values.Count == 1 ? 0 : (float)i * w / (Values.Count - 1));
                float y = pad + h - (float)((v - mn) / (mx - mn)) * h;
                pts[k++] = new PointF(x + 1, y + 1);
            }
            using (Pen p = new Pen(LineColor, 1.6f)) g.DrawLines(p, pts);
        }
    }

    public class SlimBar : Form
    {
        public Label LClock, LBatt, LTemp, LNet, LSys, LWx, LDisk, LOpen;
        public Action OpenFull;
        public Action ExitApp;
        ContextMenuStrip menu;
        ToolStripMenuItem miTop;

        public SlimBar()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 34);
            Size = new Size(Util.S(1400), Util.S(46));
            Font = new Font("Segoe UI", Util.S(9f) * 4f / 3f, FontStyle.Bold, GraphicsUnit.Pixel);
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;

            LClock = Mk(10, 100, Color.FromArgb(110, 190, 255));
            LBatt = Mk(118, 170, Color.FromArgb(115, 222, 160));
            LTemp = Mk(294, 120, Color.FromArgb(115, 222, 160));
            LNet = Mk(420, 240, Color.White);
            LSys = Mk(666, 220, Color.FromArgb(168, 174, 196));
            LWx = Mk(892, 240, Color.FromArgb(110, 190, 255));
            LDisk = Mk(1138, 130, Color.FromArgb(168, 174, 196));
            LOpen = Mk(0, 110, Color.FromArgb(110, 190, 255));
            LOpen.Text = Lang.T("fly.dash");
            LOpen.Cursor = Cursors.Hand;
            LOpen.Click += delegate { if (OpenFull != null) OpenFull(); };

            menu = new ContextMenuStrip();
            ToolStripMenuItem miFull = new ToolStripMenuItem(Lang.T("menu.full"));
            miFull.Click += delegate { if (OpenFull != null) OpenFull(); };
            miTop = new ToolStripMenuItem(Lang.T("menu.ontop"));
            miTop.Checked = true;
            miTop.CheckOnClick = true;
            miTop.Click += delegate { TopMost = miTop.Checked; };
            ToolStripMenuItem miExit = new ToolStripMenuItem(Lang.T("menu.exit"));
            miExit.Click += delegate { if (ExitApp != null) ExitApp(); };
            menu.Items.Add(miFull);
            menu.Items.Add(miTop);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            ContextMenuStrip = menu;

            MouseDoubleClick += delegate { if (OpenFull != null) OpenFull(); };
        }

        Label Mk(int x, int w, Color c)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Size = new Size(Util.S(w), Util.S(26));
            l.Location = new Point(Util.S(x), Util.S(10));
            l.ForeColor = c;
            l.BackColor = Color.Transparent;
            l.Font = new Font("Segoe UI", Util.S(9f) * 4f / 3f, FontStyle.Bold, GraphicsUnit.Pixel);
            Controls.Add(l);
            return l;
        }

        public void EnsureDocked()
        {
            try
            {
                Rectangle r = Screen.PrimaryScreen.WorkingArea;
                if (Width != r.Width) Width = r.Width;
                Location = new Point(r.X, r.Bottom - Height);
                LOpen.Left = Math.Max(0, Width - Util.S(120));
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Color.FromArgb(96, 165, 250), Util.S(2f))) e.Graphics.DrawLine(p, 0, 0, ClientSize.Width, 0);
        }
    }

    // Win32 API bersama untuk widget taskbar
    static class TWin32
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UPDATELAYEREDWINDOWINFO info);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int w, int h, uint flags);

        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        // layout native UPDATELAYEREDWINDOWINFO (x64: 80 byte)
        [StructLayout(LayoutKind.Sequential)]
        public struct UPDATELAYEREDWINDOWINFO
        {
            public int cbSize;
            public IntPtr hdcDst;
            public IntPtr pptDst;
            public IntPtr psize;
            public IntPtr hdcSrc;
            public IntPtr pptSrc;
            public int crKey;
            public IntPtr pblend;
            public int dwFlags;
            public IntPtr prcDirty;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);
    }

    // widget yang ditempel DI DALAM taskbar Windows 11.
    // Teknik (dipelajari dari TrafficMonitor, ditulis ulang sendiri): jendela anak Shell_TrayWnd + WS_EX_LAYERED
    // dengan alpha per-piksel (UpdateLayeredWindowIndirect) -> di-composite DWM DI ATAS konten XAML taskbar.
    // SYARAT: app.manifest harus menyatakan kompatibilitas Windows 8+; tanpa itu API layered untuk jendela anak
    // ditolak dengan error 87 (penyebab widget versi sebelumnya tidak pernah tampil).
    // Latar widget transparan penuh, teks 2 baris: kiri = unduh/unggah, kanan = baterai % / watt.
    public class TaskbarWidget : Form
    {
        public string TxtDown = "↓ 0 B/s";
        public string TxtUp = "↑ 0 B/s";
        public string TxtPct = "";
        public string TxtWatt = "";
        public string TxtTemp = "";          // suhu rata-rata kartu SUHU (kosong = tidak ada data)
        public string TxtCpu = "";           // "CPU 12%" (baris ke-2 kolom suhu)
        public int CpuState = 0;             // 0 biasa, 1 sibuk, 2 berat
        public Color ColTempOverride = Color.Empty;
        public int BattState = 0;            // 0 = biasa, 1 = mengisi, 2 = rendah
        public Action ClickFlyout;
        public Action OpenFull;
        public Action ExitRequested;
        public int FailCount = 0;            // gagal render berturut-turut

        IntPtr taskbarHwnd = IntPtr.Zero;
        uint ownPid;
        bool light = false;
        DateTime themeAt = DateTime.MinValue;
        int dpi = 96;
        Font font;
        float fontEm = 0f;
        Bitmap measBmp = new Bitmap(1, 1);
        Graphics measG;
        StringFormat sf;
        string lastSig = "";
        int tickN = 0;
        int lastX = -1, lastW = -1, lastH = -1;

        public TaskbarWidget()
        {
            ownPid = (uint)Process.GetCurrentProcess().Id;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(200, 60);
            Cursor = Cursors.Hand;
            MouseClick += delegate(object s, MouseEventArgs e)
            {
                Dbg.Log("widget MouseClick " + e.Button);
                if (e.Button == MouseButtons.Left && ClickFlyout != null) ClickFlyout();
            };
            MouseDown += delegate(object s, MouseEventArgs e) { Dbg.Log("widget MouseDown " + e.Button); };
            MouseUp += delegate(object s, MouseEventArgs e) { Dbg.Log("widget MouseUp " + e.Button); };
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem miFull = new ToolStripMenuItem(Lang.T("menu.full"));
            miFull.Click += delegate { if (OpenFull != null) OpenFull(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem(Lang.T("menu.exit"));
            miExit.Click += delegate { if (ExitRequested != null) ExitRequested(); };
            menu.Items.Add(miFull);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            ContextMenuStrip = menu;

            sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            measG = Graphics.FromImage(measBmp);
            measG.PageUnit = GraphicsUnit.Pixel;
            measG.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (font != null) font.Dispose();
                if (measG != null) measG.Dispose();
                if (measBmp != null) measBmp.Dispose();
                if (sf != null) sf.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- warna (menyesuaikan tema gelap/terang taskbar) ----------
        Color ColText { get { return light ? Color.FromArgb(28, 28, 28) : Color.FromArgb(245, 245, 245); } }
        Color ColDown { get { return light ? Color.FromArgb(13, 122, 62) : Color.FromArgb(118, 228, 166); } }
        Color ColUp { get { return light ? Color.FromArgb(22, 86, 196) : Color.FromArgb(124, 192, 255); } }
        Color ColGood { get { return light ? Color.FromArgb(13, 122, 62) : Color.FromArgb(118, 228, 166); } }
        Color ColBad { get { return light ? Color.FromArgb(186, 28, 28) : Color.FromArgb(255, 128, 128); } }
        Color ColWarm { get { return light ? Color.FromArgb(168, 92, 0) : Color.FromArgb(255, 200, 100); } }
        Color ColPct { get { return BattState == 2 ? ColBad : (BattState == 1 ? ColGood : ColText); } }
        Color ColCpu { get { return CpuState == 2 ? ColBad : (CpuState == 1 ? ColWarm : ColText); } }
        Color ColWatt { get { return BattState == 2 ? ColBad : (BattState == 1 ? ColGood : ColWarm); } }

        float Measure(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            return measG.MeasureString(s, font, PointF.Empty, sf).Width;
        }

        void EnsureFont(float em)
        {
            if (font != null && Math.Abs(fontEm - em) < 0.25f) return;
            Font old = font;
            font = new Font("Segoe UI Semibold", em, FontStyle.Regular, GraphicsUnit.Pixel);
            fontEm = em;
            if (old != null) old.Dispose();
        }

        void UpdateTheme()
        {
            if ((DateTime.Now - themeAt).TotalSeconds < 5) return;
            themeAt = DateTime.Now;
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
                light = v != null && Convert.ToInt32(v) == 1;
            }
            catch { }
        }

        void UpdateDpi(IntPtr tray)
        {
            try
            {
                uint d = TWin32.GetDpiForWindow(tray);
                if (d >= 96 && d <= 480) { dpi = (int)d; return; }
            }
            catch { }
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = (int)g.DpiX; }
            catch { }
        }

        public bool IsEmbeddedIn(IntPtr tray)
        {
            return IsHandleCreated && !IsDisposed && TWin32.GetParent(Handle) == tray;
        }

        public void EmbedInto(IntPtr tray)
        {
            taskbarHwnd = tray;
            // WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS
            TWin32.SetWindowLongPtr(Handle, -16, (IntPtr)(0x40000000 | 0x10000000 | 0x04000000));
            TWin32.SetParent(Handle, tray);
            // WS_EX_LAYERED (composite DWM di atas XAML taskbar), tanpa WS_EX_APPWINDOW
            IntPtr ex = TWin32.GetWindowLongPtr(Handle, -20);
            TWin32.SetWindowLongPtr(Handle, -20, (IntPtr)((ex.ToInt64() | 0x00080000L) & ~0x00040000L));
            // HWND_TOP + FRAMECHANGED, tanpa pindah/ubah ukuran/aktivasi
            TWin32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020);
            lastSig = "";
            RefreshWidget(tray);
        }

        // dipanggil tiap detik: ukur, posisikan (hindari widget pihak ketiga), lalu gambar bila ada perubahan
        public void RefreshWidget(IntPtr tray)
        {
            if (!IsHandleCreated || IsDisposed) { if (tickN % 10 == 0) Dbg.Log("Refresh skip: no handle"); return; }
            taskbarHwnd = tray;
            TWin32.RECT tb;
            if (!TWin32.GetWindowRect(tray, out tb)) { if (tickN % 10 == 0) Dbg.Log("Refresh skip: GetWindowRect gagal"); return; }
            int tbH = tb.Bottom - tb.Top;
            int tbW = tb.Right - tb.Left;
            if (tbH < 20 || tbW < 300) { if (tickN % 10 == 0) Dbg.Log("Refresh skip: taskbar kecil " + tbH + "x" + tbW); return; }
            tickN++;

            UpdateTheme();
            UpdateDpi(tray);
            float s = dpi / 96f;

            // tinggi widget = tinggi taskbar dikurangi margin atas/bawah (seperti TrafficMonitor)
            int margin = (int)Math.Round(tbH / 12.0);
            int h = tbH - 2 * margin;
            if (h < 20) { margin = 0; h = tbH; }
            float em = Math.Min(12f * s, (h - 2f) / 2.75f);   // 12 px logis = Segoe UI 9 pt
            if (em < 9f) em = 9f;
            EnsureFont(em);

            float lineH = font.GetHeight(measG);
            float padL = em * 0.45f, padR = em * 0.45f, colGap = em * 1.0f;
            // LEBAR SELALU DARI TEMPLAT TETAS — bukan teks aktual — supaya widget tidak pernah loncat
            float w1 = Math.Max(Measure("\u2193 888,8 KB/s"), Measure("\u2193 88,88 MB/s"));
            float w2 = Math.Max(Measure("100,0%"), Measure("+100,0 W"));
            bool hasTemp = TxtTemp != "";
            bool hasBatt = TxtPct != "" || TxtWatt != "";
            bool hasCpu = TxtCpu != "";
            bool hasT = hasTemp || hasCpu;
            float wT = hasT ? Math.Max(Math.Max(Measure("148,88°C"), Measure("CPU 188%")), Measure(Lang.T("card.temp"))) : 0f;   // templat tetap juga
            int w = (int)Math.Ceiling(padL + w1 + colGap + w2 + (hasT ? colGap + wT : 0f) + padR);

            int x = ComputeX(tb, tbH, w, s);
            int y = margin;

            if ((tickN % 5) == 1)
                TWin32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // tetap paling atas

            string sig = TxtDown + "|" + TxtUp + "|" + TxtPct + "|" + TxtWatt + "|" + TxtTemp + "|" + TxtCpu + "|" + CpuState + "|" + BattState + "|" + light
                + "|" + (ColTempOverride.ToArgb()) + "|" + w + "x" + h + "@" + x + "," + y + "|" + em.ToString("0.0", CultureInfo.InvariantCulture);
            if (sig == lastSig && (tickN % 30) != 0) return;
            lastSig = sig;

            TWin32.MoveWindow(Handle, x, y, w, h, false);
            if (x != lastX || w != lastW || h != lastH)
            {
                Dbg.Log("widget move x=" + x + " w=" + w + " h=" + h + " (lama x=" + lastX + " w=" + lastW + " h=" + lastH + ")");
                lastX = x; lastW = w; lastH = h;
            }
            DrawAndPush(w, h, em, padL, w1, w2, colGap, lineH, hasBatt, hasTemp, hasCpu, wT);
        }

        // posisi X (relatif ke taskbar): di kiri area notifikasi, bergeser ke kiri bila ada jendela pihak ketiga (mis. TrafficMonitor)
        int cachedRight = -1;
        DateTime rightAt = DateTime.MinValue;
        List<int[]> spansCache = new List<int[]>();

        int ComputeX(TWin32.RECT tb, int tbH, int w, float s)
        {
            // tepi kanan & spans di-cache 10 detik agar posisi tidak ikut fluktuasi sesaat area tray
            if (cachedRight < 0 || (DateTime.Now - rightAt).TotalSeconds > 10)
            {
                int tbW0 = tb.Right - tb.Left;
                int r0;
                IntPtr notify = TWin32.FindWindowEx(taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
                TWin32.RECT nr;
                if (notify != IntPtr.Zero && TWin32.GetWindowRect(notify, out nr)) r0 = nr.Left;
                else r0 = tb.Right - (int)(tbH * 3.2);

                int gap0 = (int)Math.Round(6 * s);
                spansCache = ForeignSpans(taskbarHwnd, tb.Top, tb.Bottom, tbW0);
                if (r0 != cachedRight) Dbg.Log("widget tepi-kanan " + cachedRight + " -> " + r0);
                cachedRight = r0;
                rightAt = DateTime.Now;
            }
            int right = cachedRight;

            int gap = (int)Math.Round(6 * s);
            List<int[]> spans = spansCache;
            for (int it = 0; it < 8; it++)
            {
                bool moved = false;
                foreach (int[] sp in spans)
                {
                    if (sp[0] < right && sp[1] > right - w) { right = sp[0] - gap; moved = true; }
                }
                if (!moved) break;
            }
            int x = right - w - tb.Left;
            if (x < 0) x = 0;
            return x;
        }

        // rentang horizontal (koordinat layar) jendela milik proses lain di dalam taskbar
        List<int[]> ForeignSpans(IntPtr tray, int tbTop, int tbBottom, int tbW)
        {
            List<int[]> res = new List<int[]>();
            uint tbPid;
            TWin32.GetWindowThreadProcessId(tray, out tbPid);
            uint me = ownPid;
            TWin32.EnumProc cb = delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                TWin32.GetWindowThreadProcessId(h, out pid);
                if (pid == me || pid == tbPid) return true;
                if (!TWin32.IsWindowVisible(h)) return true;
                TWin32.RECT r;
                if (!TWin32.GetWindowRect(h, out r)) return true;
                int rw = r.Right - r.Left, rh = r.Bottom - r.Top;
                if (rw < 8 || rh < 8 || rw > tbW / 2) return true;
                if (r.Bottom <= tbTop || r.Top >= tbBottom) return true;
                res.Add(new int[] { r.Left, r.Right });
                return true;
            };
            TWin32.EnumChildWindows(tray, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return res;
        }

        void DrawAndPush(int w, int h, float em, float padL, float w1, float w2, float colGap, float lineH, bool hasBatt, bool hasTemp, bool hasCpu, float wT)
        {
            bool ok = false;
            using (Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(1, 0, 0, 0));      // alpha 1: tak terlihat, tapi area tetap bisa diklik
                    g.PageUnit = GraphicsUnit.Pixel;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    float gap = em * 0.18f;
                    float y1 = (h - (2f * lineH + gap)) / 2f - em * 0.12f;   // koreksi: descender bikin teks tampak rendah
                    float y2 = y1 + lineH + gap;
                    DrawTxt(g, TxtDown, ColDown, padL, y1);
                    DrawTxt(g, TxtUp, ColUp, padL, y2);
                    if (hasBatt)
                    {
                        float x2 = padL + w1 + colGap;
                        DrawTxt(g, TxtPct, ColPct, x2, y1);
                        DrawTxt(g, TxtWatt, ColWatt, x2, y2);
                    }
                    if (hasTemp || hasCpu)
                    {
                        float x3 = padL + w1 + colGap + w2 + colGap;
                        if (hasTemp)
                        {
                            Color ct = ColTempOverride != Color.Empty ? ColTempOverride : ColWarm;
                            DrawTxt(g, TxtTemp, ct, x3, y1);
                        }
                        if (hasCpu) DrawTxt(g, TxtCpu, ColCpu, x3, y2);
                    }
                }
                ok = PushBitmap(bmp, w, h);
            }
            if (ok) FailCount = 0; else FailCount++;
        }

        void DrawTxt(Graphics g, string t, Color c, float x, float y)
        {
            if (string.IsNullOrEmpty(t)) return;
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(t, font, b, x, y, sf);
        }

        // kirim bitmap 32-bit premultiplied ke jendela layered
        bool PushBitmap(Bitmap bmp, int w, int h)
        {
            IntPtr screenDc = TWin32.GetDC(IntPtr.Zero);
            IntPtr memDc = TWin32.CreateCompatibleDC(screenDc);
            // PENTING: GetHbitmap() tanpa argumen mengomposit alpha ke latar LightGray (kotak abu-abu);
            // Color.FromArgb(0) mempertahankan alpha asli.
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = TWin32.SelectObject(memDc, hBmp);
            IntPtr pSize = Marshal.AllocHGlobal(8);
            IntPtr pSrc = Marshal.AllocHGlobal(8);
            IntPtr pBlend = Marshal.AllocHGlobal(4);
            bool ok = false;
            try
            {
                Marshal.WriteInt32(pSize, 0, w); Marshal.WriteInt32(pSize, 4, h);
                Marshal.WriteInt32(pSrc, 0, 0); Marshal.WriteInt32(pSrc, 4, 0);
                Marshal.WriteByte(pBlend, 0, 0);        // AC_SRC_OVER
                Marshal.WriteByte(pBlend, 1, 0);
                Marshal.WriteByte(pBlend, 2, 255);      // SourceConstantAlpha
                Marshal.WriteByte(pBlend, 3, 1);        // AC_SRC_ALPHA
                TWin32.UPDATELAYEREDWINDOWINFO info = new TWin32.UPDATELAYEREDWINDOWINFO();
                info.cbSize = Marshal.SizeOf(typeof(TWin32.UPDATELAYEREDWINDOWINFO));
                info.hdcDst = IntPtr.Zero;              // NULL (persis TrafficMonitor)
                info.pptDst = IntPtr.Zero;              // NULL: posisi diatur lewat MoveWindow
                info.psize = pSize;
                info.hdcSrc = memDc;
                info.pptSrc = pSrc;
                info.crKey = 0;
                info.pblend = pBlend;
                info.dwFlags = 2;                       // ULW_ALPHA
                info.prcDirty = IntPtr.Zero;
                ok = TWin32.UpdateLayeredWindowIndirect(Handle, ref info);
            }
            catch { ok = false; }
            finally
            {
                Marshal.FreeHGlobal(pSize); Marshal.FreeHGlobal(pSrc); Marshal.FreeHGlobal(pBlend);
                TWin32.SelectObject(memDc, old);
                TWin32.DeleteObject(hBmp);
                TWin32.DeleteDC(memDc);
                TWin32.ReleaseDC(IntPtr.Zero, screenDc);
            }
            return ok;
        }

        // seluruh tampilan digambar lewat DrawAndPush (layered); jangan biarkan WinForms mengecat latar
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
    }

    // panel ringkasan yang muncul dari tray / widget taskbar (dekat jam). Semua ukuran lewat Util.S (DPI sistem).
    public class FlyoutPanel : Form
    {
        public Label LTitle, LBatt, LBatt2, LBatt3, LNet, LNet2, LSys, LSys2, LWx, LAd, LTemp, LTemp2, LProc1, LProc2;
        public SparkPanel Spark;
        public Button BtnFull;
        public Action OpenFull;

        Label Mk(string t, int x, int y, int w, int h, Color c, float sz, FontStyle st)
        {
            Label l = new Label();
            l.Text = t;
            l.AutoSize = false;
            l.Size = new Size(Util.S(w), Util.S(h));
            l.Location = new Point(Util.S(x), Util.S(y));
            l.ForeColor = c;
            l.BackColor = Color.Transparent;
            l.Font = new Font("Segoe UI", Util.S(sz) * 4f / 3f, st, GraphicsUnit.Pixel);
            Controls.Add(l);
            return l;
        }

        void StyleBtn(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(96, 165, 250);
            b.ForeColor = Color.FromArgb(14, 18, 32);
            b.Font = new Font("Segoe UI", Util.S(9.5f) * 4f / 3f, FontStyle.Bold, GraphicsUnit.Pixel);
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(130, 185, 255);
        }

        public FlyoutPanel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Util.S(340), Util.S(600));
            BackColor = Color.FromArgb(28, 30, 42);
            DoubleBuffered = true;

            Color accent = Color.FromArgb(110, 190, 255);
            Color dim = Color.FromArgb(160, 166, 190);
            Color sub = Color.FromArgb(188, 194, 214);

            Mk("RESOURCE MONITORING", 16, 12, 300, 18, accent, 9.5f, FontStyle.Bold);
            LTitle = Mk("", 16, 32, 308, 18, dim, 9f, FontStyle.Regular);
            Mk(Lang.T("fly.batt"), 16, 60, 200, 16, dim, 8.5f, FontStyle.Bold);
            LBatt = Mk("", 16, 76, 308, 36, Color.White, 19f, FontStyle.Bold);
            LBatt2 = Mk("", 16, 114, 308, 18, sub, 9.5f, FontStyle.Regular);
            LBatt3 = Mk("", 16, 134, 308, 18, Color.FromArgb(115, 222, 160), 9.5f, FontStyle.Bold);
            Mk("INTERNET", 16, 162, 200, 16, dim, 8.5f, FontStyle.Bold);
            LNet = Mk("", 16, 178, 308, 24, Color.White, 11f, FontStyle.Bold);
            LNet2 = Mk("", 16, 202, 308, 18, sub, 9f, FontStyle.Regular);
            Mk(Lang.T("fly.sys"), 16, 230, 200, 16, dim, 8.5f, FontStyle.Bold);
            LSys = Mk("", 16, 246, 308, 22, Color.White, 10f, FontStyle.Bold);
            LSys2 = Mk("", 16, 268, 308, 18, sub, 9f, FontStyle.Regular);
            Mk(Lang.T("fly.wx"), 16, 296, 200, 16, dim, 8.5f, FontStyle.Bold);
            LWx = Mk("", 16, 312, 308, 38, accent, 10.5f, FontStyle.Bold);
            Mk(Lang.T("fly.temp"), 16, 356, 200, 16, dim, 8.5f, FontStyle.Bold);
            LTemp = Mk("", 16, 372, 308, 24, Color.White, 12f, FontStyle.Bold);
            LTemp2 = Mk("", 16, 398, 308, 18, sub, 9f, FontStyle.Regular);
            Mk(Lang.T("fly.histcap"), 16, 404, 200, 16, dim, 8.5f, FontStyle.Regular);
            Spark = new SparkPanel();
            Spark.Location = new Point(Util.S(16), Util.S(420));
            Spark.Size = new Size(Util.S(308), Util.S(40));
            Controls.Add(Spark);
            Mk(Lang.T("fly.proc"), 16, 468, 200, 16, dim, 8.5f, FontStyle.Bold);
            LProc1 = Mk("", 16, 484, 308, 18, sub, 9f, FontStyle.Regular);
            LProc2 = Mk("", 16, 504, 308, 18, sub, 9f, FontStyle.Regular);
            LAd = Mk("", 16, 526, 308, 18, dim, 8.5f, FontStyle.Regular);

            BtnFull = new Button();
            BtnFull.Text = Lang.T("menu.open");
            BtnFull.Location = new Point(Util.S(16), Util.S(552));
            BtnFull.Size = new Size(Util.S(150), Util.S(30));
            StyleBtn(BtnFull);
            BtnFull.Click += delegate { Hide(); if (OpenFull != null) OpenFull(); };
            Controls.Add(BtnFull);

            Button bClose = new Button();
            bClose.Text = Lang.T("menu.close");
            bClose.Location = new Point(Util.S(174), Util.S(552));
            bClose.Size = new Size(Util.S(150), Util.S(30));
            StyleBtn(bClose);
            bClose.Click += delegate { Hide(); };
            Controls.Add(bClose);

            Deactivate += delegate { Dbg.Log("flyout Deactivate -> Hide"); Hide(); };
            VisibleChanged += delegate { Dbg.Log("flyout Visible=" + Visible); };
            Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Color.FromArgb(64, 70, 94), Util.S(1f)))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                }
            };
        }
    }

    public class DashboardForm : Form
    {
        // ---------- palet ----------
        static readonly Color CText = Color.White;
        static readonly Color CSub = Color.FromArgb(188, 194, 214);
        static readonly Color CDim = Color.FromArgb(160, 166, 190);
        static readonly Color CAccent = Color.FromArgb(110, 190, 255);
        static readonly Color CGood = Color.FromArgb(115, 222, 160);
        static readonly Color CWarn = Color.FromArgb(255, 176, 92);
        static readonly Color CBad = Color.FromArgb(255, 118, 118);

        // ---------- label ----------
        Label lblClock;
        Label lblBattPct, lblBattState, lblBattWatt, lblBattRate, lblBattVolt, lblBattCap, lblBattHealth, lblBattEst;
        Label lblWxCity, lblWxTemp, lblWxDesc, lblWxFeels, lblWxHum, lblWxWind, lblWxMinMax, lblWxUpdated;
        Label lblAdapter, lblNetDown, lblNetUp, lblNetSesi, lblNetPuncak, lblNetBoot, lblIp, lblIsp, lblNetHint;
        Label lblCpu, lblRam, lblUptime, lblDisk, lblPc, lblOs;
        Label lblGpu1, lblGpu1d, lblGpu2, lblGpu2d, lblNpu, lblNpuD, lblCpuInfo;
        Label lblDiskIo;
        Button btnWx;
        GraphPanel netGraph;
        // kartu SUHU (rata-rata semua sensor + daftar sensor)
        Label lblTempAvg, lblTempStat, lblTempList, lblTempAux;
        Button btnTemp;
        long tempEpoch = 0;                  // epoch terakhir temps.json yang sudah tampak di layar
        DateTime tempSvcRetry = DateTime.MinValue;   // percobaan schtasks /Run terakhir (antara 60 dtk)
        List<DriveUi> driveUis = new List<DriveUi>();
        bool diskPerfOk = true;

        System.Windows.Forms.Timer uiTimer;
        Dictionary<int, string> weatherCodes;
        int tick = 0;
        bool cpuPrimed = false;
        ulong prevIdle = 0, prevTotal = 0;
        string osTextCache = null;
        long designFromReport = -1;
        long cycleCount = -1;

        // cuaca, bar, cache
        DateTime nextWxRetry = DateTime.Now;
        bool wxBusy = false;
        string lastWxShort = "\u2014";
        string battPctShort = "\u2014", battWShort = "0,0";
        bool battCharging = false;
        double battPctVal = 100;
        string netShort = "\u2193 0 B/s \u2191 0 B/s";
        string sysShort = "CPU \u2014%";
        // widget taskbar: teks suhu rata-rata (kosong bila kolektor belum aktif)
        string tempShort = "";
        double tempAvgVal = 0;
        double cpuPctVal = 0;
        // proses teratas (CPU/RAM) — sampling delta ~3 detik
        string procCpuRaw = "", procRamRaw = "", topCpuShort = "";
        Dictionary<string, double> procPrevMs = new Dictionary<string, double>();
        DateTime procPrevAt = DateTime.MinValue;
        // peringatan balloon (jeda agar tidak spam)
        DateTime alertTempAt = DateTime.MinValue, alertRamAt = DateTime.MinValue, alertBatAt = DateTime.MinValue;
        // riwayat: ring buffer suhu (60 mnt @30 dtk) + nilai utk CSV
        List<double> tempHist = new List<double>();
        double tempMinVal = 0, tempMaxVal = 0, ramPctVal = 0, battWNum = 0;
        string diskShort = "C: \u2014";
        SlimBar bar;
        HashSet<string> physCache = new HashSet<string>();
        DateTime physCacheAt = DateTime.MinValue;

        // tray
        NotifyIcon trayIcon;
        string lastTrayKey = "";
        bool allowExit = false;
        bool balloonShown = false;
        FlyoutPanel flyout;

        // taskbar
        TaskbarWidget tbWidget;
        bool taskbarMode = false;
        int widgetRetryLogged = 0;

        // ---------- state jaringan ----------
        double prevIn = -1, prevOut = -1;
        double emaD = 0, emaU = 0, peakD = 0, peakU = 0, sessIn = 0, sessOut = 0;
        Stopwatch netSw = new Stopwatch();
        const int HistMax = 180;

        // ---------- WinAPI ----------
        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll")]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string cls, string title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        [DllImport("user32.dll")]
        static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

        [DllImport("user32.dll")]
        static extern IntPtr GetParent(IntPtr hWnd);

        static ulong FtToUlong(FILETIME f) { return ((ulong)f.dwHighDateTime << 32) | f.dwLowDateTime; }

        public DashboardForm()
        {
            Text = "Resource Monitoring — Windows 11";
            ClientSize = new Size(Util.S(1330), Util.S(806));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 34);
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;   // semua ukuran diskalakan eksplisit lewat Util.S (DPI sistem)
            Font = new Font("Segoe UI", Util.S(9.75f) * 4f / 3f, FontStyle.Regular, GraphicsUnit.Pixel);

            BuildWeatherCodes();
            BuildUi();
            CreateTrayIcon();
            LoadDesignCapacityAsync();

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 1000;
            uiTimer.Tick += OnUiTick;
            uiTimer.Start();

            Shown += delegate { FitToWorkArea(); };

            // mulai tersembunyi pada mode tray/bar/taskbar (kecuali ada argumen /show)
            string mode0 = "full";
            try { if (File.Exists(ModeFile)) mode0 = File.ReadAllText(ModeFile).Trim(); } catch { }
            bool wantShow = false;
            foreach (string a0 in Environment.GetCommandLineArgs()) if (a0 == "/show") wantShow = true;
            startHidden = (mode0 == "bar" || mode0 == "tray" || mode0 == "taskbar") && !wantShow;

            Load += delegate { StartupInit(); };

            FormClosing += delegate(object s2, FormClosingEventArgs e2)
            {
                if (!allowExit && e2.CloseReason == CloseReason.UserClosing)
                {
                    e2.Cancel = true;
                    Hide();
                    if (!balloonShown && trayIcon != null)
                    {
                        balloonShown = true;
                        try
                        {
                            trayIcon.BalloonTipTitle = "Resource Monitoring";
                            trayIcon.BalloonTipText = Lang.T("tray.hint");
                            trayIcon.ShowBalloonTip(3000);
                        }
                        catch { }
                    }
                }
            };

            FormClosed += delegate
            {
                CloseTaskbarWidget();
                if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            };
        }

        void BuildWeatherCodes()
        {
            weatherCodes = new Dictionary<int, string>();
            weatherCodes.Add(0, Lang.T("wx.c1")); weatherCodes.Add(1, "Umumnya cerah");
            weatherCodes.Add(2, Lang.T("wx.c2")); weatherCodes.Add(3, "Mendung");
            weatherCodes.Add(45, Lang.T("wx.c3")); weatherCodes.Add(48, "Kabut deposit beku");
            weatherCodes.Add(51, Lang.T("wx.c4")); weatherCodes.Add(53, "Gerimis"); weatherCodes.Add(55, "Gerimis lebat");
            weatherCodes.Add(56, Lang.T("wx.c5")); weatherCodes.Add(57, "Gerimis membeku lebat");
            weatherCodes.Add(61, Lang.T("wx.c6")); weatherCodes.Add(63, "Hujan sedang"); weatherCodes.Add(65, "Hujan lebat");
            weatherCodes.Add(66, Lang.T("wx.c7")); weatherCodes.Add(67, "Hujan membeku lebat");
            weatherCodes.Add(71, Lang.T("wx.c8")); weatherCodes.Add(73, "Salju"); weatherCodes.Add(75, "Salju lebat");
            weatherCodes.Add(77, Lang.T("wx.c9"));
            weatherCodes.Add(80, Lang.T("wx.c10")); weatherCodes.Add(81, "Hujan lokal"); weatherCodes.Add(82, "Hujan lokal lebat");
            weatherCodes.Add(85, Lang.T("wx.c11")); weatherCodes.Add(86, "Salju lokal lebat");
            weatherCodes.Add(95, Lang.T("wx.c12")); weatherCodes.Add(96, "Badai petir + hujan es"); weatherCodes.Add(99, "Badai petir + hujan es lebat");
        }

        Label MkLabel(Control parent, string text, int x, int y, int w, int h, Color c, float size, FontStyle st)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Size = new Size(Util.S(w), Util.S(h));
            l.Location = new Point(Util.S(x), Util.S(y));
            l.ForeColor = c;
            l.BackColor = Color.Transparent;
            l.Font = new Font("Segoe UI", Util.S(size) * 4f / 3f, st, GraphicsUnit.Pixel);
            parent.Controls.Add(l);
            return l;
        }

        CardPanel MkCard(int x, int y, int w, int h)
        {
            CardPanel p = new CardPanel();
            p.Location = new Point(Util.S(x), Util.S(y));
            p.Size = new Size(Util.S(w), Util.S(h));
            Controls.Add(p);
            return p;
        }

        void BuildUi()
        {
            // ===== header =====
            MkLabel(this, "RESOURCE MONITORING", 20, 12, 420, 28, CText, 14.5f, FontStyle.Bold);
            MkLabel(this, Lang.T("hdr.sub"), 20, 46, 600, 20, CSub, 9.5f, FontStyle.Regular);
            lblClock = MkLabel(this, "", 1050, 14, 260, 30, CAccent, 12.5f, FontStyle.Bold);
            lblClock.TextAlign = ContentAlignment.MiddleRight;

            Button btnBar = new Button();
            btnBar.Text = "📌 Bar";
            btnBar.Location = new Point(Util.S(448), Util.S(14));
            btnBar.Size = new Size(Util.S(74), Util.S(30));
            StyleButton(btnBar);
            btnBar.Click += delegate { EnterBarMode(); };
            Controls.Add(btnBar);

            Button btnTray = new Button();
            btnTray.Text = "🔔 Tray";
            btnTray.Location = new Point(Util.S(526), Util.S(14));
            btnTray.Size = new Size(Util.S(74), Util.S(30));
            StyleButton(btnTray);
            btnTray.Click += delegate { EnterTrayMode(); };
            Controls.Add(btnTray);

            Button btnTask = new Button();
            btnTask.Text = "📊 Taskbar";
            btnTask.Location = new Point(Util.S(604), Util.S(14));
            btnTask.Size = new Size(Util.S(94), Util.S(30));
            StyleButton(btnTask);
            btnTask.Click += delegate { EnterTaskbarMode(); };
            Controls.Add(btnTask);

            // tata letak dipadatkan supaya seluruh jendela muat di area kerja layar (tidak tertutup taskbar)

            // ===== kartu BATERAI =====
            CardPanel cardB = MkCard(20, 72, 460, 232);
            MkLabel(cardB, Lang.T("card.batt"), 18, 12, 200, 18, CAccent, 9.5f, FontStyle.Bold);
            MkLabel(cardB, Lang.T("batt.remain"), 292, 34, 148, 15, CDim, 8.5f, FontStyle.Regular).TextAlign = ContentAlignment.MiddleRight;
            lblBattPct = MkLabel(cardB, "—%", 272, 48, 168, 60, CText, 32f, FontStyle.Bold);
            lblBattPct.TextAlign = ContentAlignment.MiddleRight;
            lblBattState = MkLabel(cardB, "Status: …", 18, 40, 250, 20, CSub, 9.5f, FontStyle.Regular);
            MkLabel(cardB, Lang.T("batt.pnow"), 18, 62, 250, 15, CDim, 8.5f, FontStyle.Regular);
            lblBattWatt = MkLabel(cardB, "— W", 18, 76, 250, 36, CText, 21f, FontStyle.Bold);
            lblBattRate = MkLabel(cardB, Lang.T("batt.rate.init"), 18, 114, 424, 19, CSub, 9.5f, FontStyle.Regular);
            lblBattVolt = MkLabel(cardB, Lang.T("batt.volt.init"), 18, 134, 424, 19, CSub, 9.5f, FontStyle.Regular);
            lblBattCap = MkLabel(cardB, Lang.T("batt.energy.init"), 18, 154, 424, 19, CSub, 9.5f, FontStyle.Regular);
            lblBattHealth = MkLabel(cardB, Lang.T("batt.health.init"), 18, 174, 424, 19, CSub, 9.5f, FontStyle.Regular);
            lblBattEst = MkLabel(cardB, Lang.T("batt.est.init"), 18, 198, 424, 22, CAccent, 9.5f, FontStyle.Bold);

            // ===== kartu CUACA =====
            CardPanel cardW = MkCard(500, 72, 460, 232);
            MkLabel(cardW, Lang.T("card.wx"), 18, 12, 200, 18, CAccent, 9.5f, FontStyle.Bold);
            lblWxCity = MkLabel(cardW, Lang.T("wx.loading"), 18, 40, 260, 24, CText, 11.5f, FontStyle.Bold);
            lblWxTemp = MkLabel(cardW, "—", 272, 48, 168, 60, CText, 32f, FontStyle.Bold);
            lblWxTemp.TextAlign = ContentAlignment.MiddleRight;
            lblWxDesc = MkLabel(cardW, "—", 18, 66, 252, 38, CSub, 10f, FontStyle.Regular);
            lblWxFeels = MkLabel(cardW, Lang.T("wx.feels.init"), 18, 108, 252, 19, CSub, 9.5f, FontStyle.Regular);
            lblWxHum = MkLabel(cardW, Lang.T("wx.hum.init"), 18, 128, 252, 19, CSub, 9.5f, FontStyle.Regular);
            lblWxWind = MkLabel(cardW, Lang.T("wx.wind.init"), 18, 148, 252, 19, CSub, 9.5f, FontStyle.Regular);
            lblWxMinMax = MkLabel(cardW, Lang.T("wx.today.init"), 18, 168, 252, 19, CSub, 9.5f, FontStyle.Regular);
            lblWxUpdated = MkLabel(cardW, "", 18, 198, 300, 18, CDim, 8.5f, FontStyle.Regular);
            btnWx = new Button();
            btnWx.Text = Lang.T("wx.reload");
            btnWx.Location = new Point(Util.S(330), Util.S(190));
            btnWx.Size = new Size(Util.S(112), Util.S(30));
            StyleButton(btnWx);
            btnWx.Click += delegate { LoadWeatherAsync(); };
            cardW.Controls.Add(btnWx);

            // ===== kartu SUHU (semua sensor termal + rata-rata keseluruhan) =====
            CardPanel cardT = MkCard(980, 72, 330, 232);
            MkLabel(cardT, Lang.T("card.temp"), 18, 12, 200, 18, CAccent, 9.5f, FontStyle.Bold);
            lblTempAvg = MkLabel(cardT, "\u2014 \u00B0C", 18, 32, 240, 44, CAccent, 24f, FontStyle.Bold);
            lblTempStat = MkLabel(cardT, "Sensor: \u2014", 18, 80, 294, 17, CSub, 9.5f, FontStyle.Regular);
            lblTempList = MkLabel(cardT, Lang.T("temp.needadmin"), 18, 98, 294, 66, CSub, 9.25f, FontStyle.Regular);
            lblTempAux = MkLabel(cardT, "", 18, 166, 294, 32, CDim, 8.75f, FontStyle.Regular);
            btnTemp = new Button();
            btnTemp.Text = Lang.T("temp.activate");
            btnTemp.Location = new Point(Util.S(18), Util.S(200));
            btnTemp.Size = new Size(Util.S(140), Util.S(26));
            StyleButton(btnTemp);
            btnTemp.Click += delegate { StartTempSvc(); };
            cardT.Controls.Add(btnTemp);

            // ===== kartu INTERNET (pemakaian real-time) =====
            CardPanel cardI = MkCard(20, 314, 660, 250);
            MkLabel(cardI, Lang.T("card.net"), 18, 12, 420, 18, CAccent, 9.5f, FontStyle.Bold);
            lblAdapter = MkLabel(cardI, Lang.T("net.adapter.init"), 18, 38, 624, 22, CSub, 9.5f, FontStyle.Regular);
            lblNetDown = MkLabel(cardI, "↓ 0 B/s", 18, 68, 300, 44, CGood, 23f, FontStyle.Bold);
            lblNetUp = MkLabel(cardI, "↑ 0 B/s", 18, 112, 300, 44, CAccent, 23f, FontStyle.Bold);
            lblIp = MkLabel(cardI, Lang.T("net.ip.init"), 18, 164, 300, 19, CSub, 9.5f, FontStyle.Regular);
            lblIsp = MkLabel(cardI, "ISP: …", 18, 184, 300, 22, CSub, 9.5f, FontStyle.Regular);
            lblNetSesi = MkLabel(cardI, Lang.T("net.session.init"), 330, 70, 310, 19, CSub, 9.5f, FontStyle.Regular);
            lblNetPuncak = MkLabel(cardI, Lang.T("net.peak.init"), 330, 92, 310, 19, CSub, 9.5f, FontStyle.Regular);
            lblNetBoot = MkLabel(cardI, Lang.T("net.boot.init"), 330, 114, 310, 19, CSub, 9.5f, FontStyle.Regular);
            netGraph = new GraphPanel();
            netGraph.Location = new Point(Util.S(330), Util.S(140));
            netGraph.Size = new Size(Util.S(310), Util.S(74));
            cardI.Controls.Add(netGraph);
            lblNetHint = MkLabel(cardI, Lang.T("net.graph.hint"), 330, 220, 310, 18, CDim, 8.5f, FontStyle.Regular);

            // ===== kartu SISTEM =====
            CardPanel cardS = MkCard(700, 314, 260, 250);
            MkLabel(cardS, Lang.T("card.sys"), 16, 12, 220, 18, CAccent, 9.5f, FontStyle.Bold);
            lblCpu = MkLabel(cardS, "CPU —%", 16, 40, 228, 28, CText, 15f, FontStyle.Bold);
            lblRam = MkLabel(cardS, "RAM —", 16, 70, 228, 28, CText, 15f, FontStyle.Bold);
            lblUptime = MkLabel(cardS, Lang.T("sys.up.init"), 16, 108, 228, 19, CSub, 9.5f, FontStyle.Regular);
            lblDisk = MkLabel(cardS, "C: —", 16, 128, 228, 38, CSub, 9.5f, FontStyle.Regular);
            lblPc = MkLabel(cardS, "PC: —", 16, 168, 228, 19, CSub, 9.5f, FontStyle.Regular);
            lblOs = MkLabel(cardS, "", 16, 190, 228, 50, CDim, 8.5f, FontStyle.Regular);

            // ===== kartu PENYIMPANAN =====
            CardPanel cardSt = MkCard(20, 574, 1290, 200);
            MkLabel(cardSt, Lang.T("card.stor"), 18, 12, 260, 18, CAccent, 9.5f, FontStyle.Bold);
            lblDiskIo = MkLabel(cardSt, Lang.T("stor.io.init"), 18, 38, 1254, 19, CSub, 9.5f, FontStyle.Regular);
            // 3 drive sejajar (kartu lebar penuh) supaya baris 3 seimbang dengan baris lain
            for (int i = 0; i < 3; i++)
            {
                int cx = 18 + i * 424;
                Label dl = MkLabel(cardSt, "", cx, 78, 400, 19, CText, 9.5f, FontStyle.Bold);
                BarPanel bar = new BarPanel();
                bar.Location = new Point(Util.S(cx), Util.S(102));
                bar.Size = new Size(Util.S(400), Util.S(14));
                cardSt.Controls.Add(bar);
                driveUis.Add(new DriveUi(dl, bar));
            }

            // ===== kartu GPU · NPU · CPU =====
            CardPanel cardG = MkCard(980, 314, 330, 250);
            MkLabel(cardG, Lang.T("card.gpu"), 18, 12, 300, 18, CAccent, 9.5f, FontStyle.Bold);
            lblGpu1 = MkLabel(cardG, Lang.T("gpu.loading"), 18, 38, 294, 20, CText, 9.5f, FontStyle.Bold);
            lblGpu1d = MkLabel(cardG, "", 18, 58, 294, 18, CSub, 8.75f, FontStyle.Regular);
            lblGpu2 = MkLabel(cardG, "", 18, 84, 294, 20, CText, 9.5f, FontStyle.Bold);
            lblGpu2d = MkLabel(cardG, "", 18, 104, 294, 18, CSub, 8.75f, FontStyle.Regular);
            lblNpu = MkLabel(cardG, Lang.T("npu.loading"), 18, 132, 294, 20, CText, 9.5f, FontStyle.Bold);
            lblNpuD = MkLabel(cardG, "", 18, 152, 294, 18, CSub, 8.75f, FontStyle.Regular);
            lblCpuInfo = MkLabel(cardG, "", 18, 186, 294, 20, CText, 9.5f, FontStyle.Bold);

            MkLabel(this, "Resource Monitoring v1.3.0 \u00B7 C# WinForms native", 970, 782, 340, 16, CDim, 8.5f, FontStyle.Regular).TextAlign = ContentAlignment.MiddleRight;
        }

        void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(96, 165, 250);
            b.ForeColor = Color.FromArgb(14, 18, 32);
            b.Font = new Font("Segoe UI", Util.S(9.5f) * 4f / 3f, FontStyle.Bold, GraphicsUnit.Pixel);
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(130, 185, 255);
        }

        // ================= MODE BAR =================
        string ModeFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mode.txt"); } }

        void SaveMode(string m)
        {
            try { File.WriteAllText(ModeFile, m); } catch { }
        }

        void EnterBarMode()
        {
            SaveMode("bar");
            CloseTaskbarWidget();
            if (flyout != null) flyout.Hide();
            if (bar == null || bar.IsDisposed)
            {
                bar = WithPrimaryScale<SlimBar>(delegate { return new SlimBar(); });
                bar.OpenFull = new Action(EnterFullMode);
                bar.ExitApp = new Action(delegate { allowExit = true; Close(); });
            }
            bar.EnsureDocked();
            bar.Show();
            Hide();
        }

        bool startHidden = false;
        bool startupDone = false;

        // inisialisasi awal + terapkan mode tersimpan; dipanggil dari Load, atau langsung bila form sengaja tidak ditampilkan
        void StartupInit()
        {
            if (startupDone) return;
            startupDone = true;
            LoadWxCache();
            UpdateBattery();
            UpdateSystem();
            UpdateAdapter();
            UpdateStorage();
            LoadWeatherAsync();
            LoadHardwareInfoAsync();
            try
            {
                string m = File.Exists(ModeFile) ? File.ReadAllText(ModeFile).Trim() : "full";
                if (m == "bar") EnterBarMode();
                else if (m == "tray") EnterTrayMode();
                else if (m == "taskbar") EnterTaskbarMode();
                foreach (string a in Environment.GetCommandLineArgs()) if (a == "/show") ShowDashboard();
            }
            catch { }
        }

        // Application.Run memanggil Visible = true SETELAH Load, sehingga Hide() di Load tidak cukup:
        // tahan permintaan tampil pertama pada mode tray/bar/taskbar.
        // WM_DPICHANGED: jendela pindah ke monitor dengan DPI berbeda -> hitung skala monitor itu dan bangun ulang UI
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0)
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                try
                {
                    TWin32.RECT r = (TWin32.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(TWin32.RECT));
                    Rectangle wa = Screen.FromRectangle(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)).WorkingArea;
                    float sc = Util.ScaleFor(dpi, wa);
                    if (Math.Abs(sc - Util.Scale) > 0.02f)
                    {
                        Util.Scale = sc;
                        RebuildUi();
                        Rectangle target = Rectangle.Intersect(new Rectangle(r.Left, r.Top, Width, Height), wa);
                        Location = new Point(Math.Max(wa.Left, Math.Min(r.Left, wa.Right - Width)), Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - Height)));
                    }
                }
                catch { }
                return;
            }
            base.WndProc(ref m);
        }

        static T WithPrimaryScale<T>(Func<T> make)
        {
            float keep = Util.Scale;
            Util.Scale = Util.PrimaryScale;
            try { return make(); }
            finally { Util.Scale = keep; }
        }

        void RebuildUi()
        {
            List<double> oldD = netGraph != null ? netGraph.HistD : null, oldU = netGraph != null ? netGraph.HistU : null;
            SuspendLayout();
            foreach (Control c in new System.Collections.ArrayList(Controls)) { Controls.Remove(c); c.Dispose(); }
            driveUis.Clear();
            ClientSize = new Size(Util.S(1330), Util.S(806));
            BuildUi();
            if (netGraph != null && oldD != null) { netGraph.HistD = oldD; netGraph.HistU = oldU; }
            ResumeLayout(true);
            // isi ulang label yang diperbarui jarang
            LoadWxCache();
            UpdateBattery();
            UpdateSystem();
            UpdateAdapter();
            UpdateStorage();
            UpdateTemps(true);
            LoadHardwareInfoAsync();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (value && startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();   // handle perlu ada untuk BeginInvoke dari thread latar
                StartupInit();
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        // tampilkan jendela dashboard tanpa mengubah mode (widget/bar/tray tetap hidup)
        bool placed = false;

        // dipanggil dari thread lain (peluncuran kedua aplikasi)
        public void RequestShow()
        {
            try { if (IsHandleCreated) BeginInvoke(new Action(ShowDashboard)); } catch { }
        }

        void ShowDashboard()
        {
            Show();
            FitToWorkArea();
            TopMost = true;      // trik agar benar-benar maju ke depan walau aplikasi sedang di latar
            TopMost = false;
            Activate();
        }

        // jaga agar jendela dashboard selalu berada di dalam area kerja layar (tidak tertutup taskbar)
        void FitToWorkArea()
        {
            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                int w = Math.Min(Width, wa.Width);
                int h = Math.Min(Height, wa.Height);
                if (w != Width || h != Height) Size = new Size(w, h);
                int x, y;
                if (!placed)
                {
                    x = wa.Left + (wa.Width - Width) / 2;
                    y = wa.Top + (wa.Height - Height) / 2;
                    placed = true;
                }
                else
                {
                    x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
                    y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
                }
                Location = new Point(x, y);
            }
            catch { }
        }

        void EnterFullMode()
        {
            SaveMode("full");
            CloseTaskbarWidget();
            if (bar != null) bar.Hide();
            ShowDashboard();
        }

        void EnterTrayMode() { EnterTrayMode(true); }

        // persist=false: jatuh ke tray sementara (mis. render widget gagal) tanpa menimpa mode tersimpan
        void EnterTrayMode(bool persist)
        {
            if (persist) SaveMode("tray");
            CloseTaskbarWidget();
            if (bar != null) bar.Hide();
            if (flyout != null) flyout.Hide();
            Hide();
        }

        void EnterTaskbarMode()
        {
            Dbg.Log("EnterTaskbarMode");
            SaveMode("taskbar");
            if (bar != null) bar.Hide();
            if (flyout != null) flyout.Hide();
            Hide();
            taskbarMode = true;
            UpdateTaskbarWidget();
        }

        // ================= WIDGET TASKBAR (di dalam taskbar, gaya TrafficMonitor) =================
        void CreateTaskbarWidget(IntPtr tray)
        {
            try
            {
                Dbg.Log("CreateTaskbarWidget tray=" + tray);
                tbWidget = new TaskbarWidget();
                tbWidget.ClickFlyout = new Action(ToggleFlyout);
                tbWidget.OpenFull = new Action(ShowDashboard);
                tbWidget.ExitRequested = new Action(delegate { allowExit = true; Close(); });
                tbWidget.Location = new Point(-3000, -3000);
                tbWidget.Show();
                tbWidget.EmbedInto(tray);
                Dbg.Log("CreateTaskbarWidget OK embedded=" + tbWidget.IsEmbeddedIn(tray));
            }
            catch (Exception ex) { Dbg.Log("CreateTaskbarWidget EX " + ex); DisposeTaskbarWidget(); }
        }

        void DisposeTaskbarWidget()
        {
            if (tbWidget != null)
            {
                try { tbWidget.Close(); } catch { }
                try { tbWidget.Dispose(); } catch { }
                tbWidget = null;
            }
        }

        void CloseTaskbarWidget()
        {
            taskbarMode = false;
            DisposeTaskbarWidget();
        }

        // tiap detik: pastikan widget masih menempel di taskbar (explorer bisa restart), lalu perbarui isinya
        void UpdateTaskbarWidget()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;           // explorer sedang restart
            if (tbWidget == null || tbWidget.IsDisposed || !tbWidget.IsEmbeddedIn(tray))
            {
                if (widgetRetryLogged < 10)
                {
                    Dbg.Log("UpdateTaskbarWidget recreate null=" + (tbWidget == null) + " disposed=" + (tbWidget != null && tbWidget.IsDisposed) + " embedded=" + (tbWidget != null && tbWidget.IsEmbeddedIn(tray)));
                    widgetRetryLogged++;
                }
                DisposeTaskbarWidget();
                CreateTaskbarWidget(tray);
                if (tbWidget == null) return;
            }
            tbWidget.TxtDown = "\u2193 " + Util.FmtRate(emaD);
            tbWidget.TxtUp = "\u2191 " + Util.FmtRate(emaU);
            tbWidget.TxtPct = battPctShort;
            tbWidget.TxtWatt = battWShort != "0,0" ? battWShort + " W" : "";
            tbWidget.BattState = battCharging ? 1 : (battPctVal <= 20 ? 2 : 0);
            tbWidget.TxtTemp = tempShort != "" ? tempShort : "";
            tbWidget.TxtCpu = lblCpu.Text != "" ? lblCpu.Text : "";
            tbWidget.CpuState = cpuPctVal >= 80 ? 2 : (cpuPctVal >= 50 ? 1 : 0);
            tbWidget.ColTempOverride = tempShort != "" ? TempColor(tempAvgVal) : Color.Empty;
            tbWidget.RefreshWidget(tray);
            if (tbWidget.FailCount >= 5) EnterTrayMode(false);   // render layered gagal terus -> jatuh ke mode tray
        }

        // ================= TRAY (area notifikasi taskbar) =================
        void CreateTrayIcon()
        {
            trayIcon = new NotifyIcon();
            trayIcon.Visible = true;
            trayIcon.Text = "Resource Monitoring";
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem miFull = new ToolStripMenuItem(Lang.T("menu.full"));
            miFull.Click += delegate { ShowDashboard(); };
            ToolStripMenuItem miBar = new ToolStripMenuItem(Lang.T("menu.bar"));
            miBar.Click += delegate { EnterBarMode(); };
            ToolStripMenuItem miHist = new ToolStripMenuItem(Lang.T("menu.history"));
            miHist.Click += delegate { OpenHistory(); };
            ToolStripMenuItem miTask = new ToolStripMenuItem(Lang.T("menu.taskbar"));
            miTask.Click += delegate { EnterTaskbarMode(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem(Lang.T("menu.exit"));
            miExit.Click += delegate { allowExit = true; Close(); };
            m.Items.Add(miFull);
            m.Items.Add(miBar);
            m.Items.Add(miTask);
            m.Items.Add(miHist);
            m.Items.Add(new ToolStripSeparator());
            // toggle bahasa: labelnya memakai bahasa TUJUAN supaya jelas bagi siapa pun
            string target = Lang.Mode == "en" ? "id" : "en";
            ToolStripMenuItem miLang = new ToolStripMenuItem(Lang.T(target == "en" ? "menu.lang.toen" : "menu.lang.toid"));
            miLang.Click += delegate
            {
                Lang.Set(Lang.Mode == "en" ? "id" : "en");
                RebuildUi();
            };
            m.Items.Add(miLang);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(miExit);
            trayIcon.ContextMenuStrip = m;
            trayIcon.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleFlyout();
            };
            UpdateTrayIcon();
        }

        DateTime flyoutHiddenAt = DateTime.MinValue;

        void ToggleFlyout()
        {
            Dbg.Log("ToggleFlyout enter; flyoutVisible=" + (flyout != null && flyout.Visible));
            if (flyout == null || flyout.IsDisposed)
            {
                flyout = WithPrimaryScale<FlyoutPanel>(delegate { return new FlyoutPanel(); });
                flyout.OpenFull = new Action(ShowDashboard);
                flyout.Spark.Values = tempHist;
                flyout.Spark.LineColor = Color.FromArgb(115, 222, 160);
                flyout.VisibleChanged += delegate { if (!flyout.Visible) flyoutHiddenAt = DateTime.Now; };
            }
            if (flyout.Visible) { flyout.Hide(); return; }
            // klik widget saat flyout terbuka: flyout sudah menutup sendiri (Deactivate) sesaat sebelumnya -> jangan buka lagi
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 400) return;
            RefreshFlyout();
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            flyout.Location = new Point(wa.Right - flyout.Width - 12, wa.Bottom - flyout.Height - 12);
            flyout.Show();
            flyout.Activate();
        }

        void RefreshFlyout()
        {
            if (flyout == null) return;
            flyout.LTitle.Text = lblClock.Text;
            flyout.LBatt.Text = battPctShort + "  " + battWShort + " W";
            flyout.LBatt2.Text = lblBattState.Text + " · " + lblBattVolt.Text;
            flyout.LBatt3.Text = lblBattEst.Text;
            flyout.LNet.Text = netShort;
            flyout.LNet2.Text = lblNetSesi.Text;
            flyout.LSys.Text = sysShort;
            flyout.LSys2.Text = lblUptime.Text;
            flyout.LWx.Text = lastWxShort;
            flyout.LTemp.Text = tempShort != "" ? tempShort : "\u2014 \u00B0C";
            flyout.LTemp2.Text = lblTempAux != null ? lblTempAux.Text : "";
            flyout.LProc1.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("proc.cpu"), procCpuRaw == "" ? "\u2014" : procCpuRaw);
            flyout.LProc2.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("proc.ram"), procRamRaw == "" ? "\u2014" : procRamRaw);
            flyout.LAd.Text = lblAdapter.Text;
        }

        void UpdateTrayIcon()
        {
            if (trayIcon == null) return;
            double p;
            double.TryParse(battPctShort.Replace("%", ""), NumberStyles.Float, CultureInfo.CurrentCulture, out p);
            if (double.IsNaN(p)) p = 0;
            int pct = (int)Math.Round(p);
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            string key = pct + "|" + battCharging;
            if (key == lastTrayKey) return;
            lastTrayKey = key;
            int n = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (Bitmap bmp = new Bitmap(n, n))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    Color bg = battCharging ? Color.FromArgb(34, 139, 84) : (pct <= 20 ? Color.FromArgb(200, 50, 50) : Color.FromArgb(40, 46, 66));
                    using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, 0, 0, n, n);
                    string s = pct.ToString();
                    float em = s.Length >= 3 ? n * 0.50f : n * 0.70f;
                    using (Font f = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (StringFormat sfm = (StringFormat)StringFormat.GenericTypographic.Clone())
                    {
                        sfm.Alignment = StringAlignment.Center;
                        sfm.LineAlignment = StringAlignment.Center;
                        using (SolidBrush tb = new SolidBrush(Color.White))
                        {
                            g.DrawString(s, f, tb, new RectangleF(0, -n * 0.04f, n, n), sfm);
                        }
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon ni = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                Icon old = trayIcon.Icon;
                trayIcon.Icon = ni;
                if (old != null) old.Dispose();
            }
        }

        // ================= tick =================
        void OnUiTick(object sender, EventArgs e)
        {
            tick++;
            lblClock.Text = DateTime.Now.ToString("ddd, dd MMM yyyy · HH:mm:ss", new CultureInfo("id-ID"));
            UpdateBattery();
            UpdateSystem();
            UpdateNetUsage();
            UpdateStorage();
            netGraph.Invalidate();
            if (tick % 10 == 0) UpdateAdapter();

            if (!wxBusy && DateTime.Now >= nextWxRetry)
            {
                nextWxRetry = DateTime.Now.AddSeconds(90);
                LoadWeatherAsync();
            }

            if (bar != null && bar.Visible)
            {
                if (tick % 5 == 0) bar.EnsureDocked();
                bar.LClock.Text = DateTime.Now.ToString("HH:mm:ss");
                bar.LBatt.Text = string.Format(CultureInfo.CurrentCulture, "\u26A1 {0} {1} W", battPctShort, battWShort);
                bar.LBatt.ForeColor = battCharging ? CGood : CWarn;
                bar.LNet.Text = netShort;
                bar.LSys.Text = sysShort;
                bar.LWx.Text = lastWxShort;
                bar.LDisk.Text = diskShort;
                bar.LTemp.Text = tempShort != "" ? tempShort : "\u2014";
                bar.LTemp.ForeColor = tempShort != "" ? TempColor(tempAvgVal) : Color.FromArgb(168, 174, 196);
            }

            UpdateTrayIcon();
            if (flyout != null && flyout.Visible) RefreshFlyout();
            if (trayIcon != null && tick % 5 == 0)
            {
                string tt = battPctShort + " " + battWShort + "W | " + netShort + " | " + lblCpu.Text + (tempShort != "" ? " | " + tempShort : "") + (topCpuShort != "" ? " \u00B7 " + topCpuShort : "") + " | " + lastWxShort;
                if (tt.Length > 63) tt = tt.Substring(0, 63);
                try { trayIcon.Text = tt; } catch { }
            }

            // suhu: dibaca tiap 2 detik bila file berubah (murah: satu stat + baca versi baru saja)
            if (tick % 2 == 0) UpdateTemps();
            if (tick % 3 == 0) UpdateTopProcs();
            if (tick % 30 == 0 && tempShort != "")
            {
                tempHist.Add(tempAvgVal);
                while (tempHist.Count > 120) tempHist.RemoveAt(0);
                if (flyout != null && !flyout.IsDisposed && flyout.Spark != null) flyout.Spark.Invalidate();
            }
            if (tick % 60 == 0) WriteHistoryLine();

            if (taskbarMode) UpdateTaskbarWidget();
        }

        // ================= SUHU (karti terpisah; kolektor = proses Administrator) =================
        void StartTempSvc()
        {
            // sudah berjalan? (temps.json masih hangat) -> jangan tumpuk kolektor kedua/UAC lagi
            try
            {
                string tf = SessionTempCollectorSvc.TempFile();
                if (File.Exists(tf) && (DateTime.UtcNow - new FileInfo(tf).LastWriteTimeUtc).TotalSeconds < 30)
                {
                    lblTempList.Text = Lang.T("temp.already");
                    if (btnTemp != null) btnTemp.Visible = false;
                    return;
                }
            }
            catch { }
            // mulai kolektor suhu dengan sekali persetujuan UAC; sukses ditandai file temps.json muncul
            string err;
            if (!SessionTempCollectorSvc.TryStart(out err))
            {
                lblTempList.Text = Lang.T("temp.cancelled") + (err ?? "?") + " — klik lagi untuk mencoba.";
                return;
            }
            lblTempList.Text = Lang.T("temp.started");
        }

        sealed class TempSample
        {
            public long Epoch;
            public double Avg;
            public string Updated;
            public List<string[]> Rows;      // {nama, kind} — hanya sensor suhu
            public List<double> Vs;          // nilai per baris (C)
            public List<string[]> Aux;       // {kind, nama, teks} — kipas (RPM) & daya (W)
        }

        static TempSample ReadTemps()
        {
            string f = SessionTempCollectorSvc.TempFile();
            try
            {
                if (!File.Exists(f)) return null;
                if (new FileInfo(f).LastWriteTimeUtc < DateTime.UtcNow.AddSeconds(-45)) return null;   // kolektor mati/usang
                Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                if (d == null) return null;
                TempSample s = new TempSample { Updated = d.ContainsKey("pembaruan") ? Convert.ToString(d["pembaruan"]) : "", Epoch = ToEp(d, "epoch") };
                s.Avg = d.ContainsKey("avg") ? Convert.ToDouble(d["avg"], CultureInfo.InvariantCulture) : 0;
                s.Rows = new List<string[]>(); s.Vs = new List<double>(); s.Aux = new List<string[]>();
                object raw;
                bool hasSensor = d.TryGetValue("sensor", out raw);
                if (hasSensor && raw is System.Collections.IEnumerable)
                {
                    foreach (object row in (System.Collections.IEnumerable)raw)
                    {
                        Dictionary<string, object> r = row as Dictionary<string, object>;
                        if (r == null) continue;
                        object cv;
                        if (!r.TryGetValue("c", out cv)) continue;
                        double c = Convert.ToDouble(cv, CultureInfo.InvariantCulture);
                        if (c <= -100 || c >= 140) continue;
                        object nv, kv;
                        string kind = r.TryGetValue("kind", out kv) ? Convert.ToString(kv) : "";
                        string nm = r.TryGetValue("name", out nv) ? Convert.ToString(nv) : "";
                        if (string.IsNullOrEmpty(nm)) nm = string.IsNullOrEmpty(kind) ? "sensor" : kind;
                        if (kind == "kipas" || kind == "daya")
                        {
                            s.Aux.Add(new string[] { kind, nm, c.ToString("0.#", CultureInfo.CurrentCulture) });
                            continue;
                        }
                        s.Rows.Add(new string[] { nm, kind });
                        s.Vs.Add(c);
                    }
                }
                if (s.Vs.Count == 0 && s.Aux.Count == 0) return null;
                return s;
            }
            catch { return null; }
        }

        static long ToEp(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null)
            {
                try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { }
            }
            return 0;
        }

        static Color TempColor(double c)
        {
            if (c < 55) return CGood;
            if (c < 70) return CAccent;
            if (c < 80) return CWarn;
            return CBad;
        }

        static string Cz(double c)
        {
            return c.ToString("0.0", CultureInfo.CurrentCulture) + "\u00B0C";
        }

        void UpdateTemps(bool force = false)
        {
            if (lblTempAvg == null) return;   // sedang dibangun ulang
            TempSample s = ReadTemps();
            if (s == null)
            {
                if (!force && lblTempAvg.Text == "\u2014 \u00B0C") return;   // statis: jangan ditulis ulang tiap 2 dtk
                lblTempAvg.Text = "\u2014 \u00B0C";
                lblTempAvg.ForeColor = CAccent;
                lblTempStat.Text = Lang.T("temp.stat.init");
                lblTempList.Text = Lang.T("temp.needadmin2");
                if (lblTempAux != null) lblTempAux.Text = "";
                if (btnTemp != null) btnTemp.Visible = true;
                tempShort = ""; tempAvgVal = 0;
                // tahan-restart: coba nyalakan kolektor lewat task terjadwal (senyap, tanpa UAC;
                // gagal = task belum terdaftar di mesin ini, cukup tiap 60 dtk maksimal)
                if ((DateTime.Now - tempSvcRetry).TotalSeconds >= 60)
                {
                    tempSvcRetry = DateTime.Now;
                    try { Process.Start(new ProcessStartInfo("schtasks", "/Run /TN \"ResourceMonitoring-TempSvc\"") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
                }
                return;
            }
            if (!force && tempEpoch == s.Epoch) return;   // kolektor belum menulis versi baru
            tempEpoch = s.Epoch;
            lblTempAvg.Text = Cz(s.Avg);
            lblTempAvg.ForeColor = TempColor(s.Avg);
            tempShort = s.Avg.ToString("0.0", CultureInfo.CurrentCulture) + "\u00B0C";
            tempAvgVal = s.Avg;
            if (s.Avg >= 85 && (DateTime.Now - alertTempAt).TotalMinutes > 10 && trayIcon != null)
            {
                alertTempAt = DateTime.Now;
                try { trayIcon.ShowBalloonTip(8000, Lang.T("alert.temp.title"), string.Format(CultureInfo.CurrentCulture, Lang.T("alert.temp.body"), s.Avg), ToolTipIcon.Warning); } catch { }
                LogAlert("SUHU", s.Avg.ToString("0.0", CultureInfo.InvariantCulture) + " C");
            }
            double mn = s.Vs.Min(), mx = s.Vs.Max();
            tempMinVal = mn; tempMaxVal = mx;
            string up = s.Updated.Length >= 8 ? s.Updated.Substring(s.Updated.Length - 8) : s.Updated;
            lblTempStat.Text = s.Rows.Count + Lang.T("temp.stat.sep") + Cz(mn) + "\u2013" + Cz(Math.Max(mn, mx)) + " \u00B7 " + up;
            // baris bantuan: kipas (RPM) + daya CPU (W) — jawaban "kipas kenapa ngebut" dalam angka
            if (lblTempAux != null)
            {
                string fan = "", watt = "";
                foreach (string[] a in s.Aux)
                {
                    if (a[0] == "kipas" && fan == "")
                    {
                        string fn = a[1];
                        if (fn.Length > 18) fn = fn.Substring(0, 18) + "\u2026";
                        fan = Lang.T("temp.fan") + a[2] + " RPM";
                    }
                    if (a[0] == "daya" && watt == "" && a[1].ToLowerInvariant().IndexOf("package", StringComparison.OrdinalIgnoreCase) >= 0)
                        watt = Lang.T("temp.cpw") + a[2] + " W";
                }
                if (watt == "" && s.Aux.Count > 0)
                {
                    foreach (string[] a in s.Aux)
                        if (a[0] == "daya" && watt == "") watt = "Daya: " + a[2] + " W";
                }
                lblTempAux.Text = (fan + (fan != "" && watt != "" ? "  \u00B7  " : "") + watt);
            }
            List<string> line = new List<string>();
            for (int i = 0; i < s.Rows.Count && i < 4; i++)
            {
                string nm = s.Rows[i][0];
                if (nm.Length > 20) nm = nm.Substring(0, 19) + "\u2026";
                line.Add(nm + ": " + Cz(s.Vs[i]));
            }
            int rest = s.Rows.Count - Math.Min(s.Rows.Count, 4);
            if (rest > 0) line.Add("\u2026 +" + rest + Lang.T("temp.more"));
            lblTempList.Text = string.Join("\n", line);
            if (btnTemp != null) btnTemp.Visible = false;
        }

        // ================= RIWAYAT (CSV per bulan + log alert + grafik) =================
        static string HistoryDir()
        {
            string d = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        void LogAlert(string kind, string msg)
        {
            try { File.AppendAllText(Path.Combine(HistoryDir(), "alerts.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + kind + "] " + msg + "\r\n"); } catch { }
        }

        void WriteHistoryLine()
        {
            try
            {
                string f = Path.Combine(HistoryDir(), "history-" + DateTime.Now.ToString("yyyy-MM") + ".csv");
                bool isNew = !File.Exists(f);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + ";" + (tempShort != "" ? tempAvgVal.ToString("0.0", CultureInfo.InvariantCulture) : "")
                    + ";" + (tempShort != "" ? tempMinVal.ToString("0.0", CultureInfo.InvariantCulture) : "")
                    + ";" + (tempShort != "" ? tempMaxVal.ToString("0.0", CultureInfo.InvariantCulture) : "")
                    + ";" + cpuPctVal.ToString("0", CultureInfo.InvariantCulture)
                    + ";" + ramPctVal.ToString("0", CultureInfo.InvariantCulture)
                    + ";" + battPctVal.ToString("0", CultureInfo.InvariantCulture)
                    + ";" + battWNum.ToString("0.0", CultureInfo.InvariantCulture)
                    + ";" + topCpuShort;
                if (isNew) File.AppendAllText(f, "waktu;temp_avg;temp_min;temp_max;cpu_pct;ram_pct;batt_pct;batt_w;top_cpu\r\n");
                File.AppendAllText(f, line + "\r\n");
            }
            catch { }
        }

        void OpenHistory()
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo("explorer.exe", "\"" + HistoryDir() + "\"");
                si.UseShellExecute = true;
                Process.Start(si);
            }
            catch { }
        }

        // ================= PROSES TERATAS (CPU & RAM) =================
        static string FmtMB(double mb)
        {
            if (mb >= 1024) return (mb / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
            return mb.ToString("0", CultureInfo.CurrentCulture) + " MB";
        }

        void UpdateTopProcs()
        {
            try
            {
                DateTime now = DateTime.Now;
                int cores = Environment.ProcessorCount;
                if (cores < 1) cores = 1;
                Dictionary<string, double> agg = new Dictionary<string, double>();
                Dictionary<string, double> ram = new Dictionary<string, double>();
                Dictionary<string, double> curMs = new Dictionary<string, double>();
                double dtMs = procPrevAt == DateTime.MinValue ? 0 : (now - procPrevAt).TotalMilliseconds;
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == 0 || p.Id == 4) continue;
                        string nm = p.ProcessName;
                        double ms = p.TotalProcessorTime.TotalMilliseconds;
                        double wsMb = p.WorkingSet64 / 1048576.0;
                        double prev;
                        if (curMs.TryGetValue(nm, out prev)) curMs[nm] = prev + ms; else curMs[nm] = ms;
                        if (ram.ContainsKey(nm)) ram[nm] += wsMb; else ram[nm] = wsMb;
                    }
                    catch { }
                }
                if (dtMs > 500)
                {
                    foreach (KeyValuePair<string, double> kv in curMs)
                    {
                        double prev;
                        if (procPrevMs.TryGetValue(kv.Key, out prev))
                        {
                            double d = kv.Value - prev;
                            if (d < 0) d = 0;
                            agg[kv.Key] = d;
                        }
                    }
                    List<KeyValuePair<string, double>> cpuTop = new List<KeyValuePair<string, double>>(agg);
                    cpuTop.Sort(delegate(KeyValuePair<string, double> a, KeyValuePair<string, double> b) { return b.Value.CompareTo(a.Value); });
                    List<KeyValuePair<string, double>> ramTop = new List<KeyValuePair<string, double>>(ram);
                    ramTop.Sort(delegate(KeyValuePair<string, double> a, KeyValuePair<string, double> b) { return b.Value.CompareTo(a.Value); });
                    List<string> cl = new List<string>();
                    for (int i = 0; i < cpuTop.Count && i < 3; i++)
                    {
                        double pct = cpuTop[i].Value / (dtMs * cores) * 100.0;
                        if (pct < 0.05) continue;
                        string nm = cpuTop[i].Key; if (nm.Length > 16) nm = nm.Substring(0, 15) + "\u2026";
                        cl.Add(nm + " " + pct.ToString("0.#", CultureInfo.CurrentCulture) + "%");
                    }
                    List<string> rl = new List<string>();
                    for (int i = 0; i < ramTop.Count && i < 3; i++)
                    {
                        string nm = ramTop[i].Key; if (nm.Length > 16) nm = nm.Substring(0, 15) + "\u2026";
                        rl.Add(nm + " " + FmtMB(ramTop[i].Value));
                    }
                    procCpuRaw = cl.Count > 0 ? string.Join(" \u00B7 ", cl.ToArray()) : "\u2014";
                    procRamRaw = rl.Count > 0 ? string.Join(" \u00B7 ", rl.ToArray()) : "\u2014";
                    topCpuShort = cl.Count > 0 ? cl[0] : "";
                    if ((DateTime.Now.Second % 30) < 4) Dbg.Log("TEMP-proc cpu=" + procCpuRaw + " | ram=" + procRamRaw);
                }
                procPrevMs = curMs;
                procPrevAt = now;
            }
            catch { }
        }

        // ================= BATERAI =================
        static long DLong(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v)) return Util.ToLong(v);
            return 0;
        }

        static bool DBool(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null)
            {
                try { return Convert.ToBoolean(v); } catch { return false; }
            }
            return false;
        }

        static string DStr(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null) return v.ToString();
            return "";
        }

        // Riwayat kapasitas untuk aliran bersih ke baterai. Laju "pengisian" dari EC laptop ini bukan aliran bersih:
        // saat beban > daya charger angkanya malah naik padahal kapasitas turun (diukur: +13,5 W "mengisi", kapasitas -15,8 W).
        List<long[]> capHist = new List<long[]>();   // {ticks, mWh}
        long cachedFullMwh = -1, cachedDesignMwh = -1;
        DateTime capCachedAt = DateTime.MinValue, designTriedAt = DateTime.MinValue;
        bool lastOnlineState = false;

        bool NetFlowMw(bool online, long remCap, out double netMw)
        {
            netMw = 0;
            if (online != lastOnlineState) { capHist.Clear(); lastOnlineState = online; }
            if (remCap <= 0) return false;
            long now = DateTime.UtcNow.Ticks;
            capHist.Add(new long[] { now, remCap });
            while (capHist.Count > 2 && now - capHist[0][0] > 90L * 10000000L) capHist.RemoveAt(0);
            double sec = (now - capHist[0][0]) / 1e7;
            if (sec < 30) return false;
            // median kemiringan antar pasangan sampel (Theil-Sen): kebal terhadap jitter +-20 mWh dan loncatan gauge tunggal
            List<double> slopes = new List<double>();
            for (int i = 0; i < capHist.Count; i++)
                for (int j = i + 1; j < capHist.Count; j++)
                {
                    double dt = (capHist[j][0] - capHist[i][0]) / 1e7;
                    if (dt >= 10) slopes.Add((capHist[j][1] - capHist[i][1]) / dt);
                }
            if (slopes.Count == 0) return false;
            slopes.Sort();
            netMw = slopes[slopes.Count / 2] * 3600.0;   // mWh/s -> mW
            return true;
        }

        void UpdateBattery()
        {
            try
            {
                PowerStatus ps = SystemInformation.PowerStatus;
                bool online = ps.PowerLineStatus == PowerLineStatus.Online;

                bool charging = false, discharging = false, powerOnline = false;
                long chargeRw = 0, dischargeRw = 0, voltage = 0, remCap = 0, fullMwh = -1, designMwh = -1;

                foreach (Dictionary<string, object> o in Util.WmiRows("root\\wmi", "SELECT * FROM BatteryStatus"))
                {
                    charging = charging || DBool(o, "Charging");
                    discharging = discharging || DBool(o, "Discharging");
                    powerOnline = powerOnline || DBool(o, "PowerOnline");
                    chargeRw += DLong(o, "ChargeRate");                // mW (terverifikasi)
                    dischargeRw += DLong(o, "DischargeRate");          // mW
                    long v = DLong(o, "Voltage");
                    if (v > voltage) voltage = v;
                    remCap += DLong(o, "RemainingCapacity");           // mWh
                }

                // kapasitas penuh/desain nyaris statis: cukup ditanyakan tiap 60 dtk (desain: tiap 5 mnt, sering gagal di laptop ini)
                if (cachedFullMwh <= 0 || (DateTime.Now - capCachedAt).TotalSeconds > 60)
                {
                    capCachedAt = DateTime.Now;
                    foreach (Dictionary<string, object> o in Util.WmiRows("root\\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                    {
                        long v = DLong(o, "FullChargedCapacity");
                        if (v > 0) cachedFullMwh = v;
                        break;
                    }
                }
                fullMwh = cachedFullMwh;
                if (cachedDesignMwh <= 0 && (DateTime.Now - designTriedAt).TotalSeconds > 300)
                {
                    designTriedAt = DateTime.Now;
                    foreach (Dictionary<string, object> o in Util.WmiRows("root\\wmi", "SELECT DesignedCapacity FROM BatteryStaticData"))
                    {
                        long v = DLong(o, "DesignedCapacity");
                        if (v > 0) cachedDesignMwh = v;
                        break;
                    }
                }
                designMwh = cachedDesignMwh;
                if (designMwh <= 0 && designFromReport > 0) designMwh = designFromReport;

                // persentase presisi dari kapasitas, fallback PowerStatus
                double pctD;
                if (remCap > 0 && fullMwh > 0) pctD = remCap * 100.0 / fullMwh;
                else
                {
                    float pctF = ps.BatteryLifePercent;
                    if (pctF > 1.0f && pctF <= 255f) pctF = pctF / 255f;
                    pctD = Math.Round(pctF * 100);
                }
                int pct = (int)Math.Round(pctD);

                lblBattPct.Text = string.Format(CultureInfo.CurrentCulture, "{0:0.#}%", pctD);

                string state;
                if (charging && pct < 100) state = Lang.T("batt.charging");
                else if (online && pct >= 100) state = Lang.T("batt.full");
                else if (online) state = Lang.T("batt.ac");
                else if (discharging) state = Lang.T("batt.ondis");
                else state = powerOnline ? Lang.T("batt.ext") : "—";
                lblBattState.Text = Lang.T("batt.status") + state;

                // rate aktif dalam mW (satuan terdokumentasi BATTERY_STATUS.Rate;
                // diverifikasi di mesin ini: net kapasitas cocok dengan interpretasi mW)
                long activeRw = 0;
                bool isCharging = false;
                if (chargeRw > 0) { activeRw = chargeRw; isCharging = true; }
                else if (dischargeRw > 0) { activeRw = dischargeRw; isCharging = false; }

                // koreksi: EC melapor "mengisi" tetapi kapasitas sebenarnya turun (charger kalah dari beban)
                double netMw;
                bool netValid = NetFlowMw(online, remCap, out netMw);
                bool mismatch = online && isCharging && netValid && netMw <= -1500;
                if (mismatch)
                {
                    isCharging = false;
                    activeRw = (long)(-netMw);
                    lblBattState.Text = Lang.T("batt.acdown");
                }

                double rateMw = activeRw;
                double watts = rateMw / 1000.0;

                if (isCharging && rateMw > 0)
                {
                    lblBattWatt.Text = string.Format(CultureInfo.CurrentCulture, "+{0:0.0} W", watts);
                    battWNum = watts;
                    lblBattWatt.ForeColor = CGood;
                    lblBattEst.ForeColor = CGood;
                }
                else if (!isCharging && rateMw > 0)
                {
                    lblBattWatt.Text = string.Format(CultureInfo.CurrentCulture, "\u2212{0:0.0} W", watts);
                    battWNum = -watts;
                    lblBattWatt.ForeColor = CWarn;
                    lblBattEst.ForeColor = CWarn;
                }
                else
                {
                    lblBattWatt.Text = "0,0 W";
                    battWNum = 0;
                    lblBattWatt.ForeColor = CSub;
                    lblBattEst.ForeColor = CAccent;
                }

                battPctShort = string.Format(CultureInfo.CurrentCulture, "{0:0.#}%", pctD);
                if (!battCharging && pctD <= 15 && (DateTime.Now - alertBatAt).TotalMinutes > 30 && trayIcon != null)
                {
                    alertBatAt = DateTime.Now;
                    try { trayIcon.ShowBalloonTip(8000, Lang.T("alert.bat.title"), string.Format(CultureInfo.CurrentCulture, Lang.T("alert.bat.body"), pctD), ToolTipIcon.Warning); } catch { }
                    LogAlert("BATERAI", pctD.ToString("0", CultureInfo.InvariantCulture) + "%");
                }
                battPctVal = pctD;
                battCharging = isCharging;
                battWShort = rateMw > 0
                    ? (isCharging ? "+" : "\u2212") + string.Format(CultureInfo.CurrentCulture, "{0:0.0}", watts)
                    : "0,0";

                lblBattRate.Text = activeRw > 0
                    ? string.Format(CultureInfo.CurrentCulture, "{0}: {1} mW (\u2248 {2:0.00} A @ {3:0.00} V)",
                        isCharging ? Lang.T("batt.chargerate") : Lang.T("batt.use"), Util.Num(activeRw),
                        voltage > 0 ? rateMw / voltage : 0, voltage / 1000.0)
                    : Lang.T("batt.rate.zero");
                if (mismatch)
                    lblBattRate.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("batt.netflow"), watts, Util.Num(chargeRw));
                lblBattVolt.Text = voltage > 0
                    ? string.Format(CultureInfo.CurrentCulture, Lang.T("batt.volt"), voltage / 1000.0)
                    : Lang.T("batt.volt.init");
                lblBattCap.Text = (remCap > 0 && fullMwh > 0)
                    ? string.Format(CultureInfo.CurrentCulture, Lang.T("batt.energy"), Util.Num(remCap), Util.Num(fullMwh), pctD)
                    : Lang.T("batt.energy.init");

                if (designMwh > 0 && fullMwh > 0)
                {
                    double health = fullMwh * 100.0 / designMwh;
                    if (cycleCount >= 0) lblBattHealth.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("batt.health2"), health, Util.Num(designMwh), cycleCount);
                    else lblBattHealth.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("batt.health"), health, Util.Num(designMwh));
                    lblBattHealth.ForeColor = health >= 80 ? CGood : (health >= 60 ? CWarn : CBad);
                }
                else lblBattHealth.Text = Lang.T("batt.health.init");

                if (isCharging && rateMw > 0 && fullMwh > 0 && remCap >= 0)
                {
                    double hoursToFull = (fullMwh - remCap) / rateMw;
                    lblBattEst.Text = Lang.T("batt.fullin") + Util.FmtDur(TimeSpan.FromHours(hoursToFull));
                }
                else if (!isCharging && rateMw > 0 && remCap > 0)
                {
                    double hoursLeft = remCap / rateMw;
                    lblBattEst.Text = Lang.T("batt.eston") + Util.FmtDur(TimeSpan.FromHours(hoursLeft));
                }
                else if (!online && ps.BatteryLifeRemaining > 0)
                {
                    lblBattEst.Text = Lang.T("batt.eston") + Util.FmtDur(TimeSpan.FromSeconds(ps.BatteryLifeRemaining));
                }
                else lblBattEst.Text = Lang.T("batt.est.init");
            }
            catch (Exception ex)
            {
                lblBattState.Text = Lang.T("batt.err") + ex.Message;
            }
        }

        // design capacity via powercfg (fallback karena BatteryStaticData gagal di laptop ini)
        void LoadDesignCapacityAsync()
        {
            Task.Run(new Action(delegate()
            {
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "resourcemonitoring_batt.xml");
                    ProcessStartInfo si = new ProcessStartInfo("powercfg", "/batteryreport /output \"" + tmp + "\" /XML");
                    si.UseShellExecute = false;
                    si.CreateNoWindow = true;
                    using (Process p = Process.Start(si)) p.WaitForExit(20000);
                    if (File.Exists(tmp))
                    {
                        System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                        doc.Load(tmp);
                        System.Xml.XmlNodeList nl = doc.GetElementsByTagName("DesignCapacity");
                        if (nl.Count > 0)
                        {
                            long d;
                            if (long.TryParse(nl[0].InnerText.Trim(), out d) && d > 0) designFromReport = d;
                        }
                        System.Xml.XmlNodeList nc = doc.GetElementsByTagName("CycleCount");
                        if (nc.Count > 0)
                        {
                            long cc;
                            if (long.TryParse(nc[0].InnerText.Trim(), out cc) && cc >= 0) cycleCount = cc;
                        }
                        try { File.Delete(tmp); } catch { }
                    }
                }
                catch { }
            }));
        }

        // ================= PEMAKAIAN INTERNET =================
        double[] ReadNetTotals()
        {
            // Statistik adapter via WMI (counter 64-bit, hanya adapter fisik)
            double inO = 0, outO = 0;
            if (physCache.Count == 0 || (DateTime.Now - physCacheAt).TotalSeconds > 60)
            {
                HashSet<string> fresh = new HashSet<string>();
                foreach (Dictionary<string, object> o in Util.WmiRows("root\\StandardCimv2", "SELECT Name FROM MSFT_NetAdapter WHERE ConnectorPresent = TRUE"))
                {
                    string n = DStr(o, "Name");
                    if (n.Trim() != "") fresh.Add(n.Trim());
                }
                if (fresh.Count > 0) physCache = fresh;
                physCacheAt = DateTime.Now;
            }
            HashSet<string> phys = physCache;
            foreach (Dictionary<string, object> o in Util.WmiRows("root\\StandardCimv2", "SELECT Name, ReceivedBytes, SentBytes FROM MSFT_NetAdapterStatisticsSettingData"))
            {
                string n = DStr(o, "Name");
                if (n == null) continue;
                if (phys.Count > 0 && !phys.Contains(n.Trim())) continue;
                inO += DLong(o, "ReceivedBytes");
                outO += DLong(o, "SentBytes");
            }
            return new double[] { inO, outO };
        }

        void UpdateNetUsage()
        {
            double[] tot = ReadNetTotals();
            if (prevIn < 0)
            {
                prevIn = tot[0]; prevOut = tot[1];
                netSw.Restart();
                lblNetBoot.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("net.boot"), Util.FmtBytes(tot[0]), Util.FmtBytes(tot[1]));
                return;
            }
            double dt = netSw.Elapsed.TotalSeconds;
            if (dt < 0.2) return;
            double dIn = tot[0] - prevIn;
            double dOut = tot[1] - prevOut;
            prevIn = tot[0]; prevOut = tot[1];
            netSw.Restart();
            if (dIn < 0 || dOut < 0) return; // counter reset (adaptor restart)

            double rateIn = dIn / dt;
            double rateOut = dOut / dt;
            sessIn += dIn;
            sessOut += dOut;

            const double alpha = 0.35;
            emaD = alpha * rateIn + (1 - alpha) * emaD;
            emaU = alpha * rateOut + (1 - alpha) * emaU;
            if (emaD > peakD) peakD = emaD;
            if (emaU > peakU) peakU = emaU;

            netGraph.HistD.Add(emaD);
            netGraph.HistU.Add(emaU);
            while (netGraph.HistD.Count > HistMax) netGraph.HistD.RemoveAt(0);
            while (netGraph.HistU.Count > HistMax) netGraph.HistU.RemoveAt(0);

            lblNetDown.Text = "\u2193 " + Util.FmtRate(emaD);
            lblNetUp.Text = "\u2191 " + Util.FmtRate(emaU);
            lblNetSesi.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("net.session"), Util.FmtBytes(sessIn), Util.FmtBytes(sessOut));
            lblNetPuncak.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("net.peak"), Util.FmtRate(peakD), Util.FmtRate(peakU));
            lblNetBoot.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("net.boot"), Util.FmtBytes(tot[0]), Util.FmtBytes(tot[1]));
            netShort = string.Format(CultureInfo.CurrentCulture, "\u2193 {0} \u2191 {1}", Util.FmtRate(emaD), Util.FmtRate(emaU));
        }

        // ================= PENYIMPANAN =================
        PerformanceCounter pcDiskRead, pcDiskWrite;

        void UpdateStorage()
        {
            if (diskPerfOk)
            {
                try
                {
                    if (pcDiskRead == null)
                    {
                        pcDiskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                        pcDiskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                        pcDiskRead.NextValue(); pcDiskWrite.NextValue();
                    }
                    double rr = pcDiskRead.NextValue();
                    double wr = pcDiskWrite.NextValue();
                    lblDiskIo.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("stor.io"), Util.FmtRate(rr), Util.FmtRate(wr));
                }
                catch
                {
                    diskPerfOk = false;
                    lblDiskIo.Text = Lang.T("stor.io.none");
                }
            }
            try
            {
                DriveInfo[] all = DriveInfo.GetDrives();
                int idx = 0;
                foreach (DriveInfo d in all)
                {
                    if (idx >= 3) break;
                    if (d.DriveType != DriveType.Fixed) continue;
                    try
                    {
                        if (!d.IsReady) continue;
                        double tot = d.TotalSize, fre = d.TotalFreeSpace;
                        if (tot <= 0) continue;
                        double used = 1.0 - fre / tot;
                        string vn = d.VolumeLabel;
                        string name = d.Name.Replace("\\", "");
                        if (vn != null && vn != "") name += " (" + vn + ")";
                        driveUis[idx].L.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("stor.drive"),
                            name, fre / 1073741824.0, tot / 1073741824.0, used * 100);
                        driveUis[idx].Bar.Value = used;
                        if (idx == 0) diskShort = string.Format(CultureInfo.CurrentCulture, "{0} {1:0}%", d.Name.Replace("\\", ""), used * 100);
                        idx++;
                    }
                    catch { }
                }
                for (int i = idx; i < 3; i++) { driveUis[i].L.Text = ""; driveUis[i].Bar.Value = 0; }
                for (int i = 0; i < 3; i++) { bool on = i < idx; driveUis[i].L.Visible = on; driveUis[i].Bar.Visible = on; }
            }
            catch { }
        }

        // ================= GPU · NPU · CPU =================
        static void SetTextSafe(Label l, string t)
        {
            if (l == null) return;
            if (!l.IsHandleCreated) { l.Text = t; return; }
            try { l.BeginInvoke(new Action(delegate() { l.Text = t; })); }
            catch { try { l.Text = t; } catch { } }
        }

        static long RegistryVram(string gpuName)
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (root == null) return 0;
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (RegistryKey k = root.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            string desc = Convert.ToString(k.GetValue("DriverDesc"));
                            object qwObj = k.GetValue("qwMemorySize");
                            if (desc == null || qwObj == null) continue;
                            if (gpuName.Contains(desc) || desc.Contains(gpuName)) return Util.ToLong(qwObj);
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        void LoadHardwareInfoAsync()
        {
            Task.Run(new Action(delegate()
            {
                Thread.Sleep(1500);
                List<string> gpuNames = new List<string>();
                List<string> gpuDetails = new List<string>();
                for (int attempt = 0; attempt < 4 && gpuNames.Count == 0; attempt++)
                {
                    foreach (Dictionary<string, object> o in Util.WmiRows("root\\cimv2", "SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController"))
                    {
                        string nm = DStr(o, "Name");
                        if (nm.Trim() == "") continue;
                        string drv = DStr(o, "DriverVersion");
                        long ram = DLong(o, "AdapterRAM");
                        long shared = DLong(o, "SharedSystemMemory");
                        long qw = RegistryVram(nm);
                        string ramTxt;
                        if (qw > 0) ramTxt = string.Format(CultureInfo.CurrentCulture, Lang.T("gpu.vram"), qw / 1073741824.0);
                        else if (ram > 0)
                        {
                            ramTxt = shared > 0
                                ? string.Format(CultureInfo.CurrentCulture, Lang.T("gpu.shared"), ram / 1073741824.0, shared / 1073741824.0)
                                : string.Format(CultureInfo.CurrentCulture, Lang.T("gpu.shared2"), ram / 1073741824.0);
                        }
                        else ramTxt = Lang.T("gpu.sharedsys");
                        gpuNames.Add(nm);
                        gpuDetails.Add((drv != "" ? "driver " + drv + " · " : "") + ramTxt);
                    }
                    if (gpuNames.Count == 0) Thread.Sleep(3000);
                }

                string npuName = null, npuStatus = "";
                foreach (Dictionary<string, object> o in Util.WmiRows("root\\CIMV2", "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'ComputeAccelerator'"))
                {
                    string nm = DStr(o, "Name");
                    if (nm.Trim() == "") continue;
                    npuName = nm;
                    npuStatus = DStr(o, "Status");
                    break;
                }

                string cpuLine = "";
                foreach (Dictionary<string, object> o in Util.WmiRows("root\\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
                {
                    cpuLine = DStr(o, "Name").Trim();
                    long cores = DLong(o, "NumberOfCores");
                    long threads = DLong(o, "NumberOfLogicalProcessors");
                    if (cores > 0) cpuLine += string.Format(CultureInfo.CurrentCulture, " ({0}C/{1}T)", cores, threads > 0 ? threads : cores);
                    break;
                }

                SetTextSafe(lblGpu1, gpuNames.Count > 0 ? gpuNames[0] : Lang.T("gpu.none"));
                SetTextSafe(lblGpu1d, gpuNames.Count > 0 ? gpuDetails[0] : "");
                SetTextSafe(lblGpu2, gpuNames.Count > 1 ? gpuNames[1] : "");
                SetTextSafe(lblGpu2d, gpuNames.Count > 1 ? gpuDetails[1] : "");
                SetTextSafe(lblNpu, npuName != null ? "NPU: " + npuName : Lang.T("npu.none"));
                SetTextSafe(lblNpuD, npuName != null ? "Status " + (npuStatus != "" ? npuStatus : "OK") + " · akselerator AI (ComputeAccelerator)" : "");
                SetTextSafe(lblCpuInfo, "CPU: " + (cpuLine != "" ? cpuLine : "\u2014"));
            }));
        }

        // ================= SISTEM =================
        void UpdateSystem()
        {
            FILETIME idle, kern, usr;
            if (GetSystemTimes(out idle, out kern, out usr))
            {
                ulong i = FtToUlong(idle), k = FtToUlong(kern), u = FtToUlong(usr);
                ulong total = k + u;
                if (cpuPrimed)
                {
                    ulong dTotal = total - prevTotal, dIdle = i - prevIdle;
                    if (dTotal > 0)
                    {
                        double cpu = 100.0 * (1.0 - (double)dIdle / (double)dTotal);
                        if (cpu < 0) cpu = 0; if (cpu > 100) cpu = 100;
                        lblCpu.Text = string.Format(CultureInfo.CurrentCulture, "CPU {0:0}%", cpu);
                        cpuPctVal = cpu;
                    }
                }
                else cpuPrimed = true;
                prevIdle = i; prevTotal = total;
            }

            MEMORYSTATUSEX mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                double totalGb = mem.ullTotalPhys / 1073741824.0;
                double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
                lblRam.Text = string.Format(CultureInfo.CurrentCulture, "RAM {0:0.0}/{1:0.0} GB", usedGb, totalGb);
                double ramPct = totalGb > 0 ? (usedGb / totalGb) * 100.0 : 0;
                ramPctVal = ramPct;
                if (ramPct >= 90 && (DateTime.Now - alertRamAt).TotalMinutes > 10 && trayIcon != null)
                {
                    alertRamAt = DateTime.Now;
                    try { trayIcon.ShowBalloonTip(8000, Lang.T("alert.ram.title"), string.Format(CultureInfo.CurrentCulture, Lang.T("alert.ram.body"), ramPct), ToolTipIcon.Warning); } catch { }
                    LogAlert("RAM", ramPct.ToString("0", CultureInfo.InvariantCulture) + "%");
                }
            }

            lblUptime.Text = Lang.T("sys.up") + Util.FmtDur(TimeSpan.FromMilliseconds(GetTickCount64()));

            try
            {
                DriveInfo c = new DriveInfo("C");
                double freeGb = c.TotalFreeSpace / 1073741824.0;
                double totGb = c.TotalSize / 1073741824.0;
                lblDisk.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("sys.cfree"), freeGb, totGb);
            }
            catch { }

            lblPc.Text = "PC: " + Environment.MachineName;
            lblOs.Text = GetOsText();
            sysShort = lblCpu.Text + " · " + lblRam.Text;
        }

        string GetOsText()
        {
            if (osTextCache != null) return osTextCache;
            string result = "";
            try
            {
                const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                string build = Convert.ToString(Registry.GetValue(key, "CurrentBuildNumber", ""));
                string ubr = Convert.ToString(Registry.GetValue(key, "UBR", ""));
                string ver = Convert.ToString(Registry.GetValue(key, "DisplayVersion", ""));
                string prod = Convert.ToString(Registry.GetValue(key, "ProductName", ""));
                int buildNo = 0;
                int.TryParse(build, out buildNo);
                string name = buildNo >= 22000 ? "Windows 11" : "Windows 10";
                string edition = prod.Replace("Windows 11 ", "").Replace("Windows 10 ", "").Trim();
                result = name + (edition != "" ? " " + edition : "") + (ver != "" ? " " + ver : "") + " (build " + build + "." + ubr + ")";
            }
            catch { result = Environment.OSVersion.ToString(); }
            osTextCache = result;
            return result;
        }

        // ================= ADAPTER =================
        void UpdateAdapter()
        {
            string ssid = GetSsidCached();
            NetworkInterface best = null;
            NetworkInterface[] allNis = new NetworkInterface[0];
            try { allNis = NetworkInterface.GetAllNetworkInterfaces(); } catch { }
            foreach (NetworkInterface ni in allNis)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (best == null) best = ni;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) { best = ni; break; }
            }
            if (best == null) { lblAdapter.Text = Lang.T("net.none"); return; }
            bool isWifi = best.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            string kind = isWifi ? "Wi-Fi" : best.NetworkInterfaceType.ToString();
            double mbps = best.Speed / 1000000.0;
            string spd = mbps >= 1000
                ? string.Format(CultureInfo.CurrentCulture, "{0:0.#} Gbps", mbps / 1000.0)
                : string.Format(CultureInfo.CurrentCulture, "{0:0} Mbps", mbps);
            string name = isWifi && ssid != null && ssid != "" ? kind + " \"" + ssid + "\"" : kind;
            lblAdapter.Text = string.Format(CultureInfo.CurrentCulture, Lang.T("net.adapter"), name, best.Description, spd);
        }

        // `netsh wlan show interfaces` dihitung Windows 11 sebagai akses LOKASI: tiap panggilan memunculkan ikon panah
        // lokasi di tray dan menggeser seluruh area tray (widget ikut loncat). Jadi hanya dipanggil saat identitas
        // jaringan Wi-Fi berubah (adaptor/alamat IP), atau paling sering sekali per jam.
        string ssidCache = null, ssidKey = "";
        DateTime ssidAt = DateTime.MinValue;

        string GetSsidCached()
        {
            string key = "";
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    key += ni.Id + ";";
                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                        if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) key += u.Address + ";";
                }
            }
            catch { }
            if (key == "") { ssidCache = null; ssidKey = ""; return null; }      // tidak ada Wi-Fi aktif: netsh tidak perlu
            if (key == ssidKey && (DateTime.Now - ssidAt).TotalMinutes < 60) return ssidCache;
            ssidKey = key; ssidAt = DateTime.Now;
            ssidCache = GetSsid();
            return ssidCache;
        }

        static string GetSsid()
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo("netsh", "wlan show interfaces");
                si.UseShellExecute = false;
                si.RedirectStandardOutput = true;
                si.CreateNoWindow = true;
                using (Process p = Process.Start(si))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (string lineT in output.Split('\n'))
                    {
                        string line = lineT.Trim();
                        if (line.StartsWith("SSID") && !line.StartsWith("BSSID"))
                        {
                            int i = line.IndexOf(':');
                            if (i >= 0)
                            {
                                string v = line.Substring(i + 1).Trim();
                                if (v != "") return v;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // ================= CUACA =================
        static async Task<string> GetStringTimed(HttpClient c, string url, int seconds)
        {
            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                using (HttpResponseMessage resp = await c.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token))
                {
                    if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);
                    return await resp.Content.ReadAsStringAsync();
                }
            }
            finally { cts.Dispose(); }
        }

        string WxCacheFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wx_cache.json"); } }

        void FailWeather(string msg)
        {
            nextWxRetry = DateTime.Now.AddSeconds(60);
            if (msg == null) msg = Lang.T("wx.unknown");
            if (msg.Length > 60) msg = msg.Substring(0, 60);
            lblWxUpdated.Text = Lang.T("wx.fail") + msg + " · ulang 60 dtk";
        }

        void SaveWxCache(Dictionary<string, object> d)
        {
            try { File.WriteAllText(WxCacheFile, new JavaScriptSerializer().Serialize(d)); }
            catch { }
        }

        void LoadWxCache()
        {
            try
            {
                if (!File.Exists(WxCacheFile)) return;
                Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(WxCacheFile));
                if (Util.GetStr(d, "city") == "") return;
                lblWxCity.Text = Util.GetStr(d, "city");
                lblWxTemp.Text = Util.GetStr(d, "temp");
                lblWxDesc.Text = Util.GetStr(d, "desc");
                lblWxFeels.Text = Util.GetStr(d, "feels");
                lblWxHum.Text = Util.GetStr(d, "hum");
                lblWxWind.Text = Util.GetStr(d, "wind");
                lblWxMinMax.Text = Util.GetStr(d, "minmax");
                lblWxUpdated.Text = "Cache " + Util.GetStr(d, "updated") + Lang.T("wx.reloading");
                lblIp.Text = Util.GetStr(d, "ip");
                lblIsp.Text = Util.GetStr(d, "isp");
                lastWxShort = Util.GetStr(d, "short");
            }
            catch { }
        }

        // Lokasi cuaca tetap. Default: Sukajadi, Bandung. Ubah lewat location.txt di folder aplikasi:
        //   baris tunggal  lat,lon,Nama tampilan   (contoh: -6.889,107.595,Sukajadi, Bandung)
        /// Lokasi manual dari location.txt (override): ada hanya bila file-nya ada dan valid.
        static bool TryLoadFixedLocation(out double lat, out double lon, out string name)
        {
            lat = 0; lon = 0; name = null;
            try
            {
                string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "location.txt");
                if (!File.Exists(f)) return false;
                string[] p = File.ReadAllText(f).Trim().Split(new char[] { ',' }, 3);
                double a, b;
                if (p.Length >= 2 && double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                    && double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b)
                    && a >= -90 && a <= 90 && b >= -180 && b <= 180)
                {
                    lat = a; lon = b;
                    name = (p.Length == 3 && p[2].Trim() != "") ? p[2].Trim() : null;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// ------------------------------------------------------------------
        /// Memori lokasi per jaringan Wi-Fi (locations.json): "otomatis kedepannya" —
        /// di rumah (SSID Meliyah.Market) terkunci koordinat rumah; di jaringan lain
        /// GPS/IP dipakai dan, bila GPS wajar, direktif otomatis tersimpan untuk SSID itu.
        /// ------------------------------------------------------------------
        static string WifiLocFile()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "locations.json");
        }

        static bool TryLoadWifiLocation(string ssid, out double lat, out double lon, out string name)
        {
            lat = 0; lon = 0; name = null;
            try
            {
                string f = WifiLocFile();
                if (!File.Exists(f) || string.IsNullOrEmpty(ssid)) return false;
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> d = js.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                if (d == null || !d.ContainsKey(ssid)) return false;
                Dictionary<string, object> e = d[ssid] as Dictionary<string, object>;
                if (e == null) return false;
                double a = Util.GetNumD(e, "lat"), b = Util.GetNumD(e, "lon");
                if (double.IsNaN(a) || double.IsNaN(b) || a < -90 || a > 90 || b < -180 || b > 180) return false;
                lat = a; lon = b;
                if (e.ContainsKey("name")) name = Convert.ToString(e["name"]);
                if (string.IsNullOrEmpty(name)) name = null;
                return true;
            }
            catch { return false; }
        }

        static void SaveWifiLocation(string ssid, double lat, double lon, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(ssid)) return;
                string f = WifiLocFile();
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> d = new Dictionary<string, object>();
                if (File.Exists(f))
                {
                    try
                    {
                        Dictionary<string, object> rd = js.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                        if (rd != null) d = rd;
                    }
                    catch { }
                }
                string cur = Math.Round(lat, 5).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(lon, 5).ToString(CultureInfo.InvariantCulture);
                try
                {
                    Dictionary<string, object> pe = d.ContainsKey(ssid) ? d[ssid] as Dictionary<string, object> : null;
                    if (pe != null)
                    {
                        string prev = Convert.ToString(pe.ContainsKey("lat") ? pe["lat"] : null) + "," + Convert.ToString(pe.ContainsKey("lon") ? pe["lon"] : null);
                        if (prev == cur) return;   // tidak berubah, tidak perlu tulis
                    }
                }
                catch { }
                Dictionary<string, object> e = new Dictionary<string, object>();
                e["lat"] = Math.Round(lat, 5);
                e["lon"] = Math.Round(lon, 5);
                if (!string.IsNullOrEmpty(name)) e["name"] = name;
                d[ssid] = e;
                File.WriteAllText(f, js.Serialize(d));
                Dbg.Log("TEMP-wx wifi-lokasi disimpan: " + ssid);
            }
            catch { }
        }

        /// jarak lingkaran besar (km) — untuk uji plausibilitas GPS vs IP
        static double HaversineKm(double a1, double b1, double a2, double b2)
        {
            double r = 6371.0;
            double dLat = (a2 - a1) * Math.PI / 180.0;
            double dLon = (b2 - b1) * Math.PI / 180.0;
            double s1 = Math.Sin(dLat / 2), s2 = Math.Sin(dLon / 2);
            double h = s1 * s1 + Math.Cos(a1 * Math.PI / 180.0) * Math.Cos(a2 * Math.PI / 180.0) * s2 * s2;
            return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }

#if !NO_WINRT
        // posisi perangkat sungguhan (Windows Location Services / GPS-Wi-Fi): akurasi tinggi,
        // di-cache 10 menit supaya pemanggilan tiap 5 menit tidak membangunkan sensor terus-menerus
        static double devLat = 0, devLon = 0;
        static DateTime devAt = DateTime.MinValue;

        static bool TryGetDeviceLocation(out double lat, out double lon)
        {
            lat = 0; lon = 0;
            if (devAt != DateTime.MinValue && (DateTime.UtcNow - devAt).TotalMinutes < 10)
            {
                lat = devLat; lon = devLon;
                return lat != 0 || lon != 0;
            }
            try
            {
                Windows.Devices.Geolocation.GeolocationAccessStatus st = Windows.Devices.Geolocation.Geolocator.RequestAccessAsync().AsTask().GetAwaiter().GetResult();
                if (st == Windows.Devices.Geolocation.GeolocationAccessStatus.Denied) return false;
                Windows.Devices.Geolocation.Geolocator g = new Windows.Devices.Geolocation.Geolocator();
                try { g.DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.High; } catch { }
                using (System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource(9000))
                {
                    Windows.Devices.Geolocation.Geoposition pos = g.GetGeopositionAsync().AsTask(cts.Token).GetAwaiter().GetResult();
                    if (pos == null || pos.Coordinate == null || pos.Coordinate.Point == null) return false;
                    double a = pos.Coordinate.Point.Position.Latitude;
                    double b = pos.Coordinate.Point.Position.Longitude;
                    if (a < -90 || a > 90 || b < -180 || b > 180 || (a == 0 && b == 0)) return false;
                    devLat = a; devLon = b; devAt = DateTime.UtcNow;
                    lat = a; lon = b;
                    Dbg.Log("TEMP-wx gps " + a.ToString("0.####", CultureInfo.InvariantCulture) + "," + b.ToString("0.####", CultureInfo.InvariantCulture));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Dbg.Log("TEMP-wx gpsEX " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }
#else
        static bool TryGetDeviceLocation(out double lat, out double lon)
        {
            lat = 0; lon = 0;
            return false;   // build tanpa referensi WinRT (tanpa SDK) — GPS tidak tersedia
        }
#endif

        /// Nama kota dari koordinat (BigDataCloud, tanpa kunci; bahasa mengikuti UI).
        async Task<string> ReverseGeocodeAsync(double lat, double lon)
        {
            try
            {
                string langCode = Lang.Mode == "en" ? "en" : "id";
                string u = string.Format(CultureInfo.InvariantCulture,
                    "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={0}&longitude={1}&localityLanguage={2}",
                    lat, lon, langCode);
                string j = await GetStringTimed(SharedHttp.Client, u, 10);
                if (string.IsNullOrEmpty(j)) return null;
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> d = js.DeserializeObject(j) as Dictionary<string, object>;
                string city = Util.GetStr(d, "city"); if (city == "") city = Util.GetStr(d, "locality");
                string sub = Util.GetStr(d, "principalSubdivision");
                string ctry = Util.GetStr(d, "countryName");
                string name = city;
                if (sub != "" && sub != city) name = (name == "" ? sub : name + ", " + sub);
                if (ctry != "") name = (name == "" ? ctry : name + " \u00B7 " + ctry);
                return name == "" ? null : name;
            }
            catch { return null; }
        }

        async void LoadWeatherAsync()
        {
            if (wxBusy) return;
            wxBusy = true;
            lblWxUpdated.Text = Lang.T("wx.loading2");
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();

                string geoJson = null;
                try { geoJson = await GetStringTimed(SharedHttp.Client, "https://ipapi.co/json/", 12); }
                catch { }
                if (geoJson == null || geoJson.Trim() == "" || geoJson.TrimStart().StartsWith("<"))
                {
                    try { geoJson = await GetStringTimed(SharedHttp.Plain, "http://ip-api.com/json/", 12); }
                    catch { }
                }
                // geolokasi IP hanya untuk menampilkan IP/ISP; lokasi cuaca TETAP (location.txt), bukan tebakan IP
                Dictionary<string, object> geo = new Dictionary<string, object>();
                if (geoJson != null && geoJson.Trim() != "" && !geoJson.TrimStart().StartsWith("<"))
                {
                    try { geo = js.DeserializeObject(geoJson) as Dictionary<string, object>; } catch { }
                    if (geo == null) geo = new Dictionary<string, object>();
                }
                string ip = Util.GetStr(geo, "ip"); if (ip == "") ip = Util.GetStr(geo, "query");
                string city = Util.GetStr(geo, "city");
                string region = Util.GetStr(geo, "region"); if (region == "") region = Util.GetStr(geo, "regionName");
                string country = Util.GetStr(geo, "country_name"); if (country == "") country = Util.GetStr(geo, "country");
                string isp = Util.GetStr(geo, "org"); if (isp == "") isp = Util.GetStr(geo, "isp");

                lblIp.Text = Lang.T("net.ip") + (ip != "" ? ip : "\u2014");
                lblIsp.Text = Lang.T("net.isp") + (isp != "" ? isp : "\u2014");

                double lat = 0, lon = 0;
                string locName = null;
                string locSrc = "";
                bool haveLoc = TryLoadFixedLocation(out lat, out lon, out locName);
                if (haveLoc) locSrc = "file";
                // memori lokasi per jaringan Wi-Fi (rumah/kantor dikenali otomatis)
                string ssidNow = null;
                try { ssidNow = GetSsidCached(); } catch { }
                if (!haveLoc)
                {
                    string wn;
                    double wa, wb;
                    if (TryLoadWifiLocation(ssidNow, out wa, out wb, out wn))
                    {
                        lat = wa; lon = wb; haveLoc = true; locSrc = "wifi"; locName = wn;
                    }
                }
                // koordinat IP — cadangan + pembanding plausibilitas GPS (VPN/Wi-Fi mapping bisa salah,
                // kasus nyata: GPS Windows bilang Dhaka-Bangladesh padahal IP Indonesia)
                double ipLat = 0, ipLon = 0;
                {
                    double a2 = Util.GetNumD(geo, "latitude"); if (double.IsNaN(a2) || a2 == 0) a2 = Util.GetNumD(geo, "lat");
                    double b2 = Util.GetNumD(geo, "longitude"); if (double.IsNaN(b2) || b2 == 0) b2 = Util.GetNumD(geo, "lon");
                    if (a2 >= -90 && a2 <= 90 && b2 >= -180 && b2 <= 180 && (a2 != 0 || b2 != 0)) { ipLat = a2; ipLon = b2; }
                }
                bool haveIp = ipLat != 0 || ipLon != 0;
                Dbg.Log("TEMP-wx ip=" + (haveIp ? ipLat.ToString("0.##", CultureInfo.InvariantCulture) + "," + ipLon.ToString("0.##", CultureInfo.InvariantCulture) : "(tidak ada)"));
                if (!haveLoc)
                {
                    double dLat, dLon;
                    if (TryGetDeviceLocation(out dLat, out dLon))
                    {
                        double dist = haveIp ? HaversineKm(dLat, dLon, ipLat, ipLon) : 0;
                        if (haveIp && dist > 300)
                        {
                            Dbg.Log("TEMP-wx gps ditolak (" + dist.ToString("0") + " km dari IP) \u2014 pakai IP");
                        }
                        else
                        {
                            lat = dLat; lon = dLon; haveLoc = true; locSrc = "gps"; locName = null;
                        }
                    }
                }
                if (!haveLoc && haveIp)
                {
                    lat = ipLat; lon = ipLon; haveLoc = true; locSrc = "ip"; locName = null;
                }
                if (!haveLoc)
                {
                    lat = -6.8890; lon = 107.5950; locName = "Sukajadi, Bandung \u00B7 Indonesia"; locSrc = "default";
                }
                if (string.IsNullOrEmpty(locName))
                {
                    if (locSrc == "gps")
                    {
                        locName = await ReverseGeocodeAsync(lat, lon);
                        if (string.IsNullOrEmpty(locName))
                            locName = string.Format(CultureInfo.InvariantCulture, "{0} ({1:0.##}, {2:0.##})", Lang.T("wx.device"), lat, lon);
                    }
                    else
                    {
                        locName = city;
                        if (locName != "" && region != "") locName += ", " + region;
                        if (locName != "" && country != "") locName += " \u00B7 " + country;
                        if (locName == "") locName = Lang.T("wx.device");
                    }
                }

                string cityText = locName;
                lblWxCity.Text = cityText;
                // auto-belajar: GPS yang wajar (dekat IP) tersimpan untuk SSID ini -> kedepannya otomatis
                if (locSrc == "gps" && !string.IsNullOrEmpty(ssidNow)) SaveWifiLocation(ssidNow, lat, lon, locName);
                Dbg.Log("TEMP-wx lokasi src=" + locSrc + " " + lat.ToString("0.####", CultureInfo.InvariantCulture) + "," + lon.ToString("0.####", CultureInfo.InvariantCulture));

                string wUrl = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                    "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m" +
                    "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1", lat, lon);

                string wJson = null;
                Exception firstErr = null;
                try { wJson = await GetStringTimed(SharedHttp.Client, wUrl, 12); }
                catch (Exception ex) { firstErr = ex; }
                if (wJson == null || wJson.Trim() == "" || wJson.TrimStart().StartsWith("<"))
                {
                    try { wJson = await GetStringTimed(SharedHttp.Plain, wUrl, 12); }
                    catch { }
                }
                if (wJson == null || wJson.Trim() == "" || wJson.TrimStart().StartsWith("<"))
                {
                    FailWeather(firstErr == null ? Lang.T("wx.err.empty") : firstErr.Message);
                    return;
                }

                Dictionary<string, object> root = js.DeserializeObject(wJson) as Dictionary<string, object>;
                Dictionary<string, object> cur = Util.GetDict(root, "current") as Dictionary<string, object>;
                Dictionary<string, object> day = Util.GetDict(root, "daily") as Dictionary<string, object>;
                if (cur == null) { FailWeather(Lang.T("wx.err.invalid")); return; }

                double t = Util.GetNumD(cur, "temperature_2m");
                double feels = Util.GetNumD(cur, "apparent_temperature");
                double hum = Util.GetNumD(cur, "relative_humidity_2m");
                double wind = Util.GetNumD(cur, "wind_speed_10m");
                double code = Util.GetNumD(cur, "weather_code");

                string tempS = double.IsNaN(t) ? "—" : string.Format(CultureInfo.CurrentCulture, "{0:0} °C", t);
                string descS = double.IsNaN(code) ? "—" : (weatherCodes.ContainsKey((int)code) ? weatherCodes[(int)code] : "Kode " + (int)code);
                string feelsS = double.IsNaN(feels) ? Lang.T("wx.feels.init") : string.Format(CultureInfo.CurrentCulture, Lang.T("wx.feels"), feels);
                string humS = double.IsNaN(hum) ? Lang.T("wx.hum.init") : string.Format(CultureInfo.CurrentCulture, Lang.T("wx.hum"), hum);
                string windS = double.IsNaN(wind) ? Lang.T("wx.wind.init") : string.Format(CultureInfo.CurrentCulture, Lang.T("wx.wind"), wind);
                string mmS = "";
                if (day != null)
                {
                    object[] mx = Util.GetArr(day, "temperature_2m_max");
                    object[] mn = Util.GetArr(day, "temperature_2m_min");
                    if (mx != null && mn != null && mx.Length > 0 && mn.Length > 0)
                    {
                        double dmax, dmin;
                        double.TryParse(Convert.ToString(mx[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out dmax);
                        double.TryParse(Convert.ToString(mn[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out dmin);
                        mmS = string.Format(CultureInfo.CurrentCulture, Lang.T("wx.today"), dmin, dmax);
                    }
                }

                lblWxTemp.Text = tempS;
                lblWxDesc.Text = descS;
                lblWxFeels.Text = feelsS;
                lblWxHum.Text = humS;
                lblWxWind.Text = windS;
                if (mmS != "") lblWxMinMax.Text = mmS;
                lblWxUpdated.Text = Lang.T("wx.updated") + DateTime.Now.ToString("HH:mm:ss") + " · Open-Meteo";

                string cityShort = cityText.Split(new char[] { ',' })[0].Trim();   // lokasi perangkat (GPS) / manual / cadangan IP
                lastWxShort = string.Format(CultureInfo.CurrentCulture, "{0} {1} · {2}", tempS.Replace(" °C", "°C"), descS, cityShort);

                Dictionary<string, object> cache = new Dictionary<string, object>();
                cache.Add("city", cityText);
                cache.Add("temp", tempS);
                cache.Add("desc", descS);
                cache.Add("feels", feelsS);
                cache.Add("hum", humS);
                cache.Add("wind", windS);
                cache.Add("minmax", mmS);
                cache.Add("updated", DateTime.Now.ToString("HH:mm:ss"));
                cache.Add("ip", lblIp.Text);
                cache.Add("isp", lblIsp.Text);
                cache.Add("short", lastWxShort);
                SaveWxCache(cache);

                nextWxRetry = DateTime.Now.AddMinutes(5);
            }
            catch (Exception ex)
            {
                FailWeather(ex.Message);
            }
            finally { wxBusy = false; }
        }
    }
}
