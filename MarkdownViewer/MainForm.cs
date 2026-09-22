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
        private const string VER = "v9";
        // 模拟真实按键（--selftest 用）：走完整窗口消息流程，才能复现"焦点在 textarea"的场景
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        private const byte VK_CONTROL = 0x11;
        private const byte VK_S = 0x53;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private WebBrowser browser;
        private string currentFile;
        private string settingsJson = "";
        private ToolStripButton tsEdit;
        private ToolStripButton tsSave;
        private ToolStripButton tsTheme;
        private ToolStripLabel tsName;
        private string logPath;      // 默认写 %TEMP%\mdviewer.log（排障用），可用 --log= 指定
        private bool synth;          // --synth：自检直接调 ProcessCmdKey，而不是模拟真实按键

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
                    browser.Document?.InvokeScript("enterEdit");
                    tsEdit.Text = "取消";
                    tsSave.Enabled = true;
                }
                else
                {
                    browser.Document?.InvokeScript("cancelEdit");
                    tsEdit.Text = "编辑";
                    tsSave.Enabled = false;
                }
            };
            tsSave.Click += (s, e) => SaveViaScript();
            tsTheme.Click += (s, e) => browser.Document?.InvokeScript("togglePalette");

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
            browser.ObjectForScripting = new ScriptBridge(this);
            this.Controls.Add(browser);

            // 关键：WebBrowser（IE 宿主）会在加速键阶段把 Ctrl+S 之类整条吞掉，
            // 既不冒泡到 WinForms 的 ProcessCmdKey，也不派发到文档的 onkeydown。
            // 唯一能抢在它前面的是消息泵级的 IMessageFilter（PreFilterMessage）。
            Application.AddMessageFilter(this);

            LoadFile(currentFile);
            if (selfTest) RunSelfTest();
        }

        // 消息泵层拦截 Ctrl+S。这里是所有按键消息的最前端，早于 WebBrowser 的加速键处理，
        // 也早于 ProcessCmdKey，所以不受 IE 吞键影响。只吃 Ctrl+S，其余按键原样放行。
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;
            if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN)
            {
                int vk = m.WParam.ToInt32();
                if (vk == VK_S && (Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    Log("PreFilterMessage: 消息泵层捕获 Ctrl+S");
                    SaveViaScript();
                    return true;   // 吞掉，避免 IE 再把它当"保存网页"处理
                }
            }
            return false;
        }

        // 页面通过 window.external.log 写进来的诊断信息，用来判断按键有没有到网页侧
        public void LogFromJs(string msg) { Log("[JS] " + msg); }

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
                try { browser.Document?.InvokeScript("save"); } catch { }
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
        // 默认用 keybd_event 发【真实 Ctrl+S】，走完整窗口消息流程（复现"焦点在 textarea"的场景）；
        // 加 --synth 则直接调 ProcessCmdKey，绕过消息队列，便于对照定位。
        private void RunSelfTest()
        {
            var t = new Timer();
            t.Interval = 1500;
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                Log("=== selftest start (synth=" + synth + ") ===");
                try
                {
                    var doc = browser.Document;
                    var ta = doc != null ? doc.GetElementById("edit") : null;
                    Log("document=" + (doc != null) + "  #edit=" + (ta != null) + "  DomElement=" + (ta != null && ta.DomElement != null));
                    if (ta == null) { Log("=== selftest end (无编辑框) ==="); Application.Exit(); return; }

                    doc.InvokeScript("enterEdit");
                    dynamic editDom = ta.DomElement;   // 必须先落到 dynamic 变量，才能访问 .style
                    dynamic editStyle = editDom.style;
                    Log("enterEdit 后 edit.display = '" + (string)editStyle.display + "'");

                    dynamic d = ta.DomElement;
                    d.value = "SELFTEST-NEW-CONTENT\r\nsecond line";
                    Log("已写入编辑框新内容");

                    // 等页面把焦点交给 textarea，再触发 Ctrl+S
                    var t1 = new Timer();
                    t1.Interval = 400;
                    t1.Tick += (s1, e1) =>
                    {
                        t1.Stop();
                        t1.Dispose();
                        try
                        {
                            try { this.Activate(); } catch { }
                            try { SetForegroundWindow(this.Handle); } catch { }
                            Application.DoEvents();

                            if (synth)
                            {
                                Log("触发方式：直接调用 ProcessCmdKey（绕过消息队列）");
                                Message m = new Message();
                                bool handled = ProcessCmdKey(ref m, Keys.Control | Keys.S);
                                Log("ProcessCmdKey(Ctrl+S) 返回 " + handled);
                            }
                            else
                            {
                                Log("触发方式：keybd_event 模拟真实 Ctrl+S（焦点应在 textarea）");
                                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                                keybd_event(VK_S, 0, 0, UIntPtr.Zero);
                                keybd_event(VK_S, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            }
                        }
                        catch (Exception ex) { Log("触发异常: " + ex.Message); }
                        ScheduleSelfTestCheck();
                    };
                    t1.Start();
                }
                catch (Exception ex)
                {
                    Log("selftest 异常: " + ex.GetType().Name + " " + ex.Message);
                    Log("=== selftest end (异常) ===");
                    Application.Exit();
                }
            };
            t.Start();
        }

        private void ScheduleSelfTestCheck()
        {
            var t2 = new Timer();
            t2.Interval = 2200;
            t2.Tick += (s2, e2) =>
            {
                t2.Stop();
                t2.Dispose();
                try
                {
                    var doc2 = browser.Document;
                    string e1 = "?", v1 = "?";
                    var ed = doc2.GetElementById("edit");
                    if (ed != null && ed.DomElement != null)
                    {
                        dynamic edd = ed.DomElement;
                        dynamic eds = edd.style;
                        try { e1 = (string)eds.display; } catch { }
                    }
                    var vw = doc2.GetElementById("view");
                    if (vw != null && vw.DomElement != null)
                    {
                        dynamic vwd = vw.DomElement;
                        dynamic vws = vwd.style;
                        try { v1 = (string)vws.display; } catch { }
                    }
                    Log("结果 edit.display='" + e1 + "'  view.display='" + v1 + "'");
                    string onDisk = File.Exists(currentFile) ? File.ReadAllText(currentFile, Encoding.UTF8) : "<不存在>";
                    Log("磁盘内容 = " + onDisk.Replace("\r", "\\r").Replace("\n", "\\n"));
                }
                catch (Exception ex3) { Log("结果检查异常: " + ex3.Message); }
                Log("=== selftest end ===");
                Application.Exit();
            };
            t2.Start();
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
