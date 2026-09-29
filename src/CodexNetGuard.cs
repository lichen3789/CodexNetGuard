using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("CodexNetGuard")]
[assembly: System.Reflection.AssemblyProduct("CodexNetGuard")]
[assembly: System.Reflection.AssemblyDescription("梯子 → cc-switch → Codex 三层链路强制修复工具")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.0")]

namespace CodexNetGuard
{
    // ================================================================ 数据结构

    public class CheckItem
    {
        public string Level;
        public string Layer;
        public string Name;
        public string Detail;

        public CheckItem(string level, string layer, string name, string detail)
        {
            Level = level;
            Layer = layer;
            Name = name;
            Detail = detail;
        }
    }

    public class FileBackup
    {
        public string Target = "";
        public string Backup = "";
    }

    // 修复前的系统层快照：用于复检失败时回滚，也用于「还原上次修改」
    public class FixSnapshot
    {
        public const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        public string Time = "";
        public bool ProxyEnable;
        public string ProxyServer = "";
        public string ProxyOverride = "";
        public string AutoConfigUrl = "";
        public string NoProxy = "";
        public string HttpsProxy = "";
        public string HttpProxy = "";
        public string AllProxy = "";
        public List<FileBackup> Files = new List<FileBackup>();
        public bool Valid;

        public static FixSnapshot Capture()
        {
            FixSnapshot s = new FixSnapshot();
            s.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey, false))
                {
                    if (key != null)
                    {
                        object en = key.GetValue("ProxyEnable");
                        s.ProxyEnable = (en != null) && (Convert.ToInt32(en) == 1);
                        s.ProxyServer = Convert.ToString(key.GetValue("ProxyServer"));
                        s.ProxyOverride = Convert.ToString(key.GetValue("ProxyOverride"));
                        s.AutoConfigUrl = Convert.ToString(key.GetValue("AutoConfigURL"));
                    }
                }
            }
            catch { }
            s.NoProxy = Environment.GetEnvironmentVariable("NO_PROXY", EnvironmentVariableTarget.User);
            s.HttpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY", EnvironmentVariableTarget.User);
            s.HttpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY", EnvironmentVariableTarget.User);
            s.AllProxy = Environment.GetEnvironmentVariable("ALL_PROXY", EnvironmentVariableTarget.User);
            s.Valid = true;
            return s;
        }

        public void AddFile(string target, string backup)
        {
            FileBackup fb = new FileBackup();
            fb.Target = target;
            fb.Backup = backup;
            Files.Add(fb);
        }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"time\":").Append(Quote(Time)).Append(",");
            sb.Append("\"proxyEnable\":").Append(ProxyEnable ? "1" : "0").Append(",");
            sb.Append("\"proxyServer\":").Append(Quote(ProxyServer)).Append(",");
            sb.Append("\"proxyOverride\":").Append(Quote(ProxyOverride)).Append(",");
            sb.Append("\"autoConfigUrl\":").Append(Quote(AutoConfigUrl)).Append(",");
            sb.Append("\"noProxy\":").Append(Quote(NoProxy)).Append(",");
            sb.Append("\"httpsProxy\":").Append(Quote(HttpsProxy)).Append(",");
            sb.Append("\"httpProxy\":").Append(Quote(HttpProxy)).Append(",");
            sb.Append("\"allProxy\":").Append(Quote(AllProxy)).Append(",");
            sb.Append("\"files\":[");
            for (int i = 0; i < Files.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"target\":").Append(Quote(Files[i].Target));
                sb.Append(",\"backup\":").Append(Quote(Files[i].Backup)).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static FixSnapshot Parse(string text)
        {
            FixSnapshot s = new FixSnapshot();
            if (string.IsNullOrEmpty(text)) return s;
            try
            {
                s.Time = Str(text, "time");
                s.ProxyEnable = Str(text, "proxyEnable") == "1";
                s.ProxyServer = Str(text, "proxyServer");
                s.ProxyOverride = Str(text, "proxyOverride");
                s.AutoConfigUrl = Str(text, "autoConfigUrl");
                s.NoProxy = Str(text, "noProxy");
                s.HttpsProxy = Str(text, "httpsProxy");
                s.HttpProxy = Str(text, "httpProxy");
                s.AllProxy = Str(text, "allProxy");
                Match arr = Regex.Match(text, "\"files\"\\s*:\\s*\\[([\\s\\S]*?)\\]");
                if (arr.Success)
                {
                    foreach (Match m in Regex.Matches(arr.Groups[1].Value,
                        "\\{\\s*\"target\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*,\\s*\"backup\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\}"))
                    {
                        FileBackup fb = new FileBackup();
                        fb.Target = Unescape(m.Groups[1].Value);
                        fb.Backup = Unescape(m.Groups[2].Value);
                        s.Files.Add(fb);
                    }
                }
                s.Valid = true;
            }
            catch { s.Valid = false; }
            return s;
        }

        private static string Str(string text, string key)
        {
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success) return Unescape(m.Groups[1].Value);
            m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*(\\d+)");
            if (m.Success) return m.Groups[1].Value;
            return "";
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append("\"");
            return sb.ToString();
        }

        private static string Unescape(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    if (s[i] == 'n') sb.Append('\n');
                    else if (s[i] == 'r') sb.Append('\r');
                    else if (s[i] == 't') sb.Append('\t');
                    else sb.Append(s[i]);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        public bool Apply()
        {
            bool ok = true;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey, true))
                {
                    if (key == null) return false;
                    if (string.IsNullOrEmpty(ProxyServer)) key.DeleteValue("ProxyServer", false);
                    else key.SetValue("ProxyServer", ProxyServer, RegistryValueKind.String);
                    if (string.IsNullOrEmpty(ProxyOverride)) key.DeleteValue("ProxyOverride", false);
                    else key.SetValue("ProxyOverride", ProxyOverride, RegistryValueKind.String);
                    if (string.IsNullOrEmpty(AutoConfigUrl)) key.DeleteValue("AutoConfigURL", false);
                    else key.SetValue("AutoConfigURL", AutoConfigUrl, RegistryValueKind.String);
                    key.SetValue("ProxyEnable", ProxyEnable ? 1 : 0, RegistryValueKind.DWord);
                }
                Guard.RefreshSystemProxy();
            }
            catch { ok = false; }
            bool envOk = SetEnv("NO_PROXY", NoProxy) && SetEnv("HTTPS_PROXY", HttpsProxy)
                && SetEnv("HTTP_PROXY", HttpProxy) && SetEnv("ALL_PROXY", AllProxy);
            Guard.BroadcastEnvChange();
            return envOk && ok;
        }

        private static bool SetEnv(string name, string value)
        {
            Guard.SetUserEnv(name, value);
            return true;
        }
    }

    // 一个「实探可用」的本地代理端点：端口 + 实测协议 + 属主进程
    public class ProxyEndpoint
    {
        public string Host = "127.0.0.1";
        public int Port;
        public string Kind = "http";      // http | socks5
        public string OwnerName = "";
        public int OwnerPid;
        public string Source = "";        // 已知代理进程 / 客户端配置 / 端口扫描
        public int Priority = 3;          // 1 已知进程，2 客户端配置，3 端口扫描
        public bool Verified;

        public string KindLabel()
        {
            return (Kind == "socks5") ? "SOCKS5" : "HTTP";
        }

        public string OwnerLabel()
        {
            if (OwnerName.Length == 0) return "属主未知";
            return OwnerName + " PID " + OwnerPid;
        }

        public string Label()
        {
            return Host + ":" + Port + " [" + KindLabel() + "，" + Source + "，" + OwnerLabel() + "]";
        }

        public string ProxyString()
        {
            return (Kind == "socks5") ? ("socks=" + Host + ":" + Port) : (Host + ":" + Port);
        }
    }

    // 已知的梯子客户端：用于识别、自动拉起，以及「不再抢系统代理」的配置改写
    public class ClientProfile
    {
        public string Name;
        public string Kind;              // v2rayN | clash-verge | cfw | generic
        public string[] ProcessNames;
        public string[] ExeNames;
        public string ExePath;

        public ClientProfile(string name, string kind, string[] processNames, string[] exeNames)
        {
            Name = name;
            Kind = kind;
            ProcessNames = processNames;
            ExeNames = exeNames;
            ExePath = null;
        }
    }

    public class PortOwner
    {
        public int Port;
        public int Pid;

        public PortOwner(int port, int pid)
        {
            Port = port;
            Pid = pid;
        }
    }

    // ================================================================ 核心

    public class Guard
    {
        public const string ProxyHost = "127.0.0.1";
        public const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        public const string DirectHosts = "localhost,127.0.0.1,::1,api.deepseek.com";
        public const int CcSwitchPort = 15721;
        public const string GstaticHost = "www.gstatic.com";
        public const string GstaticPath = "/generate_204";

        private static readonly string[] BypassBase = new string[] {
            "localhost", "127.*", "10.*",
            "172.16.*", "172.17.*", "172.18.*", "172.19.*", "172.20.*", "172.21.*", "172.22.*",
            "172.23.*", "172.24.*", "172.25.*", "172.26.*", "172.27.*", "172.28.*", "172.29.*",
            "172.30.*", "172.31.*", "192.168.*", "<local>"
        };

        // 常见本地代理端口：只用来给候选排序，不再当作结论
        private static readonly int[] CommonPorts = new int[] {
            10808, 10809, 1080, 7890, 7891, 7897, 7898, 10810, 1087, 8889, 2080, 2081, 20171, 20172, 7899, 2019
        };

        private static readonly string[] KnownProxyProcessNames = new string[] {
            "v2rayn", "xray", "v2ray", "sing-box", "clash", "clash-verge", "verge-mihomo", "mihomo",
            "clash-meta", "nekoray", "nekobox", "ss-local", "shadowsocks", "hysteria", "hysteria2",
            "tuic", "tuic-client", "trojan", "netch", "tun2socks", "naive", "hiddify", "hiddifycli",
            "clash-nyanpasu", "clash for windows", "cfw"
        };

        private static readonly string[] TunHints = new string[] {
            "wintun", "tun2socks", "clash", "mihomo", "sing-box", "singbox", "hysteria", "hiddify",
            "wireguard", "openvpn", "tap-windows", "tunnel", "netch", "nekoray"
        };

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
            int ulAf, int tableClass, int reserved);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
            uint flags, uint timeout, out IntPtr result);

        // 直接写 HKCU\Environment：Environment.SetEnvironmentVariable 会做阻塞式广播，
        // 机器上有卡住的窗口时每个变量要等十几秒
        public static void SetUserEnv(string name, string value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Environment", true))
                {
                    if (key == null) return;
                    if (string.IsNullOrEmpty(value)) key.DeleteValue(name, false);
                    else key.SetValue(name, value, RegistryValueKind.String);
                }
            }
            catch { }
        }

        public static void BroadcastEnvChange()
        {
            try
            {
                IntPtr result;
                SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 1000, out result);
            }
            catch { }
        }

        private static readonly object LogLock = new object();

        // ---- 环境
        public string ExeDir;
        public string SettingsPath;
        public string LogPath;
        public string ReportPath;
        public string SnapshotPath;
        public string TrayPidPath;

        // ---- 梯子层
        public List<ClientProfile> Clients = new List<ClientProfile>();
        public ClientProfile Client;
        public string ClientExe;
        public bool ClientRunning;
        public ProxyEndpoint Endpoint;
        public List<ProxyEndpoint> Candidates = new List<ProxyEndpoint>();
        public bool TunMode;
        public string TunName = "";
        public int StartTimeoutSec = 45;

        // ---- 系统层
        public bool ProxyEnabled;
        public string ProxyServer;
        public string ProxyOverride;
        public string AutoConfigUrl;
        public int ProxyPort;
        public bool ProxyPortLive;
        public bool NoProxySet;
        public string UserProxyEnv;
        public int UserProxyEnvPort;
        public bool UserProxyEnvLive;

        // ---- cc-switch
        public string CcSettingsPath;
        public bool CcProxyEnabled;
        public bool CcPortLive;
        public bool PointsToCcPort;

        // ---- Codex
        public string CodexConfigPath;
        public string CodexProvider = "";
        public string CodexBaseUrl = "";
        public string CodexBaseHost = "";
        public int CodexCode;
        public bool CodexOk;

        // ---- 探针
        public int DeepSeekCode;
        public bool DeepSeekOk;
        public int DeepSeekViaCode;
        public bool DeepSeekViaOk;
        public bool DeepSeekViaProxy;
        public int ExternalCode;
        public bool ExternalOk;
        public List<CheckItem> ProbeItems = new List<CheckItem>();
        public int LastProbePort = -1;
        public string ExitIp = "";
        public string ExitLoc = "";
        public string LastError;

        public List<CheckItem> Items = new List<CheckItem>();
        public List<string> Actions = new List<string>();

        private Dictionary<int, string> procNames = new Dictionary<int, string>();
        private List<PortOwner> lastPorts = new List<PortOwner>();

        public Guard()
        {
            ExeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (!CanWrite(ExeDir))
                ExeDir = EnsureDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexNetGuard"));
            SettingsPath = Path.Combine(ExeDir, "CodexNetGuard.ini");
            LogPath = Path.Combine(ExeDir, "CodexNetGuard-app.log");
            ReportPath = Path.Combine(ExeDir, "CodexNetGuard-report.txt");
            SnapshotPath = Path.Combine(ExeDir, "CodexNetGuard-restore.json");
            TrayPidPath = Path.Combine(ExeDir, "tray.pid");
            Clients = CreateClientProfiles();
            CcSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cc-switch", "settings.json");
            CodexConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "config.toml");
        }

        private static List<ClientProfile> CreateClientProfiles()
        {
            List<ClientProfile> l = new List<ClientProfile>();
            l.Add(new ClientProfile("v2rayN", "v2rayN",
                new string[] { "v2rayn" }, new string[] { "v2rayN.exe" }));
            l.Add(new ClientProfile("Clash Verge", "clash-verge",
                new string[] { "clash-verge", "clash verge", "verge-mihomo" },
                new string[] { "Clash Verge.exe", "clash-verge.exe", "verge-mihomo.exe" }));
            l.Add(new ClientProfile("Clash for Windows", "cfw",
                new string[] { "clash for windows", "cfw" }, new string[] { "Clash for Windows.exe" }));
            l.Add(new ClientProfile("mihomo / clash", "mihomo",
                new string[] { "mihomo", "clash", "clash-meta" },
                new string[] { "mihomo.exe", "clash.exe", "clash-meta.exe" }));
            l.Add(new ClientProfile("sing-box", "generic",
                new string[] { "sing-box" }, new string[] { "sing-box.exe" }));
            l.Add(new ClientProfile("Nekoray / NekoBox", "generic",
                new string[] { "nekoray", "nekobox" }, new string[] { "nekoray.exe", "nekobox.exe" }));
            l.Add(new ClientProfile("Shadowsocks", "generic",
                new string[] { "shadowsocks", "ss-local" }, new string[] { "Shadowsocks.exe", "ss-local.exe" }));
            l.Add(new ClientProfile("Hiddify", "generic",
                new string[] { "hiddify", "hiddifycli" }, new string[] { "Hiddify.exe" }));
            return l;
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private static string EnsureDir(string dir)
        {
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        // ------------------------------------------------------------ 设置读写

        public string ReadSetting(string key, string def)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return def;
                string[] lines = File.ReadAllLines(SettingsPath, Encoding.UTF8);
                foreach (string line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim() == key) return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return def;
        }

        public void WriteSetting(string key, string value)
        {
            try
            {
                List<string> lines = new List<string>();
                bool found = false;
                if (File.Exists(SettingsPath))
                {
                    foreach (string line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && line.Substring(0, eq).Trim() == key)
                        {
                            lines.Add(key + "=" + value);
                            found = true;
                        }
                        else lines.Add(line);
                    }
                }
                if (!found) lines.Add(key + "=" + value);
                File.WriteAllLines(SettingsPath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        public void Log(string message)
        {
            lock (LogLock)
            {
                try
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message;
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                    {
                        if (File.Exists(LogPath + ".1")) File.Delete(LogPath + ".1");
                        File.Move(LogPath, LogPath + ".1");
                    }
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        // ------------------------------------------------------------ 基础探测

        public static bool IsPortListening(int port)
        {
            if (port <= 0) return false;
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult ar = client.BeginConnect(ProxyHost, port, null, null);
                if (ar.AsyncWaitHandle.WaitOne(700)) { client.EndConnect(ar); return true; }
                return false;
            }
            catch { return false; }
            finally { try { client.Close(); } catch { } }
        }

        public static bool IsProcessRunning(string name)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(name);
                try { return ps != null && ps.Length > 0; }
                finally { if (ps != null) foreach (Process p in ps) p.Dispose(); }
            }
            catch { return false; }
        }

        private static string GetRunningExePath(string name)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(name);
                try
                {
                    foreach (Process p in ps)
                    {
                        try
                        {
                            string path = p.MainModule.FileName;
                            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                        }
                        catch { }
                    }
                }
                finally { if (ps != null) foreach (Process p in ps) p.Dispose(); }
            }
            catch { }
            return null;
        }

        public static void RefreshSystemProxy()
        {
            try
            {
                InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
            }
            catch { }
        }

        // ------------------------------------------------------------ 监听端口枚举

        private void RefreshProcessCache()
        {
            procNames = new Dictionary<int, string>();
            Process[] all = Process.GetProcesses();
            try
            {
                foreach (Process p in all)
                {
                    try { procNames[p.Id] = p.ProcessName; }
                    catch { }
                }
            }
            finally { foreach (Process p in all) { try { p.Dispose(); } catch { } } }
        }

        private string ProcName(int pid)
        {
            string s;
            if (procNames.TryGetValue(pid, out s)) return s;
            return "";
        }

        public List<PortOwner> GetListeningPorts()
        {
            List<PortOwner> list = new List<PortOwner>();
            try { CollectTable(list, 2, 5); }    // AF_INET
            catch { }
            try { CollectTable(list, 23, 5); }   // AF_INET6
            catch { }
            if (list.Count == 0) CollectFromNetstat(list);
            return list;
        }

        private static void CollectTable(List<PortOwner> list, int family, int tableClass)
        {
            int size = 0;
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, tableClass, 0);
            if (ret != 122 || size <= 0) return;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                ret = GetExtendedTcpTable(buf, ref size, false, family, tableClass, 0);
                if (ret != 0) return;
                int count = Marshal.ReadInt32(buf);
                int rowSize = (family == 2) ? 24 : 56;
                long baseAddr = buf.ToInt64() + 4;
                for (int i = 0; i < count; i++)
                {
                    IntPtr cur = new IntPtr(baseAddr + i * rowSize);
                    int state, pid;
                    uint rawPort;
                    if (family == 2)
                    {
                        state = Marshal.ReadInt32(cur, 0);
                        rawPort = (uint)Marshal.ReadInt32(cur, 8);
                        pid = Marshal.ReadInt32(cur, 20);
                    }
                    else
                    {
                        rawPort = (uint)Marshal.ReadInt32(cur, 20);
                        state = Marshal.ReadInt32(cur, 48);
                        pid = Marshal.ReadInt32(cur, 52);
                    }
                    if (state != 2) continue;
                    int port = (int)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));
                    if (port <= 0) continue;
                    if (!HasPort(list, port)) list.Add(new PortOwner(port, pid));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static bool HasPort(List<PortOwner> list, int port)
        {
            foreach (PortOwner p in list) if (p.Port == port) return true;
            return false;
        }

        private static void CollectFromNetstat(List<PortOwner> list)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netstat.exe", "-ano -p TCP");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                Process p = Process.Start(psi);
                string text = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                foreach (string line in text.Split('\n'))
                {
                    string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) continue;
                    string state = parts[parts.Length - 2];
                    if (state != "LISTENING" && state != "LISTEN") continue;
                    int pid;
                    if (!int.TryParse(parts[parts.Length - 1], out pid)) continue;
                    int colon = parts[1].LastIndexOf(':');
                    if (colon < 0) continue;
                    int port;
                    if (!int.TryParse(parts[1].Substring(colon + 1), out port)) continue;
                    string addr = parts[1].Substring(0, colon);
                    if (addr != "127.0.0.1" && addr != "0.0.0.0" && addr != "[::]" && addr != "[::1]") continue;
                    if (!HasPort(list, port)) list.Add(new PortOwner(port, pid));
                }
            }
            catch { }
        }

        private static bool IsKnownProxyProcess(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (string k in KnownProxyProcessNames) if (n == k) return true;
            return false;
        }

        // ------------------------------------------------------------ 代理实探（HTTP / SOCKS5）

        private static bool AlwaysValidCert(object sender, System.Security.Cryptography.X509Certificates.X509Certificate cert,
            System.Security.Cryptography.X509Certificates.X509Chain chain, SslPolicyErrors errors)
        {
            return true;
        }

        // 经本地代理（http CONNECT 或 socks5）或直连，对目标发一次 https 请求，返回 HTTP 状态码
        private static int TunnelHttpRequest(string host, int port, string path, int proxyPort, string proxyKind,
            int timeoutMs, out string bodyText)
        {
            bodyText = "";
            TcpClient client = new TcpClient();
            try
            {
                client.NoDelay = true;
                string connectHost = (proxyPort > 0) ? ProxyHost : host;
                int connectPort = (proxyPort > 0) ? proxyPort : port;
                IAsyncResult ar = client.BeginConnect(connectHost, connectPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return 0;
                client.EndConnect(ar);
                client.ReceiveTimeout = timeoutMs;
                client.SendTimeout = timeoutMs;
                NetworkStream ns = client.GetStream();
                ns.ReadTimeout = timeoutMs;
                ns.WriteTimeout = timeoutMs;

                if (proxyPort > 0)
                {
                    if (proxyKind == "socks5")
                    {
                        if (!Socks5Handshake(ns, host, port, timeoutMs)) return 0;
                    }
                    else
                    {
                        if (!HttpConnectHandshake(ns, host, port, timeoutMs)) return 0;
                    }
                }

                SslStream ssl = new SslStream(ns, false, new RemoteCertificateValidationCallback(AlwaysValidCert));
                ssl.ReadTimeout = timeoutMs;
                ssl.WriteTimeout = timeoutMs;
                // 必须显式指定 TLS1.2：SslStream 默认只谈 TLS1.0，多数站点会直接断开
                ssl.AuthenticateAsClient(host, null, System.Security.Authentication.SslProtocols.Tls12, false);

                StringBuilder req = new StringBuilder();
                req.Append("GET ").Append(path).Append(" HTTP/1.1\r\n");
                req.Append("Host: ").Append(host).Append("\r\n");
                req.Append("User-Agent: CodexNetGuard\r\n");
                req.Append("Accept: */*\r\n");
                req.Append("Connection: close\r\n\r\n");
                byte[] reqBytes = Encoding.ASCII.GetBytes(req.ToString());
                ssl.Write(reqBytes, 0, reqBytes.Length);
                ssl.Flush();

                bodyText = ReadAll(ssl, 4096, timeoutMs);
                int code = ParseStatus(bodyText);
                try { ssl.Close(); } catch { }
                return code;
            }
            catch { return 0; }
            finally { try { client.Close(); } catch { } }
        }

        private static int ParseStatus(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            Match m = Regex.Match(text, "HTTP/\\d\\.\\d\\s+(\\d{3})");
            if (!m.Success) return 0;
            int code;
            if (int.TryParse(m.Groups[1].Value, out code)) return code;
            return 0;
        }

        private static string ReadAll(Stream s, int maxBytes, int timeoutMs)
        {
            byte[] buf = new byte[1024];
            MemoryStream ms = new MemoryStream();
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (ms.Length < maxBytes && DateTime.Now < deadline)
            {
                try
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                }
                catch { break; }
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // 读到分隔符为止（用于 CONNECT 响应头），避免在隧道上无限等待
        private static string ReadUntil(Stream s, string marker, int maxBytes, int timeoutMs)
        {
            byte[] buf = new byte[256];
            StringBuilder sb = new StringBuilder();
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (sb.Length < maxBytes && DateTime.Now < deadline)
            {
                int n;
                try { n = s.Read(buf, 0, buf.Length); }
                catch { break; }
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.ToString().IndexOf(marker) >= 0) break;
            }
            return sb.ToString();
        }

        private static byte[] ReadExact(Stream s, int count, int timeoutMs)
        {
            byte[] buf = new byte[count];
            int got = 0;
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (got < count && DateTime.Now < deadline)
            {
                int n;
                try { n = s.Read(buf, got, count - got); }
                catch { return null; }
                if (n <= 0) return null;
                got += n;
            }
            if (got < count) return null;
            return buf;
        }

        private static bool HttpConnectHandshake(NetworkStream ns, string host, int port, int timeoutMs)
        {
            string req = "CONNECT " + host + ":" + port + " HTTP/1.1\r\nHost: " + host + ":" + port
                + "\r\nProxy-Connection: keep-alive\r\n\r\n";
            byte[] b = Encoding.ASCII.GetBytes(req);
            ns.Write(b, 0, b.Length);
            ns.Flush();
            string head = ReadUntil(ns, "\r\n\r\n", 2048, timeoutMs);
            if (head.Length == 0) return false;
            Match m = Regex.Match(head, "HTTP/\\d\\.\\d\\s+(\\d{3})");
            return m.Success && m.Groups[1].Value == "200";
        }

        private static bool Socks5Handshake(NetworkStream ns, string host, int port, int timeoutMs)
        {
            byte[] greet = new byte[] { 5, 1, 0 };
            ns.Write(greet, 0, greet.Length);
            ns.Flush();
            byte[] resp = ReadExact(ns, 2, timeoutMs);
            if (resp == null || resp[0] != 5 || resp[1] != 0) return false;

            byte[] hostBytes = Encoding.ASCII.GetBytes(host);
            byte[] req = new byte[7 + hostBytes.Length];
            req[0] = 5;
            req[1] = 1;
            req[2] = 0;
            req[3] = 3;
            req[4] = (byte)hostBytes.Length;
            Buffer.BlockCopy(hostBytes, 0, req, 5, hostBytes.Length);
            req[5 + hostBytes.Length] = (byte)(port >> 8);
            req[6 + hostBytes.Length] = (byte)(port & 0xFF);
            ns.Write(req, 0, req.Length);
            ns.Flush();

            byte[] head = ReadExact(ns, 4, timeoutMs);
            if (head == null || head[0] != 5 || head[1] != 0) return false;
            int addrLen;
            if (head[3] == 1) addrLen = 4;
            else if (head[3] == 4) addrLen = 16;
            else if (head[3] == 3)
            {
                byte[] l = ReadExact(ns, 1, timeoutMs);
                if (l == null) return false;
                addrLen = l[0];
            }
            else return false;
            return ReadExact(ns, addrLen + 2, timeoutMs) != null;
        }

        private static bool IsOkCode(int code, int[] accept)
        {
            foreach (int c in accept) if (code == c) return true;
            return false;
        }

        // 一个端口只有实探通过（HTTP 或 SOCKS5 至少一种能真的把流量送出去）才算可用
        private static bool VerifyEndpoint(ProxyEndpoint ep, int timeoutMs)
        {
            string body;
            int code = TunnelHttpRequest(GstaticHost, 443, GstaticPath, ep.Port, "http", timeoutMs, out body);
            if (IsOkCode(code, new int[] { 200, 204 }))
            {
                ep.Kind = "http";
                ep.Verified = true;
                return true;
            }
            code = TunnelHttpRequest(GstaticHost, 443, GstaticPath, ep.Port, "socks5", timeoutMs, out body);
            if (IsOkCode(code, new int[] { 200, 204 }))
            {
                ep.Kind = "socks5";
                ep.Verified = true;
                return true;
            }
            // 已知客户端端口再补一次：节点重启/抖动时避免误判为不可用
            if (ep.Priority <= 2)
            {
                code = TunnelHttpRequest(GstaticHost, 443, GstaticPath, ep.Port, "http", timeoutMs, out body);
                if (IsOkCode(code, new int[] { 200, 204 }))
                {
                    ep.Kind = "http";
                    ep.Verified = true;
                    return true;
                }
            }
            return false;
        }

        private static void VerifyAll(List<ProxyEndpoint> list, int timeoutMs)
        {
            if (list.Count == 0) return;
            List<Thread> threads = new List<Thread>();
            foreach (ProxyEndpoint ep in list)
            {
                ProxyEndpoint cur = ep;
                Thread t = new Thread(new ThreadStart(delegate { VerifyEndpoint(cur, timeoutMs); }));
                t.IsBackground = true;
                t.Start();
                threads.Add(t);
            }
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs * 2 + 2000);
            foreach (Thread t in threads)
            {
                int wait = (int)(deadline - DateTime.Now).TotalMilliseconds;
                if (wait < 0) wait = 0;
                t.Join(wait);
            }
        }

        private static int PortRank(int port)
        {
            for (int i = 0; i < CommonPorts.Length; i++) if (CommonPorts[i] == port) return i;
            return CommonPorts.Length + 1;
        }

        private static ProxyEndpoint PickBest(List<ProxyEndpoint> list)
        {
            ProxyEndpoint best = null;
            foreach (ProxyEndpoint ep in list)
            {
                if (!ep.Verified) continue;
                if (best == null) { best = ep; continue; }
                if (ep.Priority != best.Priority)
                {
                    if (ep.Priority < best.Priority) best = ep;
                    continue;
                }
                int ra = PortRank(ep.Port);
                int rb = PortRank(best.Port);
                if (ra != rb) { if (ra < rb) best = ep; continue; }
                if (ep.Port < best.Port) best = ep;
            }
            return best;
        }

        private static bool HasEndpoint(List<ProxyEndpoint> list, int port)
        {
            foreach (ProxyEndpoint ep in list) if (ep.Port == port) return true;
            return false;
        }

        private static PortOwner FindOwner(List<PortOwner> ports, int port)
        {
            foreach (PortOwner p in ports) if (p.Port == port) return p;
            return null;
        }

        private static readonly string[] NoiseProcesses = new string[] {
            "system", "system idle process", "idle", "svchost", "lsass", "services", "wininit", "winlogon",
            "csrss", "smss", "spoolsv", "dwm", "explorer", "runtimebroker", "searchindexer", "securityhealthservice",
            "msmpeng", "nissrv", "taskhostw", "sihost", "ctfmon", "conhost", "wmiprvse", "searchapp", "shellexperiencehost",
            "startmenuexperiencehost", "textinputhost", "wpscloudsvr", "wpscenter", "sogousmartassistant",
            "pddservice", "weixin", "wechat", "qq", "dingtalk", "alibabaprotect", "edrservice", "hipsdaemon",
            "jhi_service", "360tray", "huorong", "qqpctray", "kxe", "jhi_service", "officeclicktorun"
        };

        private static bool IsNoiseProcess(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string n = name.ToLowerInvariant();
            foreach (string k in NoiseProcesses) if (n == k) return true;
            return false;
        }

        // ------------------------------------------------------------ 端点发现

        private DateTime lastDiscovery = DateTime.MinValue;

        // 同一次运行里重复探测太贵：60 秒内复用上次结论，只做一次轻量复验
        public ProxyEndpoint DiscoverEndpoint()
        {
            if (Endpoint != null && Endpoint.Verified && IsPortListening(Endpoint.Port)
                && (DateTime.Now - lastDiscovery).TotalSeconds < 60)
            {
                string body;
                int code = TunnelHttpRequest(GstaticHost, 443, GstaticPath, Endpoint.Port, Endpoint.Kind, 8000, out body);
                if (IsOkCode(code, new int[] { 200, 204 }))
                {
                    lastDiscovery = DateTime.Now;
                    return Endpoint;
                }
            }
            return DiscoverEndpointFull();
        }

        private ProxyEndpoint DiscoverEndpointFull()
        {
            RefreshProcessCache();
            Candidates = new List<ProxyEndpoint>();
            List<PortOwner> ports = GetListeningPorts();
            lastPorts = ports;
            lastDiscovery = DateTime.Now;
            int selfPid = Process.GetCurrentProcess().Id;

            // 1) 已知代理进程拥有的端口
            foreach (PortOwner po in ports)
            {
                if (po.Pid <= 0 || po.Port == CcSwitchPort) continue;
                string name = ProcName(po.Pid);
                if (!IsKnownProxyProcess(name)) continue;
                Candidates.Add(MakeEndpoint(po, name, "已知代理进程", 1, po.Port));
            }

            // 2) 客户端配置文件里写明的端口
            foreach (int port in ClientConfigPorts())
            {
                if (port <= 0 || port == CcSwitchPort) continue;
                if (HasEndpoint(Candidates, port)) continue;
                if (!IsPortListening(port)) continue;
                PortOwner owner = FindOwner(ports, port);
                string name = (owner != null) ? ProcName(owner.Pid) : "";
                Candidates.Add(MakeEndpoint(owner, name, "客户端配置", 2, port));
            }

            VerifyAll(Candidates, 8000);
            ProxyEndpoint best = PickBest(Candidates);
            if (best != null) return best;

            // 3) 其它本机监听端口：必须实探通过才采纳
            List<ProxyEndpoint> others = new List<ProxyEndpoint>();
            foreach (PortOwner po in ports)
            {
                if (po.Port == CcSwitchPort || po.Port < 1024) continue;
                if (po.Pid <= 0 || po.Pid == selfPid || po.Pid == 4) continue;
                if (HasEndpoint(Candidates, po.Port) || HasEndpoint(others, po.Port)) continue;
                string name = ProcName(po.Pid);
                if (name.Length > 0 && IsNoiseProcess(name)) continue;
                others.Add(MakeEndpoint(po, name, "端口扫描", 3, po.Port));
            }
            others.Sort(new Comparison<ProxyEndpoint>(delegate(ProxyEndpoint a, ProxyEndpoint b)
            {
                int ra = PortRank(a.Port);
                int rb = PortRank(b.Port);
                if (ra != rb) return ra - rb;
                return a.Port - b.Port;
            }));
            if (others.Count > 12) others = others.GetRange(0, 12);
            Candidates.AddRange(others);
            VerifyAll(others, 4000);
            return PickBest(others);
        }

        private static ProxyEndpoint MakeEndpoint(PortOwner po, string name, string source, int priority, int port)
        {
            ProxyEndpoint ep = new ProxyEndpoint();
            ep.Port = port;
            ep.Source = source;
            ep.Priority = priority;
            ep.OwnerName = name == null ? "" : name;
            if (po != null) ep.OwnerPid = po.Pid;
            return ep;
        }

        private List<int> ClientConfigPorts()
        {
            List<int> list = new List<int>();
            try
            {
                string dir = ClientDir();
                if (dir != null)
                {
                    string gui = Path.Combine(dir, "guiConfigs", "guiNConfig.json");
                    if (File.Exists(gui))
                    {
                        string text = SafeRead(gui);
                        int idx = text.IndexOf("\"inbound\"");
                        if (idx >= 0)
                        {
                            int len = Math.Min(800, text.Length - idx);
                            Match m = Regex.Match(text.Substring(idx, len), "\"LocalPort\"\\s*:\\s*(\\d+)");
                            if (m.Success) AddPort(list, int.Parse(m.Groups[1].Value));
                        }
                    }
                    string cfg = Path.Combine(dir, "config.yaml");
                    if (File.Exists(cfg))
                    {
                        foreach (Match m in Regex.Matches(SafeRead(cfg),
                            "(?m)^\\s*(?:mixed-port|port|socks-port|listen_port)\\s*:\\s*(\\d+)"))
                            AddPort(list, int.Parse(m.Groups[1].Value));
                    }
                }
                string manual = ReadSetting("clientPort", "");
                if (manual.Length > 0)
                {
                    int p;
                    if (int.TryParse(manual, out p)) AddPort(list, p);
                }
            }
            catch { }
            return list;
        }

        private static void AddPort(List<int> list, int port)
        {
            if (port <= 0 || port > 65535) return;
            if (!list.Contains(port)) list.Add(port);
        }

        public string ClientDir()
        {
            if (ClientExe == null) return null;
            try { return Path.GetDirectoryName(ClientExe); }
            catch { return null; }
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch { return ""; }
        }

        // ------------------------------------------------------------ 客户端识别

        public void DetectClient()
        {
            Client = null;
            ClientExe = null;
            ClientRunning = false;

            string configured = ReadSetting("clientPath", "");
            if (configured.Length == 0) configured = ReadSetting("v2rayNPath", "");
            if (configured.Length > 0 && File.Exists(configured))
            {
                Client = ProfileForExe(configured);
                ClientExe = configured;
                try
                {
                    if (IsProcessRunning(Path.GetFileNameWithoutExtension(configured))) ClientRunning = true;
                }
                catch { }
            }

            foreach (ClientProfile p in Clients)
            {
                foreach (string proc in p.ProcessNames)
                {
                    if (!IsProcessRunning(proc)) continue;
                    ClientRunning = true;
                    if (Client == null)
                    {
                        string exe = GetRunningExePath(proc);
                        if (exe != null && File.Exists(exe))
                        {
                            Client = p;
                            ClientExe = exe;
                        }
                        else if (ClientExe == null)
                        {
                            Client = p;
                        }
                    }
                    break;
                }
            }
        }

        public ClientProfile ProfileForExe(string exePath)
        {
            if (exePath == null) return null;
            string file;
            try { file = Path.GetFileName(exePath); }
            catch { return null; }
            foreach (ClientProfile p in Clients)
                foreach (string n in p.ExeNames)
                    if (string.Equals(n, file, StringComparison.OrdinalIgnoreCase)) return p;
            ClientProfile custom = new ClientProfile(Path.GetFileNameWithoutExtension(file), "generic",
                new string[] { Path.GetFileNameWithoutExtension(file).ToLowerInvariant() }, new string[] { file });
            return custom;
        }

        // 找不到客户端时才做一次全盘浅层搜索（最多 20 秒）
        public string SearchClientExe(out ClientProfile profile)
        {
            profile = null;
            List<string> names = new List<string>();
            foreach (ClientProfile p in Clients)
                foreach (string n in p.ExeNames)
                    if (!names.Contains(n)) names.Add(n);

            List<string> roots = new List<string>();
            roots.Add(ExeDir);
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                    if (d.DriveType == DriveType.Fixed && d.IsReady) roots.Add(d.RootDirectory.FullName);
            }
            catch { }

            DateTime deadline = DateTime.Now.AddSeconds(20);
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string hit = SearchDirForAny(root, names, 0, 3, deadline);
                if (hit != null)
                {
                    profile = ProfileForExe(hit);
                    return hit;
                }
                if (DateTime.Now > deadline) break;
            }
            return null;
        }

        private static string SearchDirForAny(string dir, List<string> files, int depth, int maxDepth, DateTime deadline)
        {
            if (DateTime.Now > deadline || depth > maxDepth) return null;
            try
            {
                foreach (string f in files)
                {
                    string direct = Path.Combine(dir, f);
                    if (File.Exists(direct)) return direct;
                }
                string[] subs = Directory.GetDirectories(dir);
                foreach (string sub in subs)
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith("$")) continue;
                    if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    string hit = SearchDirForAny(sub, files, depth + 1, maxDepth, deadline);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------ 客户端「不抢系统代理」

        public string ClashVergeSettingsPath()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string[] cands = new string[] {
                    Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev", "verge.yaml"),
                    Path.Combine(appData, "clash-verge-rev", "verge.yaml"),
                    Path.Combine(appData, "clash-verge", "verge.yaml")
                };
                foreach (string c in cands) if (File.Exists(c)) return c;
            }
            catch { }
            return null;
        }

        public string CfwSettingsPath()
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string[] cands = new string[] {
                    Path.Combine(home, ".config", "clash", "cfw-settings.yaml"),
                    Path.Combine(appData, "clash", "cfw-settings.yaml")
                };
                foreach (string c in cands) if (File.Exists(c)) return c;
            }
            catch { }
            return null;
        }

        private static bool CfwSystemProxyEnabled(string text)
        {
            Match m = Regex.Match(text, "(?m)^\\s*systemProxy\\s*:");
            if (!m.Success) return false;
            int start = m.Index;
            int len = Math.Min(800, text.Length - start);
            Match e = Regex.Match(text.Substring(start, len), "(?m)^\\s*enable\\s*:\\s*(true|false)");
            return e.Success && e.Groups[1].Value == "true";
        }

        // 返回需要修正的说明；空列表表示客户端不会抢系统代理
        public List<string> CheckClientHijack()
        {
            List<string> warns = new List<string>();
            try
            {
                string dir = ClientDir();
                if (dir != null)
                {
                    string gui = Path.Combine(dir, "guiConfigs", "guiNConfig.json");
                    if (File.Exists(gui))
                    {
                        Match m = Regex.Match(SafeRead(gui), "\"SysProxyType\"\\s*:\\s*(\\d+)");
                        if (m.Success && m.Groups[1].Value != "2")
                            warns.Add("v2rayN 会改动系统代理（SysProxyType=" + m.Groups[1].Value
                                + "），应改成「不改变系统代理」");
                    }
                }
                string verge = ClashVergeSettingsPath();
                if (verge != null)
                {
                    Match m = Regex.Match(SafeRead(verge), "(?m)^\\s*enable_system_proxy\\s*:\\s*(\\w+)");
                    if (m.Success && m.Groups[1].Value.ToLowerInvariant() == "true")
                        warns.Add("Clash Verge 自带的系统代理开关是打开的，会和本工具互相覆盖");
                }
                string cfw = CfwSettingsPath();
                if (cfw != null && CfwSystemProxyEnabled(SafeRead(cfw)))
                    warns.Add("Clash for Windows 自带的系统代理开关是打开的，会和本工具互相覆盖");
            }
            catch { }
            return warns;
        }

        private string BackupFile(string path)
        {
            try
            {
                string bak = path + ".bak-CodexNetGuard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, bak, true);
                return bak;
            }
            catch { return null; }
        }

        public bool StopClientProcesses(ClientProfile p, int waitSec)
        {
            if (p == null) return false;
            bool any = false;
            foreach (string name in p.ProcessNames)
            {
                try
                {
                    Process[] ps = Process.GetProcessesByName(name);
                    foreach (Process pr in ps)
                    {
                        try { pr.Kill(); any = true; }
                        catch { }
                        try { pr.Dispose(); } catch { }
                    }
                }
                catch { }
            }
            // 只有客户端本体确实在跑时才连内核一起停，避免误杀别的梯子
            if (!any) return false;
            foreach (string core in new string[] { "xray", "v2ray", "sing-box", "verge-mihomo", "mihomo" })
            {
                try
                {
                    Process[] ps = Process.GetProcessesByName(core);
                    foreach (Process pr in ps)
                    {
                        try { pr.Kill(); }
                        catch { }
                        try { pr.Dispose(); } catch { }
                    }
                }
                catch { }
            }
            DateTime deadline = DateTime.Now.AddSeconds(waitSec);
            while (DateTime.Now < deadline)
            {
                bool alive = false;
                foreach (string name in p.ProcessNames) if (IsProcessRunning(name)) { alive = true; break; }
                if (!alive) break;
                Thread.Sleep(500);
            }
            return true;
        }

        public bool StartClientExe(string exePath)
        {
            try
            {
                if (exePath == null || !File.Exists(exePath)) return false;
                ProcessStartInfo psi = new ProcessStartInfo(exePath);
                psi.WorkingDirectory = Path.GetDirectoryName(exePath);
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        // 强制让梯子客户端不再接管系统代理（改配置 -> 重启客户端）
        public void FixClientHijack(FixSnapshot snap)
        {
            string dir = ClientDir();
            bool needRestart = false;

            // 客户端退出时会用内存里的旧配置覆盖文件，所以必须先停客户端再改
            bool preStop = false;
            {
                string pg = (dir != null) ? Path.Combine(dir, "guiConfigs", "guiNConfig.json") : null;
                if (pg != null && File.Exists(pg))
                {
                    Match pm = Regex.Match(SafeRead(pg), "\"SysProxyType\"\\s*:\\s*(\\d+)");
                    if (pm.Success && pm.Groups[1].Value != "2") preStop = true;
                }
                string pv = ClashVergeSettingsPath();
                if (pv != null)
                {
                    Match pm = Regex.Match(SafeRead(pv), "(?m)^(\\s*enable_system_proxy\\s*:\\s*)(\\w+)");
                    if (pm.Success && pm.Groups[2].Value.ToLowerInvariant() == "true") preStop = true;
                }
                string pc = CfwSettingsPath();
                if (pc != null && CfwSystemProxyEnabled(SafeRead(pc))) preStop = true;
            }
            if (preStop && ClientRunning)
            {
                StopClientProcesses((Client != null) ? Client : ProfileForExe(ClientExe), 10);
                Thread.Sleep(1000);
            }

            if (dir != null)
            {
                string gui = Path.Combine(dir, "guiConfigs", "guiNConfig.json");
                if (File.Exists(gui))
                {
                    string text = SafeRead(gui);
                    Match m = Regex.Match(text, "\"SysProxyType\"\\s*:\\s*(\\d+)");
                    if (m.Success && m.Groups[1].Value != "2")
                    {
                        string bak = BackupFile(gui);
                        string replaced = ReplaceFirst(text, "\"SysProxyType\"\\s*:\\s*\\d+", "\"SysProxyType\":2");
                        if (replaced != null && replaced != text && WriteText(gui, replaced))
                        {
                            if (bak != null) snap.AddFile(gui, bak);
                            Actions.Add("v2rayN 已设为「不改变系统代理」（保留备份）");
                            Log("FIX v2rayN SysProxyType=2");
                            needRestart = true;
                        }
                        else
                        {
                            fileFixFailed = true;
                            Actions.Add("v2rayN 配置写入失败（文件只读或被占用）：" + gui);
                            Log("WARN v2rayN 配置写入失败");
                        }
                    }
                }
            }

            bool vergeChanged = false;
            string verge = ClashVergeSettingsPath();
            if (verge != null)
            {
                string text = SafeRead(verge);
                Match m = Regex.Match(text, "(?m)^(\\s*enable_system_proxy\\s*:\\s*)(\\w+)");
                if (m.Success && m.Groups[2].Value.ToLowerInvariant() == "true")
                {
                    string bak = BackupFile(verge);
                    string replaced = text.Substring(0, m.Index) + m.Groups[1].Value + "false"
                        + text.Substring(m.Index + m.Length);
                    if (WriteText(verge, replaced))
                    {
                        if (bak != null) snap.AddFile(verge, bak);
                        Actions.Add("Clash Verge 自带系统代理开关已关闭（保留备份）");
                        Log("FIX Clash Verge enable_system_proxy=false");
                        vergeChanged = true;
                    }
                    else
                    {
                        fileFixFailed = true;
                        Actions.Add("Clash Verge 配置写入失败（文件只读或被占用）：" + verge);
                    }
                }
            }

            bool cfwChanged = false;
            string cfw = CfwSettingsPath();
            if (cfw != null)
            {
                string text = SafeRead(cfw);
                if (CfwSystemProxyEnabled(text))
                {
                    Match block = Regex.Match(text, "(?m)^\\s*systemProxy\\s*:");
                    if (block.Success)
                    {
                        int start = block.Index;
                        int len = Math.Min(800, text.Length - start);
                        string seg = text.Substring(start, len);
                        Match e = Regex.Match(seg, "(?m)^(\\s*enable\\s*:\\s*)(true)");
                        if (e.Success)
                        {
                            string bak = BackupFile(cfw);
                            string newSeg = seg.Substring(0, e.Index) + e.Groups[1].Value + "false"
                                + seg.Substring(e.Index + e.Length);
                            string replaced = text.Substring(0, start) + newSeg + text.Substring(start + len);
                            if (WriteText(cfw, replaced))
                            {
                                if (bak != null) snap.AddFile(cfw, bak);
                                Actions.Add("Clash for Windows 自带系统代理开关已关闭（保留备份）");
                                Log("FIX CFW systemProxy.enable=false");
                                cfwChanged = true;
                            }
                            else
                            {
                                fileFixFailed = true;
                                Actions.Add("Clash for Windows 配置写入失败（文件只读或被占用）：" + cfw);
                            }
                        }
                    }
                }
            }

            if (vergeChanged || cfwChanged || needRestart)
            {
                ClientProfile p = (Client != null) ? Client : ProfileForExe(ClientExe);
                string exe = ClientExe;
                if (exe != null && File.Exists(exe))
                {
                    if (StartClientExe(exe)) Actions.Add("已重新启动 " + (p != null ? p.Name : "梯子客户端") + " 使配置生效");
                    else Actions.Add("梯子客户端需要手动重新启动（自动拉起失败）");
                    clientRestarted = true;
                }
                else if (ClientRunning)
                {
                    Actions.Add("梯子客户端配置已修改，请手动重启客户端生效");
                }
            }
        }

        private static string ReplaceFirst(string text, string pattern, string replacement)
        {
            try
            {
                Match m = Regex.Match(text, pattern);
                if (!m.Success) return text;
                return text.Substring(0, m.Index) + replacement + text.Substring(m.Index + m.Length);
            }
            catch { return text; }
        }

        private static bool WriteText(string path, string text)
        {
            try
            {
                File.WriteAllText(path, text, new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ TUN / 全局模式

        public void DetectTunMode()
        {
            TunMode = false;
            TunName = "";
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    string text = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    foreach (string hint in TunHints)
                    {
                        if (text.IndexOf(hint) >= 0)
                        {
                            TunMode = true;
                            TunName = ni.Name;
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        // ------------------------------------------------------------ 系统代理

        private static int ReadDword(RegistryKey key, string name)
        {
            object v = key.GetValue(name);
            if (v == null) return 0;
            try { return Convert.ToInt32(v); }
            catch { return 0; }
        }

        private static bool ContainsFold(List<string> list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 去重 + 补齐必备项，修掉历史版本写坏的重复列表
        public static string NormalizeBypass(string raw)
        {
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (string s in raw.Split(';'))
                {
                    string t = s.Trim();
                    if (t.Length == 0) continue;
                    if (!ContainsFold(parts, t)) parts.Add(t);
                }
            }
            foreach (string b in BypassBase)
                if (!ContainsFold(parts, b)) parts.Add(b);
            return string.Join(";", parts.ToArray());
        }

        public void LoadSystemProxy()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey, false))
                {
                    if (key != null)
                    {
                        ProxyEnabled = ReadDword(key, "ProxyEnable") == 1;
                        ProxyServer = Convert.ToString(key.GetValue("ProxyServer"));
                        ProxyOverride = Convert.ToString(key.GetValue("ProxyOverride"));
                        AutoConfigUrl = Convert.ToString(key.GetValue("AutoConfigURL"));
                    }
                }
            }
            catch { }
            ProxyPort = ParsePort(ProxyServer);
            ProxyPortLive = (ProxyPort > 0) && IsPortListening(ProxyPort);
        }

        public static int ParsePort(string server)
        {
            if (string.IsNullOrEmpty(server)) return 0;
            Match m = Regex.Match(server, @"(\d{1,3}(?:\.\d{1,3}){3}):(\d{2,5})");
            if (m.Success) return int.Parse(m.Groups[2].Value);
            m = Regex.Match(server, @":(\d{2,5})");
            if (m.Success) return int.Parse(m.Groups[1].Value);
            return 0;
        }

        public bool SetSystemProxy(bool enable, string server)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey, true))
                {
                    if (key == null) return false;
                    if (!string.IsNullOrEmpty(server)) key.SetValue("ProxyServer", server, RegistryValueKind.String);
                    key.SetValue("ProxyOverride", NormalizeBypass(ProxyOverride), RegistryValueKind.String);
                    key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
                }
                RefreshSystemProxy();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public bool ClearPac()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey, true))
                {
                    if (key == null) return false;
                    if (key.GetValue("AutoConfigURL") == null) return true;
                    key.DeleteValue("AutoConfigURL", false);
                }
                RefreshSystemProxy();
                return true;
            }
            catch { return false; }
        }

        private string OwnerText(int port)
        {
            if (port <= 0) return "端口未知";
            PortOwner owner = FindOwner(lastPorts, port);
            if (owner == null) return "无监听";
            string name = ProcName(owner.Pid);
            if (name.Length == 0) return "PID " + owner.Pid + " 占用";
            return name + " (PID " + owner.Pid + ") 占用";
        }

        // ------------------------------------------------------------ 环境变量

        private static readonly string[] EnvProxyNames = new string[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY" };

        public List<string> EnvProxyProblems = new List<string>();

        public void LoadEnvProxy()
        {
            EnvProxyProblems = new List<string>();
            UserProxyEnv = Environment.GetEnvironmentVariable("HTTPS_PROXY", EnvironmentVariableTarget.User);
            UserProxyEnvPort = ParsePort(UserProxyEnv);
            UserProxyEnvLive = (UserProxyEnvPort > 0) && IsPortListening(UserProxyEnvPort);
            if (UserProxyEnvPort > 0 && !UserProxyEnvLive)
                EnvProxyProblems.Add("HTTPS_PROXY=" + UserProxyEnv);
            foreach (string name in EnvProxyNames)
            {
                if (name == "HTTPS_PROXY") continue;
                string cur = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                int port = ParsePort(cur);
                if (port > 0 && !IsPortListening(port)) EnvProxyProblems.Add(name + "=" + cur);
            }
            string noProxy = Environment.GetEnvironmentVariable("NO_PROXY", EnvironmentVariableTarget.User);
            NoProxySet = !string.IsNullOrEmpty(noProxy) && noProxy.Trim() == NoProxyList();
        }

        // 直连 DeepSeek 不通时会改走代理，对应的 NO_PROXY 也要跟着变
        public string NoProxyList()
        {
            return DeepSeekViaProxy ? "localhost,127.0.0.1,::1" : DirectHosts;
        }

        public void FixEnvProxy()
        {
            bool envChanged = false;
            if (!NoProxySet)
            {
                SetUserEnv("NO_PROXY", NoProxyList());
                Actions.Add("用户级 NO_PROXY 已设置为 " + NoProxyList());
                Log("FIX NO_PROXY");
                envChanged = true;
            }
            string target = null;
            if (Endpoint != null && Endpoint.Verified)
                target = (Endpoint.Kind == "socks5")
                    ? ("socks5://" + Endpoint.Host + ":" + Endpoint.Port)
                    : ("http://" + Endpoint.Host + ":" + Endpoint.Port);

            foreach (string name in EnvProxyNames)
            {
                string cur = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                int port = ParsePort(cur);
                if (port <= 0 || IsPortListening(port)) continue;
                if (target != null)
                {
                    SetUserEnv(name, target);
                    Actions.Add("用户级 " + name + " 指向死端口，已改写到 " + target);
                }
                else
                {
                    SetUserEnv(name, null);
                    Actions.Add("用户级 " + name + " 指向死端口，已清理");
                }
                Log("FIX " + name);
                envChanged = true;
            }
            if (envChanged) BroadcastEnvChange();
        }

        // ------------------------------------------------------------ cc-switch

        public void LoadCcSwitch()
        {
            CcProxyEnabled = false;
            try
            {
                if (File.Exists(CcSettingsPath))
                {
                    Match m = Regex.Match(SafeRead(CcSettingsPath), "\"enableLocalProxy\"\\s*:\\s*(true|false)");
                    if (m.Success) CcProxyEnabled = m.Groups[1].Value == "true";
                }
            }
            catch { }
            CcPortLive = IsPortListening(CcSwitchPort);
            PointsToCcPort = (ProxyPort > 0) && (ProxyPort == CcSwitchPort);
        }

        private static bool IsCcSwitchRunning()
        {
            Process[] ps = null;
            try
            {
                ps = Process.GetProcessesByName("cc-switch");
                return ps.Length > 0;
            }
            catch { return false; }
            finally { if (ps != null) foreach (Process p in ps) { try { p.Dispose(); } catch { } } }
        }

        private static void KillProcesses(string name)
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(name);
                foreach (Process p in ps)
                {
                    try { p.Kill(); }
                    catch { }
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
        }

        private string CcSwitchExePath()
        {
            string exe = GetRunningExePath("cc-switch");
            if (exe != null && File.Exists(exe)) return exe;
            try
            {
                string guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "CC Switch", "cc-switch.exe");
                if (File.Exists(guess)) return guess;
            }
            catch { }
            return null;
        }

        private void StartCcSwitch(string exe)
        {
            try
            {
                if (exe == null || !File.Exists(exe))
                {
                    Actions.Add("请手动重启 cc-switch 使设置生效");
                    return;
                }
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                Process.Start(psi);
                Actions.Add("已重启 cc-switch 使设置生效");
            }
            catch { }
        }

        // 只关不启：开关打开但端口没监听 -> 强制关闭；绝不主动打开它
        public void FixCcSwitch(FixSnapshot snap)
        {
            if (!CcProxyEnabled || CcPortLive || !File.Exists(CcSettingsPath)) return;
            string text = SafeRead(CcSettingsPath);
            Match m = Regex.Match(text, "(\"enableLocalProxy\"\\s*:\\s*)true");
            if (!m.Success) return;

            // cc-switch 退出时会用内存里的设置覆盖文件，所以先停它再改
            bool wasRunning = IsCcSwitchRunning();
            string exe = wasRunning ? CcSwitchExePath() : null;
            if (wasRunning)
            {
                KillProcesses("cc-switch");
                Thread.Sleep(1500);
            }

            string bak = BackupFile(CcSettingsPath);
            string replaced = text.Substring(0, m.Index) + m.Groups[1].Value + "false"
                + text.Substring(m.Index + m.Length);
            if (WriteText(CcSettingsPath, replaced))
            {
                if (bak != null) snap.AddFile(CcSettingsPath, bak);
                Actions.Add("cc-switch 本地代理开关打开但 " + CcSwitchPort + " 未监听，已强制关闭");
                Log("FIX cc-switch enableLocalProxy=false");
            }
            else
            {
                fileFixFailed = true;
                Actions.Add("cc-switch 设置写入失败（文件只读或被占用）：" + CcSettingsPath);
                Log("WARN cc-switch 设置写入失败");
            }
            if (wasRunning) StartCcSwitch(exe);
        }

        // Codex 还指向 127.0.0.1:15721 时，从 cc-switch 自己生成的备份里取回正常地址
        public void FixCodexBaseUrl(FixSnapshot snap)
        {
            try
            {
                if (!File.Exists(CodexConfigPath)) return;
                string text = SafeRead(CodexConfigPath);
                if (text.IndexOf("127.0.0.1:15721") < 0) return;

                string dir = Path.GetDirectoryName(CodexConfigPath);
                List<string> baks = new List<string>();
                foreach (string f in Directory.GetFiles(dir, "config.toml.bak-*")) baks.Add(f);
                baks.Sort();
                baks.Reverse();

                foreach (string bak in baks)
                {
                    string btext = SafeRead(bak);
                    if (btext.Length == 0 || btext.IndexOf("127.0.0.1:15721") >= 0) continue;
                    Match mb = Regex.Match(btext, "(?m)^\\s*base_url\\s*=\\s*\"([^\"]+)\"");
                    if (!mb.Success) continue;
                    string goodUrl = mb.Groups[1].Value;
                    Match cur = Regex.Match(text, "(?m)^([ \\t]*base_url[ \\t]*=[ \\t]*)\"([^\"]*15721[^\"]*)\"");
                    if (!cur.Success) break;
                    string backup = BackupFile(CodexConfigPath);
                    string replaced = text.Substring(0, cur.Index) + cur.Groups[1].Value + "\"" + goodUrl + "\""
                        + text.Substring(cur.Index + cur.Length);
                    if (WriteText(CodexConfigPath, replaced))
                    {
                        if (backup != null) snap.AddFile(CodexConfigPath, backup);
                        Actions.Add("Codex base_url 还指向 cc-switch 死端口，已按备份还原为 " + HostOf(goodUrl));
                        Log("FIX codex base_url -> " + HostOf(goodUrl));
                        CodexBaseUrl = goodUrl;
                    }
                    return;
                }
                Actions.Add("Codex base_url 指向 127.0.0.1:15721，但没找到可用的 cc-switch 备份，请手动修正 "
                    + CodexConfigPath);
                Log("WARN codex base_url 指向死端口且无可用备份");
            }
            catch { }
        }

        public static string HostOf(string url)
        {
            try
            {
                Uri u = new Uri(url);
                return u.Host + (u.IsDefaultPort ? "" : (":" + u.Port));
            }
            catch { return url; }
        }

        // ------------------------------------------------------------ Codex 层

        public void LoadCodex()
        {
            CodexProvider = "";
            CodexBaseUrl = "";
            CodexBaseHost = "";
            try
            {
                if (!File.Exists(CodexConfigPath)) return;
                string text = SafeRead(CodexConfigPath);
                Match m = Regex.Match(text, "(?m)^\\s*model_provider\\s*=\\s*\"([^\"]+)\"");
                if (m.Success) CodexProvider = m.Groups[1].Value;

                string url = "";
                if (CodexProvider.Length > 0)
                {
                    Match sec = Regex.Match(text,
                        "\\[model_providers\\." + Regex.Escape(CodexProvider) + "\\]([\\s\\S]*?)(\\r?\\n\\[|$)");
                    if (sec.Success)
                    {
                        Match b = Regex.Match(sec.Groups[1].Value, "(?m)^\\s*base_url\\s*=\\s*\"([^\"]+)\"");
                        if (b.Success) url = b.Groups[1].Value;
                    }
                }
                if (url.Length == 0)
                {
                    Match b = Regex.Match(text, "(?m)^\\s*base_url\\s*=\\s*\"([^\"]+)\"");
                    if (b.Success) url = b.Groups[1].Value;
                }
                CodexBaseUrl = url;
                if (url.Length > 0)
                {
                    Uri u;
                    if (Uri.TryCreate(url, UriKind.Absolute, out u)) CodexBaseHost = u.Host;
                }
            }
            catch { }
        }

        private string CodexProbePath()
        {
            string p = "";
            try
            {
                Uri u = new Uri(CodexBaseUrl);
                p = u.AbsolutePath;
            }
            catch { }
            if (p.Length == 0) p = "/";
            while (p.EndsWith("/") && p.Length > 1) p = p.Substring(0, p.Length - 1);
            if (p == "/") p = "";
            if (p.EndsWith("/v1")) p = p + "/models";
            else p = p + "/v1/models";
            return p;
        }

        private static bool IsDirectHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.ToLowerInvariant();
            return h == "api.deepseek.com" || h == "127.0.0.1" || h == "localhost" || h == "::1";
        }

        private int ProbeCodexEndpoint()
        {
            if (CodexBaseHost.Length == 0) return 0;
            int proxyPort = 0;
            string kind = "http";
            bool directHost = IsDirectHost(CodexBaseHost) && !DeepSeekViaProxy;
            if (!directHost && Endpoint != null && Endpoint.Verified)
            {
                proxyPort = Endpoint.Port;
                kind = Endpoint.Kind;
            }
            string body;
            return TunnelHttpRequest(CodexBaseHost, 443, CodexProbePath(), proxyPort, kind, 8000, out body);
        }

        // ------------------------------------------------------------ 诊断

        private class ProbeDef
        {
            public string Name;
            public string Host;
            public string Path;
            public int[] Accept;
            public int Code;
            public bool Ok;

            public ProbeDef(string name, string host, string path, int[] accept)
            {
                Name = name;
                Host = host;
                Path = path;
                Accept = accept;
            }
        }

        public void Diagnose()
        {
            Diagnose(true);
        }

        public void Diagnose(bool full)
        {
            Diagnose(full, false);
        }

        // reuseProbes：复检时复用刚刚测出的端点结果，避免把整套探测再跑一遍
        public void Diagnose(bool full, bool reuseProbes)
        {
            Items = new List<CheckItem>();
            LastError = null;

            DetectTunMode();
            DetectClient();
            Endpoint = DiscoverEndpoint();
            LoadSystemProxy();
            LoadCcSwitch();
            LoadEnvProxy();

            string body;
            if (!reuseProbes)
            {
                DeepSeekCode = TunnelHttpRequest("api.deepseek.com", 443, "/v1/models", 0, "http", 8000, out body);
                DeepSeekOk = IsOkCode(DeepSeekCode, new int[] { 200, 401, 403, 404 });
                DeepSeekViaCode = 0;
                DeepSeekViaOk = false;
                DeepSeekViaProxy = false;
                if (!DeepSeekOk && Endpoint != null && Endpoint.Verified)
                {
                    // 直连被挡时改走代理，并把 api.deepseek.com 从 NO_PROXY 里去掉
                    DeepSeekViaCode = TunnelHttpRequest("api.deepseek.com", 443, "/v1/models",
                        Endpoint.Port, Endpoint.Kind, 8000, out body);
                    DeepSeekViaOk = IsOkCode(DeepSeekViaCode, new int[] { 200, 401, 403, 404 });
                    DeepSeekViaProxy = DeepSeekViaOk;
                    LoadEnvProxy();
                }
            }

            int probePort = 0;
            string probeKind = "http";
            if (Endpoint != null && Endpoint.Verified)
            {
                probePort = Endpoint.Port;
                probeKind = Endpoint.Kind;
            }
            if (!reuseProbes)
            {
                ExternalCode = TunnelHttpRequest(GstaticHost, 443, GstaticPath, probePort, probeKind, 8000, out body);
                ExternalOk = IsOkCode(ExternalCode, new int[] { 200, 204 });
            }

            if (full) RunExtraProbes(probePort, probeKind);
            else if (!reuseProbes)
            {
                ProbeItems = new List<CheckItem>();
                LastProbePort = -1;
                ExitIp = "";
                ExitLoc = "";
            }

            LoadCodex();
            CodexCode = ProbeCodexEndpoint();
            CodexOk = IsOkCode(CodexCode, new int[] { 200, 401, 403, 404 });

            AddItems();
        }

        private void RunExtraProbes(int proxyPort, string proxyKind)
        {
            ProbeItems = new List<CheckItem>();
            LastProbePort = proxyPort;
            ExitIp = "";
            ExitLoc = "";

            List<ProbeDef> defs = new List<ProbeDef>();
            defs.Add(new ProbeDef("OpenAI", "api.openai.com", "/v1/models", new int[] { 200, 401 }));
            defs.Add(new ProbeDef("Anthropic", "api.anthropic.com", "/v1/models", new int[] { 200, 401, 404 }));
            defs.Add(new ProbeDef("Gemini", "generativelanguage.googleapis.com", "/v1beta/models",
                new int[] { 200, 400, 401, 403 }));
            defs.Add(new ProbeDef("GitHub", "github.com", "/", new int[] { 200, 301, 302 }));
            defs.Add(new ProbeDef("npm", "registry.npmjs.org", "/-/ping", new int[] { 200 }));

            List<Thread> threads = new List<Thread>();
            foreach (ProbeDef def in defs)
            {
                ProbeDef cur = def;
                Thread t = new Thread(new ThreadStart(delegate
                {
                    string b;
                    cur.Code = TunnelHttpRequest(cur.Host, 443, cur.Path, proxyPort, proxyKind, 8000, out b);
                    cur.Ok = IsOkCode(cur.Code, cur.Accept);
                    if (!cur.Ok)
                    {
                        // 节点偶发抖动，重试一次再下结论（短超时，别拖慢整体）
                        cur.Code = TunnelHttpRequest(cur.Host, 443, cur.Path, proxyPort, proxyKind, 4000, out b);
                        cur.Ok = IsOkCode(cur.Code, cur.Accept);
                    }
                }));
                t.IsBackground = true;
                t.Start();
                threads.Add(t);
            }

            string exitBody = "";
            Thread te = new Thread(new ThreadStart(delegate
            {
                string b;
                TunnelHttpRequest("www.cloudflare.com", 443, "/cdn-cgi/trace", proxyPort, proxyKind, 8000, out b);
                exitBody = b;
            }));
            te.IsBackground = true;
            te.Start();
            threads.Add(te);

            foreach (Thread t in threads) t.Join(12000);

            string tag = (proxyPort > 0) ? "经代理" : "直连";
            foreach (ProbeDef d in defs)
                ProbeItems.Add(new CheckItem(d.Ok ? "OK" : "WARN", "外网", d.Name + "（" + tag + "）", "HTTP " + d.Code));

            Match ipm = Regex.Match(exitBody, "(?m)^ip=(.+)$");
            Match locm = Regex.Match(exitBody, "(?m)^loc=(.+)$");
            if (ipm.Success) ExitIp = ipm.Groups[1].Value.Trim();
            if (locm.Success) ExitLoc = locm.Groups[1].Value.Trim();
        }

        private void AddItem(string level, string layer, string name, string detail)
        {
            Items.Add(new CheckItem(level, layer, name, detail));
        }

        private void AddItems()
        {
            // ---- 梯子层
            if (Client != null)
            {
                string exe = (ClientExe != null) ? ClientExe : "(路径未知)";
                AddItem(ClientRunning ? "OK" : "WARN", "梯子", "客户端识别",
                    Client.Name + (ClientRunning ? " 运行中" : " 未运行") + " — " + exe);
            }
            else if (Endpoint != null)
                AddItem("WARN", "梯子", "客户端识别", "未识别到已知客户端，但已找到可用代理端口（按其它客户端处理）");
            else
                AddItem("FAIL", "梯子", "客户端识别", "未识别到梯子客户端，可用「选择梯子…」手动指定主程序");

            if (Endpoint != null && Endpoint.Verified)
                AddItem("OK", "梯子", "代理端点", Endpoint.Label() + " 实探可用");
            else if (TunMode)
                AddItem("OK", "梯子", "代理端点", "未发现可用本地代理端口，检测到 TUN 全局隧道 " + TunName + "（全局模式）");
            else
                AddItem("FAIL", "梯子", "代理端点",
                    "未找到可用代理端口" + (Candidates.Count > 0 ? "（已实探 " + Candidates.Count + " 个候选）" : ""));

            if (Candidates.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                int shown = 0;
                foreach (ProxyEndpoint ep in Candidates)
                {
                    if (shown >= 4) break;
                    if (shown > 0) sb.Append("；");
                    sb.Append(ep.Host).Append(":").Append(ep.Port).Append(" ").Append(ep.KindLabel())
                        .Append(ep.Verified ? " 可用" : " 不可用").Append("(").Append(ep.Source);
                    if (ep.OwnerName.Length > 0) sb.Append(",").Append(ep.OwnerName);
                    sb.Append(")");
                    shown++;
                }
                if (Candidates.Count > shown) sb.Append("；另外 ").Append(Candidates.Count - shown).Append(" 个");
                AddItem("INFO", "梯子", "候选端口", sb.ToString());
            }

            foreach (string w in CheckClientHijack())
                AddItem("WARN", "梯子", "客户端抢系统代理", w);

            // ---- 系统层
            if (Endpoint != null && Endpoint.Verified)
            {
                string want = Endpoint.ProxyString();
                if (ProxyEnabled && string.Equals(ProxyServer, want, StringComparison.OrdinalIgnoreCase))
                    AddItem("OK", "系统", "系统代理", "已开启 -> " + ProxyServer);
                else if (ProxyEnabled && ProxyPortLive)
                    AddItem("WARN", "系统", "系统代理", "已开启 -> " + ProxyServer + "，但实探可用的是 " + want);
                else if (ProxyEnabled)
                    AddItem("FAIL", "系统", "系统代理", "已开启但指向未监听端口 " + ProxyPort + "（" + ProxyServer
                        + "，" + OwnerText(ProxyPort) + "）");
                else
                    AddItem("FAIL", "系统", "系统代理", "未开启，实探可用的是 " + want);
            }
            else
            {
                if (ProxyEnabled && !ProxyPortLive)
                    AddItem("FAIL", "系统", "系统代理", "已开启但指向未监听端口 " + ProxyPort + "（" + ProxyServer
                        + "，" + OwnerText(ProxyPort) + "）");
                else if (ProxyEnabled)
                    AddItem(TunMode ? "WARN" : "INFO", "系统", "系统代理", "已开启 -> " + ProxyServer);
                else
                    AddItem("OK", "系统", "系统代理", TunMode ? "未开启（全局隧道模式，无需系统代理）" : "未开启");
            }

            if (!string.IsNullOrEmpty(AutoConfigUrl))
                AddItem("FAIL", "系统", "PAC 自动配置", AutoConfigUrl + " 会覆盖系统代理，需要清除");
            else
                AddItem("OK", "系统", "PAC 自动配置", "未设置");

            AddItem(NoProxySet ? "OK" : "WARN", "系统", "用户级 NO_PROXY",
                NoProxySet ? NoProxyList() : "需要规范化为 " + NoProxyList());

            if (EnvProxyProblems.Count > 0)
                AddItem("FAIL", "系统", "用户级代理变量", string.Join("；", EnvProxyProblems.ToArray()) + " 指向未监听端口");
            else
                AddItem("OK", "系统", "用户级代理变量", "未指向死端口");

            // ---- cc-switch
            if (CcProxyEnabled && !CcPortLive)
                AddItem("FAIL", "cc-switch", "本地代理", "开关打开但 " + CcSwitchPort + " 未监听，Codex 会指向死端口，需要强制关闭");
            else if (CcProxyEnabled)
                AddItem("OK", "cc-switch", "本地代理", "开关打开，端口 " + CcSwitchPort + " 在监听");
            else if (PointsToCcPort)
                AddItem("WARN", "cc-switch", "本地代理", "系统代理里残留 cc-switch 端口 " + CcSwitchPort);
            else
                AddItem("OK", "cc-switch", "本地代理", "已关闭");

            // ---- Codex
            if (CodexBaseHost.Length == 0)
                AddItem("WARN", "Codex", "配置链路", "未解析到 base_url");
            else if (CodexBaseUrl.IndexOf("127.0.0.1:15721") >= 0)
                AddItem("FAIL", "Codex", "配置链路", "base_url 指向 cc-switch 本地代理 " + CodexBaseHost + "，需要还原");
            else
                AddItem(CodexOk ? "OK" : "FAIL", "Codex", "配置链路",
                    CodexProvider + " -> " + CodexBaseHost + "（HTTP " + CodexCode + "）");

            // ---- 外网
            if (DeepSeekOk)
                AddItem("OK", "外网", "直连 DeepSeek", "HTTP " + DeepSeekCode);
            else if (DeepSeekViaOk)
                AddItem("OK", "外网", "DeepSeek（经代理）",
                    "HTTP " + DeepSeekViaCode + "，直连不通已自动改为经代理");
            else
                AddItem("FAIL", "外网", "DeepSeek", "直连 HTTP " + DeepSeekCode
                    + (DeepSeekViaProxy || DeepSeekViaCode > 0 ? "，经代理 HTTP " + DeepSeekViaCode : "，经代理也不通"));
            if (Endpoint != null && Endpoint.Verified)
                AddItem(ExternalOk ? "OK" : "FAIL", "外网", "经代理出网", "HTTP " + ExternalCode + "（经 " + Endpoint.Port + "）");
            else if (TunMode)
                AddItem(ExternalOk ? "OK" : "FAIL", "外网", "全局隧道出网", "HTTP " + ExternalCode);

            foreach (CheckItem it in ProbeItems) Items.Add(it);
            if (ExitIp.Length > 0)
                AddItem("INFO", "外网", "出口 IP", ExitIp + (ExitLoc.Length > 0 ? "（" + ExitLoc + "）" : ""));
        }

        public bool HasFailure()
        {
            foreach (CheckItem it in Items) if (it.Level == "FAIL") return true;
            return false;
        }

        public int Count(string level)
        {
            int n = 0;
            foreach (CheckItem it in Items) if (it.Level == level) n++;
            return n;
        }

        // 我们改过的那几项是否仍未达标（只用来判断要不要回滚，不受既有故障影响）
        public bool SystemLayerBroken()
        {
            if (!string.IsNullOrEmpty(AutoConfigUrl)) return true;
            bool needProxy = (Endpoint != null && Endpoint.Verified);
            if (needProxy)
                return !(ProxyEnabled && string.Equals(ProxyServer, Endpoint.ProxyString(), StringComparison.OrdinalIgnoreCase));
            return ProxyEnabled && !ProxyPortLive;
        }

        // ------------------------------------------------------------ 修复

        private bool fileFixFailed;
        private bool clientRestarted;
        private bool restartTried;

        private ProxyEndpoint WaitEndpoint(int seconds, List<PortOwner> before)
        {
            DateTime deadline = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(1200);
                List<PortOwner> now = GetListeningPorts();
                foreach (PortOwner p in now)
                {
                    if (HasPort(before, p.Port)) continue;
                    ProxyEndpoint ep = DiscoverEndpoint();
                    if (ep != null) return ep;
                    break;
                }
            }
            return null;
        }

        public List<string> Fix()
        {
            Actions = new List<string>();
            fileFixFailed = false;
            clientRestarted = false;
            restartTried = false;
            FixSnapshot snap = FixSnapshot.Capture();
            try { File.WriteAllText(SnapshotPath, snap.ToJson(), new UTF8Encoding(false)); } catch { }

            // 1) 梯子客户端层：让它不再抢系统代理
            FixClientHijack(snap);

            // 2) 梯子层：拿到真实可用的端点
            if (Endpoint == null || !Endpoint.Verified)
            {
                if (!ClientRunning)
                {
                    if (ClientExe == null || !File.Exists(ClientExe))
                    {
                        ClientProfile prof;
                        string found = SearchClientExe(out prof);
                        if (found != null)
                        {
                            Client = prof;
                            ClientExe = found;
                            WriteSetting("clientPath", found);
                        }
                    }
                    if (ClientExe != null && File.Exists(ClientExe))
                    {
                        List<PortOwner> before = GetListeningPorts();
                        if (StartClientExe(ClientExe))
                        {
                            Actions.Add("已启动 " + (Client != null ? Client.Name : "梯子客户端") + "，等待端口就绪");
                            Log("FIX 启动客户端 " + ClientExe);
                            Endpoint = WaitEndpoint(StartTimeoutSec, before);
                        }
                    }
                }
                else
                {
                    // 客户端在跑但端口实探不通：多半是内核卡死，重启一次再试
                    ClientProfile rp = (Client != null) ? Client : ProfileForExe(ClientExe);
                    string rexe = ClientExe;
                    if (rp != null && !restartTried)
                    {
                        restartTried = true;
                        Actions.Add("梯子客户端在运行但端口实探不通，正在重启客户端");
                        Log("FIX 重启客户端（端口不通）");
                        StopClientProcesses(rp, 10);
                        Thread.Sleep(1500);
                        List<PortOwner> before2 = GetListeningPorts();
                        if (rexe != null && File.Exists(rexe)) StartClientExe(rexe);
                        Endpoint = WaitEndpoint(StartTimeoutSec, before2);
                        if (Endpoint != null) Actions.Add("重启客户端后端口恢复可用");
                        else Actions.Add("重启客户端后仍不可用：节点可能失效，请换节点或更新订阅");
                    }
                    else
                    {
                        Actions.Add("梯子客户端在运行但没有实探可用的端口：可能是节点刚重启或节点失效，请换节点后重试");
                    }
                }
            }
            if (clientRestarted)
            {
                Endpoint = null;
                lastDiscovery = DateTime.MinValue;
            }
            Endpoint = DiscoverEndpoint();

            // 3) 系统层
            if (Endpoint != null && Endpoint.Verified)
            {
                string want = Endpoint.ProxyString();
                bool needWrite = !ProxyEnabled || !string.Equals(ProxyServer, want, StringComparison.OrdinalIgnoreCase)
                    || ProxyOverride != NormalizeBypass(ProxyOverride);
                if (needWrite)
                {
                    if (SetSystemProxy(true, want))
                    {
                        Actions.Add("系统代理已指向 " + want + "（" + Endpoint.KindLabel() + " 实探可用）");
                        Log("FIX 系统代理 -> " + want);
                    }
                    else Actions.Add("写入系统代理失败：" + LastError);
                }
            }
            else
            {
                if (ProxyEnabled && !ProxyPortLive && SetSystemProxy(false, ProxyServer))
                {
                    Actions.Add("未找到可用代理，已关闭系统代理（避免指向死端口）");
                    Log("FIX 关闭系统代理");
                }
            }
            if (!string.IsNullOrEmpty(AutoConfigUrl))
            {
                if (ClearPac())
                {
                    Actions.Add("已清除 PAC 自动配置（原值记录在还原快照里）");
                    Log("FIX 清除 PAC " + AutoConfigUrl);
                }
            }
            FixEnvProxy();

            // 4) cc-switch 层与 Codex 指向
            LoadCcSwitch();
            FixCcSwitch(snap);
            LoadCodex();
            if (CodexBaseUrl.IndexOf("127.0.0.1:15721") >= 0) FixCodexBaseUrl(snap);

            // 5) 复检，未达标就回滚系统层
            // 探测路径变了（例如刚把系统代理指过去）就重跑端点探测，否则复用刚才的结果
            int nowProbePort = (Endpoint != null && Endpoint.Verified) ? Endpoint.Port : 0;
            Diagnose(nowProbePort != LastProbePort, true);
            if (SystemLayerBroken() || fileFixFailed)
            {
                RestoreSnapshot(snap);
                Actions.Add("修复后复检未达标，已回滚本次改动（文件备份可手动还原）");
                Log("ROLLBACK 复检未达标 fileFixFailed=" + fileFixFailed);
                Diagnose(true);
            }
            if (Actions.Count == 0)
            {
                Actions.Add("未发现需要修复的问题");
                Log("INFO 无需修复");
            }
            return Actions;
        }

        public FixSnapshot LoadSnapshotFromDisk()
        {
            try
            {
                if (File.Exists(SnapshotPath)) return FixSnapshot.Parse(File.ReadAllText(SnapshotPath));
            }
            catch { }
            return null;
        }

        public bool RestoreSnapshot(FixSnapshot s)
        {
            if (s == null || !s.Valid) return false;
            foreach (FileBackup fb in s.Files)
            {
                try { if (File.Exists(fb.Backup)) File.Copy(fb.Backup, fb.Target, true); }
                catch { }
            }
            return s.Apply();
        }

        // ------------------------------------------------------------ 报告

        public string ChainSummary()
        {
            StringBuilder sb = new StringBuilder();
            if (Endpoint != null && Endpoint.Verified)
                sb.Append("梯子 ").Append(Endpoint.Port).Append("(").Append(Endpoint.KindLabel()).Append(")");
            else if (TunMode) sb.Append("梯子 全局隧道");
            else sb.Append("梯子 未就绪");
            sb.Append(" → cc-switch ");
            if (CcProxyEnabled && CcPortLive) sb.Append("本地代理开");
            else if (CcProxyEnabled) sb.Append("开关开着但端口死");
            else sb.Append("关");
            sb.Append(" → Codex ");
            if (CodexBaseHost.Length == 0) sb.Append("未解析到 base_url");
            else sb.Append(CodexBaseHost).Append(CodexOk ? " 可达" : (" 不可达(" + CodexCode + ")"));
            return sb.ToString();
        }

        public string BuildReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CodexNetGuard 体检报告  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("链路: " + ChainSummary());
            if (Client != null) sb.AppendLine("梯子客户端: " + Client.Name + " — " + (ClientExe == null ? "(路径未知)" : ClientExe));
            if (Endpoint != null) sb.AppendLine("代理端点: " + Endpoint.Label());
            if (CodexBaseUrl.Length > 0) sb.AppendLine("Codex: " + CodexProvider + " -> " + CodexBaseHost);
            sb.AppendLine("--------------------------------------------------------------");
            foreach (CheckItem it in Items)
                sb.AppendLine("[" + it.Level + "] [" + it.Layer + "] " + it.Name + ": " + it.Detail);
            sb.AppendLine("--------------------------------------------------------------");
            sb.AppendLine("结果: " + Count("FAIL") + " 项失败 / " + Count("WARN") + " 项警告");
            if (Actions.Count > 0)
            {
                sb.AppendLine("本次修复动作:");
                foreach (string a in Actions) sb.AppendLine("  - " + a);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ 自启 / 常驻

        public bool IsAutoRunInstalled()
        {
            try
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "CodexNetGuard(仅运行一次).cmd");
                return File.Exists(cmd);
            }
            catch { return false; }
        }

        public string InstallAutoRun()
        {
            try
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "CodexNetGuard(仅运行一次).cmd");
                string[] lines = new string[] {
                    "@echo off",
                    "rem CodexNetGuard: run one repair pass at logon, then exit (not resident)",
                    "start \"\" \"" + Application.ExecutablePath + "\" --auto --quiet"
                };
                File.WriteAllLines(cmd, lines, Encoding.Default);
                WriteSetting("autoRunOnce", "1");
                return "已设置：登录时自动修复一次";
            }
            catch (Exception ex)
            {
                return "设置自启失败：" + ex.Message;
            }
        }

        public string RemoveAutoRun()
        {
            try
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "CodexNetGuard(仅运行一次).cmd");
                if (File.Exists(cmd)) File.Delete(cmd);
                WriteSetting("autoRunOnce", "0");
                return "已取消登录时自动修复";
            }
            catch (Exception ex)
            {
                return "取消失败：" + ex.Message;
            }
        }

        public bool IsTrayInstalled()
        {
            try
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "CodexNetGuard(常驻自愈).cmd");
                return File.Exists(cmd);
            }
            catch { return false; }
        }

        public int TrayProcessId()
        {
            try
            {
                if (!File.Exists(TrayPidPath)) return 0;
                int pid;
                if (!int.TryParse(File.ReadAllText(TrayPidPath).Trim(), out pid)) return 0;
                Process p = Process.GetProcessById(pid);
                try { return (p != null) ? pid : 0; }
                finally { if (p != null) p.Dispose(); }
            }
            catch { return 0; }
        }

        public string InstallTray()
        {
            try
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "CodexNetGuard(常驻自愈).cmd");
                string[] lines = new string[] {
                    "@echo off",
                    "rem CodexNetGuard: resident tray watchdog",
                    "start \"\" \"" + Application.ExecutablePath + "\" --tray"
                };
                File.WriteAllLines(cmd, lines, Encoding.Default);
                WriteSetting("trayResident", "1");
                if (TrayProcessId() == 0)
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--tray");
                    psi.WorkingDirectory = ExeDir;
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                return "已开启常驻托盘自愈（60 秒一轮，只在状态变化时提醒）";
            }
            catch (Exception ex)
            {
                return "开启常驻失败：" + ex.Message;
            }
        }

        public string RemoveTray()
        {
            try
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "CodexNetGuard(常驻自愈).cmd");
                if (File.Exists(cmd)) File.Delete(cmd);
                int pid = TrayProcessId();
                if (pid > 0)
                {
                    try
                    {
                        Process p = Process.GetProcessById(pid);
                        p.Kill();
                        p.Dispose();
                    }
                    catch { }
                }
                try { if (File.Exists(TrayPidPath)) File.Delete(TrayPidPath); }
                catch { }
                WriteSetting("trayResident", "0");
                return "已关闭常驻托盘自愈";
            }
            catch (Exception ex)
            {
                return "关闭常驻失败：" + ex.Message;
            }
        }

        // ------------------------------------------------------------ 移除旧版常驻守护

        public string RemoveLegacyGuard()
        {
            StringBuilder sb = new StringBuilder();
            bool any = false;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/delete /tn CodexNetGuard /f");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0)
                {
                    sb.AppendLine("已删除计划任务 CodexNetGuard");
                    any = true;
                }
            }
            catch { }

            try
            {
                string vbs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CodexNetGuard.vbs");
                if (File.Exists(vbs)) { File.Delete(vbs); sb.AppendLine("已删除启动文件夹脚本 CodexNetGuard.vbs"); any = true; }
            }
            catch { }

            try
            {
                string pidFile = Path.Combine(ExeDir, "watch.pid");
                if (File.Exists(pidFile))
                {
                    int pid = 0;
                    if (int.TryParse(File.ReadAllText(pidFile).Trim(), out pid))
                    {
                        try
                        {
                            Process wp = Process.GetProcessById(pid);
                            wp.Kill();
                            sb.AppendLine("已结束常驻守护进程 PID " + pid);
                            any = true;
                        }
                        catch { }
                    }
                    File.Delete(pidFile);
                }
            }
            catch { }

            int trayPid = TrayProcessId();
            if (trayPid > 0)
            {
                try
                {
                    Process tp = Process.GetProcessById(trayPid);
                    tp.Kill();
                    tp.Dispose();
                    sb.AppendLine("已结束托盘常驻进程 PID " + trayPid);
                    any = true;
                }
                catch { }
            }
            try { if (File.Exists(TrayPidPath)) File.Delete(TrayPidPath); }
            catch { }

            if (!any) sb.AppendLine("未发现旧版常驻守护（本机已干净或从未安装）");
            Log("REMOVE-LEGACY " + sb.ToString().Replace(Environment.NewLine, " "));
            return sb.ToString();
        }

        // ------------------------------------------------------------ 启动 Codex

        public bool StartCodexApp()
        {
            try
            {
                string aumid = ReadSetting("codexAumid", "");
                if (aumid.Length == 0)
                {
                    // 桌面版的开始菜单名字是 “ChatGPT”，AppID 里才带 Codex，所以优先按 AppID 找
                    aumid = RunPowerShell("Get-StartApps | Where-Object { $_.AppID -like '*Codex*' } | "
                        + "Select-Object -First 1 -ExpandProperty AppID");
                    if (aumid.Length == 0)
                        aumid = RunPowerShell("Get-StartApps | Where-Object { $_.Name -like '*Codex*' "
                            + "-or $_.Name -like '*ChatGPT*' } | Select-Object -First 1 -ExpandProperty AppID");
                    if (aumid.Length > 0) WriteSetting("codexAumid", aumid);
                }
                if (aumid.Length == 0)
                {
                    Log("WARN 未能解析 Codex AUMID");
                    return false;
                }
                ProcessStartInfo ex = new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid);
                ex.UseShellExecute = true;
                Process.Start(ex);
                Log("INFO 已启动 Codex：" + aumid);
                return true;
            }
            catch (Exception ex2)
            {
                LastError = ex2.Message;
                return false;
            }
        }

        private static string RunPowerShell(string command)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + command + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                string text = p.StandardOutput.ReadToEnd().Trim();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return text;
            }
            catch { return ""; }
        }

    }

    // ================================================================ 图形界面

    public class MainForm : Form
    {
        private Guard guard = new Guard();
        private Label lblTitle;
        private Label lblChain;
        private Label lblStatus;
        private ListView list;
        private Button btnFix;
        private Button btnCheck;
        private Button btnPick;
        private Button btnCopy;
        private Button btnLog;
        private Button btnRestore;
        private Button btnRemoveGuard;
        private CheckBox chkAutoRun;
        private CheckBox chkTray;
        private CheckBox chkOpenCodex;
        private bool busy;
        private bool pendingOpenCodex;

        public MainForm()
        {
            Text = "CodexNetGuard — 梯子 → cc-switch → Codex 修复工具";
            ClientSize = new Size(900, 622);
            MinimumSize = new Size(900, 622);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            lblTitle = new Label();
            lblTitle.Text = "CodexNetGuard（便携版）";
            lblTitle.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            lblTitle.SetBounds(16, 12, 600, 30);
            Controls.Add(lblTitle);

            lblChain = new Label();
            lblChain.Text = "正在读取链路…";
            lblChain.SetBounds(16, 46, 868, 20);
            lblChain.ForeColor = Color.DimGray;
            Controls.Add(lblChain);

            lblStatus = new Label();
            lblStatus.Text = "准备中…";
            lblStatus.Font = new Font("Microsoft YaHei UI", 10F);
            lblStatus.SetBounds(16, 70, 868, 42);
            Controls.Add(lblStatus);

            list = new ListView();
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = false;
            list.SetBounds(16, 116, 868, 374);
            list.Columns.Add("状态", 60);
            list.Columns.Add("层", 80);
            list.Columns.Add("项目", 170);
            list.Columns.Add("说明", 540);
            Controls.Add(list);

            chkAutoRun = new CheckBox();
            chkAutoRun.Text = "登录时自动修复一次（只运行一次后退出，不常驻）";
            chkAutoRun.SetBounds(16, 504, 440, 24);
            chkAutoRun.Checked = guard.IsAutoRunInstalled();
            chkAutoRun.CheckedChanged += new EventHandler(OnAutoRunChanged);
            Controls.Add(chkAutoRun);

            chkTray = new CheckBox();
            chkTray.Text = "常驻托盘自愈（每 60 秒一轮，只在状态变化时提醒）";
            chkTray.SetBounds(16, 532, 440, 24);
            chkTray.Checked = guard.IsTrayInstalled();
            chkTray.CheckedChanged += new EventHandler(OnTrayChanged);
            Controls.Add(chkTray);

            chkOpenCodex = new CheckBox();
            chkOpenCodex.Text = "修复后打开 Codex";
            chkOpenCodex.SetBounds(16, 560, 440, 24);
            chkOpenCodex.Checked = guard.ReadSetting("openCodexAfterFix", "0") == "1";
            chkOpenCodex.CheckedChanged += new EventHandler(OnOpenCodexChanged);
            Controls.Add(chkOpenCodex);

            btnFix = MakeButton("一键修复", 470, 502, 100);
            btnFix.Click += new EventHandler(delegate(object s, EventArgs e) { RunWork(true); });
            btnCheck = MakeButton("仅检测", 582, 502, 100);
            btnCheck.Click += new EventHandler(delegate(object s, EventArgs e) { RunWork(false); });
            btnPick = MakeButton("选择梯子…", 694, 502, 180);
            btnPick.Click += new EventHandler(OnPick);
            btnCopy = MakeButton("复制报告", 470, 536, 100);
            btnCopy.Click += new EventHandler(OnCopy);
            btnLog = MakeButton("打开日志", 582, 536, 100);
            btnLog.Click += new EventHandler(OnOpenLog);
            btnRestore = MakeButton("还原上次修改", 694, 536, 180);
            btnRestore.Click += new EventHandler(OnRestore);
            btnRemoveGuard = MakeButton("移除后台守护", 470, 570, 180);
            btnRemoveGuard.Click += new EventHandler(OnRemoveGuard);
            Button btnExit = MakeButton("退出", 804, 570, 70);
            btnExit.Click += new EventHandler(delegate(object s, EventArgs e) { Close(); });
        }

        private Button MakeButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 26);
            Controls.Add(b);
            return b;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RunWork(true);
        }

        private void OnAutoRunChanged(object sender, EventArgs e)
        {
            string msg = chkAutoRun.Checked ? guard.InstallAutoRun() : guard.RemoveAutoRun();
            guard.Log("AUTORUN " + msg);
        }

        private void OnTrayChanged(object sender, EventArgs e)
        {
            string msg = chkTray.Checked ? guard.InstallTray() : guard.RemoveTray();
            guard.Log("TRAY " + msg);
            if (msg.IndexOf("失败") >= 0) MessageBox.Show(this, msg, "常驻托盘", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnOpenCodexChanged(object sender, EventArgs e)
        {
            guard.WriteSetting("openCodexAfterFix", chkOpenCodex.Checked ? "1" : "0");
        }

        // 支持指定任意梯子主程序，不再局限于 v2rayN
        private void OnPick(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择梯子客户端主程序";
            dlg.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                guard.WriteSetting("clientPath", dlg.FileName);
                RunWork(true);
            }
        }

        private void OnCopy(object sender, EventArgs e)
        {
            try
            {
                Clipboard.SetText(guard.BuildReport());
                lblStatus.Text = "报告已复制到剪贴板";
            }
            catch { }
        }

        private void OnOpenLog(object sender, EventArgs e)
        {
            try { Process.Start("notepad.exe", guard.LogPath); }
            catch { }
        }

        private void OnRestore(object sender, EventArgs e)
        {
            FixSnapshot snap = guard.LoadSnapshotFromDisk();
            if (snap == null || !snap.Valid)
            {
                MessageBox.Show(this, "没有找到还原快照（CodexNetGuard-restore.json）。", "还原上次修改",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool ok = guard.RestoreSnapshot(snap);
            guard.Log("RESTORE ok=" + ok);
            MessageBox.Show(this, ok ? "已还原到上次修复前的系统设置。" : "还原失败，请查看日志。", "还原上次修改",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RunWork(false);
        }

        private void OnRemoveGuard(object sender, EventArgs e)
        {
            string result = guard.RemoveLegacyGuard();
            try
            {
                if (guard.IsTrayInstalled()) guard.RemoveTray();
                if (File.Exists(guard.TrayPidPath)) File.Delete(guard.TrayPidPath);
            }
            catch { }
            chkTray.Checked = guard.IsTrayInstalled();
            chkAutoRun.Checked = guard.IsAutoRunInstalled();
            MessageBox.Show(this, result, "移除后台守护", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RunWork(false);
        }

        private void RunWork(bool doFix)
        {
            if (busy) return;
            busy = true;
            pendingOpenCodex = chkOpenCodex.Checked;
            lock (this)
            {
                SetStatus(doFix ? "正在检测并修复…" : "正在检测…", Color.DimGray);
                list.Items.Clear();
            }
            SetButtonsEnabled(false);
            Thread t = new Thread(new ThreadStart(delegate { WorkThread(doFix); }));
            t.IsBackground = true;
            t.Start();
        }

        private void WorkThread(bool doFix)
        {
            try
            {
                guard.Diagnose(true);
                if (doFix)
                {
                    guard.Fix();
                    if (pendingOpenCodex) guard.StartCodexApp();
                }
                try { File.WriteAllText(guard.ReportPath, guard.BuildReport(), Encoding.UTF8); }
                catch { }
            }
            catch (Exception ex)
            {
                guard.Log("ERROR " + ex.ToString());
            }
            try { BeginInvoke(new Action(delegate { ShowResults(doFix); })); }
            catch { }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            btnFix.Enabled = enabled;
            btnCheck.Enabled = enabled;
            btnPick.Enabled = enabled;
            btnCopy.Enabled = enabled;
            btnRestore.Enabled = enabled;
            btnRemoveGuard.Enabled = enabled;
        }

        private void SetStatus(string text, Color color)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color;
        }

        private void ShowResults(bool didFix)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (CheckItem it in guard.Items)
            {
                ListViewItem row = new ListViewItem(it.Level);
                row.SubItems.Add(it.Layer);
                row.SubItems.Add(it.Name);
                row.SubItems.Add(it.Detail);
                if (it.Level == "OK") row.ForeColor = Color.FromArgb(21, 128, 61);
                else if (it.Level == "WARN") row.ForeColor = Color.FromArgb(180, 110, 0);
                else if (it.Level == "FAIL") row.ForeColor = Color.FromArgb(185, 28, 28);
                else row.ForeColor = Color.DimGray;
                list.Items.Add(row);
            }
            list.EndUpdate();

            lblChain.Text = guard.ChainSummary();
            int fails = guard.Count("FAIL");
            int warns = guard.Count("WARN");
            if (fails == 0 && warns == 0)
                SetStatus("链路正常：梯子、cc-switch、Codex 三层都通。", Color.FromArgb(21, 128, 61));
            else if (fails == 0)
                SetStatus("基本正常，有 " + warns + " 项提醒（不影响 Codex 使用）。", Color.FromArgb(180, 110, 0));
            else
            {
                string hint = "";
                if (guard.Endpoint == null && !guard.TunMode)
                    hint = " 梯子没起来：请打开梯子客户端后点「一键修复」。";
                else if (guard.Endpoint != null && !guard.ExternalOk)
                    hint = " 节点可能不可用：请换节点或更新订阅后重试。";
                else if (guard.CodexBaseUrl.IndexOf("127.0.0.1:15721") >= 0)
                    hint = " Codex 还指向 cc-switch 的死端口，请查看日志。";
                SetStatus("还有 " + fails + " 项失败。" + hint, Color.FromArgb(185, 28, 28));
            }
            if (didFix && guard.Actions.Count > 0 && guard.Actions[0] != "未发现需要修复的问题")
                SetStatus(lblStatus.Text + "  本次动作：" + string.Join("；", guard.Actions.ToArray()), lblStatus.ForeColor);

            busy = false;
            SetButtonsEnabled(true);
        }
    }

    // ================================================================ 常驻托盘

    public class TrayContext : ApplicationContext
    {
        private Guard guard = new Guard();
        private NotifyIcon icon;
        private System.Windows.Forms.Timer timer;
        private int lastSeverity = -1;
        private bool working;

        public TrayContext()
        {
            try { File.WriteAllText(guard.TrayPidPath, Process.GetCurrentProcess().Id.ToString()); }
            catch { }

            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("立即修复", new EventHandler(delegate(object s, EventArgs e) { Tick(true); }));
            menu.MenuItems.Add("打开界面", new EventHandler(delegate(object s, EventArgs e)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                    psi.WorkingDirectory = guard.ExeDir;
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                catch { }
            }));
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出常驻", new EventHandler(delegate(object s, EventArgs e) { ExitTray(); }));

            icon = new NotifyIcon();
            icon.Icon = SystemIcons.Shield;
            icon.Text = "CodexNetGuard 常驻自愈";
            icon.ContextMenu = menu;
            icon.Visible = true;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 60000;
            timer.Tick += new EventHandler(delegate(object s, EventArgs e) { Tick(false); });
            timer.Start();

            guard.Log("TRAY 常驻启动，间隔 60s");
            Tick(false);
        }

        private void ExitTray()
        {
            guard.WriteSetting("trayResident", "0");
            try
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "CodexNetGuard(常驻自愈).cmd");
                if (File.Exists(cmd)) File.Delete(cmd);
            }
            catch { }
            try { if (File.Exists(guard.TrayPidPath)) File.Delete(guard.TrayPidPath); }
            catch { }
            try { icon.Visible = false; }
            catch { }
            guard.Log("TRAY 退出常驻");
            ExitThread();
        }

        private void Tick(bool force)
        {
            if (working) return;
            working = true;
            Thread t = new Thread(new ThreadStart(delegate
            {
                try
                {
                    guard.Diagnose(false);
                    if (force || guard.SystemLayerBroken()) guard.Fix();
                    int severity = guard.Count("FAIL") * 10 + guard.Count("WARN");
                    if (force || severity != lastSeverity)
                    {
                        lastSeverity = severity;
                        string title = (guard.Count("FAIL") > 0) ? "CodexNetGuard：链路仍有问题"
                            : (severity == 0 ? "CodexNetGuard：链路正常" : "CodexNetGuard：有提醒");
                        ShowBalloon(title, guard.ChainSummary());
                    }
                    guard.Log("TICK fails=" + guard.Count("FAIL") + " warns=" + guard.Count("WARN") + " " + guard.ChainSummary());
                }
                catch (Exception ex)
                {
                    guard.Log("ERROR tray " + ex.Message);
                }
                finally { working = false; }
            }));
            t.IsBackground = true;
            t.Start();
        }

        private void ShowBalloon(string title, string text)
        {
            try
            {
                icon.BalloonTipTitle = title;
                icon.BalloonTipText = text;
                icon.ShowBalloonTip(5000);
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            try { if (icon != null) icon.Visible = false; }
            catch { }
            base.Dispose(disposing);
        }
    }

    // ================================================================ 入口

    public static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        public static int Main(string[] args)
        {
            bool headless = false;
            bool doFix = false;
            bool quiet = false;
            bool removeLegacy = false;
            bool restore = false;
            bool tray = false;
            foreach (string a in args)
            {
                string s = a.ToLowerInvariant();
                if (s == "--auto") { headless = true; doFix = true; }
                else if (s == "--check") { headless = true; }
                else if (s == "--quiet") { quiet = true; }
                else if (s == "--remove-legacy") { headless = true; removeLegacy = true; }
                else if (s == "--restore") { headless = true; restore = true; }
                else if (s == "--tray") { tray = true; }
            }

            if (tray)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
                return 0;
            }

            if (!headless)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }

            try
            {
                AttachConsole(-1);
                StreamWriter w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                w.AutoFlush = true;
                Console.SetOut(w);
            }
            catch { }

            Guard g = new Guard();
            if (removeLegacy)
            {
                string result = g.RemoveLegacyGuard();
                if (!quiet) Console.WriteLine(result);
                return 0;
            }
            if (restore)
            {
                FixSnapshot snap = g.LoadSnapshotFromDisk();
                bool ok = g.RestoreSnapshot(snap);
                g.Diagnose(true);
                try { File.WriteAllText(g.ReportPath, g.BuildReport(), Encoding.UTF8); }
                catch { }
                if (!quiet)
                {
                    Console.WriteLine(ok ? "已还原到上次修复前的系统设置。" : "没有可用的还原快照。");
                    Console.WriteLine(g.BuildReport());
                }
                g.Log("RESTORE ok=" + ok);
                return ok ? 0 : 1;
            }

            g.Diagnose(true);
            if (doFix) g.Fix();
            string report = g.BuildReport();
            try { File.WriteAllText(g.ReportPath, report, Encoding.UTF8); }
            catch { }
            if (!quiet) Console.WriteLine(report);
            g.Log((doFix ? "AUTO-FIX" : "CHECK") + " fails=" + g.Count("FAIL") + " warns=" + g.Count("WARN"));
            return g.HasFailure() ? 1 : 0;
        }
    }
}
