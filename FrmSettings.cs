using System;
using System.Windows.Forms;

namespace phanLoaiCaChua
{
    public partial class FrmSettings : Form
    {
        public int CameraIndex => (int)numCamera.Value;

        public FrmSettings(int currentCameraIndex = 0)
        {
            InitializeComponent();
            numCamera.Value = Math.Max(numCamera.Minimum, Math.Min(numCamera.Maximum, currentCameraIndex));
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
