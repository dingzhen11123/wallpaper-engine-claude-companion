// Claude 壁纸桥接服务：Claude 会话（desktop-feel mod）把状态推到这里，Wallpaper Engine 壁纸来取。
// 只监听 127.0.0.1，内存常驻约 10MB，空闲时不占 CPU。端口被占用（已经有一个在跑）就直接退出。
//
//   POST /session   mod 推送一个会话的状态（JSON，校验后原样保存），回 {"pokes":n}（壁纸上被戳了几次）
//   POST /end       mod 通知会话结束，正文是会话 id
//   GET  /state?v=N 壁纸取全部会话；版本没变回 204
//   POST /poke      壁纸上戳了某个会话的玩偶，正文是会话 id
//   POST /focus     壁纸上点了会话卡片的「打开」：把 Claude 桌面版切到前台
//   POST /media     壁纸播放器按钮：正文 playpause / next / prev，发送系统媒体键
//   GET  /music     正在放的歌：系统媒体信息（网易云、Folia）优先，读不到退回网易云窗口标题；能读到进度时带上 pos / dur
//   GET  /lyric?title=&artist=  到网易云公开接口搜这首歌并取歌词（含翻译），同一首只请求一次
//   GET  /track     当前这首歌的时间线：什么时候开始放、是否暂停中、是否从头看到（歌词对齐用）
//   POST /track     壁纸告诉服务：start（换歌）/ pause / resume / align（手动对齐）/ seek（拖进度条），正文三行：事件、歌名、歌手（seek 第四行是毫秒）
//   POST /seek      正文两行：毫秒、歌名。让播放器跳到这个位置（要播放器支持，网易云不支持，回 {"ok":false}）
//   GET  /plan      Claude 套餐名，比如 {"plan":"Ultra"}
//   GET  /health
//   /finder/*       会话管理（finder.cs），/input/* 搜索框（input.cs），都要带口令
//
// 只接受本机页面（file://、localhost）和没有 Origin 的请求（mod 经由 Claude 发出），别的网页一律拒绝。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

static partial class Bridge
{
    const int Port = 47321;
    const int MaxBody = 256 * 1024;
    const int MaxSessions = 32;
    const int MaxConnections = 16;
    // 一个请求从连上到读完最多这么久
    const int RequestDeadlineMs = 2500;
    // 超过这么久没心跳的会话视为已关闭
    const long StaleMs = 20000;

    class Session { public string Json; public long Seen; public int Pokes; }

    static readonly object Gate = new object();
    static readonly Dictionary<string, Session> Sessions = new Dictionary<string, Session>();
    // 从「启动时刻毫秒数 x 1000」开始递增：除非一毫秒里变了上千次，重启后都不会和壁纸手里的旧版本号撞上
    static long version = Now() * 1000;
    static long lastFocus = 0;
    static long lastMedia = 0;
    static readonly object MediaGate = new object();
    static int active = 0;
    // 放在静态字段里，免得定时器被回收
    static Timer trimTimer;
    static Timer trackTimer;

    static readonly Regex SafeId = new Regex("^[A-Za-z0-9_.:-]{1,128}$");
    static readonly Regex LocalOrigin = new Regex("^(null|file://.*|https?://(localhost|127\\.0\\.0\\.1)(:\\d+)?)$", RegexOptions.IgnoreCase);

    static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

