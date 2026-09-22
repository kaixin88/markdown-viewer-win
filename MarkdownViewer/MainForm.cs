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

        // 统一保存入口（按钮 / Ctrl+S 共用）：
        // 1) C# 直读 textarea 实时值（DomElement.value，等价 JS 的 ta.value）并写盘；
        // 2) 把写入磁盘的内容回传给页面（afterSaveContent），让页面刷新为最新内容并退出编辑态；
        //    这一步用 BeginInvoke 延迟到当前按键/点击消息处理结束之后再执行，
        //    避免在 ProcessCmdKey 上下文里重入 IE 脚本引擎导致 InvokeScript 失效。
        // 不可使用 HTMLElement.GetAttribute("value") 读 textarea：
        //    IE/MSHTML 下它返回的是初始内容而非当前编辑，会把旧内容写回文件。
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
                    SaveCurrentFile(content);
                    saved = true;
                }
            }
            catch (Exception ex)
            {
                if (!saved) MessageBox.Show("保存失败：" + ex.Message, "保存",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (saved)
            {
                string payload = content;
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        try { browser.Document.InvokeScript("afterSaveContent", new object[] { payload }); }
                        catch { try { browser.Document.InvokeScript("afterSave"); } catch { } }
                    }));
                }
                catch { }
            }
            else
            {
                // C# 直读失败才走页面 JS 兜底
                try { browser.Document?.InvokeScript("save"); } catch { }
            }

            if (tsEdit != null) tsEdit.Text = "编辑";
            if (tsSave != null) tsSave.Enabled = false;
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
