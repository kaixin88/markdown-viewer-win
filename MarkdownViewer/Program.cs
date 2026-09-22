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
            string logPath = null;
            bool selfTest = false;
            bool synth = false;
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (string.IsNullOrEmpty(a)) continue;
                    if (a == "--selftest") { selfTest = true; continue; }
                    if (a == "--synth") { synth = true; continue; }
                    if (a.StartsWith("--log=")) { logPath = a.Substring(6); continue; }
                    if (a.StartsWith("-")) continue;
                    // 注意：这里不能 break，否则后面的 --log= 之类的开关会被跳过
                    if (System.IO.File.Exists(a)) file = a;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(file, logPath, selfTest, synth));
        }
    }
}
