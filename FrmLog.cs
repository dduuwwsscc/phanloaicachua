using System.Windows.Forms;

namespace phanLoaiCaChua
{
    public partial class FrmLog : Form
    {
        public FrmLog()
        {
            InitializeComponent();
            rtbLog.Text = AppLogger.GetAll();
        }

        public void AppendLog(string line)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new System.Action<string>(AppendLog), line);
                return;
            }

            rtbLog.AppendText(line + System.Environment.NewLine);
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.ScrollToCaret();
        }

        public void ClearLog()
        {
            if (IsDisposed) return;
            rtbLog.Clear();
        }
    }
}