    [DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    // 把暂时用不到的内存还给系统（需要时会自动换回来）
    static void Trim()
    {
        lock (Gate) Prune(Now());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        // 滚轮钩子挂着时不换出内存：下一次鼠标事件要等缺页，整个系统的鼠标都会顿一下
        if (Interlocked.CompareExchange(ref wheelThreadOn, 0, 0) == 1) return;
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
    }

    static int Main()
    {
        // 要在第一次调用 user32 之前设置，否则搜索框的位置和字号在缩放屏上会错位
        try { SetProcessDPIAware(); } catch { }
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(32);
        }
        catch (SocketException) { return 0; }

        InitToken();
        Trim();
        trimTimer = new Timer(_ => Trim(), null, 60000, 120000);
        // 每 2 秒看一眼网易云在放哪首：壁纸关着、暂停着也照样记住每首歌开始的时间
        trackTimer = new Timer(_ => WatchTrack(), null, 1000, 2000);
        // 每秒采一次播放状态和进度（壁纸不来问就不采）
        smtcTimer = new Timer(_ => SampleSmtc(), null, 500, 1000);

        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { Thread.Sleep(200); continue; }
            // 同时处理的连接有上限，超出的直接关掉，免得慢连接把线程占满
            if (Interlocked.Increment(ref active) > MaxConnections)
            {
                Interlocked.Decrement(ref active);
                try { client.Client.Close(0); } catch { }
                client.Close();
                continue;
            }
            var clock = Stopwatch.StartNew();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Handle(client, clock); }
                finally { Interlocked.Decrement(ref active); }
            });
        }
    }

    static void Handle(TcpClient client, Stopwatch clock)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 1000;
                client.SendTimeout = 2000;
                var stream = client.GetStream();
                string method, target; Dictionary<string, string> headers; byte[] body;
                if (!ReadRequest(stream, clock, out method, out target, out headers, out body)) return;

                string origin;
                if (headers.TryGetValue("Origin", out origin) && !LocalOrigin.IsMatch(origin))
                {
                    Send(stream, 403, "{\"error\":\"forbidden\"}");
                    return;
                }

                string path = target, query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }

                // 雾笺的笔顺数据和专辑封面：不含隐私，不要口令；文件大，发送时间放宽
                if (method == "GET" && (path.StartsWith("/stage/") || path.StartsWith("/cover/")))
                {
                    client.SendTimeout = 20000;
                    if (path.StartsWith("/cover/")) ServeCover(stream, path);
                    else ServeStage(stream, path);
                    return;
                }

                if (method == "OPTIONS") { Send(stream, 204, null); return; }
                string text = body.Length > 0 ? Encoding.UTF8.GetString(body) : "";

                if (path.StartsWith("/finder/") || path.StartsWith("/input/"))
                {
                    if (!TokenOk(query)) { Send(stream, 403, "{\"error\":\"bad token\"}"); return; }
                    if (path.StartsWith("/finder/")) FinderRoute(stream, method, path, query, text);
                    else InputRoute(stream, method, path, text);
                }
                else if (method == "GET" && path == "/health") Send(stream, 200, "{\"ok\":true}");
                else if (method == "GET" && path == "/state") State(stream, query);
                else if (method == "GET" && path == "/music") Music(stream);
                else if (method == "GET" && path == "/sys") Sys(stream);
                else if (method == "GET" && path == "/lyric") Lyric(stream, query);
                else if (method == "GET" && path == "/track") TrackState(stream);
                else if (method == "POST" && path == "/track") TrackEvent(stream, text);
                else if (method == "POST" && path == "/session") Push(stream, text);
                else if (method == "POST" && path == "/end") End(stream, text.Trim());
                else if (method == "POST" && path == "/poke") Poke(stream, text.Trim());
                else if (method == "POST" && path == "/focus") Focus(stream);
                else if (method == "POST" && path == "/open") Open(stream, text.Trim());
                else if (method == "POST" && path == "/media") Media(stream, text.Trim());
                else if (method == "POST" && path == "/seek") Seek(stream, text.Trim());
                else if (method == "GET" && path == "/plan") Plan(stream);
                else Send(stream, 404, "{\"error\":\"not found\"}");
            }
            catch { }
        }
    }

    // 完整解析一遍：必须是对象、顶层有合法的字符串 id、数字都是有限值；
    // 存的是重新序列化后的规范 JSON（解析器能接受的非标准写法不会原样流到壁纸），/state 才能一直是合法 JSON
    static bool Normalize(string json, out string id, out string normalized)
    {
        id = normalized = null;
        try
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaxBody, RecursionLimit = 16 };
            var obj = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (obj == null || !IsFinite(obj)) return false;
            object raw;
            var text = obj.TryGetValue("id", out raw) ? raw as string : null;
            if (text == null || !SafeId.IsMatch(text)) return false;
            id = text;
            // 桌面版侧栏上的标题（找得到才加）
            var desk = FindDesktop(id, false);
            if (desk != null) obj["desktopSession"] = true;
            if (desk != null && desk.Title.Length > 0) obj["desktopTitle"] = desk.Title;
            normalized = serializer.Serialize(obj);
            return true;
        }
        catch { return false; }
    }

    static bool IsFinite(object v)
    {
        if (v is double) return !double.IsNaN((double)v) && !double.IsInfinity((double)v);
        if (v is float) return !float.IsNaN((float)v) && !float.IsInfinity((float)v);
        var map = v as Dictionary<string, object>;
        if (map != null) { foreach (var kv in map) if (!IsFinite(kv.Value)) return false; return true; }
        var list = v as object[];
        if (list != null) { foreach (var one in list) if (!IsFinite(one)) return false; return true; }
        var items = v as System.Collections.ArrayList;
        if (items != null) { foreach (var one in items) if (!IsFinite(one)) return false; return true; }
        return true;
    }

    static void Prune(long now)
    {
        var gone = new List<string>();
        foreach (var kv in Sessions) if (now - kv.Value.Seen > StaleMs) gone.Add(kv.Key);
        foreach (var id in gone) Sessions.Remove(id);
        if (gone.Count > 0) version++;
    }

    static void Push(Stream s, string json)
    {
        string id, normalized;
        if (!Normalize(json, out id, out normalized)) { Send(s, 400, "{\"error\":\"bad session\"}"); return; }
        json = normalized;
        int pokes;
        lock (Gate)
        {
            long now = Now();
            Prune(now);
            Session one;
            if (!Sessions.TryGetValue(id, out one))
            {
                if (Sessions.Count >= MaxSessions) { Send(s, 429, "{\"error\":\"too many sessions\"}"); return; }
                one = new Session();
                Sessions[id] = one;
                version++;
            }
            if (one.Json != json) { one.Json = json; version++; }
            one.Seen = now;
            pokes = one.Pokes;
            one.Pokes = 0;
        }
        Send(s, 200, "{\"pokes\":" + pokes + "}");
    }

    static void End(Stream s, string id)
    {
        lock (Gate) { if (Sessions.Remove(id)) version++; }
        Send(s, 200, "{\"ok\":true}");
    }

    static void Poke(Stream s, string id)
    {
        bool found;
        lock (Gate)
        {
            Session one;
            found = Sessions.TryGetValue(id, out one);
            // 攒太多没意义，最多记 5 次
            if (found && one.Pokes < 5) one.Pokes++;
        }
        Send(s, found ? 200 : 404, found ? "{\"ok\":true}" : "{\"error\":\"no session\"}");
    }

    static void State(Stream s, string query)
    {
        long since = 0;
        foreach (var part in query.Split('&'))
            if (part.StartsWith("v=")) long.TryParse(part.Substring(2), out since);

        var sb = new StringBuilder();
        lock (Gate)
        {
            long now = Now();
            Prune(now);
            if (since == version) { Send(s, 204, null); return; }

            sb.Append("{\"v\":").Append(version).Append(",\"now\":").Append(now).Append(",\"sessions\":[");
            bool first = true;
            foreach (var kv in Sessions)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(kv.Value.Json);
            }
            sb.Append("]}");
        }
        Send(s, 200, sb.ToString());
    }

    // ---- 桌面版会话：Claude Code 的会话 ID → 桌面版自己的会话 ID（local_…）和侧栏标题 ----
    // 桌面版给每个会话存了一个 local_*.json（cliSessionId、sessionId、title）。应用商店版把大部分写在自己的私有目录
    // （Packages\Claude_*\LocalCache），普通进程只看得到真实目录，所以两处都扫。
    // 每个文件按「修改时间 + 大小」缓存解析结果，重扫时只读改过的；某个子目录读不了就沿用它上次的结果。
    class DesktopSession { public string Local; public string Title; }
    class DesktopFile { public DateTime Stamp; public long Size; public string Cli; public DesktopSession Session; public bool Archived; }
    static readonly object DesktopGate = new object();
    static Dictionary<string, DesktopSession> desktopIndex = new Dictionary<string, DesktopSession>();
    static Dictionary<string, DesktopFile> desktopFiles = new Dictionary<string, DesktopFile>(StringComparer.OrdinalIgnoreCase);
    static long desktopScannedAt;
    static bool desktopScanning;
    static readonly Regex LocalId = new Regex("^local_[A-Za-z0-9-]{1,64}$");
    const int DesktopMaxFiles = 5000;
    const long DesktopMaxBytes = 1024 * 1024;

    static List<string> DesktopRoots()
    {
        var dirs = new List<string>();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions"));
        try
        {
            string pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.GetDirectories(pk, "Claude_*"))
                    dirs.Add(Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude-code-sessions"));
        }
        catch { }
        // 2026-11 起桌面端把数据搬到 %LOCALAPPDATA%\Claude-Data\<包名>（启动参数 --user-data-dir），旧目录不再写入。
        // 放在最后：ActiveRoot 打平时后面的优先
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
            if (Directory.Exists(data))
                foreach (var d in Directory.GetDirectories(data))
                    dirs.Add(Path.Combine(d, "claude-code-sessions"));
        }
        catch { }
        return dirs;
    }

    // 逐层列出 local_*.json；某一层读不了只跳过那一层，并记下来（那一层的旧结果继续用）
    static void ListDesktopFiles(string root, List<string> found, List<string> broken)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        int dirs = 0;
        while (stack.Count > 0 && found.Count < DesktopMaxFiles && ++dirs < 2000)
        {
            string dir = stack.Pop();
            try
            {
                foreach (var f in Directory.GetFiles(dir, "local_*.json"))
                {
                    if (found.Count >= DesktopMaxFiles) break;
                    found.Add(f);
                }
                foreach (var sub in Directory.GetDirectories(dir)) stack.Push(sub);
            }
            catch { broken.Add(dir.TrimEnd('\\') + "\\"); }
        }
    }

    static DesktopFile ParseDesktopFile(string file, FileInfo info, JavaScriptSerializer json)
    {
        var entry = new DesktopFile { Stamp = info.LastWriteTimeUtc, Size = info.Length };
        if (info.Length > DesktopMaxBytes) return entry;
        try
        {
            var obj = json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
            if (obj == null) return entry;
            object cli, local, title, archived;
            string c = obj.TryGetValue("cliSessionId", out cli) ? cli as string : null;
            string l = obj.TryGetValue("sessionId", out local) ? local as string : null;
            if (c == null || l == null || !SafeId.IsMatch(c) || !LocalId.IsMatch(l)) return entry;
            string t = obj.TryGetValue("title", out title) ? (title as string ?? "") : "";
            t = Regex.Replace(t, "[\\x00-\\x1f]", " ").Trim();
            if (t.Length > 80) t = t.Substring(0, 80);
            entry.Cli = c;
            entry.Archived = obj.TryGetValue("isArchived", out archived) && archived is bool && (bool)archived;
            entry.Session = new DesktopSession { Local = l, Title = t };
        }
        catch { }
        return entry;
    }

    // 同一时间只扫一次；扫完整体替换索引
    static void ScanDesktop(int minGapMs)
    {
        Dictionary<string, DesktopFile> oldFiles;
        lock (DesktopGate)
        {
            if (desktopScanning || Now() - desktopScannedAt < minGapMs) return;
            desktopScanning = true;
            oldFiles = desktopFiles;
        }
        var files = new Dictionary<string, DesktopFile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = new JavaScriptSerializer { MaxJsonLength = (int)DesktopMaxBytes };
            var broken = new List<string>();
            foreach (var root in DesktopRoots())
            {
                if (!Directory.Exists(root)) continue;
                var found = new List<string>();
                ListDesktopFiles(root, found, broken);
                foreach (var file in found)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        DesktopFile old;
                        files[file] = oldFiles.TryGetValue(file, out old) && old.Stamp == info.LastWriteTimeUtc && old.Size == info.Length
                            ? old : ParseDesktopFile(file, info, json);
                    }
                    catch { }
                }
            }
            // 读不了的目录：沿用上次在那里找到的文件
            foreach (var pair in oldFiles)
                if (!files.ContainsKey(pair.Key))
                    foreach (var dir in broken)
                        if (pair.Key.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) { files[pair.Key] = pair.Value; break; }
        }
        catch { }

        // 同一个会话两处都有：用最近改过的那份；最新那份是归档的就不收
        var newest = new Dictionary<string, DesktopFile>();
        foreach (var f in files.Values)
        {
            if (f.Cli == null) continue;
            DesktopFile seen;
            if (!newest.TryGetValue(f.Cli, out seen) || f.Stamp > seen.Stamp) newest[f.Cli] = f;
        }
        var index = new Dictionary<string, DesktopSession>();
        foreach (var pair in newest) if (!pair.Value.Archived) index[pair.Key] = pair.Value.Session;

        lock (DesktopGate)
        {
            desktopFiles = files;
            desktopIndex = index;
            desktopScannedAt = Now();
            desktopScanning = false;
        }
    }

    static DesktopSession FindDesktop(string cliId, bool rescanOnMiss)
    {
        ScanDesktop(20000);
        DesktopSession hit;
        lock (DesktopGate) { if (desktopIndex.TryGetValue(cliId, out hit)) return hit; }
        if (!rescanOnMiss) return null;
        ScanDesktop(3000);
        lock (DesktopGate) { return desktopIndex.TryGetValue(cliId, out hit) ? hit : null; }
    }

    // 切到前台 / 打开会话共用一个节流：判断和占用在同一把锁里，并发请求不会一起放行
    static readonly object FocusGate = new object();
    static bool TakeFocusSlot(int gapMs)
    {
        lock (FocusGate)
        {
            long now = Now();
            if (now - lastFocus <= gapMs) return false;
            lastFocus = now;
            return true;
        }
    }

    // 打开指定会话：只接受壁纸上正在显示的会话（mod 登记过的）。
    // 桌面版的会话用 claude://code/continue?session=local_… 直接跳进去；找不到对应记录就只把桌面版切到前台
    static void Open(Stream s, string cliId)
    {
        if (!SafeId.IsMatch(cliId)) { Send(s, 400, "{\"error\":\"bad id\"}"); return; }
        bool known;
        lock (Gate) known = Sessions.ContainsKey(cliId);
        if (!known) { Send(s, 404, "{\"error\":\"unknown session\"}"); return; }
        var hit = FindDesktop(cliId, true);
        bool ok = false;
        if (TakeFocusSlot(800))
        {
            try
            {
                string url = hit != null && LocalId.IsMatch(hit.Local) ? "claude://code/continue?session=" + hit.Local : "claude://";
                using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
                ok = true;
            }
            catch { }
        }
        Send(s, 200, ok ? (hit != null ? "{\"ok\":true,\"session\":true}" : "{\"ok\":true,\"session\":false}") : "{\"ok\":false}");
    }

    // 通过 claude:// 协议唤起：由系统启动，Claude 单实例会把已有窗口切到前台
    static void Focus(Stream s)
    {
        bool ok = false;
        if (TakeFocusSlot(1500))
        {
            try
            {
                using (Process.Start(new ProcessStartInfo("claude://") { UseShellExecute = true })) { }
                ok = true;
            }
            catch { }
        }
        Send(s, 200, ok ? "{\"ok\":true}" : "{\"ok\":false}");
    }

    // ---- 系统占用：CPU、内存、显卡 ----
    // CPU：两次 GetSystemTimes 的差值；内存：GlobalMemoryStatusEx；
    // 显卡：NVIDIA 驱动自带的 nvml.dll（直接调用，不额外启动进程），加载不了就只报 CPU 和内存。
    // 0.8 秒内重复请求直接用上一次的结果
    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    class MemoryStatus
    {
        public uint Length = 64;
        public uint Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus m);

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlUtil { public uint Gpu; public uint Mem; }
    [StructLayout(LayoutKind.Sequential)]
    struct NvmlMem { public ulong Total; public ulong Free; public ulong Used; }
    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    static extern int NvmlInit();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    static extern int NvmlShutdown();
    // NVML 返回码：0 成功，3 不支持（这一项读不了而已）；其余（未初始化、设备丢失、驱动出错……）都当句柄失效
    static bool NvmlBroken(int code) { return code != 0 && code != 3; }
    static void DropGpu()
    {
        gpuDevice = IntPtr.Zero;
        gpuTriedAt = Now();
        try { NvmlShutdown(); } catch { }
    }
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    static extern int NvmlCount(out uint count);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    static extern int NvmlHandle(uint index, out IntPtr device);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    static extern int NvmlUtilization(IntPtr device, out NvmlUtil util);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    static extern int NvmlMemory(IntPtr device, out NvmlMem mem);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    static extern int NvmlTemperature(IntPtr device, int sensor, out uint temp);

    static readonly object SysGate = new object();
    static long sysAt, lastIdle, lastKernel, lastUser;
    static double cpuPct = -1;
    static string sysJson = "{}";
    static IntPtr gpuDevice = IntPtr.Zero;
    static long gpuTriedAt;

    static bool GpuReady()
    {
        if (gpuDevice != IntPtr.Zero) return true;
        long now = Now();
        // 加载失败的话一分钟后再试
        if (now - gpuTriedAt < 60000) return false;
        gpuTriedAt = now;
        try
        {
            uint count;
            IntPtr dev;
            if (NvmlInit() != 0 || NvmlCount(out count) != 0 || count == 0) return false;
            if (NvmlHandle(0, out dev) != 0) return false;
            gpuDevice = dev;
            return true;
        }
        catch { return false; }
    }

    static void Sys(Stream s)
    {
        string body;
        lock (SysGate)
        {
            long now = Now();
            if (now - sysAt >= 800)
            {
                sysAt = now;
                var result = new Dictionary<string, object>();
                long idle, kernel, user;
                if (GetSystemTimes(out idle, out kernel, out user))
                {
                    long di = idle - lastIdle, dt = (kernel - lastKernel) + (user - lastUser);
                    if (lastKernel != 0 && dt > 0) cpuPct = Math.Max(0, Math.Min(100, 100.0 * (dt - di) / dt));
                    lastIdle = idle; lastKernel = kernel; lastUser = user;
                }
                if (cpuPct >= 0) result["cpu"] = Math.Round(cpuPct);
                var mem = new MemoryStatus();
                if (GlobalMemoryStatusEx(mem))
                {
                    result["memUsed"] = Math.Round((mem.TotalPhys - mem.AvailPhys) / 1073741824.0, 1);
                    result["memTotal"] = Math.Round(mem.TotalPhys / 1073741824.0, 1);
                }
                if (GpuReady())
                {
                    try
                    {
                        NvmlUtil util;
                        NvmlMem vram;
                        uint temp;
                        int c1 = NvmlUtilization(gpuDevice, out util);
                        if (c1 == 0) result["gpu"] = (int)util.Gpu;
                        int c2 = NvmlMemory(gpuDevice, out vram);
                        if (c2 == 0)
                        {
                            result["vramUsed"] = Math.Round(vram.Used / 1073741824.0, 1);
                            result["vramTotal"] = Math.Round(vram.Total / 1073741824.0, 1);
                        }
                        int c3 = NvmlTemperature(gpuDevice, 0, out temp);
                        if (c3 == 0) result["gpuTemp"] = (int)temp;
                        if (NvmlBroken(c1) || NvmlBroken(c2) || NvmlBroken(c3)) DropGpu();
                    }
                    catch { DropGpu(); }
                }
                sysJson = new JavaScriptSerializer().Serialize(result);
            }
            body = sysJson;
        }
        Send(s, 200, body);
    }

    // 系统媒体键：网易云等播放器都会响应
    static void Media(Stream s, string key)
    {
        byte vk = key == "playpause" ? (byte)0xB3 : key == "next" ? (byte)0xB0 : key == "prev" ? (byte)0xB1 : (byte)0;
        if (vk == 0) { Send(s, 400, "{\"error\":\"bad key\"}"); return; }
        // 防连点：加锁判断并发送，两个并发请求不会连切两首
        lock (MediaGate)
        {
            long now = Now();
            if (now - lastMedia < 300) { Send(s, 200, "{\"ok\":false}"); return; }
            lastMedia = now;
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
        }
        Send(s, 200, "{\"ok\":true}");
    }

    // 网易云播放时窗口标题是「歌名 - 歌手」，没在放时是「网易云音乐」；暂停时标题不变
    static bool ReadCloudMusic(out string title, out string artist)
    {
        title = artist = null;
        string raw = null;
        foreach (var p in Process.GetProcessesByName("cloudmusic"))
        {
            using (p)
            {
                try
                {
                    var t = p.MainWindowTitle;
                    if (!string.IsNullOrEmpty(t) && raw == null) raw = t;
                }
                catch { }
            }
        }
        if (raw != null && raw != "网易云音乐" && raw.Contains(" - "))
        {
            int cut = raw.LastIndexOf(" - ", StringComparison.Ordinal);
            title = raw.Substring(0, cut).Trim();
            artist = raw.Substring(cut + 3).Trim();
        }
        return raw != null;
    }

    // ---- 系统媒体（SMTC）：播放器上报给 Windows 的播放状态和进度 ----
    // 网易云暂停时窗口标题不变，只有这里能知道是播放还是暂停；Folia 还会报精确进度（网易云不报）。
    // 后台计时器每秒采一次样（壁纸 10 秒没来问就不采），/music 直接返回最近一次结果，不会卡住请求。
    // 同一时间最多一个没完成的查询：超时就取消，结束后关闭；出错后 30 秒再重试
    static Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager smtc;
    static Windows.Foundation.IAsyncInfo smtcPending;
    static long smtcRetryAfter, smtcAt, smtcWanted;
    static string smtcTitle, smtcArtist, smtcState, smtcApp;
    // 进度：上报那一刻的位置（毫秒）、上报时间（UTC 毫秒）、总时长（毫秒）；总时长是 0 表示播放器没报
    static long smtcPos, smtcPosAt, smtcDur;
    static double smtcRate = 1;
    static Windows.Media.Control.GlobalSystemMediaTransportControlsSession smtcPick;
    static readonly Regex KnownPlayer = new Regex("cloudmusic|foliamajor", RegexOptions.IgnoreCase);
    static int smtcBusy;
    static Timer smtcTimer;

    // 等异步操作结束：超时就请求取消，留给下一轮收尾（smtcPending），返回 false
    static bool WaitOp(Windows.Foundation.IAsyncInfo info, int ms)
    {
        var clock = Stopwatch.StartNew();
        while (info.Status == Windows.Foundation.AsyncStatus.Started && clock.ElapsedMilliseconds < ms) Thread.Sleep(5);
        if (info.Status == Windows.Foundation.AsyncStatus.Started)
        {
            try { info.Cancel(); } catch { }
            smtcPending = info;
            return false;
        }
        return info.Status == Windows.Foundation.AsyncStatus.Completed;
    }
    static void CloseOp(Windows.Foundation.IAsyncInfo info)
    {
        try { if (info.Status != Windows.Foundation.AsyncStatus.Started) info.Close(); } catch { }
    }

    static void SampleSmtc()
    {
        // 壁纸没来问、也没有在记时的歌，就不采
        bool tracking;
        lock (TrackGate) tracking = trackKey.Length > 0;
        if (!tracking && Now() - Interlocked.Read(ref smtcWanted) > 10000) return;
        if (Interlocked.Exchange(ref smtcBusy, 1) == 1) return;
        try
        {
            // 上一轮超时的查询：还没结束就先不发新的；结束了就关掉
            if (smtcPending != null)
            {
                if (smtcPending.Status == Windows.Foundation.AsyncStatus.Started) return;
                CloseOp(smtcPending);
                smtcPending = null;
            }
            if (smtc == null)
            {
                if (Now() < smtcRetryAfter) return;
                var req = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (!WaitOp(req, 2000)) { smtcRetryAfter = Now() + 30000; return; }
                smtc = req.GetResults();
                CloseOp(req);
                if (smtc == null) { smtcRetryAfter = Now() + 30000; return; }
            }
            string title = null, artist = null, state = null, appId = null;
            long pos = 0, posAt = 0, dur = 0;
            double rate = 1;
            // 认识的播放器里挑一个：正在放的优先，其次第一个找到的
            Windows.Media.Control.GlobalSystemMediaTransportControlsSession pick = null;
            int pickRank = -1;
            foreach (var one in smtc.GetSessions())
            {
                if (!KnownPlayer.IsMatch(one.SourceAppUserModelId ?? "")) continue;
                var st = one.GetPlaybackInfo().PlaybackStatus;
                // Folia 同时在放时对外显示的是它，但网易云的暂停和继续照样要记
                NoteCloudMusicState(one.SourceAppUserModelId,
                    st == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "playing"
                    : st == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ? "paused" : "none");
                int rank = st == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? 2
                    : st == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ? 1 : 0;
                if (rank > pickRank) { pick = one; pickRank = rank; }
            }
            if (pick != null)
            {
                var session = pick;
                appId = session.SourceAppUserModelId ?? "";
                var info = session.GetPlaybackInfo();
                var status = info.PlaybackStatus;
                state = status == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "playing"
                    : status == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ? "paused" : "none";
                if (info.PlaybackRate.HasValue && info.PlaybackRate.Value > 0) rate = info.PlaybackRate.Value;
                var op = session.TryGetMediaPropertiesAsync();
                if (WaitOp(op, 1500))
                {
                    var props = op.GetResults();
                    CloseOp(op);
                    if (props != null && Norm(props.Title).Length > 0)
                    {
                        title = props.Title.Trim();
                        artist = (props.Artist ?? "").Trim();
                    }
                }
                // 歌名之后才读时间线：换歌瞬间宁可是「旧歌名 + 新进度」，下一秒就更正；
                // 反过来会把上一首的进度套到新歌上
                var timeline = session.GetTimelineProperties();
                dur = (long)timeline.EndTime.TotalMilliseconds;
                pos = (long)timeline.Position.TotalMilliseconds;
                posAt = timeline.LastUpdatedTime.ToUnixTimeMilliseconds();
            }
            lock (SmtcGate)
            {
                // 这一轮取歌名超时：同一个播放器就沿用上一轮的，别让壁纸以为歌停了
                if (title == null && appId != null && appId == smtcApp) { title = smtcTitle; artist = smtcArtist; }
                smtcPick = pick;
                smtcTitle = title;
                smtcArtist = artist;
                smtcState = state;
                smtcApp = appId;
                smtcPos = pos;
                smtcPosAt = posAt;
                smtcDur = dur;
                smtcRate = rate;
                smtcAt = Now();
            }
        }
        catch
        {
            smtc = null;
            smtcRetryAfter = Now() + 30000;
        }
        finally { Interlocked.Exchange(ref smtcBusy, 0); }
    }
    static readonly object SmtcGate = new object();

    // 最近一次采样（5 秒内有效）；顺便告诉后台「壁纸还在问」
    static bool ReadSmtc(out string title, out string artist, out string state)
    {
        Interlocked.Exchange(ref smtcWanted, Now());
        lock (SmtcGate)
        {
            title = smtcTitle;
            artist = smtcArtist;
            state = smtcState;
            return state != null && Now() - smtcAt < 5000;
        }
    }

    // 让播放器跳到某个位置。正文两行：毫秒、壁纸上正显示的歌名（对不上就不跳，免得跳到另一个播放器上）。
    // 网易云报告不支持，这种情况回 ok:false，壁纸只调自己的歌词时钟。
    // 同一时间只有一个跳转；超时的取消掉，并等它真正结束后关闭，不会迟到生效后又没人收尾
    static int seekBusy;

    static void Seek(Stream s, string text)
    {
        var parts = text.Split('\n');
        long ms;
        bool ok = false;
        Windows.Media.Control.GlobalSystemMediaTransportControlsSession session;
        string current;
        lock (SmtcGate) { session = smtcPick; current = smtcTitle; }
        bool sameSong = parts.Length > 1 && current != null && Norm(parts[1]) == Norm(current);
        if (long.TryParse(parts[0].Trim(), out ms) && ms >= 0 && ms <= 86400000 && session != null && sameSong &&
            Interlocked.Exchange(ref seekBusy, 1) == 0)
        {
            bool handedOff = false;
            try
            {
                if (session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled)
                {
                    var op = session.TryChangePlaybackPositionAsync(ms * 10000);
                    var clock = Stopwatch.StartNew();
                    while (op.Status == Windows.Foundation.AsyncStatus.Started && clock.ElapsedMilliseconds < 1000) Thread.Sleep(5);
                    if (op.Status == Windows.Foundation.AsyncStatus.Started)
                    {
                        try { op.Cancel(); } catch { }
                        // 还没结束：交给后台等它结束再关，这段时间不接受新的跳转
                        handedOff = true;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                var wait = Stopwatch.StartNew();
                                while (op.Status == Windows.Foundation.AsyncStatus.Started && wait.ElapsedMilliseconds < 10000) Thread.Sleep(50);
                                CloseOp(op);
                            }
                            finally { Interlocked.Exchange(ref seekBusy, 0); }
                        });
                    }
                    else
                    {
                        ok = op.Status == Windows.Foundation.AsyncStatus.Completed && op.GetResults();
                        CloseOp(op);
                    }
                }
            }
            catch { }
            finally { if (!handedOff) Interlocked.Exchange(ref seekBusy, 0); }
        }
        Send(s, 200, ok ? "{\"ok\":true}" : "{\"ok\":false}");
    }

    // ---- Claude 套餐：从 ~/.claude.json 的 oauthAccount 里取，只读这两个字段，不碰登录凭据 ----
    static readonly object PlanGate = new object();
    static long planStamp = -1;
    static string planJson = "{}";

    static void Plan(Stream s)
    {
        string body;
        lock (PlanGate)
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            long stamp = 0;
            try { stamp = new FileInfo(file).LastWriteTimeUtc.Ticks; } catch { }
            if (stamp != planStamp)
            {
                planStamp = stamp;
                string plan = "";
                try
                {
                    string text;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Encoding.UTF8)) text = reader.ReadToEnd();
                    var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
                    var obj = json.DeserializeObject(text) as Dictionary<string, object>;
                    var account = obj != null && obj.ContainsKey("oauthAccount") ? obj["oauthAccount"] as Dictionary<string, object> : null;
                    string org = account != null && account.ContainsKey("organizationType") ? account["organizationType"] as string : null;
                    string tier = account != null && account.ContainsKey("organizationRateLimitTier") ? account["organizationRateLimitTier"] as string : null;
                    plan = PlanName(org ?? "", tier ?? "");
                }
                catch { planStamp = -1; }
                var d = new Dictionary<string, object>();
                d["plan"] = plan;
                planJson = new JavaScriptSerializer().Serialize(d);
            }
            body = planJson;
        }
        Send(s, 200, body);
    }

    static string PlanName(string org, string tier)
    {
        org = org.ToLowerInvariant();
        tier = tier.ToLowerInvariant();
        // Anthropic 把 Max 20x 改名叫 Ultra 了，账号数据里的字段还是旧名字
        if (org.Contains("max")) return tier.Contains("20x") ? "Ultra" : tier.Contains("5x") ? "Max 5x" : "Max";
        if (org.Contains("pro")) return "Pro";
        if (org.Contains("team")) return "Team";
        if (org.Contains("enterprise")) return "Enterprise";
        return "";
    }

    // 网易云不报进度，只能靠「开始时间 + 暂停时长」计时。壁纸被暂停时（比如有窗口最大化）它看不到你暂停了歌，
    // 所以暂停和继续由这里直接记，壁纸睡着时也不会走时
    static void NoteCloudMusicState(string app, string state)
    {
        if (app == null || app.IndexOf("cloudmusic", StringComparison.OrdinalIgnoreCase) < 0) return;
        lock (TrackGate)
        {
            if (trackKey.Length == 0) return;
            long now = Now();
            if (state == "paused" && trackPaused == 0) { trackPaused = now; trackRev++; }
            else if (state == "playing" && trackPaused != 0) { trackStart += now - trackPaused; trackPaused = 0; trackRev++; }
        }
    }

    // 现在这一刻的进度。放着的时候从上报时刻往后推；推出来超过总时长（比如单曲循环没再上报）就当不可信
    static bool LivePosition(long now, out long pos, out long dur)
    {
        lock (SmtcGate)
        {
            pos = smtcPos;
            dur = smtcDur;
            if (dur <= 0 || smtcPosAt <= 0 || smtcPosAt > now + 60000) return false;
            if (smtcState == "playing") pos += (long)((now - smtcPosAt) * smtcRate);
            return pos >= 0 && pos <= dur + 2000;
        }
    }

    // 网易云的歌名优先用窗口标题（和歌词时间线用的一致）；别的播放器（Folia）用系统媒体信息。
    // 播放 / 暂停用系统媒体状态，拿不到就不报 state
    static void Music(Stream s)
    {
        string title, artist;
        bool running = ReadCloudMusic(out title, out artist);
        string mTitle, mArtist, state, app;
        bool fromSmtc, hasPos;
        long pos, dur;
        // 一把锁里读完：歌名和进度必须来自同一次采样
        lock (SmtcGate)
        {
            fromSmtc = ReadSmtc(out mTitle, out mArtist, out state);
            app = smtcApp ?? "";
            hasPos = LivePosition(Now(), out pos, out dur);
        }
        bool isCloudMusic = app.IndexOf("cloudmusic", StringComparison.OrdinalIgnoreCase) >= 0;
        // Folia 开着但没在放歌（没有歌名）：不能拿它的状态去盖网易云的
        if (fromSmtc && !isCloudMusic && mTitle == null) fromSmtc = false;
        if (fromSmtc)
        {
            running = true;
            if ((title == null || !isCloudMusic) && mTitle != null) { title = mTitle; artist = mArtist; }
        }
        var result = new Dictionary<string, object>();
        result["running"] = running;
        if (title != null) { result["title"] = title; result["artist"] = artist; }
        if (fromSmtc && state != null) result["state"] = state;
        if (fromSmtc)
        {
            result["app"] = app;
            if (hasPos) { result["pos"] = pos; result["dur"] = dur; }
        }
        Send(s, 200, new JavaScriptSerializer().Serialize(result));
    }

    // ---- 歌曲时间线 ----

    static readonly object TrackGate = new object();
    static string trackKey = "", trackTitle = "", trackArtist = "";
    static long trackStart = 0, trackPaused = 0;
    static bool trackSynced = false;
    // 服务启动后是否已经看过一次网易云的状态：看过之后再出现的新歌，才知道它是从头开始放的
    static bool trackWatched = false;
    // 时间线每改一次加一：轮询读到的标题如果早于期间收到的事件，就作废
    static long trackRev = 0;
    static int watching = 0;

    static string TrackKey(string title, string artist) { return SongKey(title, artist); }

    static void SetTrack(string title, string artist, long start, bool synced)
    {
        trackKey = TrackKey(title, artist);
        trackTitle = title;
        trackArtist = artist;
        trackStart = start;
        trackPaused = 0;
        trackSynced = synced;
        trackRev++;
    }

    static void WatchTrack()
    {
        // 上一轮还没跑完（读标题偶尔会慢）就跳过，不叠加
        if (Interlocked.Exchange(ref watching, 1) == 1) return;
        try
        {
            long rev;
            lock (TrackGate) rev = trackRev;
            string title, artist;
            ReadCloudMusic(out title, out artist);
            lock (TrackGate)
            {
                // 读标题期间壁纸发来了新事件：这次读到的可能是旧的，丢掉
                if (rev != trackRev) return;
                if (title == null)
                {
                    // 没在放（或网易云没开）：清空，之后出现的歌就是从头放的
                    trackKey = "";
                    trackWatched = true;
                    return;
                }
                if (TrackKey(title, artist) == trackKey) { trackWatched = true; return; }
                // 新歌：每 2 秒看一次，平均晚 1 秒发现
                SetTrack(title, artist, Now() - 1000, trackWatched);
                trackWatched = true;
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref watching, 0); }
    }

    static void TrackState(Stream s)
    {
        var result = new Dictionary<string, object>();
        lock (TrackGate)
        {
            result["key"] = trackKey;
            result["title"] = trackTitle;
            result["artist"] = trackArtist;
            result["startAt"] = trackStart;
            result["pausedAt"] = trackPaused;
            result["synced"] = trackSynced;
        }
        result["now"] = Now();
        Send(s, 200, new JavaScriptSerializer().Serialize(result));
    }

    static void TrackEvent(Stream s, string text)
    {
        var parts = text.Split((char)10);
        string ev = parts[0].Trim();
        string title = parts.Length > 1 ? parts[1].Trim() : "";
        string artist = parts.Length > 2 ? parts[2].Trim() : "";
        if (title.Length == 0 || title.Length > 200 || artist.Length > 200) { Send(s, 400, "{\"error\":\"bad track\"}"); return; }
        string key = TrackKey(title, artist);
        long now = Now();
        // 第 4 行写 paused：换歌那一刻就处于暂停（合成一条事件，免得 start 和 pause 先后乱序）
        bool startPaused = parts.Length > 3 && parts[3].Trim() == "paused";
        lock (TrackGate)
        {
            if (ev == "start")
            {
                // 壁纸看到换歌比这里的 2 秒轮询更及时：不是同一首就换上；刚记下没几秒就用更准的时间
                if (key != trackKey) SetTrack(title, artist, now, true);
                else if (now - trackStart < 4000) { trackStart = now; trackSynced = true; trackRev++; }
                if (startPaused && trackPaused == 0) trackPaused = now;
            }
            else if (key == trackKey)
            {
                if (ev == "pause" && trackPaused == 0) trackPaused = now;
                else if (ev == "resume" && trackPaused != 0) { trackStart += now - trackPaused; trackPaused = 0; }
                else if (ev == "align") { trackStart = now; trackSynced = true; if (trackPaused != 0) trackPaused = now; }
                // 在壁纸上拖了进度条：第 4 行是拖到的位置（毫秒）
                else if (ev == "seek")
                {
                    long ms;
                    if (parts.Length > 3 && long.TryParse(parts[3].Trim(), out ms) && ms >= 0 && ms <= 86400000)
                    {
                        trackStart = now - ms;
                        trackSynced = true;
                        if (trackPaused != 0) trackPaused = now;
                    }
                }
                // 停止：这一次播放结束，同一首再放起来就是新的一次
                else if (ev == "stop") trackKey = "";
                trackRev++;
            }
            else if (ev == "align") SetTrack(title, artist, now, true);
        }
        Send(s, 200, "{\"ok\":true}");
    }

    // ---- 歌词 ----

    static readonly object LyricGate = new object();
    static readonly Dictionary<string, string> LyricCache = new Dictionary<string, string>();

    static string Arg(string query, string name)
    {
        foreach (var part in query.Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part.Substring(0, eq) == name)
                return Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' ')).Trim();
        }
        return "";
    }

    // 整个歌词查询（搜索 + 下载）最多这么久，到点强制中断，上游再慢也不会一直占着连接
    const int LyricBudgetMs = 8000;
    // 同时最多 2 个歌词查询在访问网易云
    static readonly SemaphoreSlim LyricSlots = new SemaphoreSlim(2);

    static string HttpGet(string url, Stopwatch clock, int budgetMs = LyricBudgetMs, string referer = "https://music.163.com/")
    {
        int left = budgetMs - (int)clock.ElapsedMilliseconds;
        if (left <= 0) throw new TimeoutException();
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
        req.Referer = referer;
        req.Timeout = left;
        req.ReadWriteTimeout = left;
        // Timeout 只管单次读写，这里再加一个总截止：到点直接 Abort
        using (new Timer(_ => { try { req.Abort(); } catch { } }, null, left, Timeout.Infinite))
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        {
            var buf = new char[1024 * 1024];
            int n = reader.ReadBlock(buf, 0, buf.Length);
            return new string(buf, 0, n);
        }
    }

    // 封面：只下载网易云图片服务器上的，要 800×800 的大图（莫奈这类样式会放很大显示），最多 3MB。
    // 不再转成 data URL 塞进歌词响应：存在内存里，给壁纸一个 /cover/<歌曲 id>.jpg 的地址
    class CoverEntry { public byte[] Data; public string Type; }
    static readonly object CoverGate = new object();
    static readonly Dictionary<long, CoverEntry> Covers = new Dictionary<long, CoverEntry>();
    static readonly Queue<long> CoverOrder = new Queue<long>();
    const int CoverKeep = 16;
    static readonly Regex CoverUrl = new Regex(@"^https?://p[0-9]+\.music\.126\.net/[A-Za-z0-9_=./-]+\.(jpg|jpeg|png)$", RegexOptions.IgnoreCase);
    static string FetchCover(long id, string url, Stopwatch clock)
    {
        lock (CoverGate) { if (Covers.ContainsKey(id)) return "/cover/" + id + ".jpg"; }
        if (string.IsNullOrEmpty(url) || !CoverUrl.IsMatch(url)) return null;
        int left = LyricBudgetMs - (int)clock.ElapsedMilliseconds;
        if (left <= 300) return null;
        var req = (HttpWebRequest)WebRequest.Create("https://" + url.Substring(url.IndexOf("://") + 3) + "?param=800y800");
        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
        req.Referer = "https://music.163.com/";
        req.AllowAutoRedirect = false;
        req.Timeout = left;
        req.ReadWriteTimeout = left;
        using (new Timer(_ => { try { req.Abort(); } catch { } }, null, left, Timeout.Infinite))
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var body = resp.GetResponseStream())
        {
            string type = (resp.ContentType ?? "").ToLowerInvariant();
            if (resp.StatusCode != HttpStatusCode.OK || !(type.StartsWith("image/jpeg") || type.StartsWith("image/jpg") || type.StartsWith("image/png"))) return null;
            const int Max = 3 * 1024 * 1024;
            var buf = new byte[Max + 1];
            int total = 0, n;
            while (total <= Max && (n = body.Read(buf, total, buf.Length - total)) > 0) total += n;
            if (total == 0 || total > Max) return null;
            var data = new byte[total];
            Buffer.BlockCopy(buf, 0, data, 0, total);
            lock (CoverGate)
            {
                if (!Covers.ContainsKey(id)) CoverOrder.Enqueue(id);
                Covers[id] = new CoverEntry { Data = data, Type = type.StartsWith("image/png") ? "image/png" : "image/jpeg" };
                while (CoverOrder.Count > CoverKeep) Covers.Remove(CoverOrder.Dequeue());
            }
            return "/cover/" + id + ".jpg";
        }
    }

    static bool CoverStillThere(string lyricBody)
    {
        var m = Regex.Match(lyricBody, "/cover/([0-9]{1,20})\\.jpg");
        long id;
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out id)) return true; // 这首本来就没封面
        lock (CoverGate) return Covers.ContainsKey(id);
    }

    static void ServeCover(Stream s, string path)
    {
        var m = Regex.Match(path, "^/cover/([0-9]{1,20})\\.jpg$");
        long id;
        CoverEntry entry = null;
        if (m.Success && long.TryParse(m.Groups[1].Value, out id)) lock (CoverGate) Covers.TryGetValue(id, out entry);
        if (entry == null) { Send(s, 404, "{\"error\":\"not found\"}"); return; }
        var head = "HTTP/1.1 200 OK\r\nContent-Type: " + entry.Type + "\r\nContent-Length: " + entry.Data.Length +
            "\r\nCache-Control: public, max-age=86400\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        var h = Encoding.ASCII.GetBytes(head);
        s.Write(h, 0, h.Length);
        s.Write(entry.Data, 0, entry.Data.Length);
        s.Flush();
    }

    // 去掉控制字符（键里用 \u0001 当分隔符，歌名里不能有它）
    static string Norm(string v) { return Regex.Replace(v ?? "", "[\\x00-\\x1f]", "").Trim().ToLowerInvariant(); }
    // 歌名和歌手之间用控制字符隔开，歌名里带「|」之类的也不会和别的歌撞上
    static string SongKey(string title, string artist) { return Norm(title) + "\u0001" + Norm(artist); }

    static void Lyric(Stream s, string query)
    {
        string title = Arg(query, "title"), artist = Arg(query, "artist");
        if (title.Length == 0 || title.Length > 200 || artist.Length > 200) { Send(s, 400, "{\"error\":\"bad title\"}"); return; }
        string key = SongKey(title, artist);
        string hit;
        bool cached;
        lock (LyricGate) cached = LyricCache.TryGetValue(key, out hit);
        if (cached && !CoverStillThere(hit)) cached = false;
        if (cached) { SendLyric(s, hit, title); return; }

        // 排队等名额；等到以后再查一次缓存——同一首歌前一个请求可能刚查完
        if (!LyricSlots.Wait(3000)) { Send(s, 200, "{\"found\":false,\"error\":true}"); return; }
        try
        {
            lock (LyricGate) cached = LyricCache.TryGetValue(key, out hit);
            if (cached && !CoverStillThere(hit)) cached = false;
            if (cached) { SendLyric(s, hit, title); return; }
            FetchLyric(s, title, artist, key);
        }
        finally { LyricSlots.Release(); }
    }

    static void FetchLyric(Stream s, string title, string artist, string key)
    {
        var clock = Stopwatch.StartNew();
        var json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        var result = new Dictionary<string, object>();
        result["found"] = false;
        bool neteaseFailed = false;
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var search = json.DeserializeObject(HttpGet("https://music.163.com/api/cloudsearch/pc?type=1&limit=8&s=" +
                Uri.EscapeDataString((title + " " + artist).Trim()), clock)) as Dictionary<string, object>;
            object resultObj, songsObj;
            var songs = search != null && search.TryGetValue("result", out resultObj) && resultObj is Dictionary<string, object> &&
                ((Dictionary<string, object>)resultObj).TryGetValue("songs", out songsObj) ? songsObj as object[] : null;

            // 歌名必须对得上（完全一致 4 分，互相包含 2 分），歌手对上再加分，只用来在同名歌里挑
            Dictionary<string, object> best = null;
            int bestScore = 0;
            foreach (var one in songs ?? new object[0])
            {
                var song = one as Dictionary<string, object>;
                if (song == null) continue;
                string name = Norm(song.ContainsKey("name") ? song["name"] as string : "");
                int score = name == Norm(title) ? 4 : (name.Contains(Norm(title)) || Norm(title).Contains(name)) && name.Length > 0 ? 2 : 0;
                if (score == 0) continue;
                var ar = song.ContainsKey("ar") ? song["ar"] as object[] : null;
                foreach (var a in ar ?? new object[0])
                {
                    var an = a as Dictionary<string, object>;
                    string aname = an != null && an.ContainsKey("name") ? Norm(an["name"] as string) : "";
                    if (aname.Length > 0 && Norm(artist).Contains(aname)) { score += 2; break; }
                }
                if (score > bestScore) { bestScore = score; best = song; }
            }

            if (best != null && bestScore >= 2)
            {
                var id = Convert.ToInt64(best["id"]);
                var lyric = json.DeserializeObject(HttpGet("https://music.163.com/api/song/lyric?lv=1&tv=-1&yv=1&id=" + id, clock)) as Dictionary<string, object>;
                Func<string, string> text = k =>
                {
                    object v;
                    var part = lyric != null && lyric.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
                    object l;
                    return part != null && part.TryGetValue("lyric", out l) ? (l as string ?? "") : "";
                };
                result["found"] = true;
                result["id"] = id;
                result["name"] = best["name"];
                result["duration"] = best.ContainsKey("dt") ? best["dt"] : 0;
                result["lrc"] = text("lrc");
                result["tlyric"] = text("tlyric");
                result["yrc"] = text("yrc");
                // 专辑封面：媒体集成没报封面时壁纸用它。下载失败不影响歌词
                try
                {
                    var al = best.ContainsKey("al") ? best["al"] as Dictionary<string, object> : null;
                    string pic = al != null && al.ContainsKey("picUrl") ? al["picUrl"] as string : null;
                    string cover = FetchCover(id, pic, clock);
                    if (cover != null) result["cover"] = "http://127.0.0.1:" + Port + cover;
                }
                catch { }
            }
        }
        catch { neteaseFailed = true; }

        // 网易云没有词（或连不上）：依次问 QQ 音乐、酷狗
        object have;
        bool hasWords = result.TryGetValue("lrc", out have) && !string.IsNullOrWhiteSpace(have as string);
        if (!hasWords && (FallbackQq(title, artist, json, result) || FallbackKugou(title, artist, json, result))) hasWords = true;

        // 都没查到、又是因为网络出错：不缓存，壁纸待会儿会重试。Folia 那边还可能有
        if (!hasWords && neteaseFailed) { SendLyric(s, "{\"found\":false,\"error\":true}", title); return; }

        string body = json.Serialize(result);
        // 查不到的也不缓存：下次放到还能再试
        if (hasWords)
            lock (LyricGate)
            {
                // 每条带一张封面，不能攒太多
                if (LyricCache.Count >= 12) LyricCache.Clear();
                LyricCache[key] = body;
            }
        SendLyric(s, body, title);
    }

    // 正在用 Folia 放这首歌、又开着它的歌词接口时，用它的歌词：和正在播放的音源、用户调过的偏移都对得上。
    // 接口没开（默认）或者歌名对不上，就原样用网易云查到的。Folia 的结果不缓存
    static void SendLyric(Stream s, string body, string title)
    {
        string app;
        lock (SmtcGate) app = smtcApp ?? "";
        if (app.IndexOf("foliamajor", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:32109/v1/lyric");
                req.Timeout = 400;
                req.ReadWriteTimeout = 400;
                req.Proxy = null;
                string text;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = reader.ReadToEnd();
                var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var folia = json.DeserializeObject(text) as Dictionary<string, object>;
                object lines, name;
                if (folia != null && folia.TryGetValue("lines", out lines) && lines is object[] && ((object[])lines).Length > 0 &&
                    folia.TryGetValue("title", out name) && Norm(name as string) == Norm(title))
                {
                    var merged = json.DeserializeObject(body) as Dictionary<string, object>;
                    if (merged != null)
                    {
                        merged["found"] = true;
                        merged.Remove("error");
                        merged["lines"] = lines;
                        merged["source"] = "folia";
                        // 歌词时间 = 播放进度 + 片段起点 - 偏移（正值表示歌词延后）
                        object offset, clip;
                        merged["offset"] = folia.TryGetValue("offset", out offset) ? offset : 0;
                        merged["clipStartMs"] = folia.TryGetValue("clipStartMs", out clip) ? clip : 0;
                        body = json.Serialize(merged);
                    }
                }
            }
            catch { }
        }
        Send(s, 200, body);
    }

    // ---- 备用歌词源：只要明文 LRC，每家最多 2.5 秒 ----

    const int FallbackBudgetMs = 2500;

    // 歌名必须对得上（完全一致 4 分，互相包含 2 分），歌手对上再加 2 分
    static int MatchScore(string name, string singer, string title, string artist)
    {
        string n = Norm(name), t = Norm(title), want = Norm(artist), have = Norm(singer);
        int score = n == t ? 4 : n.Length > 0 && t.Length > 0 && (n.Contains(t) || t.Contains(n)) ? 2 : 0;
        if (score == 0) return 0;
        if (have.Length > 0 && want.Length > 0 && (have.Contains(want) || want.Contains(have))) score += 2;
        return score;
    }

    static bool FallbackQq(string title, string artist, JavaScriptSerializer json, Dictionary<string, object> result)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            const string Referer = "https://y.qq.com/";
            var search = json.DeserializeObject(HttpGet("https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg?format=json&key=" +
                Uri.EscapeDataString((title + " " + artist).Trim()), clock, FallbackBudgetMs, Referer)) as Dictionary<string, object>;
            var song = ObjOf(ObjOf(search, "data"), "song");
            object items;
            if (song == null || !song.TryGetValue("itemlist", out items) || !(items is object[])) return false;
            string mid = null;
            int best = 0;
            foreach (var one in (object[])items)
            {
                var item = one as Dictionary<string, object>;
                int score = MatchScore(StrOf(item, "name"), StrOf(item, "singer"), title, artist);
                string m = StrOf(item, "mid");
                if (score >= 2 && score > best && m != null && Regex.IsMatch(m, "^[A-Za-z0-9]{1,32}$")) { best = score; mid = m; }
            }
            if (mid == null) return false;
            var lyric = json.DeserializeObject(HttpGet("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?format=json&nobase64=1&g_tk=5381&songmid=" + mid,
                clock, FallbackBudgetMs + 1000, Referer)) as Dictionary<string, object>;
            string lrc = WebUtility.HtmlDecode(StrOf(lyric, "lyric") ?? "");
            if (!Regex.IsMatch(lrc, @"\[\d+:\d+")) return false;
            result["found"] = true;
            result["lrc"] = lrc;
            result["tlyric"] = WebUtility.HtmlDecode(StrOf(lyric, "trans") ?? "");
            result["yrc"] = "";
            result["source"] = "qq";
            return true;
        }
        catch { return false; }
    }

    static bool FallbackKugou(string title, string artist, JavaScriptSerializer json, Dictionary<string, object> result)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            const string Referer = "http://www.kugou.com/";
            var search = json.DeserializeObject(HttpGet("http://lyrics.kugou.com/search?ver=1&man=yes&client=pc&duration=&hash=&keyword=" +
                Uri.EscapeDataString((title + " " + artist).Trim()), clock, FallbackBudgetMs, Referer)) as Dictionary<string, object>;
            object list;
            if (search == null || !search.TryGetValue("candidates", out list) || !(list is object[])) return false;
            Dictionary<string, object> pick = null;
            int best = 0;
            foreach (var one in (object[])list)
            {
                var item = one as Dictionary<string, object>;
                int score = MatchScore(StrOf(item, "song"), StrOf(item, "singer"), title, artist);
                if (score >= 2 && score > best) { best = score; pick = item; }
            }
            if (pick == null) return false;
            object idObj;
            string access = StrOf(pick, "accesskey");
            if (access == null || !Regex.IsMatch(access, "^[A-Za-z0-9]{1,64}$") || !pick.TryGetValue("id", out idObj)) return false;
            string id = Convert.ToString(idObj);
            if (!Regex.IsMatch(id, "^[0-9]{1,20}$")) return false;
            var down = json.DeserializeObject(HttpGet("http://lyrics.kugou.com/download?ver=1&client=pc&fmt=lrc&charset=utf8&id=" + id + "&accesskey=" + access,
                clock, FallbackBudgetMs + 1000, Referer)) as Dictionary<string, object>;
            string content = StrOf(down, "content");
            if (string.IsNullOrEmpty(content)) return false;
            string lrc = Encoding.UTF8.GetString(Convert.FromBase64String(content));
            if (!Regex.IsMatch(lrc, @"\[\d+:\d+")) return false;
            result["found"] = true;
            result["lrc"] = lrc;
            result["tlyric"] = "";
            result["yrc"] = "";
            result["source"] = "kugou";
            return true;
        }
        catch { return false; }
    }

    // ---- 歌词舞台的文件：壁纸按本地文件打开舞台，但 file:// 下 fetch 不到本地文件，
    // 雾笺样式的笔顺数据从这里取 ----

    static string stageDir;
    static readonly Dictionary<string, string> StageTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" }, { ".mjs", "text/javascript; charset=utf-8" },
        { ".css", "text/css; charset=utf-8" }, { ".json", "application/json" }, { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" },
        { ".webp", "image/webp" }, { ".svg", "image/svg+xml" }, { ".gif", "image/gif" }, { ".woff2", "font/woff2" }, { ".woff", "font/woff" }, { ".ttf", "font/ttf" },
        { ".otf", "font/otf" }, { ".wasm", "application/wasm" }, { ".txt", "text/plain; charset=utf-8" }, { ".mp4", "video/mp4" }, { ".glb", "model/gltf-binary" }, { ".ktx2", "image/ktx2" },
    };

    static void ServeStage(Stream s, string path)
    {
        if (stageDir == null)
        {
            try
            {
                string where = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wallpaper-dir.txt");
                stageDir = Path.GetFullPath(Path.Combine(File.ReadAllText(where, Encoding.UTF8).Trim(), "stage")).TrimEnd('\\') + "\\";
            }
            catch { stageDir = ""; }
        }
        string rel = Uri.UnescapeDataString(path.Substring("/stage/".Length)).Replace('/', '\\');
        string type;
        string full = null;
        // 只认 stage 目录里、扩展名在白名单上的文件，「..」和绝对路径一律不要
        if (stageDir.Length > 0 && rel.Length > 0 && rel.IndexOf("..", StringComparison.Ordinal) < 0 && rel.IndexOf(':') < 0 && rel[0] != '\\' &&
            StageTypes.TryGetValue(Path.GetExtension(rel), out type))
        {
            full = Path.GetFullPath(Path.Combine(stageDir, rel));
            if (!full.StartsWith(stageDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) full = null;
        }
        else type = null;
        if (full == null) { Send(s, 404, "{\"error\":\"not found\"}"); return; }
        using (var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // 打包出来的 assets 文件名带哈希，可以放心长期缓存，下次打开不用再读
            bool hashed = rel.StartsWith("assets\\", StringComparison.OrdinalIgnoreCase);
            var head = "HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nContent-Length: " + file.Length + "\r\n" +
                "Cache-Control: " + (hashed ? "public, max-age=31536000, immutable" : "no-store") + "\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            s.Write(h, 0, h.Length);
            file.CopyTo(s, 64 * 1024);
            s.Flush();
        }
    }

    static bool ReadRequest(Stream s, Stopwatch clock, out string method, out string target,
        out Dictionary<string, string> headers, out byte[] body)
    {
        method = target = null; headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); body = new byte[0];
        var head = new MemoryStream();
        // 读到 \r\n\r\n 为止（最后 4 个字节），头部最多 16KB
        uint last4 = 0;
        while (last4 != 0x0D0A0D0A)
        {
            if (clock.ElapsedMilliseconds > RequestDeadlineMs) return false;
            int b = s.ReadByte();
            if (b < 0 || head.Length > 16384) return false;
            head.WriteByte((byte)b);
            last4 = (last4 << 8) | (uint)b;
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length < 2) return false;
        method = first[0].ToUpperInvariant();
        target = first[1];
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0) headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
        }
        string lenText;
        int len = 0;
        if (headers.TryGetValue("Content-Length", out lenText) && !int.TryParse(lenText, out len)) return false;
        if (len < 0 || len > MaxBody) return false;
        body = new byte[len];
        int got = 0;
        while (got < len)
        {
            if (clock.ElapsedMilliseconds > RequestDeadlineMs) return false;
            int n = s.Read(body, got, len - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    static void Send(Stream s, int status, string json)
    {
        string reason = status == 200 ? "OK" : status == 204 ? "No Content" : status == 400 ? "Bad Request" :
            status == 403 ? "Forbidden" : status == 429 ? "Too Many Requests" : "Not Found";
        byte[] payload = json == null ? new byte[0] : Encoding.UTF8.GetBytes(json);
        var head = "HTTP/1.1 " + status + " " + reason + "\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Cache-Control: no-store\r\n" +
            (json == null ? "" : "Content-Type: application/json; charset=utf-8\r\n") +
            "Content-Length: " + payload.Length + "\r\n" +
            "Connection: close\r\n\r\n";
        var h = Encoding.ASCII.GetBytes(head);
        s.Write(h, 0, h.Length);
        if (payload.Length > 0) s.Write(payload, 0, payload.Length);
        s.Flush();
    }
}
