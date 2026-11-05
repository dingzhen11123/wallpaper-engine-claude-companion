// 会话管理：原来独立工具 claude-finder 的后台，搬进桥接服务，不再需要单独的进程。
// 只读 Claude 桌面端的会话记录；判断规则来自它的 server.js。
//
//   GET  /finder/sessions?t=&h=   全部会话和状态；h 和这次内容的指纹相同时回 204
//   POST /finder/open?t=          正文：会话 id。让桌面端打开这个对话
//   POST /finder/new?t=           正文：会话 id。在它所在的项目里开新对话
//   POST /finder/reveal?t=        正文：会话 id。在资源管理器里打开项目文件夹
//   POST /finder/pin?t=           正文两行：会话 id、1 或 0
//   POST /finder/handled?t=       正文两行：会话 id、1 或 0
//
// 这些接口带着会话标题、路径和摘要，比 /state 敏感：要带上口令 t。
// 口令由服务生成，写进壁纸目录的 bridge-token.js，只有壁纸页面能读到（网页读不了本机文件）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

static partial class Bridge
{
    const long MinMs = 60 * 1000, HourMs = 60 * MinMs, DayMs = 24 * HourMs;
    // 只为最近有动静的对话去读 CLI 记录，旧对话不可能还在运行
    const long TranscriptCheckMs = 2 * DayMs;
    // 这一轮停在工具调用上，但主记录和子代理记录都这么久没写入，就不再当作“运行中”
    const long RunningStaleMs = 30 * MinMs;
    // 桌面端已记下新消息、CLI 记录还没跟上时，最多这么久内仍算“运行中”
    const long PendingUserMs = 10 * MinMs;
    // “等你处理 / 待验收”只看最近这段时间内提出的；更早的多半已经处理过了
    const long WaitingWindowMs = 2 * DayMs;
    const long ErrorWindowMs = 1 * DayMs;
    // 太久没收到结束通知的后台任务（比如桌面端重启丢了），不再算数
    const long BgMaxAgeMs = 12 * HourMs;
    const int ReadChunk = 4 * 1024 * 1024;
    const long SessionFileMax = 16 * 1024 * 1024;
    const int FinderFileLimit = 5000;
    const int TextLimit = 500;

