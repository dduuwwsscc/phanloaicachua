using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace phanLoaiCaChua
{
    public sealed class CameraDisplayManager : IDisposable
    {
        private readonly PictureBox _pictureBox;
        private readonly object _frameLock = new object();

        private VideoCapture _capture;
        private Mat _lastFrame;
        private CancellationTokenSource _cts;
        private Task _cameraTask;
        private bool _disposed;

        public bool IsRunning { get; private set; }
        public bool AutoDisplayEnabled { get; set; } = true;

        public CameraDisplayManager(PictureBox pictureBox)
        {
            _pictureBox = pictureBox;
        }

        public bool StartFromIndex(int cameraIndex)
        {
            Stop();
            Thread.Sleep(180);

            try
            {
                _capture = new VideoCapture();
                bool opened = _capture.Open(cameraIndex, VideoCaptureAPIs.DSHOW);
                if (!opened || !_capture.IsOpened())
                {
                    _capture.Dispose();
                    _capture = null;
                    return false;
                }

                try
                {
                    _capture.FrameWidth = 640;
                    _capture.FrameHeight = 480;
                    try { _capture.Set(VideoCaptureProperties.FourCC, FourCC.MJPG); } catch { }
                    _capture.Fps = 30;
                    _capture.BufferSize = 1;
                }
                catch
                {
                }

                _cts = new CancellationTokenSource();
                _cameraTask = Task.Run(() => CameraLoop(_cts.Token));
                IsRunning = true;
                return true;
            }
            catch
            {
                Stop();
                return false;
            }
        }

        private void CameraLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_capture == null || !_capture.IsOpened())
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    using (Mat frame = new Mat())
                    {
                        if (!_capture.Read(frame) || frame.Empty())
                        {
                            Thread.Sleep(30);
                            continue;
                        }

                        lock (_frameLock)
                        {
                            _lastFrame?.Dispose();
                            _lastFrame = frame.Clone();
                        }

                        if (AutoDisplayEnabled)
                        {
                            ShowMat(frame);
                        }
                    }
                }
                catch
                {
                    Thread.Sleep(50);
                }
            }
        }

        public Mat GetCurrentFrameClone()
        {
            lock (_frameLock)
            {
                if (_lastFrame == null || _lastFrame.Empty())
                    return null;

                return _lastFrame.Clone();
            }
        }

        public void ShowMat(Mat frameToShow)
        {
            if (frameToShow == null || frameToShow.Empty())
                return;

            Bitmap bmp = BitmapConverter.ToBitmap(frameToShow);
            if (_pictureBox.IsDisposed)
            {
                bmp.Dispose();
                return;
            }

            if (_pictureBox.InvokeRequired)
            {
                _pictureBox.BeginInvoke(new Action(() => ReplaceImage(bmp)));
            }
            else
            {
                ReplaceImage(bmp);
            }
        }

        public void ClearView()
        {
            if (_pictureBox.IsDisposed) return;

            if (_pictureBox.InvokeRequired)
            {
                _pictureBox.BeginInvoke(new Action(ClearViewInternal));
            }
            else
            {
                ClearViewInternal();
            }
        }

        private void ClearViewInternal()
        {
            var old = _pictureBox.Image;
            _pictureBox.Image = null;
            old?.Dispose();
        }

        private void ReplaceImage(Bitmap bmp)
        {
            var old = _pictureBox.Image;
            _pictureBox.Image = bmp;
            old?.Dispose();
        }

        public void Stop()
        {
            IsRunning = false;

            try
            {
                _cts?.Cancel();
                try { _cameraTask?.Wait(1000); } catch { }
                _cts?.Dispose();
            }
            catch { }
            finally
            {
                _cts = null;
                _cameraTask = null;
            }

            try
            {
                if (_capture != null)
                {
                    if (_capture.IsOpened())
                    {
                        try { _capture.Release(); } catch { }
                    }
                    _capture.Dispose();
                }
            }
            catch { }
            finally
            {
                _capture = null;
            }

            lock (_frameLock)
            {
                _lastFrame?.Dispose();
                _lastFrame = null;
            }

            ClearView();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }
}
