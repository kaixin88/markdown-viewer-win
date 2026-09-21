using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace MarkdownViewer
{
    public class MainForm : Form
    {
        private WebBrowser browser;
        private string currentFile;

        public MainForm(string file)
        {
            currentFile = file;
            this.Text = "Markdown 查看器";
            this.Width = 920;
            this.Height = 720;

            var menu = new MenuStrip();
            menu.Dock = DockStyle.Top;
            var fileMenu = new ToolStripMenuItem("文件(&F)");
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("打开(&O)", null, (s, e) => OpenFile()));
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("退出(&X)", null, (s, e) => Application.Exit()));
            var viewMenu = new ToolStripMenuItem("视图(&V)");
            viewMenu.DropDownItems.Add(new ToolStripMenuItem("切换主题(&T)", null, (s, e) => ToggleTheme()));
            menu.Items.Add(fileMenu);
            menu.Items.Add(viewMenu);
            this.MainMenuStrip = menu;
            this.Controls.Add(menu);

            browser = new WebBrowser();
            browser.Dock = DockStyle.Fill;
            browser.ScriptErrorsSuppressed = true;
            this.Controls.Add(browser);

            LoadMarkdown(currentFile);
        }

        private void OpenFile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Markdown 文件|*.md;*.markdown;*.txt|所有文件|*.*";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    currentFile = dlg.FileName;
                    LoadMarkdown(currentFile);
                }
            }
        }

        private void ToggleTheme()
        {
            try
            {
                if (browser.Document != null)
                {
                    browser.Document.InvokeScript("eval",
                        new object[] { "var d=document.documentElement; d.setAttribute('data-theme', d.getAttribute('data-theme')==='dark'?'light':'dark');" });
                }
            }
            catch { }
        }

        private void LoadMarkdown(string path)
        {
            string md = "";
            if (!string.IsNullOrEmpty(path))
            {
                if (File.Exists(path))
                {
                    md = File.ReadAllText(path, Encoding.UTF8);
                    this.Text = "Markdown 查看器 — " + Path.GetFileName(path);
                }
                else
                {
                    md = "# 无法读取文件\n\n路径：" + path;
                }
            }
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(md ?? ""));
            string html = LoadTemplate().Replace("__B64__", b64);
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
