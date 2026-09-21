using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MarkdownViewer
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 让 WebBrowser 控件使用 IE11 引擎（仅当前用户，无需管理员）
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    key.SetValue("MarkdownViewer.exe", 11001, RegistryValueKind.DWord);
                }
            }
            catch { }

            string file = null;
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (string.IsNullOrEmpty(a) || a.StartsWith("-")) continue;
                    if (System.IO.File.Exists(a))
                    {
                        file = a;
                        break;
                    }
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(file));
        }
    }
}
