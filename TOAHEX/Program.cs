using System;
using System.Windows.Forms;

namespace TOAHEX
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 语言词典：Dictory/ 下的 zh-CN.txt / ja-JP.txt / en-US.txt
            Lang.Load();
            Lang.LoadPreference();

            Application.Run(new MainForm());
        }
    }
}
