using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace phanLoaiCaChua
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (s, e) =>
            {
                try { AppLogger.Log("UI Exception: " + e.Exception.Message); } catch { }
                MessageBox.Show(e.Exception.Message, "Lỗi ứng dụng", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    AppLogger.Log("Unhandled Exception: " + (ex != null ? ex.ToString() : "Unknown"));
                }
                catch { }
            };

            Application.Run(new Form1());
        }
    }
}