    static readonly Regex CliId = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase);
    static readonly Regex WorktreeTail = new Regex(@"[\\/]\.claude[\\/]worktrees[\\/][^\\/]+[\\/]?$", RegexOptions.IgnoreCase);
    static readonly Regex ScratchDir = new Regex(@"[\\/]scratch-workspaces[\\/]", RegexOptions.IgnoreCase);
    static readonly Regex NoticeId = new Regex(@"<task-notification>[\s\S]*?<task-id>([^<\s]+)</task-id>");
    static readonly Regex NotAlnum = new Regex("[^A-Za-z0-9]");

    class FSession
    {
        public string Id, ParentId = "", Cli = "", Title, Cwd, Root, Project, Branch = "", Error = "", Summary = "", NeedsAction = "", StatusCategory = "";
        public bool Scratch, Archived;
        public double CreatedAt, LastActivityAt, LastTurnEdgeAt, LatestUserFrameAt, ErrorAt;
        public List<string> PreviousTitles = new List<string>();
    }
    class FFile { public long Stamp, Size; public FSession S; }
    class FTranscript { public long Stamp, Size; public string Turn; public double At; }
    class FSub { public long CheckedAt; public double Newest; }
    class FBg
    {
        public DateTime Birth; public ulong FileId; public long Offset; public List<byte[]> Rest = new List<byte[]>();
        public Dictionary<string, double> Pending = new Dictionary<string, double>();
        public HashSet<string> Ended = new HashSet<string>();
        public int Count;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ByHandleInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleInfo info);

    static readonly object FinderGate = new object();
    static readonly Dictionary<string, FFile> finderFiles = new Dictionary<string, FFile>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> transcriptPaths = new Dictionary<string, string>();
    static readonly Dictionary<string, long> transcriptMisses = new Dictionary<string, long>();
    static readonly Dictionary<string, FTranscript> transcriptCache = new Dictionary<string, FTranscript>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, FSub> subagentCache = new Dictionary<string, FSub>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, FBg> bgCache = new Dictionary<string, FBg>(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> bgSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static List<string> projectDirs = new List<string>();
    static long projectDirsAt;
    static string accountFile = "", accountId;
    static long accountStamp;
    static long finderAt;
    static string finderBody = "", finderHash = "";
    static long lastFinderOpen;

    static readonly JavaScriptSerializer FJson = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

    static long StampOf(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    static long StampOf(string file) { try { return File.Exists(file) ? StampOf(File.GetLastWriteTimeUtc(file)) : 0; } catch { return 0; } }
    static string ProjectsDir() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"); }
    static string Clip(string s) { return s != null && s.Length > TextLimit ? s.Substring(0, TextLimit) : (s ?? ""); }

    static string StrOf(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v as string : null; }
    static Dictionary<string, object> ObjOf(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
    static bool BoolOf(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) && v is bool && (bool)v; }
    static double NumOf(Dictionary<string, object> o, string k)
    {
        object v;
        if (o == null || !o.TryGetValue(k, out v) || v == null) return 0;
        if (v is int) return (int)v;
        if (v is long) return (long)v;
        if (v is decimal) return (double)(decimal)v;
        if (v is double) { var d = (double)v; return double.IsNaN(d) || double.IsInfinity(d) ? 0 : d; }
        return 0;
    }

    // ---- 读取会话 ----

    // 本机可能同时留着 Store 版和旧普通版两套数据；配置最近被写过的那套才是正在用的
    static string ActiveRoot()
    {
        string best = null;
        long bestStamp = -1;
        foreach (var root in DesktopRoots())
        {
            if (!Directory.Exists(root)) continue;
            long stamp = StampOf(Path.Combine(Path.GetDirectoryName(root), "config.json"));
            // 打平时后面的优先：DesktopRoots 里应用商店版排在普通版后面
            if (stamp >= bestStamp) { bestStamp = stamp; best = root; }
        }
        return best;
    }

    // 会话按 <账号>/<组织>/ 存放。桌面端只显示当前登录账号的对话，以前登录过的账号留下的记录要排除
    static string CurrentAccount(string root)
    {
        string file = Path.Combine(Path.GetDirectoryName(root), "config.json");
        long stamp = StampOf(file);
        if (accountFile == file && accountStamp == stamp && accountId != null) return accountId;
        string id = null;
        try
        {
            var cfg = FJson.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
            string v = StrOf(cfg, "lastKnownAccountUuid");
            if (v != null && CliId.IsMatch(v)) id = v;
        }
        catch { }
        if (id != null) { accountFile = file; accountStamp = stamp; accountId = id; } // 读失败不缓存，下次重试
        return id;
    }

    static void ListJsonFiles(string dir, int depth, List<FileInfo> found)
    {
        if (depth > 4 || found.Count >= FinderFileLimit) return;
        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((e.Attributes & FileAttributes.Directory) != 0) ListJsonFiles(e.FullName, depth + 1, found);
                else if (e.Name.StartsWith("local_", StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (found.Count >= FinderFileLimit) return;
                    found.Add((FileInfo)e);
                }
            }
        }
        catch { }
    }

    static string FirstLine(string text)
    {
        if (text == null) return "";
        var one = text.Split('\n', '\u23CE')[0].Trim();
        return one.Length > 300 ? one.Substring(0, 300) : one;
    }

    static FSession Summarize(Dictionary<string, object> j)
    {
        string id = StrOf(j, "sessionId");
        if (id == null || !LocalId.IsMatch(id)) return null;
        var post = ObjOf(j, "postTurnSummary");
        string postFor = StrOf(j, "postTurnSummaryFor");
        var fresh = post != null && !string.IsNullOrEmpty(postFor) && postFor == StrOf(j, "lastAssistantUuid") ? post : null;
        string cwd = StrOf(j, "cwd");
        if (string.IsNullOrEmpty(cwd)) cwd = StrOf(j, "originCwd") ?? "";
        var s = new FSession { Id = id, Cwd = cwd };
        // worktree 归到它所属的仓库
        string root = WorktreeTail.Replace(cwd, "");
        s.Root = root;
        if (root.Length == 0) s.Project = "未知位置";
        else if (ScratchDir.IsMatch(root)) { s.Project = "无文件夹"; s.Scratch = true; }
        else
        {
            string trimmed = root.TrimEnd('\\', '/');
            int cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            string name = cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;
            s.Project = name.Length > 0 ? name : root;
        }
        var spawned = ObjOf(j, "spawnedFrom");
        string parent = StrOf(spawned, "sessionId");
        // Claude 自己发起的子对话：记下父对话，界面上收在父对话下面
        if (parent != null && LocalId.IsMatch(parent) && parent != id) s.ParentId = parent;
        string cli = StrOf(j, "cliSessionId");
        if (cli != null && CliId.IsMatch(cli)) s.Cli = cli;
        string title = StrOf(j, "title");
        s.Title = !string.IsNullOrWhiteSpace(title) ? title.Trim() : "（未命名对话）";
        object prev;
        if (j.TryGetValue("previousTitles", out prev) && prev is object[])
            foreach (var t in (object[])prev)
                if (t is string && s.PreviousTitles.Count < 200) s.PreviousTitles.Add(Clip((string)t));
        s.Branch = StrOf(j, "branch") ?? "";
        s.CreatedAt = NumOf(j, "createdAt");
        double last = NumOf(j, "lastActivityAt");
        s.LastActivityAt = last != 0 ? last : s.CreatedAt;
        s.LastTurnEdgeAt = NumOf(j, "lastTurnEdgeAt");
        s.LatestUserFrameAt = NumOf(j, "latestUserFrameAt");
        s.Archived = BoolOf(j, "isArchived");
        s.Error = Clip(StrOf(j, "error"));
        s.ErrorAt = NumOf(j, "errorAt");
        string summary = StrOf(ObjOf(j, "turnWrapUp"), "description");
        if (string.IsNullOrEmpty(summary)) summary = FirstLine(StrOf(j, "lastTurnReport"));
        if (string.IsNullOrEmpty(summary)) summary = StrOf(fresh, "status_detail");
        s.Summary = Clip(summary);
        s.NeedsAction = Clip(StrOf(fresh, "needs_action"));
        s.StatusCategory = StrOf(fresh, "status_category") ?? "";
        return s;
    }

    static List<FSession> LoadSessions()
    {
        var files = new List<FileInfo>();
        string root = ActiveRoot();
        if (root != null)
        {
            string account = CurrentAccount(root);
            // 读不到当前账号时退回列出这套目录的全部，总比什么都不显示好
            if (account != null) ListJsonFiles(Path.Combine(root, account), 1, files);
            else ListJsonFiles(root, 0, files);
        }
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) alive.Add(f.FullName);
        var gone = new List<string>();
        foreach (var k in finderFiles.Keys) if (!alive.Contains(k)) gone.Add(k);
        foreach (var k in gone) finderFiles.Remove(k);

        var byId = new Dictionary<string, FSession>();
        foreach (var f in files)
        {
            FFile c;
            finderFiles.TryGetValue(f.FullName, out c);
            long stamp = StampOf(f.LastWriteTimeUtc);
            if (c == null || c.Stamp != stamp || c.Size != f.Length)
            {
                FSession parsed = null;
                if (f.Length <= SessionFileMax)
                {
                    try
                    {
                        string text;
                        using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(fs, Encoding.UTF8)) text = reader.ReadToEnd();
                        var obj = FJson.DeserializeObject(text) as Dictionary<string, object>;
                        if (obj != null) parsed = Summarize(obj);
                    }
                    catch { }
                }
                // 文件正在被写入时可能解析失败：先沿用旧结果，下次再读
                if (parsed != null) { c = new FFile { Stamp = stamp, Size = f.Length, S = parsed }; finderFiles[f.FullName] = c; }
            }
            if (c == null) continue;
            FSession seen;
            if (!byId.TryGetValue(c.S.Id, out seen) || c.S.LastActivityAt > seen.LastActivityAt) byId[c.S.Id] = c.S;
        }
        return new List<FSession>(byId.Values);
    }

    // ---- 从 CLI 对话记录判断是否还在运行 ----

    static List<string> ListProjectDirs()
    {
        if (Now() - projectDirsAt > MinMs)
        {
            var list = new List<string>();
            try { list.AddRange(Directory.GetDirectories(ProjectsDir())); } catch { }
            projectDirs = list;
            projectDirsAt = Now();
        }
        return projectDirs;
    }

    static string FindTranscript(FSession s)
    {
        string known;
        if (transcriptPaths.TryGetValue(s.Cli, out known) && File.Exists(known)) return known;
        long missAt;
        // 找不到的隔一会儿再全量找：每次都扫几百个目录太费
        if (transcriptMisses.TryGetValue(s.Cli, out missAt) && Now() - missAt < 30000) return null;
        string name = s.Cli + ".jsonl";
        // 记录目录名就是把 cwd 里的非字母数字换成 -，先试这个，再全量找
        string found = null;
        if (s.Cwd.Length > 0)
        {
            string guess = Path.Combine(ProjectsDir(), NotAlnum.Replace(s.Cwd, "-"), name);
            if (File.Exists(guess)) found = guess;
        }
        if (found == null)
            foreach (var dir in ListProjectDirs())
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) { found = p; break; }
            }
        if (found != null) { transcriptPaths[s.Cli] = found; transcriptMisses.Remove(s.Cli); }
        else transcriptMisses[s.Cli] = Now();
        return found;
    }

    static double EntryTime(Dictionary<string, object> e)
    {
        string t = StrOf(e, "timestamp");
        DateTimeOffset when;
        return t != null && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out when) ? when.ToUnixTimeMilliseconds() : 0;
    }

    // 从文件末尾往前找最后一条能说明这一轮状态的记录。找不到（例如末尾是一整行超大记录）就返回 false，交给时间戳兜底
    static bool LastTurnState(string file, long size, out string turn, out double at)
    {
        turn = null; at = 0;
        foreach (long span in new long[] { 64 * 1024, 1024 * 1024 })
        {
            int len = (int)Math.Min(span, size);
            var buf = new byte[len];
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(size - len, SeekOrigin.Begin);
                int got = 0;
                while (got < len)
                {
                    int n = fs.Read(buf, got, len - got);
                    if (n <= 0) break;
                    got += n;
                }
                len = got;
            }
            var lines = Encoding.UTF8.GetString(buf, 0, len).Split('\n');
            // 第一行可能被截断（多字节字符被切开也只影响这一行）
            int first = len < size ? 1 : 0;
            for (int i = lines.Length - 1; i >= first; i--)
            {
                if (lines[i].Trim().Length == 0) continue;
                Dictionary<string, object> e;
                try { e = FJson.DeserializeObject(lines[i]) as Dictionary<string, object>; } catch { continue; }
                if (e == null || BoolOf(e, "isSidechain")) continue;
                double when = EntryTime(e);
                string type = StrOf(e, "type");
                if (type == "system")
                {
                    string sub = StrOf(e, "subtype");
                    if (sub == "stop_hook_summary" || sub == "turn_duration") { turn = "done"; at = when; return true; }
                    continue;
                }
                if (type == "assistant")
                {
                    string stop = StrOf(ObjOf(e, "message"), "stop_reason");
                    if (stop == "tool_use") { turn = "running"; at = when; return true; }
                    if (!string.IsNullOrEmpty(stop)) { turn = "done"; at = when; return true; }
                    continue;
                }
                if (type == "user")
                {
                    string text = "";
                    object c;
                    var msg = ObjOf(e, "message");
                    if (msg != null && msg.TryGetValue("content", out c))
                    {
                        if (c is string) text = (string)c;
                        else if (c is object[])
                            foreach (var part in (object[])c)
                            {
                                var one = part as Dictionary<string, object>;
                                string piece = StrOf(one, "text");
                                if (piece != null) text += piece;
                            }
                    }
                    turn = text.Contains("[Request interrupted") ? "done" : "running";
                    at = when;
                    return true;
                }
            }
            if (len >= size) break;
        }
        return false;
    }

    // 子代理、工作流运行时，主记录可能很久不动，但它们自己的记录在持续写入
    static double NewestSubagentWrite(string file, string cli)
    {
        FSub c;
        if (subagentCache.TryGetValue(file, out c) && Now() - c.CheckedAt < 10000) return c.Newest;
        double newest = 0;
        try
        {
            foreach (var one in new DirectoryInfo(Path.Combine(Path.GetDirectoryName(file), cli, "subagents")).EnumerateFiles("*.jsonl"))
                newest = Math.Max(newest, StampOf(one.LastWriteTimeUtc));
        }
        catch { }
        subagentCache[file] = new FSub { CheckedAt = Now(), Newest = newest };
        return newest;
    }

    // 没有记录可读时 turn 为 null
    static void TranscriptState(FSession s, long now, out string turn, out double at, out double activeAt)
    {
        turn = null; at = 0; activeAt = 0;
        if (s.Cli.Length == 0 || s.Archived || now - Math.Max(s.LastActivityAt, s.LatestUserFrameAt) > TranscriptCheckMs) return;
        string file = FindTranscript(s);
        if (file == null) return;
        FileInfo st;
        try { st = new FileInfo(file); if (!st.Exists) return; } catch { return; }
        long stamp = StampOf(st.LastWriteTimeUtc);
        FTranscript c;
        if (!transcriptCache.TryGetValue(file, out c) || c.Stamp != stamp || c.Size != st.Length)
        {
            string t; double when;
            bool ok;
            try { ok = LastTurnState(file, st.Length, out t, out when); } catch { return; } // 读失败不缓存，下次重试
            c = new FTranscript { Stamp = stamp, Size = st.Length, Turn = ok ? t : null, At = ok ? when : 0 };
            transcriptCache[file] = c;
        }
        turn = c.Turn;
        at = c.At;
        activeAt = c.Stamp;
        if (c.Turn == "running" && now - activeAt > MinMs) activeAt = Math.Max(activeAt, NewestSubagentWrite(file, s.Cli));
        // 文件存在但没能判断出状态：按“有记录、状态不明”处理（走时间戳兜底）
        if (turn == null) activeAt = 0;
    }

    // ---- 后台任务：启动过、还没收到结束通知的 ----
    // Claude 让命令或子代理在后台跑时，这一轮会先结束，但事情其实还没做完。
    // 启动：工具结果里有 backgroundTaskId（后台命令），或 isAsync + agentId（后台子代理）；
    // 结束：出现带同一个 task-id 的 <task-notification>，或被停止（Successfully stopped task）。
    // 记录文件可能有十几 MB，按字节偏移增量读，只解析新追加的部分

    static string NoticeText(Dictionary<string, object> e)
    {
        string type = StrOf(e, "type");
        if (type == "user")
        {
            string content = StrOf(ObjOf(e, "message"), "content");
            if (content != null) return content;
        }
        if (type == "queue-operation")
        {
            string content = StrOf(e, "content");
            if (content != null) return content;
        }
        var attachment = ObjOf(e, "attachment");
        if (attachment != null && StrOf(attachment, "type") == "queued_command")
        {
            string prompt = StrOf(attachment, "prompt");
            if (prompt != null) return prompt;
        }
        return "";
    }

    static void ScanBgLine(string line, FBg st)
    {
        // 绝大多数行和后台任务无关，先粗筛再解析
        if (line.IndexOf("backgroundTaskId", StringComparison.Ordinal) < 0 && line.IndexOf("\"isAsync\"", StringComparison.Ordinal) < 0 &&
            line.IndexOf("task-notification", StringComparison.Ordinal) < 0 && line.IndexOf("\"task_id\"", StringComparison.Ordinal) < 0) return;
        Dictionary<string, object> e;
        try { e = FJson.DeserializeObject(line) as Dictionary<string, object>; } catch { return; }
        if (e == null || BoolOf(e, "isSidechain")) return;
        var r = ObjOf(e, "toolUseResult");
        if (StrOf(e, "type") == "user" && r != null)
        {
            double at = EntryTime(e);
            if (at == 0) at = Now();
            string bg = StrOf(r, "backgroundTaskId");
            // 结束通知可能比启动记录先落盘（很快就跑完的任务），所以已结束的编号不会再被启动记录加回来
            if (bg != null && !st.Ended.Contains(bg)) st.Pending[bg] = at;
            string agent = StrOf(r, "agentId");
            if (BoolOf(r, "isAsync") && agent != null && !st.Ended.Contains(agent)) st.Pending[agent] = at;
            string task = StrOf(r, "task_id"), message = StrOf(r, "message");
            if (task != null && message != null && message.StartsWith("Successfully stopped", StringComparison.Ordinal))
            {
                st.Pending.Remove(task);
                st.Ended.Add(task);
            }
        }
        string text = NoticeText(e);
        if (text.Contains("<task-notification>"))
            foreach (Match m in NoticeId.Matches(text))
            {
                st.Pending.Remove(m.Groups[1].Value);
                st.Ended.Add(m.Groups[1].Value);
            }
    }

    // 把一块新读到的字节切成整行交给 ScanBgLine；没写完的半行分段存进 Rest，整行到齐再拼一次
    static void FeedChunk(byte[] buf, int len, FBg st)
    {
        int first = Array.IndexOf(buf, (byte)10, 0, len);
        if (first < 0)
        {
            var part = new byte[len];
            Buffer.BlockCopy(buf, 0, part, 0, len);
            st.Rest.Add(part);
            return;
        }
        if (st.Rest.Count > 0)
        {
            using (var head = new MemoryStream())
            {
                foreach (var one in st.Rest) head.Write(one, 0, one.Length);
                head.Write(buf, 0, first);
                ScanBgLine(Encoding.UTF8.GetString(head.GetBuffer(), 0, (int)head.Length), st);
            }
            st.Rest.Clear();
        }
        else ScanBgLine(Encoding.UTF8.GetString(buf, 0, first), st);
        int start = first + 1;
        while (start < len)
        {
            int end = Array.IndexOf(buf, (byte)10, start, len - start);
            if (end < 0) break;
            if (end > start) ScanBgLine(Encoding.UTF8.GetString(buf, start, end - start), st);
            start = end + 1;
        }
        if (start < len)
        {
            var part = new byte[len - start];
            Buffer.BlockCopy(buf, start, part, 0, len - start);
            st.Rest.Add(part);
        }
    }

    static int CountPending(FBg st, long now)
    {
        int n = 0;
        var old = new List<string>();
        foreach (var kv in st.Pending)
        {
            if (now - kv.Value < BgMaxAgeMs) n++;
            else old.Add(kv.Key);
        }
        foreach (var k in old) st.Pending.Remove(k); // 过期的直接丢掉
        return n;
    }

    static int BackgroundCount(FSession s, long now)
    {
        if (s.Cli.Length == 0 || s.Archived || now - Math.Max(s.LastActivityAt, s.LatestUserFrameAt) > TranscriptCheckMs) return 0;
        string file = FindTranscript(s);
        if (file == null) return 0;
        bgSeen.Add(file);
        FBg c;
        bgCache.TryGetValue(file, out c);
        FileInfo st;
        try { st = new FileInfo(file); if (!st.Exists) return c != null ? c.Count : 0; } catch { return c != null ? c.Count : 0; }
        // 文件被截短、或换成了另一个文件（创建时间变了），就从头重读
        if (c == null || st.Length < c.Offset || st.CreationTimeUtc != c.Birth) c = new FBg { Birth = st.CreationTimeUtc };
        if (st.Length > c.Offset)
        {
            // 先改一份副本：读到一半失败不留下半截状态，下次重试
            var next = new FBg { Birth = c.Birth, FileId = c.FileId, Offset = c.Offset, Rest = new List<byte[]>(c.Rest), Pending = new Dictionary<string, double>(c.Pending), Ended = new HashSet<string>(c.Ended) };
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    // 同名文件被替换后创建时间可能不变（NTFS 的文件隧道）：再比一次文件 ID，不是同一个就从头读
                    ByHandleInfo id;
                    ulong fileId = GetFileInformationByHandle(fs.SafeFileHandle, out id) ? ((ulong)id.IndexHigh << 32) | id.IndexLow : 0;
                    if (next.FileId != 0 && fileId != 0 && fileId != next.FileId)
                        next = new FBg { Birth = st.CreationTimeUtc };
                    next.FileId = fileId;
                    fs.Seek(next.Offset, SeekOrigin.Begin);
                    var buf = new byte[Math.Min(ReadChunk, st.Length - next.Offset)];
                    while (next.Offset < st.Length)
                    {
                        int want = (int)Math.Min(buf.Length, st.Length - next.Offset);
                        int got = fs.Read(buf, 0, want);
                        if (got <= 0) break;
                        next.Offset += got;
                        FeedChunk(buf, got, next);
                    }
                }
            }
            catch
            {
                FBg kept;
                return bgCache.TryGetValue(file, out kept) ? kept.Count : 0; // 读失败沿用上次结果
            }
            c = next;
        }
        c.Count = CountPending(c, now);
        bgCache[file] = c;
        return c.Count;
    }

    // 每次列表里用到的记录文件；没再用到的缓存清掉（对话被归档、删除或过了检查期）
    static void PruneBgCache()
    {
        var gone = new List<string>();
        foreach (var k in bgCache.Keys) if (!bgSeen.Contains(k)) gone.Add(k);
        foreach (var k in gone) bgCache.Remove(k);
        bgSeen.Clear();
    }

    static bool RunningOf(FSession s, long now)
    {
        string turn; double at, activeAt;
        TranscriptState(s, now, out turn, out at, out activeAt);
        if (turn == "running") return now - activeAt < RunningStaleMs;
        // 记录停在上一轮结束，但桌面端已经收到新消息：新一轮刚开始、记录还没写到
        if (turn == "done") return s.LatestUserFrameAt > at + 5000 && now - s.LatestUserFrameAt < PendingUserMs;
        // 没有 CLI 记录可读时，退回用桌面端的时间戳估计
        return s.LastTurnEdgeAt != 0 && s.LatestUserFrameAt > s.LastTurnEdgeAt && now - s.LastActivityAt < RunningStaleMs;
    }

    // 这一轮结束的时间，用来判断“已处理”标记是否还有效
    static double TurnKey(FSession s) { return s.LastTurnEdgeAt != 0 ? s.LastTurnEdgeAt : s.LastActivityAt; }

    static string StatusOf(FSession s, long now, bool handled, int background, bool running)
    {
        if (s.Archived) return "archived";
        double user = s.LatestUserFrameAt;
        bool freshError = s.Error.Length > 0 && s.ErrorAt >= user && now - s.ErrorAt < ErrorWindowMs;
        if (running) return freshError && s.ErrorAt > now - 5 * MinMs ? "error" : "running";
        // 出错、等你处理优先：这些要你动手，后台任务数会补在标签后面
        bool fresh = !handled && now - TurnKey(s) <= WaitingWindowMs;
        if (!handled && freshError) return "error";
        if (fresh && (s.StatusCategory == "blocked" || s.NeedsAction.Length > 0)) return "waiting";
        // 这一轮结束了，但还有后台任务在跑：事情没做完，不能算“待验收 / 空闲”
        if (background > 0) return "running";
        if (fresh && s.StatusCategory == "review_ready") return "review";
        return "idle";
    }

    // ---- 置顶、已处理：存在桥接服务目录里；第一次运行时把 claude-finder 的数据搬过来 ----

    static HashSet<string> finderPins;
    static Dictionary<string, double> finderHandled;

    static string FinderDataFile() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "finder-data.json"); }

    static void LoadFinderData()
    {
        if (finderPins != null) return;
        finderPins = new HashSet<string>();
        finderHandled = new Dictionary<string, double>();
        string file = FinderDataFile();
        string source = file;
        if (!File.Exists(file))
            source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeFinder", "data.json");
        try
        {
            var d = FJson.DeserializeObject(File.ReadAllText(source, Encoding.UTF8)) as Dictionary<string, object>;
            object pins;
            if (d != null && d.TryGetValue("pins", out pins) && pins is object[])
                foreach (var p in (object[])pins)
                    if (p is string && LocalId.IsMatch((string)p)) finderPins.Add((string)p);
            var handled = ObjOf(d, "handled");
            if (handled != null)
                foreach (var kv in handled)
                    if (LocalId.IsMatch(kv.Key) && NumOf(handled, kv.Key) != 0) finderHandled[kv.Key] = NumOf(handled, kv.Key);
        }
        catch { }
    }

    static void SaveFinderData()
    {
        var d = new Dictionary<string, object>();
        d["pins"] = new List<string>(finderPins);
        d["handled"] = finderHandled;
        string file = FinderDataFile();
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, FJson.Serialize(d), new UTF8Encoding(false));
        if (File.Exists(file)) File.Replace(tmp, file, null);
        else File.Move(tmp, file);
    }

    // ---- 接口 ----

    // 把现在的列表算好放进缓存；1 秒内重复请求直接用上次的结果
    static void RefreshFinder()
    {
        long now = Now();
        if (finderBody.Length > 0 && now - finderAt < 1000) return;
        LoadFinderData();
        var list = new List<Dictionary<string, object>>();
        foreach (var s in LoadSessions())
        {
            double mark;
            bool handled = finderHandled.TryGetValue(s.Id, out mark) && mark >= TurnKey(s);
            int background = BackgroundCount(s, now);
            bool running = !s.Archived && RunningOf(s, now);
            var one = new Dictionary<string, object>();
            one["id"] = s.Id;
            one["parentId"] = s.ParentId;
            one["cliSessionId"] = s.Cli;
            one["title"] = s.Title;
            one["previousTitles"] = s.PreviousTitles;
            one["cwd"] = s.Cwd;
            one["projectRoot"] = s.Root;
            one["projectName"] = s.Project;
            one["scratch"] = s.Scratch;
            one["branch"] = s.Branch;
            one["createdAt"] = s.CreatedAt;
            one["lastActivityAt"] = s.LastActivityAt;
            one["archived"] = s.Archived;
            one["error"] = s.Error;
            one["summary"] = s.Summary;
            one["needsAction"] = s.NeedsAction;
            one["status"] = StatusOf(s, now, handled, background, running);
            one["background"] = background;
            one["turnRunning"] = running;
            one["pinned"] = finderPins.Contains(s.Id);
            list.Add(one);
        }
        PruneBgCache();
        finderBody = FJson.Serialize(list);
        // 指纹：内容没变就不用让壁纸重新解析、重画
        uint hash = 2166136261;
        foreach (char c in finderBody) { hash ^= c; hash *= 16777619; }
        finderHash = hash.ToString("x8") + finderBody.Length.ToString("x");
        finderAt = Now();
    }

    static FSession FindFinderSession(string id)
    {
        if (id == null || !LocalId.IsMatch(id)) return null;
        foreach (var s in LoadSessions()) if (s.Id == id) return s;
        return null;
    }

    static string ErrorJson(string message)
    {
        var d = new Dictionary<string, object>();
        d["error"] = message;
        return FJson.Serialize(d);
    }

    // 防连点：短时间内重复唤起会让桌面端来回切
    static bool TakeOpenSlot()
    {
        long now = Now();
        if (now - lastFinderOpen < 800) return false;
        lastFinderOpen = now;
        return true;
    }

    static void ShellOpen(string url)
    {
        using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
    }

    static void FinderRoute(Stream stream, string method, string path, string query, string text)
    {
        if (method == "GET" && path == "/finder/sessions")
        {
            string list, hash;
            lock (FinderGate)
            {
                RefreshFinder();
                list = finderBody;
                hash = finderHash;
            }
            if (Arg(query, "h") == hash) { Send(stream, 204, null); return; }
            Send(stream, 200, "{\"now\":" + Now() + ",\"h\":\"" + hash + "\",\"sessions\":" + list + "}");
            return;
        }
        if (method != "POST") { Send(stream, 404, "{\"error\":\"not found\"}"); return; }
        var parts = text.Split('\n');
        string id = parts[0].Trim();
        bool on = parts.Length > 1 && parts[1].Trim() == "1";
        // 锁里只读写内存和数据文件；唤起程序（可能卡几秒）和发送响应都放到锁外，别挡住 /finder/sessions
        int status = 200;
        string body = "{\"ok\":true}";
        string openUrl = null, revealDir = null;
        lock (FinderGate)
        {
            if (path == "/finder/open")
            {
                var one = FindFinderSession(id);
                if (one == null) { status = 404; body = ErrorJson("找不到这个对话"); }
                else if (one.Archived) { status = 400; body = ErrorJson("这个对话已归档，Claude 不支持直接跳转，请先在 Claude 里取消归档"); }
                else if (!TakeOpenSlot()) { status = 429; body = ErrorJson("点得太快了，稍等一下"); }
                else openUrl = "claude://code/continue?session=" + one.Id;
            }
            else if (path == "/finder/new")
            {
                // 在某个项目里开新对话：只认对话编号，由服务查出它的项目文件夹，请求传不进任意路径
                var one = FindFinderSession(id);
                if (one == null) { status = 404; body = ErrorJson("找不到这个项目"); }
                // 外部链接没法指定“无文件夹”：不带文件夹时桌面端会沿用上次选的目录，所以临时对话不支持
                else if (one.Scratch || one.Root.Length == 0) { status = 400; body = ErrorJson("临时对话请在 Claude 侧边栏“本电脑”那组点“+”新建"); }
                else if (!Directory.Exists(one.Root)) { status = 404; body = ErrorJson("这个项目的文件夹已不存在"); }
                else if (!TakeOpenSlot()) { status = 429; body = ErrorJson("点得太快了，稍等一下"); }
                else
                {
                    openUrl = "claude://code/new?folder=" + Uri.EscapeDataString(one.Root);
                    var d = new Dictionary<string, object>();
                    d["ok"] = true;
                    d["folder"] = one.Root;
                    body = FJson.Serialize(d);
                }
            }
            else if (path == "/finder/reveal")
            {
                var one = FindFinderSession(id);
                if (one == null || one.Cwd.Length == 0 || one.Cwd.IndexOf('"') >= 0 || !Directory.Exists(one.Cwd)) { status = 404; body = ErrorJson("这个对话的文件夹已不存在"); }
                else revealDir = one.Cwd;
            }
            else if (path == "/finder/pin" || path == "/finder/handled")
            {
                if (!LocalId.IsMatch(id)) { status = 400; body = ErrorJson("bad id"); }
                else
                {
                    LoadFinderData();
                    bool changed = true;
                    if (path == "/finder/pin")
                    {
                        if (on) finderPins.Add(id); else finderPins.Remove(id);
                    }
                    else
                    {
                        var one = FindFinderSession(id);
                        if (one == null) { status = 404; body = ErrorJson("找不到这个对话"); changed = false; }
                        // 记下当时这一轮的时间；对话之后有新的一轮，标记自动失效
                        else if (on) finderHandled[id] = TurnKey(one);
                        else finderHandled.Remove(id);
                    }
                    if (changed)
                    {
                        SaveFinderData();
                        finderAt = 0; // 下次请求马上重算
                    }
                }
            }
            else { status = 404; body = "{\"error\":\"not found\"}"; }
        }
        try
        {
            if (openUrl != null) ShellOpen(openUrl);
            if (revealDir != null)
                using (Process.Start(new ProcessStartInfo("explorer.exe", "\"" + revealDir + "\"") { UseShellExecute = false })) { }
        }
        catch (Exception err) { status = 400; body = ErrorJson("打不开：" + err.Message); }
        Send(stream, status, body);
    }

    // ---- 口令 ----

    static string bridgeToken = "";

    // 口令存在服务目录，重启不变；同时写一份到壁纸目录给页面读。壁纸目录写在 wallpaper-dir.txt 里
    static void InitToken()
    {
        try
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string file = Path.Combine(dir, "token.txt");
            string token = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            if (!Regex.IsMatch(token, "^[0-9a-f]{32}$"))
            {
                var bytes = new byte[16];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
                token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(file, token);
            }
            bridgeToken = token;
            string where = Path.Combine(dir, "wallpaper-dir.txt");
            if (!File.Exists(where)) return;
            string wallpaper = File.ReadAllText(where, Encoding.UTF8).Trim();
            if (!Directory.Exists(wallpaper)) return;
            string js = Path.Combine(wallpaper, "bridge-token.js");
            string content = "window.BRIDGE_TOKEN = \"" + token + "\";\n";
            if (!File.Exists(js) || File.ReadAllText(js) != content) File.WriteAllText(js, content);
        }
        catch { }
    }

    static bool TokenOk(string query)
    {
        string given = Arg(query, "t");
        if (bridgeToken.Length == 0 || given.Length != bridgeToken.Length) return false;
        int diff = 0;
        for (int i = 0; i < given.Length; i++) diff |= given[i] ^ bridgeToken[i];
        return diff == 0;
    }
}
