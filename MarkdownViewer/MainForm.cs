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
    }

    public class MainForm : Form
    {
        private WebBrowser browser;
        private string currentFile;
        private string settingsJson = "";
        private ToolStripButton tsEdit;
        private ToolStripButton tsSave;
        private ToolStripButton tsTheme;
        private ToolStripLabel tsName;

        public MainForm(string file)
        {
            SetBrowserEmulation();
            currentFile = file;
            settingsJson = LoadSettings();
            this.Text = "文件查看器";
            this.Width = 980;
            this.Height = 740;

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

            LoadFile(currentFile);
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
        // IE/MSHTML 下的两条硬约束决定了这里的写法：
        //  1) 在按键消息上下文里调用 InvokeScript 会因脚本引擎重入而静默失效。
        //     这正是「Ctrl+S 能保存、但界面仍停在编辑态」的根因：写盘靠 C#（能成功），
        //     而退出编辑态原来依赖的 afterSaveContent 脚本回调被吞掉了。
        //     所以关键路径只用 IDispatch 调 DOM（读值 / 改 style / blur），不调脚本。
        //  2) HTMLElement.GetAttribute("value") 读 textarea 返回的是初始内容，
        //     会把旧内容写回文件 —— 必须走 DomElement.value（等价 JS 的 ta.value）。
        private void SaveViaScript()
        {
            string content = null;
            bool saved = false;
            try
            {
                var doc = browser.Document;
                var ta = doc != null ? doc.GetElementById("edit") : null;
                if (ta != null && ta.DomElement != null)
                {
                    dynamic dom = ta.DomElement;
                    content = (string)dom.value;
                    if (content == null) content = "";
                    SaveCurrentFile(content);   // 只写盘，绝不回头调脚本
                    saved = true;
                    ExitEditByDom(ta);          // 纯 IDispatch：当场退出编辑态
                }
            }
            catch (Exception ex)
            {
                if (!saved) MessageBox.Show("保存失败：" + ex.Message, "保存",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (saved)
            {
                // 正文重新渲染必须由脚本引擎完成，改到按键上下文之外再用定时器触发
                RefreshViewLater();
                // 仅在确认写盘成功后复位工具栏，与页面的阅读态保持一致
                SyncToolbar(true);
            }
            else
            {
                // 拿不到编辑框（页面未就绪等）才退化为让页面自己走一遍保存；
                // 此时工具栏状态由页面 notifyState 回传，避免和页面的编辑态对不上
                try { browser.Document?.InvokeScript("save"); } catch { }
            }
        }

        // 直接操作 DOM 退出编辑态。全部是 IDispatch 属性/方法调用，不经过脚本引擎，
        // 因此在 ProcessCmdKey 的按键上下文里同样可靠。
        private void ExitEditByDom(HtmlElement ta)
        {
            try
            {
                dynamic d = ta.DomElement;
                try { d.blur(); } catch { }
                dynamic ds = d.style;
                ds.display = "none";

                var doc = browser.Document;
                var view = doc != null ? doc.GetElementById("view") : null;
                if (view != null && view.DomElement != null)
                {
                    dynamic vd = view.DomElement;
                    dynamic vs = vd.style;
                    vs.display = "block";
                }
                // 焦点移出已隐藏的编辑框，避免后续按键继续落到不可见的 textarea
                try { browser.Focus(); } catch { }
            }
            catch { }
        }

        // 延迟到当前按键/点击消息处理完毕后再让页面重新渲染最新内容。
        // 不传参：页面自己从 textarea 取内容，避免长字符串跨语言编组失败
        //（IE 下 InvokeScript 传长参数不可靠）。脚本调用仍失败时用整页重载兜底 ——
        // 此时文件早已写盘，重载必然得到最新内容与阅读态。
        private void RefreshViewLater()
        {
            var t = new Timer();
            t.Interval = 60;
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                bool ok = false;
                try
                {
                    browser.Document.InvokeScript("afterSaveFromEdit");
                    ok = true;
                }
                catch { ok = false; }
                if (!ok)
                {
                    try { LoadFile(currentFile); } catch { }
                }
            };
            t.Start();
        }

        private void SyncToolbar(bool viewMode)
        {
            if (tsEdit != null) tsEdit.Text = viewMode ? "编辑" : "取消";
            if (tsSave != null) tsSave.Enabled = !viewMode;
        }

        // 全局 Ctrl+S：焦点在任意位置（含菜单栏）都能保存
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
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
                    this.Text = "文件查看器 — " + name;
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
