using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MarkdownViewer
{
    // 供页面 JS 通过 window.external 调用，用于把编辑后的内容写回文件
    [System.Runtime.InteropServices.ComVisible(true)]
    public class ScriptBridge
    {
        private MainForm form;
        public ScriptBridge(MainForm f) { form = f; }
        public void SaveFile(string content) { form.SaveCurrentFile(content); }
        public void SaveSettings(string json) { form.SaveSettings(json); }
        public void SetEditState(bool editing, bool canSave) { form.SetEditState(editing, canSave); }
        public void SetFileName(string name) { form.SetFileName(name); }
        public void SetThemeName(string name) { form.SetThemeName(name); }
        public void Log(string msg) { form.LogFromJs(msg); }
    }

    public class MainForm : Form, IMessageFilter
    {
        // 版本标识：显示在标题栏，用来一眼确认跑的是不是最新构建
        private const string VER = "v12";
        // 自检用：把按键消息直接投递到 IE 子窗口，不依赖前台焦点（keybd_event 在无焦点时会送丢）
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        // 自检用：真实按键注入 + 抢前台焦点（keybd_event 只送给前台窗口，无人值守时必须先抢）
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int VK_CONTROL_ = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_S = 0x53;
        private const int VK_A = 0x41;
        private const int VK_B = 0x42;
        private const int VK_C = 0x43;
        private const int VK_V = 0x56;
        private const int VK_X = 0x58;
        private const int VK_Z = 0x5A;
        private const int VK_Y = 0x59;
        private const int VK_N = 0x4E;
        private const int VK_O = 0x4F;
        private const int VK_P = 0x50;
        private const int VK_F5 = 0x74;
        private bool ctrlDown;       // 自跟踪 Ctrl 状态：不依赖 Control.ModifierKeys（PostMessage/无焦点时它不准）

        private WebBrowser browser;
        private string currentFile;
        private string settingsJson = "";
        private ToolStripButton tsEdit;
        private ToolStripButton tsSave;
        private ToolStripButton tsTheme;
        private ToolStripLabel tsName;
        private string logPath;      // 默认写 %TEMP%\mdviewer.log（排障用），可用 --log= 指定
        private bool synth;          // --synth：自检直接调 ProcessCmdKey，而不是模拟真实按键
        private bool useRealKeys;    // 自检期间用真实按键注入（抢到前台才敢用，否则会打到别人窗口）

        public MainForm(string file) : this(file, null, false, false) { }

        public MainForm(string file, string logPath, bool selfTest, bool synth)
        {
            this.synth = synth;
            this.logPath = string.IsNullOrEmpty(logPath)
                ? Path.Combine(Path.GetTempPath(), "mdviewer.log")
                : logPath;
            SetBrowserEmulation();
            currentFile = file;
            settingsJson = LoadSettings();
            this.Text = "文件查看器 " + VER;
            this.Width = 980;
            this.Height = 740;
            Log("=== 启动 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + " | 版本=" + VER
                + " | 文件=" + (string.IsNullOrEmpty(file) ? "<无>" : file)
                + " | selftest=" + selfTest + " | synth=" + synth + " ===");

            var menu = new MenuStrip();
            menu.Dock = DockStyle.Top;

            var fileMenu = new ToolStripMenuItem("文件(&F)");
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("打开(&O)", null, (s, e) => OpenFile()));
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("退出(&X)", null, (s, e) => Application.Exit()));

            var viewMenu = new ToolStripMenuItem("视图(&V)");
            viewMenu.DropDownItems.Add(new ToolStripMenuItem("切换主题(&T)", null, (s, e) => ToggleTheme()));

            tsName = new ToolStripLabel("未命名") { Overflow = ToolStripItemOverflow.Never };
            tsEdit = new ToolStripButton("编辑") { Overflow = ToolStripItemOverflow.Never };
            tsSave = new ToolStripButton("保存") { Overflow = ToolStripItemOverflow.Never, Enabled = false };
            tsTheme = new ToolStripButton("配色") { Overflow = ToolStripItemOverflow.Never };

            tsEdit.Click += (s, e) =>
            {
                if (tsEdit.Text == "编辑")
                {
                    InvokeJs("enterEdit");
                    tsEdit.Text = "取消";
                    tsSave.Enabled = true;
                }
                else
                {
                    InvokeJs("cancelEdit");
                    tsEdit.Text = "编辑";
                    tsSave.Enabled = false;
                }
            };
            tsSave.Click += (s, e) => SaveViaScript();
            tsTheme.Click += (s, e) => InvokeJs("togglePalette");

            menu.Items.Add(fileMenu);
            menu.Items.Add(viewMenu);
            menu.Items.Add(new ToolStripSeparator() { Overflow = ToolStripItemOverflow.Never });
            menu.Items.Add(tsName);
            menu.Items.Add(new ToolStripSeparator() { Overflow = ToolStripItemOverflow.Never });
            menu.Items.Add(tsEdit);
            menu.Items.Add(tsSave);
            menu.Items.Add(tsTheme);

            this.MainMenuStrip = menu;
            this.Controls.Add(menu);

            browser = new WebBrowser();
            browser.Dock = DockStyle.Fill;
            browser.ScriptErrorsSuppressed = true;
            browser.AllowNavigation = false;       // 查看器不跳转页面
            browser.AllowWebBrowserDrop = false;
            // ★ 关键：这个属性必须是 true。
            //   v11 为了解决"Ctrl+S 被 IE 吃掉"把它设成了 false —— 结果连带关掉了 IE 宿主的
            //   所有内置编辑快捷键，Ctrl+C / Ctrl+V / Ctrl+X / Ctrl+A / Ctrl+Z 以及右键菜单里
            //   的复制粘贴全部失效，编辑框直接没法用。
            //   现在改成：平时保持 true（剪贴板、撤销栈、IME、右键菜单全部走 IE 原生实现），
            //   只在"Ctrl 被按下"的这段时间里临时置 false —— 见 PreFilterMessage。
            //   这样 Ctrl+S 依然能被消息泵层截获，而 Ctrl+C/V 完全没有被影响。
            browser.WebBrowserShortcutsEnabled = true;
            browser.ObjectForScripting = new ScriptBridge(this);
            this.Controls.Add(browser);

            // 第二重：消息泵级拦截，抢在按键被派发之前。
            Application.AddMessageFilter(this);

            LoadFile(currentFile);
            if (selfTest) RunSelfTest();
        }

        // 消息泵层拦截 Ctrl+S。这里是所有按键消息的最前端，早于 WebBrowser 的加速键处理，
        // 也早于 ProcessCmdKey，所以不受 IE 吞键影响。只吃 Ctrl+S，其余按键原样放行。
        // Ctrl 状态自己跟踪：Control.ModifierKeys 在 PostMessage / 无前台焦点时不可靠。
        //
        // v12 起 WebBrowserShortcutsEnabled 保持 true（剪贴板/撤销/IME 走 IE 原生实现），
        // 所以这里必须"看完就放行"——除了 Ctrl+S 和几个只会帮倒忙的 IE 内置快捷键。
        public bool PreFilterMessage(ref Message m)
        {
            // 只关心键盘按下/抬起，其余消息原样放行。
            // 注意 WParam 是 IntPtr：64 位下某些消息的值会超出 int 范围，
            // 必须先按 long 取并校验范围，否则 ToInt32() 会抛 OverflowException 把程序打崩。
            if (m.Msg != WM_KEYDOWN && m.Msg != WM_KEYUP
                && m.Msg != 0x0104 /*WM_SYSKEYDOWN*/ && m.Msg != 0x0105 /*WM_SYSKEYUP*/)
                return false;

            long wp = m.WParam.ToInt64();
            if (wp < 0 || wp > 0xFF) return false;   // 键盘消息的 wParam 只可能是 0..255
            int vk = (int)wp;
            bool isDown = (m.Msg == WM_KEYDOWN || m.Msg == 0x0104);

            bool isCtrlKey = (vk == VK_CONTROL_ || vk == VK_LCONTROL || vk == VK_RCONTROL);

            if (isDown)
            {
                if (isCtrlKey) { ctrlDown = true; return false; }

                bool ctrl = ctrlDown || (Control.ModifierKeys & Keys.Control) == Keys.Control;
                if (!ctrl)
                {
                    // 无 Ctrl 的 F5：IE 会刷新页面，未保存的编辑内容会被冲掉，直接吃掉
                    if (vk == VK_F5) { Log("PreFilterMessage: 吞掉 F5（避免刷新丢失编辑内容）"); return true; }
                    return false;
                }

                if (vk == VK_S)
                {
                    Log("PreFilterMessage: 消息泵层捕获 Ctrl+S -> 保存");
                    SaveViaScript();
                    return true;   // 吞掉，避免 IE 再把它当"保存网页"处理
                }

                // IE 内置的 Ctrl+N/O/P（新窗口 / 打开 / 打印）对本程序只会帮倒忙，吃掉。
                if (vk == VK_N || vk == VK_O || vk == VK_P)
                {
                    Log("PreFilterMessage: 吞掉 IE 内置快捷键 Ctrl+" + vk.ToString("X2"));
                    return true;
                }

                // ★ 关键：其余组合键（Ctrl+C / Ctrl+V / Ctrl+X / Ctrl+A / Ctrl+Z ...）一律放行，
                //   交给 WebBrowser 的原生实现处理。剪贴板、撤销栈、IME、右键菜单都靠它。
                //   千万不要在这里"自己实现复制粘贴"——那会破坏原生撤销栈和输入法。
                Log("PreFilterMessage: 放行 Ctrl+" + vk.ToString("X2") + " 给 IE 原生处理");
                return false;
            }
            else
            {
                if (isCtrlKey) ctrlDown = false;
            }
            return false;
        }

        // 页面通过 window.external.log 写进来的诊断信息，用来判断按键有没有到网页侧
        public void LogFromJs(string msg) { Log("[JS] " + msg); }

        // 安全调用页面脚本：页面可能尚未就绪 / 已卸载，Document 为 null 时直接忽略。
        // （写成独立方法是为了兼容只支持 C# 5 的编译器，不使用 ?. 语法）
        private void InvokeJs(string fn)
        {
            try
            {
                var doc = browser != null ? browser.Document : null;
                if (doc != null) doc.InvokeScript(fn);
            }
            catch (Exception ex) { Log("InvokeJs(" + fn + ") 异常: " + ex.Message); }
        }

        private void Log(string msg)
        {
            if (string.IsNullOrEmpty(logPath)) return;
            try
            {
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }


        private void SetBrowserEmulation()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    string exeName = Path.GetFileName(Application.ExecutablePath);
                    key.SetValue(exeName, 11001, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public void SaveSettings(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                string p = SettingsPath();
                string dir = Path.GetDirectoryName(p);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(p, json ?? "", Encoding.UTF8);
                settingsJson = json; // 让本次会话内打开新文件也沿用上次配色
            }
            catch { }
        }

        public void SetEditState(bool editing, bool canSave)
        {
            if (tsEdit != null) tsEdit.Text = editing ? "取消" : "编辑";
            if (tsSave != null) tsSave.Enabled = canSave;
        }

        public void SetFileName(string name)
        {
            if (tsName != null) tsName.Text = string.IsNullOrEmpty(name) ? "未命名" : name;
        }

        // 页面把当前生效的配色名同步到工具栏，方便一眼确认配色是否真的切换了
        public void SetThemeName(string name)
        {
            if (tsTheme != null && !string.IsNullOrEmpty(name)) tsTheme.Text = "配色：" + name;
        }

        private string SettingsPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MarkdownViewer");
            return Path.Combine(dir, "settings.json");
        }

        private string LoadSettings()
        {
            try
            {
                string p = SettingsPath();
                if (File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8);
            }
            catch { }
            return "";
        }

        public void SaveCurrentFile(string content)
        {
            if (string.IsNullOrEmpty(currentFile)) return;
            try
            {
                File.WriteAllText(currentFile, content ?? "", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, "保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenFile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "全部支持|*.md;*.markdown;*.txt;*.text;*.log;*.sql;*.py;*.js;*.ts;*.cs;*.java;*.c;*.cpp;*.h;*.go;*.rs;*.sh;*.json;*.yaml;*.yml;*.xml;*.html;*.htm;*.css;*.ini;*.conf;*.php;*.rb;*.ps1|Markdown|*.md;*.markdown|代码文件|*.txt;*.sql;*.py;*.js;*.ts;*.cs;*.java;*.c;*.cpp;*.h;*.go;*.rs;*.sh;*.json;*.yaml;*.yml;*.xml;*.html;*.htm;*.css;*.ini;*.conf;*.php;*.rb;*.ps1|所有文件|*.*";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    currentFile = dlg.FileName;
                    LoadFile(currentFile);
                }
            }
        }

        // 统一保存入口（工具栏「保存」按钮 / Ctrl+S 共用）。
        // IE/MSHTML 下的硬约束：
        //  1) 在按键消息上下文（ProcessCmdKey）里调用 InvokeScript 会被脚本引擎重入吞掉，
        //     所以「退出编辑态」不能依赖 C# 回调页面 JS。
        //  2) HTMLElement.GetAttribute("value") 读 textarea 返回初始内容，必须走 DomElement.value。
        // 退出编辑态采用两级策略：
        //   A. 首选纯 IDispatch 改写内联 style（读回校验，确认真的生效）；
        //   B. A 不生效时直接整页重载 —— 文件已写盘，重载后必然是阅读态 + 最新内容。
        //      B 这条路完全不碰脚本引擎，物理上必然有效，是兜底保证。
        private void SaveViaScript()
        {
            string content = null;
            bool saved = false;
            HtmlElement ta = null;
            try
            {
                var doc = browser.Document;
                ta = doc != null ? doc.GetElementById("edit") : null;
                if (ta != null && ta.DomElement != null)
                {
                    dynamic dom = ta.DomElement;
                    content = (string)dom.value;
                    if (content == null) content = "";
                    SaveCurrentFile(content);   // 只写盘，绝不回头调脚本
                    saved = true;
                    Log("SaveViaScript: 已写盘 " + content.Length + " 字符 -> " + currentFile);
                }
                else
                {
                    Log("SaveViaScript: 拿不到 #edit 元素（doc=" + (doc != null) + "）");
                }
            }
            catch (Exception ex)
            {
                Log("SaveViaScript: 读值写盘异常 " + ex.GetType().Name + " " + ex.Message);
                if (!saved) MessageBox.Show("保存失败：" + ex.Message, "保存",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (saved)
            {
                SyncToolbar(true);
                bool uiOk = TryExitEditByDom(ta);
                if (uiOk)
                {
                    Log("SaveViaScript: IDispatch 退出编辑态成功，稍后刷新正文");
                    RefreshViewLater();
                }
                else
                {
                    // IDispatch 没生效：延迟整页重载。不依赖 IDispatch / 脚本引擎任何一环，
                    // 文件已写盘 -> 重载后必然是新内容 + 阅读态。
                    Log("SaveViaScript: IDispatch 未生效 -> 延迟整页重载兜底");
                    ReloadLater(0);
                }
                // 事后复查（最后一根保险丝）：此刻页面若还没回到阅读态，强制重载
                VerifyViewModeLater();
            }
            else
            {
                // 拿不到编辑框（页面未就绪等）才退化为让页面自己走一遍保存；
                // 此时工具栏状态由页面 notifyState 回传，避免和页面的编辑态对不上
                InvokeJs("save");
            }
        }

        // 直接操作 DOM 退出编辑态，返回是否【确认生效】（写入后读回校验）。
        // 全部是 IDispatch 属性/方法调用，不经过脚本引擎。
        private bool TryExitEditByDom(HtmlElement ta)
        {
            try
            {
                dynamic d = ta.DomElement;
                try { d.blur(); } catch { }

                dynamic ds = d.style;
                ds.display = "none";

                string back = null;
                try { back = (string)ds.display; } catch { }
                bool ok = string.Equals(back, "none", StringComparison.OrdinalIgnoreCase);
                Log("  edit.style.display 写后读回 = '" + back + "' -> " + (ok ? "已生效" : "未生效"));
                if (!ok) return false;

                var doc = browser.Document;
                var view = doc != null ? doc.GetElementById("view") : null;
                if (view != null && view.DomElement != null)
                {
                    dynamic vd = view.DomElement;
                    dynamic vs = vd.style;
                    vs.display = "block";
                    string vback = null;
                    try { vback = (string)vs.display; } catch { }
                    Log("  view.style.display 写后读回 = '" + vback + "'");
                }
                // 焦点移出已隐藏的编辑框，避免后续按键继续落到不可见的 textarea
                try { browser.Focus(); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                Log("  TryExitEditByDom 异常: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        // 延迟到当前按键/点击消息处理完毕后再让页面重新渲染最新内容。
        // 不传参：页面自己从 textarea 取内容，避免长字符串跨语言编组失败（IE 下不可靠）。
        private void RefreshViewLater()
        {
            var t = new Timer();
            t.Interval = 60;
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                try
                {
                    browser.Document.InvokeScript("afterSaveFromEdit");
                    Log("  afterSaveFromEdit 已调用");
                }
                catch (Exception ex)
                {
                    Log("  afterSaveFromEdit 失败(" + ex.Message + ") -> 整页重载");
                    ReloadLater(0);
                }
            };
            t.Start();
        }

        // 延迟整页重载（BeginInvoke = 等当前按键/点击消息处理完毕再执行）。
        // DocumentText 赋值走 MSHTML 解析器、不涉及脚本引擎重入，延迟执行再加一道保险。
        private void ReloadLater(int delayMs)
        {
            Action act = () =>
            {
                Log("  ReloadLater: 重载页面 " + currentFile);
                try { LoadFile(currentFile); }
                catch (Exception ex) { Log("  重载失败: " + ex.Message); }
            };
            if (delayMs <= 0)
            {
                try { this.BeginInvoke(act); } catch { try { act(); } catch { } }
            }
            else
            {
                var t = new Timer();
                t.Interval = delayMs;
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); act(); };
                t.Start();
            }
        }

        // 读 #edit 的内联 display（纯 IDispatch，不碰脚本引擎）
        private string ReadEditDisplay()
        {
            try
            {
                var doc = browser.Document;
                var ta = doc != null ? doc.GetElementById("edit") : null;
                if (ta == null || ta.DomElement == null) return "<无#edit>";
                dynamic d = ta.DomElement;
                dynamic ds = d.style;
                return (string)ds.display;
            }
            catch (Exception ex) { return "<异常:" + ex.Message + ">"; }
        }

        // 保存后复查（最后一根保险丝）：编辑框还没隐藏，说明前两步都没生效，强制重载
        private void VerifyViewModeLater()
        {
            var t = new Timer();
            t.Interval = 700;
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                string disp = ReadEditDisplay();
                bool hidden = string.Equals(disp, "none", StringComparison.OrdinalIgnoreCase);
                Log("  复查 edit.display = '" + disp + "' -> " + (hidden ? "已是阅读态" : "仍在编辑态"));
                if (!hidden)
                {
                    Log("  复查发现仍在编辑态 -> 强制重载");
                    ReloadLater(0);
                }
            };
            t.Start();
        }

        // ===== 自检（--selftest）=====
        // 默认把真实按键消息 PostMessage 进 IE 子窗口（不依赖前台焦点，且必经消息泵的
        // PreFilterMessage —— 正是要验证的那条路径）；加 --synth 则直接调 ProcessCmdKey 对照。
        // 每条用例打一行 `CHECK|名字|PASS/FAIL`，便于机读判定。
        //   ctrl_s_saved  Ctrl+S 真的写进了磁盘
        //   ctrl_s_exit   Ctrl+S 后回到阅读态
        //   ctrl_v_paste  Ctrl+V 把剪贴板内容粘进编辑框（v12 剪贴板修复的核心验收项）
        //   ctrl_a_select Ctrl+A 全选
        //   ctrl_c_copy   Ctrl+C 把选中内容写进剪贴板
        private void RunSelfTest()
        {
            var t = new Timer();
            t.Interval = 1500;   // 等页面 onload 初始化跑完
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                Log("=== selftest start (synth=" + synth + ") ===");
                try { SelfTestBody(); }
                catch (Exception ex) { Log("selftest 异常: " + ex.GetType().Name + " " + ex.Message); }
                Log("=== selftest end ===");
                Application.Exit();
            };
            t.Start();
        }

        private void SelfTestBody()
        {
            var doc = browser.Document;
            var ta = doc != null ? doc.GetElementById("edit") : null;
            Log("document=" + (doc != null) + "  #edit=" + (ta != null)
                + "  DomElement=" + (ta != null && ta.DomElement != null)
                + "  WebBrowserShortcutsEnabled=" + browser.WebBrowserShortcutsEnabled);
            if (ta == null) { Log("CHECK|edit_element|FAIL"); return; }

            // 先抢前台：IE 的编辑类按键（Ctrl+C/V/A）只有在窗口真正获得焦点时才会被处理，
            // 拿不到前台就只能退回 PostMessage（结果可能低估真实能力，日志会写明）。
            useRealKeys = EnsureForeground();
            Log("按键盘方式 = " + (useRealKeys ? "真实按键注入(keybd_event)" : "PostMessage 直投(未获前台)"));

            // ---------- 用例 1：Ctrl+S 保存并退出编辑态 ----------
            InvokeJs("enterEdit");
            Pump(250);
            SetEditValue(ta, "SELFTEST-NEW-CONTENT\r\nsecond line");
            ta.Focus();
            Pump(150);
            if (synth)
            {
                Log("触发方式：直接调用 ProcessCmdKey（绕过消息队列）");
                Message m = new Message();
                Log("ProcessCmdKey(Ctrl+S) 返回 " + ProcessCmdKey(ref m, Keys.Control | Keys.S));
            }
            else
            {
                PressCtrl(VK_S);
            }
            Pump(900);
            string disp = ReadEditDisplay();
            Log("CHECK|ctrl_s_exit|" + (IsNone(disp) ? "PASS" : "FAIL") + "|edit.display=" + disp);
            string onDisk = File.Exists(currentFile) ? File.ReadAllText(currentFile, Encoding.UTF8) : "<不存在>";
            Log("磁盘内容 = " + Esc(onDisk));
            Log("CHECK|ctrl_s_saved|" + (IndexOf(onDisk, "SELFTEST-NEW-CONTENT") >= 0 ? "PASS" : "FAIL"));

            if (synth) return;   // synth 只对照 ProcessCmdKey 那条老路径，不测消息泵

            // ---------- 用例 2：Ctrl+V 粘贴 ----------
            InvokeJs("enterEdit");
            Pump(250);
            SetEditValue(ta, "PASTE-BASE-");
            CaretToEnd(ta);
            ta.Focus();
            Pump(200);
            SetClip("XYZZY-PASTE-MARK");
            Log("剪贴板已置为 XYZZY-PASTE-MARK；编辑框当前 = " + Esc(GetEditValue(ta)));
            PressCtrl(VK_V);
            Pump(900);
            string after = GetEditValue(ta);
            Log("粘贴后编辑框 = " + Esc(after));
            Log("CHECK|ctrl_v_paste|" + (IndexOf(after, "XYZZY-PASTE-MARK") >= 0 ? "PASS" : "FAIL"));

            // ---------- 用例 3：Ctrl+A 全选 ----------
            PressCtrl(VK_A);
            Pump(500);
            Log("CHECK|ctrl_a_select|" + (IsAllSelected(ta) ? "PASS" : "FAIL") + "|" + SelInfo(ta));

            // ---------- 用例 4：Ctrl+C 复制 ----------
            SetClip("CLEARED-BEFORE-COPY");
            PressCtrl(VK_C);
            Pump(900);
            string clip = ReadClip();
            Log("复制后剪贴板 = " + Esc(clip));
            Log("CHECK|ctrl_c_copy|" + (IndexOf(clip, "XYZZY-PASTE-MARK") >= 0 ? "PASS" : "FAIL"));

            // ---------- 用例 5：Ctrl+X 剪切（全选后剪切，编辑框应变空、剪贴板拿到内容）----------
            SetClip("CLEARED-BEFORE-CUT");
            PressCtrl(VK_X);
            Pump(900);
            string afterCut = GetEditValue(ta);
            string cutClip = ReadClip();
            Log("剪切后编辑框 = " + Esc(afterCut) + " / 剪贴板 = " + Esc(cutClip));
            Log("CHECK|ctrl_x_cut|" + ((afterCut != null && afterCut.Length == 0 && IndexOf(cutClip, "XYZZY-PASTE-MARK") >= 0) ? "PASS" : "FAIL"));

            // ---------- 用例 6：普通打字回归（确认消息泵拦截没有影响正常输入）----------
            if (useRealKeys)
            {
                SetEditValue(ta, "");
                CaretToEnd(ta);
                ta.Focus();
                Pump(200);
                TypeChar(VK_A); TypeChar(0x42 /*B*/); TypeChar(0x43 /*C*/);
                Pump(600);
                string typed = GetEditValue(ta);
                Log("打字后编辑框 = " + Esc(typed));
                Log("CHECK|plain_typing|" + (IndexOf(typed, "abc") >= 0 ? "PASS" : "FAIL"));
            }
            else
            {
                Log("跳过 plain_typing：未获得前台，真实打字无法验证");
            }
        }

        // 注入一个普通字符（不带 Ctrl），验证正常打字没被消息泵拦截影响
        private void TypeChar(int vk)
        {
            keybd_event((byte)vk, (byte)ScanCode(vk), 0, IntPtr.Zero);
            Pump(40);
            keybd_event((byte)vk, (byte)ScanCode(vk), KEYEVENTF_KEYUP, IntPtr.Zero);
            Pump(60);
        }

        // ---------- 自检用的小工具 ----------

        // 抽消息泵：把队列里的事件（含 PostMessage 进去的按键）真实处理掉
        private void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                try { Application.DoEvents(); } catch { }
                System.Threading.Thread.Sleep(20);
            }
        }

        // 模拟一次 Ctrl+<vk>：把消息直投 IE 子窗口，不依赖前台焦点。
        // ★ 注意：IE 的服务器窗口（Internet Explorer_Server）是 WebBrowser 的**孙窗口**
        //   （WebBrowser → Shell DocObject View → Internet Explorer_Server），
        //   FindWindowEx 只找直接子窗口，所以必须递归往下找，否则投到 WebBrowser 自己身上，
        //   编辑类按键（Ctrl+C/V/A）根本到不了文档 —— 会误判成"功能坏了"。
        private void PressCtrl(int vk)
        {
            if (useRealKeys) { PressCtrlReal(vk); return; }

            IntPtr ieHwnd = FindDescendant(browser.Handle, "Internet Explorer_Server");
            IntPtr target = ieHwnd != IntPtr.Zero ? ieHwnd : browser.Handle;
            int scan = ScanCode(vk);
            Log("PressCtrl(PostMessage): vk=0x" + vk.ToString("X2") + " -> hwnd=" + target.ToString("X")
                + " (IE服务器窗口=" + (ieHwnd != IntPtr.Zero) + ", scan=0x" + scan.ToString("X2") + ")");
            long up = 0xC0000000L | ((long)scan << 16) | 1L;
            PostMessage(target, (uint)WM_KEYDOWN, (IntPtr)VK_CONTROL_, (IntPtr)0x001D0001L);
            System.Threading.Thread.Sleep(30);
            PostMessage(target, (uint)WM_KEYDOWN, (IntPtr)vk, (IntPtr)(((long)scan << 16) | 1L));
            System.Threading.Thread.Sleep(30);
            PostMessage(target, (uint)WM_KEYUP, (IntPtr)vk, (IntPtr)up);
            System.Threading.Thread.Sleep(30);
            PostMessage(target, (uint)WM_KEYUP, (IntPtr)VK_CONTROL_, (IntPtr)0xC01D0001L);
        }

        // 真实按键注入：完全走用户那条路径（前台窗口 + 真实输入队列），保真度最高。
        // 前提是本窗口已抢到前台，否则会打到别人的窗口上 —— 所以调用前必须 EnsureForeground 成功。
        private void PressCtrlReal(int vk)
        {
            Log("PressCtrlReal(真实按键): vk=0x" + vk.ToString("X2") + " scan=0x" + ScanCode(vk).ToString("X2"));
            keybd_event((byte)VK_CONTROL_, 0x1D, 0, IntPtr.Zero);
            Pump(60);
            keybd_event((byte)vk, (byte)ScanCode(vk), 0, IntPtr.Zero);
            Pump(60);
            keybd_event((byte)vk, (byte)ScanCode(vk), KEYEVENTF_KEYUP, IntPtr.Zero);
            Pump(60);
            keybd_event((byte)VK_CONTROL_, 0x1D, KEYEVENTF_KEYUP, IntPtr.Zero);
        }

        // 抢前台：本进程不是"最后输入"的进程时 SetForegroundWindow 会被系统拒绝，
        // 必须先 AttachThreadInput 挂到当前前台线程上，这是唯一稳定的做法。
        private bool EnsureForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == this.Handle)
                {
                    Log("EnsureForeground: 本窗口已是前台");
                    return true;
                }
                uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint myThread = GetCurrentThreadId();
                bool attached = false;
                if (fgThread != 0 && fgThread != myThread)
                    attached = AttachThreadInput(myThread, fgThread, true);
                SetForegroundWindow(this.Handle);
                this.Activate();
                if (attached) AttachThreadInput(myThread, fgThread, false);
                Pump(200);
                IntPtr now = GetForegroundWindow();
                bool ok = (now == this.Handle);
                Log("EnsureForeground: 原前台=" + fg.ToString("X") + " 目标=" + this.Handle.ToString("X")
                    + " 现在前台=" + now.ToString("X") + " 附加线程=" + attached
                    + " -> " + (ok ? "已获得前台" : "未获得前台"));
                return ok;
            }
            catch (Exception ex) { Log("EnsureForeground 异常 " + ex.Message); return false; }
        }

        // 递归查找后代窗口（FindWindowEx 只找直接子窗口，不够用）
        private static IntPtr FindDescendant(IntPtr parent, string cls)
        {
            IntPtr child = FindWindowEx(parent, IntPtr.Zero, null, null);
            while (child != IntPtr.Zero)
            {
                var sb = new StringBuilder(256);
                if (GetClassName(child, sb, sb.Capacity) > 0 && sb.ToString() == cls) return child;
                IntPtr deeper = FindDescendant(child, cls);
                if (deeper != IntPtr.Zero) return deeper;
                child = FindWindowEx(parent, child, null, null);
            }
            return IntPtr.Zero;
        }

        private static int ScanCode(int vk)
        {
            switch (vk)
            {
                case VK_A: return 0x1E;
                case VK_B: return 0x30;
                case VK_C: return 0x2E;
                case VK_S: return 0x1F;
                case VK_V: return 0x2F;
                case VK_X: return 0x2D;
                case VK_Z: return 0x2C;
                case VK_Y: return 0x15;
                default: return 0;
            }
        }

        private void SetEditValue(HtmlElement ta, string v)
        {
            try { dynamic d = ta.DomElement; d.value = v; }
            catch (Exception ex) { Log("SetEditValue 异常 " + ex.Message); }
        }

        private string GetEditValue(HtmlElement ta)
        {
            try
            {
                dynamic d = ta.DomElement;
                string s = (string)d.value;
                // IE 在 textarea 为空时会把空串回传成 null，统一成空串，免得判据误报
                return s == null ? "" : s;
            }
            catch (Exception ex) { return "<异常:" + ex.Message + ">"; }
        }

        // 把光标移到编辑框末尾（IE11 的 textarea 支持 selectionStart/End）
        private void CaretToEnd(HtmlElement ta)
        {
            try
            {
                dynamic d = ta.DomElement;
                string v = (string)d.value;
                int n = v == null ? 0 : v.Length;
                d.focus();
                d.selectionStart = n;
                d.selectionEnd = n;
            }
            catch (Exception ex) { Log("CaretToEnd 异常 " + ex.Message); }
        }

        private string SelInfo(HtmlElement ta)
        {
            try
            {
                dynamic d = ta.DomElement;
                string v = (string)d.value;
                return "selectionStart=" + d.selectionStart + " selectionEnd=" + d.selectionEnd
                    + " valueLength=" + (v == null ? -1 : v.Length);
            }
            catch (Exception ex) { return "selection<异常:" + ex.Message + ">"; }
        }

        private bool IsAllSelected(HtmlElement ta)
        {
            try
            {
                dynamic d = ta.DomElement;
                string v = (string)d.value;
                int s = Convert.ToInt32(d.selectionStart);
                int e = Convert.ToInt32(d.selectionEnd);
                if (v != null && v.Length > 0 && s == 0 && e == v.Length) return true;
            }
            catch { }
            // 退化判据：文档当前选中的文本长度 == 编辑框内容长度
            try
            {
                dynamic d2 = ta.DomElement;
                string v2 = (string)d2.value;
                dynamic dd = browser.Document.DomDocument;   // object -> dynamic，才能访问 selection
                dynamic sel = dd.selection;
                dynamic rng = sel.createRange();
                string t = (string)rng.text;
                if (v2 != null && t != null && v2.Length > 0 && t.Length == v2.Length) return true;
            }
            catch { }
            return false;
        }

        private void SetClip(string s)
        {
            try { Clipboard.SetText(s == null ? "" : s, TextDataFormat.UnicodeText); }
            catch (Exception ex) { Log("SetClip 异常 " + ex.Message); }
        }

        private string ReadClip()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : "<剪贴板无文本>"; }
            catch (Exception ex) { return "<异常:" + ex.Message + ">"; }
        }

        private static bool IsNone(string s)
        {
            return string.Equals(s, "none", StringComparison.OrdinalIgnoreCase);
        }

        private static int IndexOf(string hay, string needle)
        {
            if (hay == null || needle == null) return -1;
            return hay.IndexOf(needle, StringComparison.Ordinal);
        }

        private static string Esc(string s)
        {
            if (s == null) return "<null>";
            return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private void SyncToolbar(bool viewMode)
        {
            if (tsEdit != null) tsEdit.Text = viewMode ? "编辑" : "取消";
            if (tsSave != null) tsSave.Enabled = !viewMode;
        }

        // 第二层兜底：焦点在 WinForms 控件（菜单栏等）时，按键会正常走到这里。
        // 焦点在 WebBrowser 内部时按键已被 IE 吞掉，走不到这里 —— 那条路由 PreFilterMessage 负责。
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                Log("ProcessCmdKey: 捕获 Ctrl+S");
                SaveViaScript();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ToggleTheme()
        {
            try
            {
                if (browser.Document != null)
                    browser.Document.InvokeScript("cycleTheme");
            }
            catch { }
        }

        private void LoadFile(string path)
        {
            string text = "";
            string ext = "";
            string name = "未命名";
            if (!string.IsNullOrEmpty(path))
            {
                if (File.Exists(path))
                {
                    text = File.ReadAllText(path, Encoding.UTF8);
                    ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                    name = Path.GetFileName(path);
                    this.Text = "文件查看器 " + VER + " — " + name;
                    if (tsName != null) tsName.Text = name;
                }
                else
                {
                    text = "# 无法读取文件\n\n路径：" + path;
                    ext = "md";
                    name = Path.GetFileName(path);
                }
            }
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
            string eb64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ext ?? ""));
            string nb64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(name ?? ""));
            string html = LoadTemplate()
                .Replace("__B64__", b64)
                .Replace("__EXTB64__", eb64)
                .Replace("__NAMEB64__", nb64)
                .Replace("__SETSB64__", Convert.ToBase64String(Encoding.UTF8.GetBytes(settingsJson ?? "")));
            browser.DocumentText = html;
        }

        private string LoadTemplate()
        {
            using (var s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("MarkdownViewer.viewer.html"))
            {
                using (var r = new StreamReader(s))
                    return r.ReadToEnd();
            }
        }
    }
}
