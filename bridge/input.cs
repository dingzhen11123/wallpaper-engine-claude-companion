// 壁纸的搜索框：Wallpaper Engine 只把鼠标转发给网页壁纸，键盘进不去，也没有输入法。
// 所以点搜索框时，由桥接服务在同一个位置弹出一个原生小输入框（中文输入法可用），打的字由壁纸轮询取走。
//
//   POST /input/open?t=    正文两行：第一行 "x y 宽 高 字号"（屏幕物理像素），第二行是已有文字
//   GET  /input/state?t=   {"open":true,"text":"...","keys":["Down","Enter"]}；keys 取走后清空
//   POST /input/close?t=   收起
//   GET  /input/wheel?t=   {"d":滚轮累计量}；见下面「滚轮」
//
// 点到输入框以外（失去焦点）就自己收起，文字留给壁纸继续用来筛选。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static partial class Bridge
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    // 见 Main：进程一开始就调用
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static readonly object InputGate = new object();
    static InputBox inputBox;
    static bool inputOpen;
    static string inputText = "";
    static readonly List<string> inputKeys = new List<string>();

    class InputBox : Form
    {
        public readonly TextBox Box;
        // 程序自己改文字时不算用户输入
        bool setting;

        public InputBox()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(38, 35, 32);
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = BackColor,
                ForeColor = Color.FromArgb(237, 230, 218),
                Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel),
            };
            Controls.Add(Box);
            Box.TextChanged += (a, b) =>
            {
                if (setting) return;
                lock (InputGate) inputText = Box.Text;
            };
            Box.KeyDown += (a, e) =>
            {
                string key = e.KeyCode == Keys.Up ? "Up" : e.KeyCode == Keys.Down ? "Down" : e.KeyCode == Keys.Enter ? "Enter" : e.KeyCode == Keys.Escape ? "Escape" : null;
                if (key == null) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (key == "Escape")
                {
                    // 有字先清空，再按一次收起
                    if (Box.Text.Length > 0) { Box.Text = ""; return; }
                    Collapse();
                    return;
                }
                lock (InputGate) { if (inputKeys.Count < 32) inputKeys.Add(key); }
            };
            Deactivate += (a, b) => Collapse();
        }

        // Alt+F4 也只是收起：这个窗口要一直留着，销毁了搜索框就再也开不出来
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.ApplicationExitCall && e.CloseReason != CloseReason.WindowsShutDown)
            {
                e.Cancel = true;
                Collapse();
                return;
            }
            base.OnFormClosing(e);
        }

        // 不抢别的窗口的 Alt+Tab 位置
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public void Collapse()
        {
            lock (InputGate) inputOpen = false;
            Hide();
        }

        public void Open(int x, int y, int w, int h, int fontPx, string text)
        {
            setting = true;
            var font = Box.Font;
            if (Math.Abs(font.Size - fontPx) > 0.5f)
            {
                Box.Font = new Font("Microsoft YaHei UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
                font.Dispose();
            }
            Box.Text = text;
            setting = false;
            Bounds = new Rectangle(x, y, w, h);
            Box.Left = 0;
            Box.Width = w;
            // 微软雅黑在原生输入框里字形偏下，往上提一点，点开时才不会和页面上的字错开
            Box.Top = Math.Max(0, (h - Box.Height) / 2 - fontPx / 7);
            Show();
            Activate();
            ForceForeground();
            Box.Focus();
            Box.SelectionStart = Box.Text.Length;
        }

        // 后台进程不能随便抢前台：临时把输入队列接到当前前台窗口上再切
        void ForceForeground()
        {
            var fg = GetForegroundWindow();
            uint pid;
            uint target = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid);
            uint me = GetCurrentThreadId();
            bool joined = target != 0 && target != me && AttachThreadInput(me, target, true);
            try
            {
                BringWindowToTop(Handle);
                SetForegroundWindow(Handle);
            }
            finally
            {
                if (joined) AttachThreadInput(me, target, false);
            }
        }
    }

    // 输入框有自己的消息循环线程，第一次用到时才创建，而且只创建一次。
    // 等它就绪时不持锁：窗口线程里的事件处理也要拿 InputGate
    static readonly object InputStartGate = new object();
    static ManualResetEventSlim inputReady;

    static InputBox EnsureInputBox()
    {
        ManualResetEventSlim ready;
        lock (InputStartGate)
        {
            if (inputReady == null)
            {
                inputReady = new ManualResetEventSlim();
                var signal = inputReady;
                var t = new Thread(() =>
                {
                    Application.EnableVisualStyles();
                    var form = new InputBox();
                    var unused = form.Handle; // 先把窗口句柄建好，别的线程才能 BeginInvoke
                    lock (InputGate) inputBox = form;
                    signal.Set();
                    Application.Run();
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }
            ready = inputReady;
        }
        ready.Wait(3000);
        lock (InputGate) return inputBox != null && inputBox.IsHandleCreated && !inputBox.IsDisposed ? inputBox : null;
    }

    // ---- 滚轮 ----
    // Wallpaper Engine 不一定把滚轮转发给网页壁纸。壁纸的鼠标停在会话面板上时会不断来问 /input/wheel，
    // 被问的时候才挂一个低级鼠标钩子记下滚了多少；超过 2 秒没人问就摘掉，平时不碰系统鼠标。
    // 只统计鼠标下面是桌面的情况：别的窗口盖在上面时滚轮是给那个窗口的。钩子不拦截，事件照常往下传。

    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct HookPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MouseHookInfo { public HookPoint Pt; public uint Data, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(HookPoint p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    static readonly HookProc WheelProc = OnMouseHook; // 放在静态字段里，免得被回收
    static int wheelDelta;
    static long wheelAskedAt;
    static int wheelThreadOn;

    static IntPtr OnMouseHook(int code, IntPtr w, IntPtr l)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (code >= 0 && w.ToInt64() == WM_MOUSEWHEEL)
        {
            try
            {
                var info = (MouseHookInfo)Marshal.PtrToStructure(l, typeof(MouseHookInfo));
                var sb = new System.Text.StringBuilder(32);
                GetClassName(WindowFromPoint(info.Pt), sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls == "SysListView32" || cls == "SHELLDLL_DefView" || cls == "WorkerW" || cls == "Progman")
                    Interlocked.Add(ref wheelDelta, (short)(info.Data >> 16));
            }
            catch { }
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    // 钩子放在一个专门的线程上，只做这一件事，也不和搜索框共用线程：
    // 低级钩子由挂它的线程处理，这个线程一卡，全系统的鼠标都会卡。没人问了就摘钩子、线程退出
    static void ArmWheel()
    {
        if (Interlocked.Exchange(ref wheelThreadOn, 1) == 1) return;
        var t = new Thread(() =>
        {
            IntPtr hook = IntPtr.Zero;
            try
            {
                Interlocked.Exchange(ref wheelDelta, 0);
                hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, WheelProc, GetModuleHandle(null), 0);
                using (var timer = new System.Windows.Forms.Timer { Interval = 500 })
                {
                    timer.Tick += (a, b) =>
                    {
                        if (Now() - Interlocked.Read(ref wheelAskedAt) >= 2000) Application.ExitThread();
                    };
                    timer.Start();
                    Application.Run();
                }
            }
            catch { }
            finally
            {
                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
                Interlocked.Exchange(ref wheelThreadOn, 0);
            }
        });
        t.IsBackground = true;
        t.Priority = ThreadPriority.AboveNormal;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void InputRoute(Stream stream, string method, string path, string text)
    {
        if (method == "GET" && path == "/input/wheel")
        {
            Interlocked.Exchange(ref wheelAskedAt, Now());
            ArmWheel();
            Send(stream, 200, "{\"d\":" + Interlocked.Exchange(ref wheelDelta, 0) + "}");
            return;
        }
        if (method == "GET" && path == "/input/state")
        {
            var d = new Dictionary<string, object>();
            lock (InputGate)
            {
                d["open"] = inputOpen;
                d["text"] = inputText;
                d["keys"] = new List<string>(inputKeys);
                inputKeys.Clear();
            }
            Send(stream, 200, FJson.Serialize(d));
            return;
        }
        if (method == "POST" && path == "/input/open")
        {
            var lines = text.Split('\n');
            var nums = lines[0].Trim().Split(' ');
            int x, y, w, h, font;
            if (nums.Length != 5 || !int.TryParse(nums[0], out x) || !int.TryParse(nums[1], out y) || !int.TryParse(nums[2], out w) ||
                !int.TryParse(nums[3], out h) || !int.TryParse(nums[4], out font) || w < 40 || h < 12 || w > 4000 || h > 200 || font < 8 || font > 60)
            {
                Send(stream, 400, "{\"error\":\"bad rect\"}");
                return;
            }
            string initial = lines.Length > 1 ? lines[1] : "";
            if (initial.Length > 200) initial = initial.Substring(0, 200);
            var box = EnsureInputBox();
            if (box == null) { Send(stream, 200, "{\"ok\":false}"); return; }
            lock (InputGate)
            {
                inputOpen = true;
                inputText = initial;
                inputKeys.Clear();
            }
            box.BeginInvoke((Action)(() => box.Open(x, y, w, h, font, initial)));
            Send(stream, 200, "{\"ok\":true}");
            return;
        }
        if (method == "POST" && path == "/input/close")
        {
            InputBox box;
            lock (InputGate) box = inputBox;
            if (box != null && box.IsHandleCreated && !box.IsDisposed) box.BeginInvoke((Action)(() => box.Collapse()));
            Send(stream, 200, "{\"ok\":true}");
            return;
        }
        Send(stream, 404, "{\"error\":\"not found\"}");
    }
}
