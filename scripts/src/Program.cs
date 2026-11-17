using System;
using System.Windows.Forms;

namespace SerialRf {
    static class Program {
        [STAThread]
        static void Main() {
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs args) { Theme.Notice(null,args.Exception.Message,"Application error"); };
            Application.Run(new MainForm());
        }
    }
}
