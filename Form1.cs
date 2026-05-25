using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;
using OpenCvSharp.Extensions;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Text;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace phanLoaiCaChua
{
    public partial class Form1 : Form
    {
        private CameraDisplayManager _cameraManager;
        private System.Windows.Forms.Timer _previewTimer;
        private TcpEsp32Client _tcpClient;
        private FrmLog _frmLog;
        private AiTomatoClient _aiTomatoClient;
        private PythonAiServerManager _pythonAiServer;
        private const string AiHealthUrl = "http://127.0.0.1:5058/health";

        private int _cameraIndex = 0;
        private bool _isRunning = false;
        private bool _isAnalyzing = false;
        private bool _isClosing = false;
        private DateTime _lastAnalyzeAt = DateTime.MinValue;
        private DateTime _lastAiDebugAt = DateTime.MinValue;
        private readonly TimeSpan _analyzeInterval = TimeSpan.FromMilliseconds(900);
        private DateTime _lastLoggedAt = DateTime.MinValue;
        private string _lastLoggedLabel = "none";

        // ================= TOMATO DETECTION FILTER =================
        // Chỉ nhận quả nằm trong vùng băng tải. Vùng dưới ảnh thường là nền gỗ/dây điện,
        // YOLO đôi khi bắt nhầm thành quả vàng nên cần loại trước khi phân loại màu.
        private const double TOMATO_ROI_X_MIN = 0.02;
        private const double TOMATO_ROI_X_MAX = 0.98;
        private const double TOMATO_ROI_Y_MIN = 0.02;
        private const double TOMATO_ROI_Y_MAX = 0.76;
        private const double TOMATO_ASPECT_MIN = 0.60;
        private const double TOMATO_ASPECT_MAX = 1.65;

        // ================= EMPTY BELT BASELINE FOR GREEN TOMATO =================
        // Khi bắt đầu mở camera, app sẽ lấy nền băng tải trong vài giây lúc chưa đặt quả.
        // Fallback bắt quả xanh chỉ hoạt động sau khi có nền này, và chỉ bắt vùng xanh
        // khác nền băng tải để tránh nhận cả băng tải là quả xanh.
        private Mat _emptyBeltBaselineRoi = null;
        private bool _emptyBeltBaselineReady = false;
        private DateTime _emptyBeltBaselineStartedAt = DateTime.MinValue;
        private DateTime _lastEmptyBeltBaselineLogAt = DateTime.MinValue;
        private readonly TimeSpan _emptyBeltBaselineDuration = TimeSpan.FromSeconds(3);

        // HSV trung bình của chính phần băng tải xanh lúc chưa có quả.
        // Fallback quả xanh sẽ yêu cầu màu xanh của ứng viên lệch đủ xa so với nền này.
        private bool _emptyBeltHsvMeanReady = false;
        private double _emptyBeltMeanH = 0.0;
        private double _emptyBeltMeanS = 0.0;
        private double _emptyBeltMeanV = 0.0;

        // ================= PIC_FEATURE HOLD CONTROL =================
        // pic_camera vẫn chạy realtime. pic_feature chỉ cập nhật khi bắt đầu thấy một quả mới,
        // sau đó giữ nguyên ảnh trích xuất cho tới khi quả mới xuất hiện hoặc hết timeout.
        private bool _featureTrackingTomato = false;
        private bool _featureHasHeldTomato = false;
        private bool _featureTimeoutShown = false;
        private DateTime _featureLastDetectedAt = DateTime.MinValue;
        private DateTime _featureLastShownAt = DateTime.MinValue;
        private readonly TimeSpan _featureNewTomatoGap = TimeSpan.FromMilliseconds(900);
        private readonly TimeSpan _featureHoldTimeout = TimeSpan.FromSeconds(10);

        // ================= ESP32 COMMAND / COUNT CONTROL =================
        // Mục tiêu:
        // - Mỗi quả cà chua chỉ được gửi 1 lệnh xuống ESP32.
        // - Chỉ tăng bộ đếm khi ESP32 phản hồi DONE sau khi đã đẩy/xử lý xong.
        // - Muốn gửi quả tiếp theo thì camera phải có khoảng thời gian không thấy quả,
        //   tránh việc cùng một quả bị gửi lệnh liên tục qua nhiều frame.
        private bool _readyToSendNextTomato = true;
        private bool _waitingEsp32Done = false;
        private string _pendingEsp32Command = null;
        private string _pendingCounterLabel = null;
        private string _pendingDisplayLabel = null;
        private DateTime _lastTomatoSeenAt = DateTime.MinValue;
        private DateTime _lastEsp32BlockLogAt = DateTime.MinValue;
        private DateTime _pendingEsp32SentAt = DateTime.MinValue;
        private DateTime _lastEsp32TimeoutLogAt = DateTime.MinValue;
        private readonly TimeSpan _noTomatoResetDelay = TimeSpan.FromMilliseconds(1000);
        private readonly TimeSpan _esp32DoneTimeout = TimeSpan.FromSeconds(25);
        private string _tcpRxLineBuffer = string.Empty;

        public Form1()
        {
            InitializeComponent();

            btnConnect.Click += btnConnect_Click;
            btnPing.Click += btnPing_Click;
            btn_setting.Click += btn_setting_Click;
            btnLog.Click += btnLog_Click;
            btn_batDau.Click += btn_batDau_Click;
            btn_dungChuyen.Click += btn_dungChuyen_Click;
            btn_xoa.Click += btn_xoa_Click;

            Load += Form1_Load;
            FormClosing += Form1_FormClosing;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            _cameraManager = new CameraDisplayManager(pic_camera)
            {
                AutoDisplayEnabled = false
            };

            _previewTimer = new System.Windows.Forms.Timer();
            _previewTimer.Interval = 220;
            _previewTimer.Tick += PreviewTimer_Tick;

            _tcpClient = new TcpEsp32Client();
            _tcpClient.LogMessage += TcpClient_LogMessage;
            _tcpClient.ConnectionChanged += TcpClient_ConnectionChanged;
            _tcpClient.DataReceived += TcpClient_DataReceived;

            string aiDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_ai_server");
            _pythonAiServer = new PythonAiServerManager(aiDir, "ai_tomato_server.py", AiHealthUrl);
            _aiTomatoClient = new AiTomatoClient("http://127.0.0.1:5058/classify");

            AppLogger.LogAdded += AppLogger_LogAdded;
            SetIdleUi();
            AppLogger.Log("Ứng dụng khởi động.");
            AppLogger.Log("Camera target resolution: 640x480.");
            AppLogger.Log("AI endpoint: http://127.0.0.1:5058/classify");
            AppLogger.Log("AI health: " + AiHealthUrl);

            bool aiOk = _pythonAiServer.EnsureStarted();
            AppLogger.Log(aiOk ? "AI server đã được khởi động." : "AI server chưa sẵn sàng. Hãy kiểm tra Python, pip và requirements.");
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                _isClosing = true;
                _isRunning = false;
                _previewTimer?.Stop();
                if (_previewTimer != null) _previewTimer.Tick -= PreviewTimer_Tick;

                AppLogger.LogAdded -= AppLogger_LogAdded;

                _cameraManager?.Stop();
                _cameraManager?.Dispose();
                _cameraManager = null;

                ResetEmptyBeltBaseline(true);

                _aiTomatoClient?.Dispose();
                _aiTomatoClient = null;

                _pythonAiServer?.Dispose();
                _pythonAiServer = null;

                _tcpClient?.Dispose();
                _tcpClient = null;
            }
            catch
            {
            }
        }


        private string SafeCvText(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            string normalized = input.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder();
            foreach (char c in normalized)
            {
                UnicodeCategory uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }

            string ascii = sb.ToString().Normalize(NormalizationForm.FormC);
            return ascii.Replace('đ', 'd').Replace('Đ', 'D');
        }

        private Mat BuildStatusCanvas(string title, string detail)
        {
            using (Bitmap bmp = new Bitmap(640, 480, PixelFormat.Format24bppRgb))
            using (Graphics g = Graphics.FromImage(bmp))
            using (Brush bg = new SolidBrush(Color.Black))
            using (Brush orange = new SolidBrush(Color.Orange))
            using (Brush white = new SolidBrush(Color.White))
            using (Font titleFont = new Font("Arial", 20, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font detailFont = new Font("Arial", 14, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.FillRectangle(bg, 0, 0, bmp.Width, bmp.Height);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                string t = title ?? "THONG BAO";
                string d = detail ?? "";
                if (d.Length > 100) d = d.Substring(0, 100);

                g.DrawString(t, titleFont, orange, new PointF(40, 150));
                g.DrawString(d, detailFont, white, new RectangleF(40, 220, 560, 180));

                return BitmapConverter.ToMat(bmp);
            }
        }

        private void SetIdleUi()
        {
            lb_quaChin.Text = "0";
            lb_quaXanh.Text = "0";
            lb_quaVang.Text = "0";
            lb_quaError.Text = "0";
            btnConnect.BackColor = SystemColors.Control;
            btnConnect.Text = "Connect to esp32";
            btnPing.BackColor = SystemColors.Control;
            ClearPicture(pic_camera);
            ClearPicture(pic_feature);
        }

        private void AppLogger_LogAdded(string msg)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppLogger_LogAdded), msg);
                return;
            }

            if (_frmLog != null && !_frmLog.IsDisposed)
            {
                _frmLog.AppendLog(msg);
            }
        }

        private void TcpClient_LogMessage(string msg)
        {
            AppLogger.Log(msg);
        }

        private void TcpClient_ConnectionChanged(bool connected)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool>(TcpClient_ConnectionChanged), connected);
                return;
            }

            btnConnect.BackColor = connected ? Color.LightGreen : SystemColors.Control;
            btnConnect.Text = connected ? "Connected" : "Connect to esp32";

            if (!connected && _waitingEsp32Done)
            {
                AppLogger.Log("Mất kết nối ESP32 khi đang chờ DONE. Hủy lệnh đang chờ.");
                _waitingEsp32Done = false;
                _pendingEsp32Command = null;
                _pendingCounterLabel = null;
                _pendingDisplayLabel = null;
                _pendingEsp32SentAt = DateTime.MinValue;
                _readyToSendNextTomato = true;
            }
        }

        private void TcpClient_DataReceived(string text)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(TcpClient_DataReceived), text);
                return;
            }

            if (string.IsNullOrEmpty(text))
                return;

            AppLogger.Log("ESP32 -> " + text.Trim());
            ProcessEsp32ReceivedText(text);
        }

        private async void btnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                if (_tcpClient.IsConnected)
                {
                    _tcpClient.Disconnect();
                    return;
                }

                string ip = rtbIp.Text.Trim();
                if (!int.TryParse(rtbPort.Text.Trim(), out int port))
                {
                    MessageBox.Show("Port không hợp lệ.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                btnConnect.Enabled = false;
                bool ok = await _tcpClient.ConnectAsync(ip, port);
                if (!ok)
                {
                    MessageBox.Show("Không kết nối được tới esp32.", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                btnConnect.Enabled = true;
            }
        }

        private async void btnPing_Click(object sender, EventArgs e)
        {
            try
            {
                btnPing.Enabled = false;
                btnPing.BackColor = SystemColors.Control;

                string ip = rtbIp.Text.Trim();
                using (Ping ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(ip, 1200);
                    if (reply.Status == IPStatus.Success)
                    {
                        btnPing.BackColor = Color.LightGreen;
                        AppLogger.Log($"Ping OK: {reply.Address} - {reply.RoundtripTime} ms");
                    }
                    else
                    {
                        btnPing.BackColor = Color.LightCoral;
                        AppLogger.Log("Ping fail: " + reply.Status);
                    }
                }
            }
            catch (Exception ex)
            {
                btnPing.BackColor = Color.LightCoral;
                AppLogger.Log("Ping lỗi: " + ex.Message);
            }
            finally
            {
                btnPing.Enabled = true;
            }
        }

        private void btn_setting_Click(object sender, EventArgs e)
        {
            using (FrmSettings frm = new FrmSettings(_cameraIndex))
            {
                if (frm.ShowDialog(this) == DialogResult.OK)
                {
                    _cameraIndex = frm.CameraIndex;
                    bool ok = StartCamera(_cameraIndex);
                    if (ok)
                    {
                        AppLogger.Log($"Đã đổi camera index = {_cameraIndex}");
                    }
                }
            }
        }

        private void btnLog_Click(object sender, EventArgs e)
        {
            if (_frmLog == null || _frmLog.IsDisposed)
            {
                _frmLog = new FrmLog();
                _frmLog.FormClosed += (s, ev) => _frmLog = null;
                _frmLog.Show(this);
            }
            else
            {
                _frmLog.BringToFront();
            }
        }

        private void btn_batDau_Click(object sender, EventArgs e)
        {
            _isRunning = true;

            bool aiOk = _pythonAiServer != null && _pythonAiServer.EnsureStarted();
            string healthMsg = _aiTomatoClient != null ? _aiTomatoClient.CheckHealth(AiHealthUrl) : "FAIL: no client";
            AppLogger.Log("AI health check: " + healthMsg);

            if (!aiOk)
            {
                MessageBox.Show(
                    "AI server chưa chạy được. Hãy cài Python và chạy requirements_tomato_ai.txt trước.",
                    "AI server",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            ShowMatOnPictureBox(pic_feature, BuildStatusCanvas("AI SAN SANG", "Health " + healthMsg));

            bool ok = StartCamera(_cameraIndex);
            if (ok)
            {
                AppLogger.Log("Bắt đầu chạy camera và phân loại cà chua bằng YOLO Python server.");
            }
        }

        private void btn_dungChuyen_Click(object sender, EventArgs e)
        {
            StopCameraAndUi("Đã dừng camera.");
        }

        private void btn_xoa_Click(object sender, EventArgs e)
        {
            lb_quaChin.Text = "0";
            lb_quaXanh.Text = "0";
            lb_quaVang.Text = "0";
            lb_quaError.Text = "0";
            _lastLoggedLabel = "none";
            _lastLoggedAt = DateTime.MinValue;

            _readyToSendNextTomato = true;
            _waitingEsp32Done = false;
            _pendingEsp32Command = null;
            _pendingCounterLabel = null;
            _pendingDisplayLabel = null;
            _pendingEsp32SentAt = DateTime.MinValue;
            _lastTomatoSeenAt = DateTime.MinValue;
            _tcpRxLineBuffer = string.Empty;
            ResetFeatureHoldState(false);

            AppLogger.Clear();
            _frmLog?.ClearLog();
            AppLogger.Log("Đã xóa bộ đếm, log và trạng thái gửi lệnh ESP32.");
        }

        private void StopCameraAndUi(string logText)
        {
            _isRunning = false;
            _isAnalyzing = false;
            _previewTimer?.Stop();
            _cameraManager?.Stop();
            ResetFeatureHoldState(false);
            ResetEmptyBeltBaseline(true);
            ClearPicture(pic_feature);
            AppLogger.Log(logText);
        }

        private bool StartCamera(int cameraIndex)
        {
            try
            {
                _previewTimer?.Stop();
                _cameraManager?.Stop();
                _isAnalyzing = false;
                ResetFeatureHoldState(false);
                BeginEmptyBeltBaselineCalibration();

                bool ok = _cameraManager.StartFromIndex(cameraIndex);
                if (!ok)
                {
                    MessageBox.Show($"Không mở được camera. Index = {cameraIndex}", "Lỗi camera", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }

                UpdatePreview();
                _previewTimer?.Start();
                AppLogger.Log("Preview timer started.");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi mở camera: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        
private void PreviewTimer_Tick(object sender, EventArgs e)
        {
            if (!_isRunning || _isClosing) return;

            try
            {
                UpdatePreview();
            }
            catch (Exception ex)
            {
                AppLogger.Log("Preview timer lỗi: " + ex.Message);
            }
        }

        private void UpdatePreview()
        {
            Mat frame = null;
            Mat cameraView = null;

            try
            {
                frame = _cameraManager.GetCurrentFrameClone();
                if (frame == null || frame.Empty())
                    return;

                cameraView = frame.Clone();

                // Luôn ưu tiên hiển thị camera trước để UI không bị đơ.
                _cameraManager.ShowMat(cameraView);

                // 3 giây đầu dùng để lấy nền băng tải khi chưa có quả.
                // Trong thời gian này chưa phân tích AI để tránh bắt nhầm băng tải là quả xanh.
                if (!_emptyBeltBaselineReady)
                {
                    UpdateEmptyBeltBaseline(frame);
                    return;
                }

                // Chỉ phân tích theo chu kỳ và không cho chồng request.
                if (_isAnalyzing) return;
                if (DateTime.Now - _lastAnalyzeAt < _analyzeInterval) return;

                _lastAnalyzeAt = DateTime.Now;
                if (DateTime.Now - _lastAiDebugAt > TimeSpan.FromSeconds(3))
                {
                    _lastAiDebugAt = DateTime.Now;
                    AppLogger.Log("Dang gui frame sang AI...");
                    // Không cập nhật pic_feature ở đây nữa.
                    // Nếu cập nhật trạng thái "ĐANG GỬI AI" liên tục thì ảnh crop vừa trích xuất
                    // sẽ bị mất ngay khi frame sau chưa phát hiện quả.
                }

                Mat frameForAi = frame.Clone();
                Task.Run(() => AnalyzeFrameWorker(frameForAi));
            }
            catch (Exception ex)
            {
                AppLogger.Log("Preview lỗi: " + ex.Message);
            }
            finally
            {
                cameraView?.Dispose();
                frame?.Dispose();
            }
        }

        
private void AnalyzeFrameWorker(Mat frame)
        {
            Mat cameraView = null;
            Mat featureView = null;

            if (frame == null || frame.Empty())
            {
                frame?.Dispose();
                return;
            }

            if (_isClosing || _isAnalyzing)
            {
                frame.Dispose();
                return;
            }

            _isAnalyzing = true;

            try
            {
                using (Mat aiInput = new Mat())
                {
                    Cv2.Resize(frame, aiInput, new OpenCvSharp.Size(416, 416));

                    AiTomatoResult result = _aiTomatoClient.Analyze(aiInput);
                    result = ScaleResultToFrame(result, aiInput.Width, aiInput.Height, frame.Width, frame.Height);

                    // Model YOLO đôi khi không bắt được quả xanh vì màu quả gần giống băng tải.
                    // Nếu AI không tìm thấy box, dùng thêm bộ phát hiện màu xanh theo ROI băng tải.
                    if (result != null && result.Success && !result.Found)
                        result = TryDetectGreenTomatoByColor(frame, result);

                    result = RefineClassificationByColor(frame, result);

                    // Nếu box AI bị loại vì không giống cà chua, vẫn thử fallback xanh một lần nữa.
                    if (result != null && result.Success && !result.Found)
                    {
                        var greenFallback = TryDetectGreenTomatoByColor(frame, result);
                        if (greenFallback != null && greenFallback.Found)
                            result = RefineClassificationByColor(frame, greenFallback);
                    }
                    Debug.WriteLine("AI result success=" + result.Success + ", found=" + result.Found + ", label=" + result.Label + ", err=" + result.ErrorMessage);

                    if (_isClosing || IsDisposed)
                        return;

                    cameraView = frame.Clone();

                    if (!result.Success)
                    {
                        featureView = BuildStatusCanvas("AI LOI", result.ErrorMessage ?? "Khong ro loi");

                        Mat uiFeature = featureView?.Clone();
                        BeginInvoke((Action)(() =>
                        {
                            try
                            {
                                if (_isClosing || IsDisposed) return;

                                // Nếu đang giữ ảnh trích xuất của quả trước đó thì không ghi đè bằng màn hình lỗi tạm thời.
                                // Chỉ hiện lỗi lên pic_feature khi chưa có ảnh crop nào đang được giữ.
                                if (uiFeature != null && !_featureHasHeldTomato)
                                    ShowMatOnPictureBox(pic_feature, uiFeature);

                                AppLogger.Log("AI loi: " + result.ErrorMessage);
                            }
                            finally
                            {
                                uiFeature?.Dispose();
                            }
                        }));
                        return;
                    }

                    featureView = BuildFeatureView(frame, result, ref cameraView);

                    Mat uiCamera = cameraView?.Clone();
                    Mat uiFeature2 = featureView?.Clone();

                    BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            if (_isClosing || IsDisposed) return;

                            if (uiCamera != null)
                                _cameraManager.ShowMat(uiCamera);

                            UpdateFeaturePanelHold(result, uiFeature2);

                            AppLogger.Log("Detect ok: found=" + result.Found + ", label=" + result.Label + ", conf=" + result.Confidence.ToString("P1"));
                            HandleAnalysisResult(result);
                        }
                        catch (Exception exUi)
                        {
                            AppLogger.Log("UI update loi: " + exUi.Message);
                        }
                        finally
                        {
                            uiCamera?.Dispose();
                            uiFeature2?.Dispose();
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                if (!_isClosing && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke((Action)(() => AppLogger.Log("AI worker loi: " + ex.Message)));
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                featureView?.Dispose();
                cameraView?.Dispose();
                frame?.Dispose();
                _isAnalyzing = false;
            }
        }


        private AiTomatoResult ScaleResultToFrame(AiTomatoResult srcResult, int aiWidth, int aiHeight, int frameWidth, int frameHeight)
        {
            if (srcResult == null) return null;
            if (!srcResult.Found || srcResult.W <= 0 || srcResult.H <= 0) return srcResult;

            double sx = frameWidth / (double)Math.Max(1, aiWidth);
            double sy = frameHeight / (double)Math.Max(1, aiHeight);

            int x = (int)Math.Round(srcResult.X * sx);
            int y = (int)Math.Round(srcResult.Y * sy);
            int w = (int)Math.Round(srcResult.W * sx);
            int h = (int)Math.Round(srcResult.H * sy);

            return AiTomatoResult.FromValues(
                srcResult.Success,
                srcResult.Found,
                x, y, w, h,
                srcResult.Label,
                srcResult.DisplayLabel,
                srcResult.Confidence,
                srcResult.ErrorMessage
            );
        }


        private AiTomatoResult RefineClassificationByColor(Mat frame, AiTomatoResult srcResult)
        {
            if (frame == null || frame.Empty() || srcResult == null || !srcResult.Success || !srcResult.Found)
                return srcResult;

            string rejectReason;
            if (!IsTomatoCandidateBox(srcResult, frame.Width, frame.Height, out rejectReason))
            {
                AppLogger.Log("Reject AI box: " + rejectReason +
                              $" | box=({srcResult.X},{srcResult.Y},{srcResult.W},{srcResult.H})");

                return AiTomatoResult.FromValues(
                    srcResult.Success,
                    false,
                    srcResult.X, srcResult.Y, srcResult.W, srcResult.H,
                    "none",
                    "Khong phat hien ca chua",
                    0.0f,
                    srcResult.ErrorMessage
                );
            }

            var rect = SafeRect(srcResult.X, srcResult.Y, srcResult.W, srcResult.H, frame.Width, frame.Height);

            // Không mở rộng crop quá nhiều, vì băng tải màu xanh đậm dễ lọt vào crop
            // và bị tính nhầm là vùng tối/vùng hỏng của quả.
            rect = ExpandRect(rect, frame.Width, frame.Height, 0.02);

            using (Mat crop = new Mat(frame, rect).Clone())
            {
                if (crop.Empty())
                    return srcResult;

                using (Mat resized = crop.Resize(new OpenCvSharp.Size(240, 240)))
                using (Mat hsv = new Mat())
                using (Mat ellipseMask = Mat.Zeros(resized.Rows, resized.Cols, MatType.CV_8UC1))
                using (Mat spotEllipseMask = Mat.Zeros(resized.Rows, resized.Cols, MatType.CV_8UC1))
                using (Mat satMask = new Mat())
                using (Mat workMask = new Mat())
                using (Mat red1 = new Mat())
                using (Mat red2 = new Mat())
                using (Mat red = new Mat())
                using (Mat yellow = new Mat())
                using (Mat green = new Mat())
                using (Mat brown = new Mat())
                using (Mat dark = new Mat())
                using (Mat spotDark = new Mat())
                {
                    Cv2.CvtColor(resized, hsv, ColorConversionCodes.BGR2HSV);

                    // Chỉ xét vùng trung tâm crop để giảm ảnh hưởng của băng tải/nền.
                    Cv2.Ellipse(
                        ellipseMask,
                        new CvPoint(resized.Width / 2, resized.Height / 2),
                        new OpenCvSharp.Size((int)(resized.Width * 0.32), (int)(resized.Height * 0.32)),
                        0, 0, 360, Scalar.White, -1
                    );

                    // Mask lớn hơn để bắt các đốm đen ở gần mép quả.
                    // Bản cũ chỉ kiểm tra vùng trung tâm nên dễ bỏ sót đốm đen sát mép.
                    Cv2.Ellipse(
                        spotEllipseMask,
                        new CvPoint(resized.Width / 2, resized.Height / 2),
                        new OpenCvSharp.Size((int)(resized.Width * 0.46), (int)(resized.Height * 0.46)),
                        0, 0, 360, Scalar.White, -1
                    );

                    // Chỉ xét các pixel có màu đủ rõ. Nền quá tối/ít bão hòa sẽ bị loại khỏi mẫu màu chính.
                    Cv2.InRange(hsv, new Scalar(0, 50, 40), new Scalar(179, 255, 255), satMask);
                    Cv2.BitwiseAnd(ellipseMask, satMask, workMask);

                    Cv2.InRange(hsv, new Scalar(0, 70, 45), new Scalar(12, 255, 255), red1);
                    Cv2.InRange(hsv, new Scalar(165, 70, 45), new Scalar(179, 255, 255), red2);
                    Cv2.BitwiseOr(red1, red2, red);

                    Cv2.InRange(hsv, new Scalar(13, 60, 60), new Scalar(38, 255, 255), yellow);

                    // Quả cà chua xanh thật thường nằm trong dải H khoảng 39..95.
                    // Băng tải xanh đậm cũng có thể rơi vào dải này, nên phần quyết định bên dưới
                    // phải ưu tiên màu xanh trước khi xét lỗi tối/nâu.
                    Cv2.InRange(hsv, new Scalar(39, 45, 40), new Scalar(95, 255, 255), green);
                    Cv2.InRange(hsv, new Scalar(5, 50, 25), new Scalar(25, 255, 150), brown);
                    Cv2.InRange(hsv, new Scalar(0, 0, 0), new Scalar(179, 120, 50), dark);

                    // Đốm đen trên quả đỏ có thể không đen tuyệt đối do ánh sáng camera,
                    // nên cho phép V tới khoảng 105 và S rộng hơn để bắt cả đốm nâu đậm/đen.
                    Cv2.InRange(hsv, new Scalar(0, 0, 0), new Scalar(179, 255, 105), spotDark);

                    double baseCount = Math.Max(1.0, Cv2.CountNonZero(workMask));

                    double redRatio = CountMaskedRatio(red, workMask, baseCount);
                    double yellowRatio = CountMaskedRatio(yellow, workMask, baseCount);
                    double greenRatio = CountMaskedRatio(green, workMask, baseCount);
                    double brownRatio = CountMaskedRatio(brown, workMask, baseCount);
                    double darkRatio = CountMaskedRatio(spotDark, spotEllipseMask, Math.Max(1.0, Cv2.CountNonZero(spotEllipseMask)));

                    int darkSpotCount;
                    double darkSpotAreaRatio;
                    double maxDarkSpotRatio;
                    AnalyzeDarkSpots(spotDark, spotEllipseMask, out darkSpotCount, out darkSpotAreaRatio, out maxDarkSpotRatio);

                    string label = srcResult.Label;
                    string display = srcResult.DisplayLabel;
                    string srcNorm = NormalizeTomatoLabel(srcResult.Label);

                    // Ưu tiên giữ nhãn xanh nếu AI gốc đã thiên về xanh và màu xanh vẫn hiện diện rõ.
                    bool aiSaysGreen = srcNorm == "green";
                    bool aiSaysYellow = srcNorm == "yellow";
                    bool aiSaysRipe = srcNorm == "ripe";

                    bool greenDominant = greenRatio >= 0.11 &&
                                         greenRatio >= yellowRatio * 1.05 &&
                                         greenRatio >= redRatio * 0.80;

                    bool redDominant = redRatio >= 0.18 &&
                                       redRatio >= yellowRatio * 1.08 &&
                                       redRatio >= greenRatio * 1.10;

                    // Chỉ đổi sang vàng khi vàng thực sự vượt xanh khá rõ.
                    bool yellowDominant = yellowRatio >= 0.15 &&
                                          yellowRatio >= greenRatio * 1.22 &&
                                          yellowRatio >= redRatio * 0.92;

                    // Quả lỗi theo yêu cầu mới: quả đỏ nhưng có các đốm đen rõ trên bề mặt.
                    // Yêu cầu ít nhất 2 đốm tách rời để tránh nhận nhầm phản sáng / bóng đổ đơn lẻ.
                    double warmRatio = redRatio + yellowRatio * 0.65;
                    bool redOrOrangeTomato = redRatio >= 0.14 ||
                                             (srcNorm == "ripe" && warmRatio >= 0.25 && redRatio >= greenRatio * 1.10);

                    bool blackSpottedRedDefect = redOrOrangeTomato &&
                                                 darkSpotCount >= 2 &&
                                                 darkSpotAreaRatio >= 0.010 &&
                                                 maxDarkSpotRatio >= 0.0025;

                    // Ngoài ra vẫn giữ điều kiện lỗi tổng quát cho quả thật sự hỏng.
                    bool strongDefect = (brownRatio > 0.34 || darkRatio > 0.40) &&
                                        greenRatio < 0.12 && yellowRatio < 0.14 && redRatio < 0.20;

                    if (blackSpottedRedDefect)
                    {
                        label = "defect";
                        display = "Qua co van de";
                    }
                    else if (aiSaysGreen && greenRatio >= 0.09 && yellowRatio <= greenRatio * 1.45 && redRatio < 0.22)
                    {
                        label = "green";
                        display = "Qua xanh";
                    }
                    else if (greenDominant)
                    {
                        label = "green";
                        display = "Qua xanh";
                    }
                    else if (redDominant)
                    {
                        label = "ripe";
                        display = "Qua chin";
                    }
                    else if (yellowDominant)
                    {
                        label = "yellow";
                        display = "Qua vang";
                    }
                    else if (strongDefect)
                    {
                        label = "defect";
                        display = "Qua co van de";
                    }
                    else if (aiSaysYellow && yellowRatio >= 0.10 && greenRatio <= yellowRatio * 1.10)
                    {
                        label = "yellow";
                        display = "Qua vang";
                    }
                    else if (aiSaysRipe && redRatio >= 0.10)
                    {
                        label = "ripe";
                        display = "Qua chin";
                    }
                    else if (greenRatio >= yellowRatio * 0.90 && greenRatio >= redRatio)
                    {
                        label = "green";
                        display = "Qua xanh";
                    }
                    else if (yellowRatio >= redRatio)
                    {
                        label = "yellow";
                        display = "Qua vang";
                    }
                    else
                    {
                        label = "ripe";
                        display = "Qua chin";
                    }

                    AppLogger.Log(
                        $"Color refine: src={srcNorm}, R={redRatio:P0}, Y={yellowRatio:P0}, G={greenRatio:P0}, warm={warmRatio:P0}, B={brownRatio:P0}, D={darkRatio:P0}, spots={darkSpotCount}, spotArea={darkSpotAreaRatio:P1}, maxSpot={maxDarkSpotRatio:P1}, redOrange={redOrOrangeTomato}, blackSpottedRed={blackSpottedRedDefect}, strongDefect={strongDefect} -> {display}");

                    return AiTomatoResult.FromValues(
                        srcResult.Success,
                        srcResult.Found,
                        srcResult.X, srcResult.Y, srcResult.W, srcResult.H,
                        label,
                        display,
                        srcResult.Confidence,
                        srcResult.ErrorMessage
                    );
                }
            }
        }

        private OpenCvSharp.Rect GetTomatoRoiRect(Mat frame)
        {
            if (frame == null || frame.Empty())
                return new OpenCvSharp.Rect(0, 0, 1, 1);

            int roiX1 = Math.Max(0, (int)Math.Round(frame.Width * TOMATO_ROI_X_MIN));
            int roiY1 = Math.Max(0, (int)Math.Round(frame.Height * TOMATO_ROI_Y_MIN));
            int roiX2 = Math.Min(frame.Width, (int)Math.Round(frame.Width * TOMATO_ROI_X_MAX));
            int roiY2 = Math.Min(frame.Height, (int)Math.Round(frame.Height * TOMATO_ROI_Y_MAX));

            int roiW = Math.Max(1, roiX2 - roiX1);
            int roiH = Math.Max(1, roiY2 - roiY1);
            return new OpenCvSharp.Rect(roiX1, roiY1, roiW, roiH);
        }

        private void BeginEmptyBeltBaselineCalibration()
        {
            ResetEmptyBeltBaseline(true);
            _emptyBeltBaselineStartedAt = DateTime.Now;
            _lastEmptyBeltBaselineLogAt = DateTime.MinValue;
            AppLogger.Log($"Bắt đầu lấy nền băng tải trong {_emptyBeltBaselineDuration.TotalSeconds:0}s. Chưa đặt cà chua lên băng tải trong thời gian này.");
        }

        private void ResetEmptyBeltBaseline(bool disposeOld)
        {
            _emptyBeltBaselineReady = false;
            _emptyBeltBaselineStartedAt = DateTime.MinValue;
            _lastEmptyBeltBaselineLogAt = DateTime.MinValue;
            _emptyBeltHsvMeanReady = false;
            _emptyBeltMeanH = 0.0;
            _emptyBeltMeanS = 0.0;
            _emptyBeltMeanV = 0.0;

            if (disposeOld && _emptyBeltBaselineRoi != null)
            {
                _emptyBeltBaselineRoi.Dispose();
                _emptyBeltBaselineRoi = null;
            }
        }

        private void UpdateEmptyBeltBaseline(Mat frame)
        {
            if (frame == null || frame.Empty())
                return;

            DateTime now = DateTime.Now;
            if (_emptyBeltBaselineStartedAt == DateTime.MinValue)
                _emptyBeltBaselineStartedAt = now;

            try
            {
                OpenCvSharp.Rect roiRect = GetTomatoRoiRect(frame);
                using (Mat roi = new Mat(frame, roiRect).Clone())
                using (Mat hsv = new Mat())
                using (Mat beltGreenMask = new Mat())
                {
                    _emptyBeltBaselineRoi?.Dispose();
                    _emptyBeltBaselineRoi = roi.Clone();

                    // Lấy HSV trung bình của vùng băng tải xanh, không lấy trung bình toàn ROI
                    // vì ROI còn có nền trắng, mạch điện, dây điện.
                    Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
                    Cv2.InRange(hsv, new Scalar(32, 25, 25), new Scalar(105, 255, 255), beltGreenMask);

                    int greenPixelCount = Cv2.CountNonZero(beltGreenMask);
                    if (greenPixelCount > 800)
                    {
                        Scalar mean = Cv2.Mean(hsv, beltGreenMask);
                        _emptyBeltMeanH = mean.Val0;
                        _emptyBeltMeanS = mean.Val1;
                        _emptyBeltMeanV = mean.Val2;
                        _emptyBeltHsvMeanReady = true;
                    }
                }

                double elapsed = (now - _emptyBeltBaselineStartedAt).TotalSeconds;
                double remain = Math.Max(0.0, _emptyBeltBaselineDuration.TotalSeconds - elapsed);

                if (now - _lastEmptyBeltBaselineLogAt > TimeSpan.FromSeconds(1))
                {
                    _lastEmptyBeltBaselineLogAt = now;
                    AppLogger.Log($"Đang lấy nền băng tải... còn {remain:0.0}s");
                }

                if (now - _emptyBeltBaselineStartedAt >= _emptyBeltBaselineDuration)
                {
                    _emptyBeltBaselineReady = _emptyBeltBaselineRoi != null && !_emptyBeltBaselineRoi.Empty();
                    if (_emptyBeltBaselineReady && _emptyBeltHsvMeanReady)
                    {
                        AppLogger.Log($"Đã lấy xong nền băng tải. HSV nền xanh: H={_emptyBeltMeanH:0.0}, S={_emptyBeltMeanS:0.0}, V={_emptyBeltMeanV:0.0}. Bắt đầu phân loại cà chua.");
                    }
                    else if (_emptyBeltBaselineReady)
                    {
                        AppLogger.Log("Đã lấy xong ảnh nền, nhưng chưa lấy được HSV xanh của băng tải. Fallback quả xanh sẽ siết rất chặt.");
                    }
                    else
                    {
                        AppLogger.Log("Không lấy được nền băng tải, fallback quả xanh sẽ tạm tắt.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Lỗi lấy nền băng tải: " + ex.Message);
            }
        }

        private AiTomatoResult TryDetectGreenTomatoByColor(Mat frame, AiTomatoResult baseResult)
        {
            if (frame == null || frame.Empty())
                return baseResult;

            // Không dùng fallback xanh nếu chưa lấy xong nền băng tải.
            // Nền này dùng để tách vùng thay đổi khi quả xanh xuất hiện.
            if (!_emptyBeltBaselineReady || _emptyBeltBaselineRoi == null || _emptyBeltBaselineRoi.Empty())
                return baseResult;

            try
            {
                OpenCvSharp.Rect roiRect = GetTomatoRoiRect(frame);
                if (roiRect.Width <= 1 || roiRect.Height <= 1)
                    return baseResult;

                using (Mat roi = new Mat(frame, roiRect).Clone())
                {
                    if (roi.Width != _emptyBeltBaselineRoi.Width || roi.Height != _emptyBeltBaselineRoi.Height)
                    {
                        AppLogger.Log("Green fallback: kích thước nền băng tải không khớp, cần bấm BẮT ĐẦU lại để lấy nền mới.");
                        return baseResult;
                    }

                    using (Mat hsv = new Mat())
                    using (Mat greenMask = new Mat())
                    using (Mat diffBgr = new Mat())
                    using (Mat diffGray = new Mat())
                    using (Mat changedMask = new Mat())
                    using (Mat candidateMask = new Mat())
                    using (Mat clean = new Mat())
                    {
                        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);

                        // 1) Mask màu xanh rộng hơn một chút để không bỏ sót quả xanh thật.
                        // Việc chống bắt nhầm băng tải sẽ dựa thêm vào ảnh nền và hình dạng bên dưới.
                        Cv2.InRange(hsv, new Scalar(30, 35, 35), new Scalar(105, 255, 255), greenMask);

                        // 2) Tách vùng thay đổi so với nền băng tải trống đã học trong 3 giây đầu.
                        // Cách này tốt hơn việc chỉ so HSV, vì quả xanh có thể gần màu với băng tải
                        // nhưng khi đặt lên vẫn làm vùng ảnh thay đổi rõ về biên, bóng, texture và độ sáng.
                        Cv2.Absdiff(roi, _emptyBeltBaselineRoi, diffBgr);
                        Cv2.CvtColor(diffBgr, diffGray, ColorConversionCodes.BGR2GRAY);
                        Cv2.Threshold(diffGray, changedMask, 18, 255, ThresholdTypes.Binary);

                        using (Mat kernelSmall = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3)))
                        using (Mat kernelMid = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(11, 11)))
                        {
                            Cv2.MorphologyEx(changedMask, changedMask, MorphTypes.Open, kernelSmall);
                            Cv2.MorphologyEx(changedMask, changedMask, MorphTypes.Close, kernelMid, iterations: 1);
                        }

                        Cv2.BitwiseAnd(greenMask, changedMask, candidateMask);

                        // Nếu quả xanh quá giống nền làm diff nhỏ, dùng ngưỡng thay đổi mềm hơn.
                        // Vẫn không nhận bừa vì các bước hình dạng phía dưới sẽ lọc tiếp.
                        if (Cv2.CountNonZero(candidateMask) < 500)
                        {
                            Cv2.Threshold(diffGray, changedMask, 12, 255, ThresholdTypes.Binary);
                            using (Mat kernelSmall = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3)))
                            using (Mat kernelMid = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(13, 13)))
                            {
                                Cv2.MorphologyEx(changedMask, changedMask, MorphTypes.Open, kernelSmall);
                                Cv2.MorphologyEx(changedMask, changedMask, MorphTypes.Close, kernelMid, iterations: 2);
                            }
                            Cv2.BitwiseAnd(greenMask, changedMask, candidateMask);
                        }

                        using (Mat kernelSmall = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3)))
                        using (Mat kernelLarge = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(17, 17)))
                        {
                            Cv2.MorphologyEx(candidateMask, clean, MorphTypes.Open, kernelSmall);
                            Cv2.MorphologyEx(clean, clean, MorphTypes.Close, kernelLarge, iterations: 2);
                        }

                        OpenCvSharp.Point[][] contours;
                        HierarchyIndex[] hierarchy;
                        Cv2.FindContours(clean, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                        double bestScore = 0.0;
                        OpenCvSharp.Rect bestBox = new OpenCvSharp.Rect();
                        double bestAreaRatio = 0.0;
                        double bestFill = 0.0;
                        double bestAspect = 0.0;
                        double bestCircularity = 0.0;
                        double bestMeanH = 0.0;
                        double bestMeanS = 0.0;
                        double bestMeanV = 0.0;
                        double bestDiffH = 0.0;
                        double bestDiffS = 0.0;
                        double bestDiffV = 0.0;
                        double bestChangeRatio = 0.0;
                        double bestGreenRatio = 0.0;
                        double bestMinCircleFill = 0.0;
                        double bestStdS = 0.0;
                        double bestStdV = 0.0;

                        double frameArea = Math.Max(1.0, frame.Width * frame.Height);
                        double roiArea = Math.Max(1.0, roi.Width * roi.Height);

                        foreach (var contour in contours)
                        {
                            double area = Cv2.ContourArea(contour);

                            // Siết kích thước tối thiểu: băng tải đang quay hay tạo các mảng xanh nhỏ
                            // cỡ 40-60 px và rất dễ bị nhận nhầm. Quả cà chua thật trong setup này
                            // thường lớn hơn rõ rệt.
                            if (area < 4200 || area > 35000)
                                continue;

                            OpenCvSharp.Rect br = Cv2.BoundingRect(contour);
                            if (br.Width < 72 || br.Height < 72)
                                continue;

                            // Băng tải thường là mảng lớn, dài, hoặc chiếm phần lớn ROI.
                            if (br.Width > roi.Width * 0.55 || br.Height > roi.Height * 0.70)
                                continue;
                            if (br.Width > 260 || br.Height > 260)
                                continue;

                            bool touchesRoiBorder = br.X <= 4 || br.Y <= 4 ||
                                                    br.X + br.Width >= roi.Width - 4 ||
                                                    br.Y + br.Height >= roi.Height - 4;
                            // Quả có thể hơi sát mép, nhưng mảng băng tải lớn chạm mép thì loại.
                            if (touchesRoiBorder && (area / roiArea > 0.035 || br.Width > 120 || br.Height > 120))
                                continue;

                            double aspect = br.Width / (double)Math.Max(1, br.Height);
                            if (aspect < 0.55 || aspect > 1.80)
                                continue;

                            double fill = area / Math.Max(1.0, br.Width * br.Height);

                            // Quan trọng: mảng băng tải thường tạo mask gần kín hình chữ nhật,
                            // fill rất cao. Quả cà chua tròn/bầu dục thường fill khoảng 0.55-0.82.
                            if (fill < 0.30 || fill > 0.86)
                                continue;

                            double perimeter = Cv2.ArcLength(contour, true);
                            double circularity = perimeter > 1e-6 ? (4.0 * Math.PI * area / (perimeter * perimeter)) : 0.0;
                            if (circularity < 0.30)
                                continue;

                            OpenCvSharp.Point2f minCircleCenter;
                            float minCircleRadius;
                            Cv2.MinEnclosingCircle(contour, out minCircleCenter, out minCircleRadius);
                            double minCircleFill = minCircleRadius > 1e-6f
                                ? area / (Math.PI * minCircleRadius * minCircleRadius)
                                : 0.0;

                            // Hình chữ nhật/square có circularity có thể vẫn cao, nên kiểm thêm
                            // tỉ lệ diện tích so với đường tròn bao ngoài. Quả tròn sẽ cao hơn.
                            if (minCircleFill < 0.66)
                                continue;

                            // Loại mảng hình chữ nhật của băng tải: quá kín khung, kéo dài, hoặc giống miếng chữ nhật.
                            bool rectLikeBelt = (fill > 0.82 && minCircleFill < 0.78) ||
                                                (aspect > 1.45 && fill > 0.70) ||
                                                (aspect < 0.70 && fill > 0.70);
                            if (rectLikeBelt)
                                continue;

                            double areaRatio = area / frameArea;
                            if (areaRatio < 0.0012 || areaRatio > 0.090)
                                continue;

                            using (Mat contourMask = Mat.Zeros(roi.Rows, roi.Cols, MatType.CV_8UC1))
                            {
                                Cv2.DrawContours(contourMask, new[] { contour }, -1, Scalar.White, -1);

                                using (Mat maskBox = new Mat(contourMask, br))
                                using (Mat hsvBox = new Mat(hsv, br))
                                using (Mat greenBox = new Mat(greenMask, br))
                                using (Mat changedBox = new Mat(changedMask, br))
                                {
                                    double boxArea = Math.Max(1.0, br.Width * br.Height);
                                    double contourPixels = Math.Max(1.0, Cv2.CountNonZero(maskBox));

                                    double greenRatio = Cv2.CountNonZero(greenBox) / boxArea;
                                    if (greenRatio < 0.18)
                                        continue;

                                    // Nếu gần như toàn bộ bbox là xanh đặc và contour cũng gần kín chữ nhật,
                                    // đây thường là mặt băng tải chứ không phải quả.
                                    if (greenRatio > 0.92 && fill > 0.80 && minCircleFill < 0.82)
                                        continue;

                                    double changeRatio = Cv2.CountNonZero(changedBox) / boxArea;
                                    if (changeRatio < 0.10)
                                        continue;

                                    Scalar mean;
                                    Scalar stddev;
                                    Cv2.MeanStdDev(hsvBox, out mean, out stddev, maskBox);
                                    double meanH = mean.Val0;
                                    double meanS = mean.Val1;
                                    double meanV = mean.Val2;
                                    double stdS = stddev.Val1;
                                    double stdV = stddev.Val2;

                                    // Mặt băng tải thường khá đồng đều trong một ô nhỏ; quả thật có bóng, biên,
                                    // nếp/độ cong nên S hoặc V dao động cao hơn.
                                    bool hasTomatoTexture = stdV >= 10.0 || stdS >= 12.0 || changeRatio >= 0.24;
                                    if (!hasTomatoTexture)
                                        continue;

                                    bool greenEnough = meanH >= 30 && meanH <= 105 && meanS >= 35 && meanV >= 35;
                                    if (!greenEnough)
                                        continue;

                                    double diffH = _emptyBeltHsvMeanReady ? Math.Abs(meanH - _emptyBeltMeanH) : 99.0;
                                    double diffS = _emptyBeltHsvMeanReady ? Math.Abs(meanS - _emptyBeltMeanS) : 99.0;
                                    double diffV = _emptyBeltHsvMeanReady ? Math.Abs(meanV - _emptyBeltMeanV) : 99.0;

                                    // Không bắt buộc HSV phải lệch quá nhiều, vì quả xanh có thể gần màu băng tải.
                                    // Nhưng nếu HSV rất giống nền thì yêu cầu vùng thay đổi và hình dạng phải mạnh hơn.
                                    bool colorClearlyDifferent = diffH >= 6.0 || diffS >= 16.0 || diffV >= 16.0;
                                    bool strongShapeAndMotion = changeRatio >= 0.18 && circularity >= 0.32 && fill >= 0.32;
                                    if (!colorClearlyDifferent && !strongShapeAndMotion)
                                        continue;

                                    OpenCvSharp.Rect absBox = new OpenCvSharp.Rect(br.X + roiRect.X, br.Y + roiRect.Y, br.Width, br.Height);
                                    double cx = (absBox.X + absBox.Width * 0.5) / Math.Max(1.0, frame.Width);
                                    double cy = (absBox.Y + absBox.Height * 0.5) / Math.Max(1.0, frame.Height);
                                    if (cx < TOMATO_ROI_X_MIN || cx > TOMATO_ROI_X_MAX || cy < TOMATO_ROI_Y_MIN || cy > TOMATO_ROI_Y_MAX)
                                        continue;

                                    double roundScore = Math.Max(0.10, Math.Min(1.0, circularity / 0.75));
                                    double aspectScore = 1.0 - Math.Min(0.85, Math.Abs(aspect - 1.0));
                                    double fillScore = Math.Max(0.10, Math.Min(1.0, fill));
                                    double changeScore = Math.Max(0.10, Math.Min(1.0, changeRatio * 2.2));
                                    double greenScore = Math.Max(0.10, Math.Min(1.0, greenRatio * 1.6));
                                    double colorDiffScore = Math.Max(diffH / 6.0, Math.Max(diffS / 16.0, diffV / 16.0));
                                    colorDiffScore = Math.Min(2.0, Math.Max(0.35, colorDiffScore));

                                    double score = contourPixels * roundScore * aspectScore * fillScore * changeScore * greenScore * colorDiffScore;

                                    if (score > bestScore)
                                    {
                                        bestScore = score;
                                        bestBox = absBox;
                                        bestAreaRatio = areaRatio;
                                        bestFill = fill;
                                        bestAspect = aspect;
                                        bestCircularity = circularity;
                                        bestMeanH = meanH;
                                        bestMeanS = meanS;
                                        bestMeanV = meanV;
                                        bestDiffH = diffH;
                                        bestDiffS = diffS;
                                        bestDiffV = diffV;
                                        bestChangeRatio = changeRatio;
                                        bestGreenRatio = greenRatio;
                                        bestMinCircleFill = minCircleFill;
                                        bestStdS = stdS;
                                        bestStdV = stdV;
                                    }
                                }
                            }
                        }

                        if (bestScore > 0.0 && bestBox.Width > 0 && bestBox.Height > 0)
                        {
                            bestBox = ExpandRect(bestBox, frame.Width, frame.Height, 0.06);
                            AppLogger.Log($"Green fallback V4: found Qua xanh, box=({bestBox.X},{bestBox.Y},{bestBox.Width},{bestBox.Height}), area={bestAreaRatio:P1}, fill={bestFill:0.00}, aspect={bestAspect:0.00}, circ={bestCircularity:0.00}, circleFill={bestMinCircleFill:0.00}, greenRatio={bestGreenRatio:P0}, change={bestChangeRatio:P0}, stdS={bestStdS:0.0}, stdV={bestStdV:0.0}, HSV=({bestMeanH:0.0},{bestMeanS:0.0},{bestMeanV:0.0}), beltHSV=({_emptyBeltMeanH:0.0},{_emptyBeltMeanS:0.0},{_emptyBeltMeanV:0.0}), diff=({bestDiffH:0.0},{bestDiffS:0.0},{bestDiffV:0.0})");

                            return AiTomatoResult.FromValues(
                                true,
                                true,
                                bestBox.X, bestBox.Y, bestBox.Width, bestBox.Height,
                                "green",
                                "Qua xanh",
                                0.68f,
                                baseResult != null ? baseResult.ErrorMessage : null
                            );
                        }

                        AppLogger.Log("Green fallback V4: chưa thấy quả xanh hợp lệ. Nếu quả xanh thật bị bỏ qua, mở log để xem cần nới br/area/fill/circleFill.");
                        return baseResult;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("Green fallback lỗi: " + ex.Message);
                return baseResult;
            }
        }

        private bool IsTomatoCandidateBox(AiTomatoResult result, int frameWidth, int frameHeight, out string reason)
        {
            reason = "";

            if (result == null || !result.Found || result.W <= 0 || result.H <= 0)
            {
                reason = "khong co box hop le";
                return false;
            }

            double cx = (result.X + result.W * 0.5) / Math.Max(1.0, frameWidth);
            double cy = (result.Y + result.H * 0.5) / Math.Max(1.0, frameHeight);
            double aspect = result.W / (double)Math.Max(1, result.H);

            if (cx < TOMATO_ROI_X_MIN || cx > TOMATO_ROI_X_MAX ||
                cy < TOMATO_ROI_Y_MIN || cy > TOMATO_ROI_Y_MAX)
            {
                reason = $"ngoai ROI bang tai cx={cx:0.00}, cy={cy:0.00}";
                return false;
            }

            if (aspect < TOMATO_ASPECT_MIN || aspect > TOMATO_ASPECT_MAX)
            {
                reason = $"ti le box khong giong qua tron aspect={aspect:0.00}";
                return false;
            }

            reason = "ok";
            return true;
        }

        private void AnalyzeDarkSpots(Mat darkMask, Mat validMask, out int spotCount, out double totalSpotAreaRatio, out double maxSpotAreaRatio)
        {
            spotCount = 0;
            totalSpotAreaRatio = 0.0;
            maxSpotAreaRatio = 0.0;

            if (darkMask == null || darkMask.Empty() || validMask == null || validMask.Empty())
                return;

            using (Mat masked = new Mat())
            using (Mat cleaned = new Mat())
            {
                Cv2.BitwiseAnd(darkMask, validMask, masked);

                using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3)))
                {
                    Cv2.MorphologyEx(masked, cleaned, MorphTypes.Open, kernel);
                }

                double denom = Math.Max(1.0, Cv2.CountNonZero(validMask));

                OpenCvSharp.Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(cleaned, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                foreach (var contour in contours)
                {
                    double area = Cv2.ContourArea(contour);
                    if (area < 8)
                        continue;

                    double ratio = area / denom;

                    var br = Cv2.BoundingRect(contour);
                    double blobAspect = br.Width / (double)Math.Max(1, br.Height);
                    if (blobAspect > 5.0 || blobAspect < 0.20)
                        continue;

                    // Bỏ qua blob quá lớn vì thường là bóng nền / vùng che khuất lớn chứ không phải đốm.
                    if (ratio > 0.22)
                        continue;

                    spotCount++;
                    totalSpotAreaRatio += ratio;
                    if (ratio > maxSpotAreaRatio)
                        maxSpotAreaRatio = ratio;
                }
            }
        }

        private double CountMaskedRatio(Mat targetMask, Mat validMask, double denom)
        {
            using (Mat tmp = new Mat())
            {
                Cv2.BitwiseAnd(targetMask, validMask, tmp);
                return Cv2.CountNonZero(tmp) / Math.Max(1.0, denom);
            }
        }

        private int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private OpenCvSharp.Rect ExpandRect(OpenCvSharp.Rect rect, int frameWidth, int frameHeight, double padRatio)
        {
            int padX = (int)Math.Round(rect.Width * padRatio);
            int padY = (int)Math.Round(rect.Height * padRatio);

            int x = Math.Max(0, rect.X - padX);
            int y = Math.Max(0, rect.Y - padY);
            int right = Math.Min(frameWidth, rect.Right + padX);
            int bottom = Math.Min(frameHeight, rect.Bottom + padY);

            int w = Math.Max(1, right - x);
            int h = Math.Max(1, bottom - y);
            return new OpenCvSharp.Rect(x, y, w, h);
        }

        private void ResetFeatureHoldState(bool clearPicture)
        {
            _featureTrackingTomato = false;
            _featureHasHeldTomato = false;
            _featureTimeoutShown = false;
            _featureLastDetectedAt = DateTime.MinValue;
            _featureLastShownAt = DateTime.MinValue;

            if (clearPicture)
                ClearPicture(pic_feature);
        }

        private void UpdateFeaturePanelHold(AiTomatoResult result, Mat featureMat)
        {
            DateTime now = DateTime.Now;

            if (result != null && result.Success && result.Found && result.W > 0 && result.H > 0)
            {
                _featureLastDetectedAt = now;

                // Chỉ chốt ảnh khi bắt đầu thấy một quả mới.
                // Khi cùng một quả vẫn còn trước camera, pic_feature giữ nguyên ảnh crop cũ
                // để tránh bị chớp/mất ảnh theo từng frame AI.
                if (!_featureTrackingTomato)
                {
                    if (featureMat != null)
                        ShowMatOnPictureBox(pic_feature, featureMat);

                    _featureTrackingTomato = true;
                    _featureHasHeldTomato = true;
                    _featureTimeoutShown = false;
                    _featureLastShownAt = now;
                    AppLogger.Log("Pic_feature: đã chốt ảnh trích xuất, giữ ảnh cho tới khi có quả mới hoặc timeout.");
                }

                return;
            }

            // Không thấy quả một khoảng ngắn thì xem như quả cũ đã rời vùng nhìn.
            // Ảnh crop vẫn giữ trên pic_feature, nhưng lần sau thấy quả sẽ chốt ảnh mới.
            if (_featureTrackingTomato &&
                _featureLastDetectedAt != DateTime.MinValue &&
                now - _featureLastDetectedAt >= _featureNewTomatoGap)
            {
                _featureTrackingTomato = false;
                AppLogger.Log("Pic_feature: quả cũ đã rời vùng nhìn, vẫn giữ ảnh cũ để chờ quả mới.");
            }

            // Nếu giữ ảnh quá lâu mà không có quả mới thì xóa ảnh cũ một cách im lặng.
            // Không hiện dòng chữ "Hết thời gian giữ ảnh" lên pic_feature nữa.
            if (_featureHasHeldTomato &&
                !_featureTimeoutShown &&
                _featureLastShownAt != DateTime.MinValue &&
                now - _featureLastShownAt >= _featureHoldTimeout)
            {
                ClearPicture(pic_feature);

                _featureHasHeldTomato = false;
                _featureTrackingTomato = false;
                _featureTimeoutShown = true;
                _featureLastShownAt = DateTime.MinValue;
                AppLogger.Log($"Pic_feature: hết {_featureHoldTimeout.TotalSeconds:0}s, đã xóa ảnh trích xuất cũ.");
            }
        }

        private Mat BuildFeatureView(Mat frame, AiTomatoResult result, ref Mat cameraView)
        {
            if (frame == null || frame.Empty())
                return null;

            Mat canvas = new Mat(new OpenCvSharp.Size(640, 480), MatType.CV_8UC3, Scalar.Black);

            if (!result.Found || result.W <= 0 || result.H <= 0)
            {
                // Không trả canvas "KHÔNG PHÁT HIỆN" nữa, vì nó sẽ ghi đè ảnh crop đang giữ.
                // Việc xóa/hiện thông báo timeout do UpdateFeaturePanelHold() quản lý.
                canvas.Dispose();
                return null;
            }

            var rect = SafeRect(result.X, result.Y, result.W, result.H, frame.Width, frame.Height);
            rect = ExpandRect(rect, frame.Width, frame.Height, 0.06);
            Scalar color = GetLabelColor(result.Label);
            Cv2.Rectangle(cameraView, rect, color, 3);
            Cv2.PutText(cameraView, $"{SafeCvText(result.DisplayLabel)} {result.Confidence:P0}", new CvPoint(rect.X, Math.Max(25, rect.Y - 8)), HersheyFonts.HersheySimplex, 0.8, color, 2);

            using (Mat crop = new Mat(frame, rect).Clone())
            using (Mat cropResized = crop.Resize(new OpenCvSharp.Size(280, 280)))
            using (Mat hsv = new Mat())
            using (Mat mask = new Mat())
            using (Mat maskBgr = new Mat())
            {
                Cv2.CvtColor(cropResized, hsv, ColorConversionCodes.BGR2HSV);

                Scalar low1 = new Scalar(0, 70, 40);
                Scalar high1 = new Scalar(12, 255, 255);
                Scalar low2 = new Scalar(160, 70, 40);
                Scalar high2 = new Scalar(179, 255, 255);
                using (Mat red1 = new Mat())
                using (Mat red2 = new Mat())
                using (Mat yellow = new Mat())
                using (Mat green = new Mat())
                {
                    Cv2.InRange(hsv, low1, high1, red1);
                    Cv2.InRange(hsv, low2, high2, red2);
                    Cv2.BitwiseOr(red1, red2, mask);
                    Cv2.InRange(hsv, new Scalar(15, 60, 60), new Scalar(40, 255, 255), yellow);
                    Cv2.InRange(hsv, new Scalar(35, 50, 40), new Scalar(95, 255, 255), green);
                    Cv2.BitwiseOr(mask, yellow, mask);
                    Cv2.BitwiseOr(mask, green, mask);
                }

                Cv2.CvtColor(mask, maskBgr, ColorConversionCodes.GRAY2BGR);
                                cropResized.CopyTo(new Mat(canvas, new OpenCvSharp.Rect(20, 85, 280, 280)));
                maskBgr.CopyTo(new Mat(canvas, new OpenCvSharp.Rect(340, 85, 280, 280)));

                Cv2.PutText(canvas, "Crop ca chua", new CvPoint(20, 35), HersheyFonts.HersheySimplex, 0.75, color, 2);
                Cv2.PutText(canvas, "Mask mau/Hue", new CvPoint(340, 35), HersheyFonts.HersheySimplex, 0.75, Scalar.White, 2);
                Cv2.PutText(canvas, "Ket qua: " + SafeCvText(result.DisplayLabel), new CvPoint(20, 65), HersheyFonts.HersheySimplex, 0.75, color, 2);
                Cv2.PutText(canvas, "CONF: " + result.Confidence.ToString("P1"), new CvPoint(20, 405), HersheyFonts.HersheySimplex, 0.8, Scalar.White, 2);
                Cv2.PutText(canvas, $"BOX: {rect.Width}x{rect.Height}", new CvPoint(20, 440), HersheyFonts.HersheySimplex, 0.8, Scalar.White, 2);
            }

            return canvas;
        }

        private void HandleAnalysisResult(AiTomatoResult result)
        {
            CheckEsp32DoneTimeout();

            if (result == null || !result.Success)
                return;

            if (!result.Found)
            {
                MarkNoTomatoDetected();
                return;
            }

            DateTime now = DateTime.Now;
            _lastTomatoSeenAt = now;

            string label = NormalizeTomatoLabel(result.Label);
            if (label == "none")
                return;

            bool shouldLog = label != _lastLoggedLabel || (now - _lastLoggedAt).TotalSeconds >= 1.2;
            if (shouldLog)
            {
                _lastLoggedLabel = label;
                _lastLoggedAt = now;
                AppLogger.Log($"Kết quả AI: {result.DisplayLabel} | label={label} | conf={result.Confidence:P1} | box=({result.X},{result.Y},{result.W},{result.H})");
            }

            // Không tăng bộ đếm tại đây nữa.
            // Chỉ gửi lệnh 1 lần cho mỗi quả, sau đó đợi ESP32 phản hồi DONE mới tăng bộ đếm.
            _ = TrySendCommandForTomatoAsync(label, result.DisplayLabel);
        }

        private string NormalizeTomatoLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return "none";

            label = label.Trim().ToLowerInvariant();

            switch (label)
            {
                case "ripe":
                case "red":
                case "chin":
                case "chín":
                    return "ripe";

                case "yellow":
                case "vang":
                case "vàng":
                    return "yellow";

                case "green":
                case "xanh":
                    return "green";

                case "defect":
                case "error":
                case "black":
                case "loi":
                case "lỗi":
                    return "defect";

                default:
                    return label;
            }
        }

        private string MapLabelToEsp32Command(string normalizedLabel)
        {
            switch (normalizedLabel)
            {
                case "ripe":
                    return "red";
                case "yellow":
                    return "yellow";
                case "green":
                    return "green";
                case "defect":
                    return "error";
                default:
                    return null;
            }
        }

        private async Task TrySendCommandForTomatoAsync(string normalizedLabel, string displayLabel)
        {
            if (_isClosing || IsDisposed)
                return;

            if (_waitingEsp32Done && !CheckEsp32DoneTimeout())
                return;

            if (!_readyToSendNextTomato)
                return;

            string command = MapLabelToEsp32Command(normalizedLabel);
            if (string.IsNullOrWhiteSpace(command))
                return;

            if (_tcpClient == null || !_tcpClient.IsConnected)
            {
                if ((DateTime.Now - _lastEsp32BlockLogAt).TotalSeconds >= 2)
                {
                    _lastEsp32BlockLogAt = DateTime.Now;
                    AppLogger.Log($"Đã nhận diện {displayLabel}, nhưng chưa kết nối ESP32 nên chưa gửi lệnh {command}.");
                }
                return;
            }

            _readyToSendNextTomato = false;
            _waitingEsp32Done = true;
            _pendingEsp32Command = command;
            _pendingCounterLabel = normalizedLabel;
            _pendingDisplayLabel = string.IsNullOrWhiteSpace(displayLabel) ? normalizedLabel : displayLabel;
            _pendingEsp32SentAt = DateTime.Now;

            AppLogger.Log($"PC -> ESP32: {command} | loại={_pendingDisplayLabel}. Đang chờ ESP32 phản hồi DONE tối đa {_esp32DoneTimeout.TotalSeconds:0}s...");

            bool ok = await _tcpClient.SendAsync(command + "\n");
            if (!ok)
            {
                AppLogger.Log("Gửi lệnh xuống ESP32 thất bại. Hủy trạng thái chờ DONE.");
                _waitingEsp32Done = false;
                _pendingEsp32Command = null;
                _pendingCounterLabel = null;
                _pendingDisplayLabel = null;
                _pendingEsp32SentAt = DateTime.MinValue;
                _readyToSendNextTomato = true;
            }
        }

        private void MarkNoTomatoDetected()
        {
            if (_waitingEsp32Done && !CheckEsp32DoneTimeout())
                return;

            if (_readyToSendNextTomato)
                return;

            if (_lastTomatoSeenAt == DateTime.MinValue)
                return;

            if (DateTime.Now - _lastTomatoSeenAt >= _noTomatoResetDelay)
            {
                _readyToSendNextTomato = true;
                _lastLoggedLabel = "none";
                _lastLoggedAt = DateTime.MinValue;
                AppLogger.Log("Không còn thấy quả trong khung hình. Sẵn sàng gửi lệnh cho quả tiếp theo.");
            }
        }

        private bool CheckEsp32DoneTimeout()
        {
            if (!_waitingEsp32Done)
                return false;

            if (_pendingEsp32SentAt == DateTime.MinValue)
                return false;

            DateTime now = DateTime.Now;
            if (now - _pendingEsp32SentAt < _esp32DoneTimeout)
                return false;

            string timeoutCommand = _pendingEsp32Command;
            string timeoutDisplay = _pendingDisplayLabel;

            _waitingEsp32Done = false;
            _pendingEsp32Command = null;
            _pendingCounterLabel = null;
            _pendingDisplayLabel = null;
            _pendingEsp32SentAt = DateTime.MinValue;

            // Không tăng bộ đếm khi timeout vì ESP32 chưa xác nhận DONE.
            // Vẫn giữ _readyToSendNextTomato = false cho tới khi camera có khoảng trống không thấy quả,
            // để tránh gửi lặp lại cùng một quả đang đứng trước camera.
            if ((now - _lastEsp32TimeoutLogAt).TotalSeconds >= 1)
            {
                _lastEsp32TimeoutLogAt = now;
                AppLogger.Log($"Quá {_esp32DoneTimeout.TotalSeconds:0}s chưa nhận DONE từ ESP32 cho lệnh {timeoutCommand}. Hủy trạng thái chờ để không kẹt app. Chưa tăng bộ đếm cho {timeoutDisplay}.");
            }

            return true;
        }

        private void ProcessEsp32ReceivedText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            _tcpRxLineBuffer += text.Replace("\r", "\n");

            while (true)
            {
                int idx = _tcpRxLineBuffer.IndexOf('\n');
                if (idx < 0)
                    break;

                string line = _tcpRxLineBuffer.Substring(0, idx).Trim();
                _tcpRxLineBuffer = _tcpRxLineBuffer.Substring(idx + 1);
                HandleEsp32Line(line);
            }

            // Phòng trường hợp ESP32 gửi một dòng ngắn không kèm \n.
            string remain = _tcpRxLineBuffer.Trim();
            if (remain.StartsWith("DONE", StringComparison.OrdinalIgnoreCase))
            {
                _tcpRxLineBuffer = string.Empty;
                HandleEsp32Line(remain);
            }
        }

        private void HandleEsp32Line(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            if (line.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            HandleEsp32Done(line);
        }

        private void HandleEsp32Done(string doneLine)
        {
            if (!_waitingEsp32Done)
            {
                AppLogger.Log("ESP32 báo DONE nhưng PC không có lệnh nào đang chờ: " + doneLine);
                return;
            }

            string doneCommand = ExtractDoneCommand(doneLine);
            if (!string.IsNullOrWhiteSpace(doneCommand) &&
                !string.IsNullOrWhiteSpace(_pendingEsp32Command) &&
                !doneCommand.Equals(_pendingEsp32Command, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Log($"ESP32 DONE={doneCommand}, khác lệnh đang chờ={_pendingEsp32Command}. Vẫn xác nhận hoàn thành để tránh kẹt trạng thái.");
            }

            string labelToCount = _pendingCounterLabel;
            string display = _pendingDisplayLabel;
            string command = _pendingEsp32Command;

            _waitingEsp32Done = false;
            _pendingEsp32Command = null;
            _pendingCounterLabel = null;
            _pendingDisplayLabel = null;
            _pendingEsp32SentAt = DateTime.MinValue;

            if (!string.IsNullOrWhiteSpace(labelToCount))
            {
                IncreaseCounter(labelToCount);
                AppLogger.Log($"ESP32 đã hoàn thành lệnh {command}. Tăng bộ đếm: {display}.");
            }

            // Nếu quả đã rời khỏi khung hình thì có thể nhận quả mới ngay.
            // Nếu quả vẫn còn trong khung hình, vẫn khóa để tránh gửi lại cùng một quả.
            if (_lastTomatoSeenAt != DateTime.MinValue && DateTime.Now - _lastTomatoSeenAt >= _noTomatoResetDelay)
            {
                _readyToSendNextTomato = true;
                _lastLoggedLabel = "none";
                _lastLoggedAt = DateTime.MinValue;
                AppLogger.Log("Sẵn sàng gửi lệnh cho quả tiếp theo.");
            }
        }

        private string ExtractDoneCommand(string doneLine)
        {
            if (string.IsNullOrWhiteSpace(doneLine))
                return null;

            string[] parts = doneLine.Trim().Split(new[] { ' ', ':', '=', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Equals("DONE", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                    return parts[i + 1].Trim().ToLowerInvariant();
            }

            return null;
        }

        private void IncreaseCounter(string label)
        {
            switch (label)
            {
                case "ripe":
                    lb_quaChin.Text = (ParseInt(lb_quaChin.Text) + 1).ToString();
                    break;
                case "green":
                    lb_quaXanh.Text = (ParseInt(lb_quaXanh.Text) + 1).ToString();
                    break;
                case "yellow":
                    lb_quaVang.Text = (ParseInt(lb_quaVang.Text) + 1).ToString();
                    break;
                case "defect":
                    lb_quaError.Text = (ParseInt(lb_quaError.Text) + 1).ToString();
                    break;
            }
        }

        private int ParseInt(string text)
        {
            return int.TryParse(text, out int v) ? v : 0;
        }

        private OpenCvSharp.Rect SafeRect(int x, int y, int w, int h, int maxW, int maxH)
        {
            x = Math.Max(0, x);
            y = Math.Max(0, y);
            w = Math.Max(1, Math.Min(w, maxW - x));
            h = Math.Max(1, Math.Min(h, maxH - y));
            return new OpenCvSharp.Rect(x, y, w, h);
        }

        private Scalar GetLabelColor(string label)
        {
            switch (label)
            {
                case "green": return new Scalar(0, 255, 0);
                case "yellow": return new Scalar(0, 255, 255);
                case "ripe": return new Scalar(0, 0, 255);
                case "defect": return new Scalar(255, 0, 255);
                default: return new Scalar(255, 255, 255);
            }
        }

        private void ShowMatOnPictureBox(PictureBox pictureBox, Mat mat)
        {
            if (pictureBox == null || mat == null || mat.Empty())
                return;

            Bitmap bmp = BitmapConverter.ToBitmap(mat);
            var old = pictureBox.Image;
            pictureBox.Image = bmp;
            old?.Dispose();
        }

        private void ClearPicture(PictureBox pictureBox)
        {
            if (pictureBox == null) return;
            var old = pictureBox.Image;
            pictureBox.Image = null;
            old?.Dispose();
        }
    }
}
