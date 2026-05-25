using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace phanLoaiCaChua
{
    internal sealed class PythonAiServerManager : IDisposable
    {
        private Process _process;
        private readonly string _workingDir;
        private readonly string _scriptPath;
        private readonly string _healthUrl;
        private bool _disposed;

        // Chỉ dọn python.exe một lần khi app khởi động.
        // Không dọn lặp lại mỗi lần EnsureStarted() để tránh giết nhầm server vừa mở.
        private bool _killedPythonProcessesAtStartup = false;

        public PythonAiServerManager(string workingDir, string scriptName, string healthUrl)
        {
            _workingDir = workingDir;
            _scriptPath = Path.Combine(workingDir, scriptName);
            _healthUrl = healthUrl;
        }

        public bool EnsureStarted()
        {
            // Quan trọng:
            // Dọn python.exe/pythonw.exe cũ trước khi kiểm tra health.
            // Nếu kiểm tra health trước, app có thể gọi nhầm server Python cũ đang giữ port 5058.
            KillAllPythonProcessesOnceAtStartup();

            if (IsHealthy())
                return true;

            if (!File.Exists(_scriptPath))
            {
                AppLogger.Log("Không thấy file Python AI server: " + _scriptPath);
                return false;
            }

            try
            {
                Stop();

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c py -3 ai_tomato_server.py",
                    WorkingDirectory = _workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _process = new Process();
                _process.StartInfo = psi;
                _process.EnableRaisingEvents = true;
                _process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        AppLogger.Log("AI> " + e.Data);
                };
                _process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        AppLogger.Log("AI! " + e.Data);
                };

                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(250);

                    if (IsHealthy())
                    {
                        AppLogger.Log("Python AI server đã sẵn sàng.");
                        return true;
                    }
                }

                AppLogger.Log("Python AI server khởi động nhưng chưa phản hồi health-check.");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Log("Không khởi động được Python AI server: " + ex.Message);
                return false;
            }
        }

        private void KillAllPythonProcessesOnceAtStartup()
        {
            if (_killedPythonProcessesAtStartup)
                return;

            _killedPythonProcessesAtStartup = true;

            try
            {
                AppLogger.Log("Đang dọn các tiến trình python.exe/pythonw.exe cũ trước khi khởi động AI server...");

                int killedCount = 0;

                killedCount += KillProcessesByName("python");
                killedCount += KillProcessesByName("pythonw");

                if (killedCount > 0)
                    AppLogger.Log("Đã tắt " + killedCount + " tiến trình Python cũ.");
                else
                    AppLogger.Log("Không thấy tiến trình Python cũ cần tắt.");

                // Đợi Windows giải phóng port 5058 sau khi kill python.exe
                Thread.Sleep(800);
            }
            catch (Exception ex)
            {
                AppLogger.Log("Lỗi khi dọn tiến trình Python cũ: " + ex.Message);
            }
        }

        private int KillProcessesByName(string processName)
        {
            int count = 0;

            Process[] processes;

            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                AppLogger.Log("Không thể liệt kê tiến trình " + processName + ": " + ex.Message);
                return 0;
            }

            foreach (Process p in processes)
            {
                try
                {
                    if (p == null || p.HasExited)
                        continue;

                    int pid = p.Id;
                    string name = p.ProcessName;

                    p.Kill();
                    p.WaitForExit(1500);

                    count++;
                    AppLogger.Log("Đã tắt tiến trình " + name + ".exe, PID=" + pid);
                }
                catch (Exception ex)
                {
                    try
                    {
                        AppLogger.Log("Không tắt được " + processName + ".exe PID=" + p.Id + ": " + ex.Message);
                    }
                    catch
                    {
                        AppLogger.Log("Không tắt được " + processName + ".exe: " + ex.Message);
                    }
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            return count;
        }

        public bool IsHealthy()
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMilliseconds(1200);
                    var s = http.GetStringAsync(_healthUrl).GetAwaiter().GetResult();
                    return !string.IsNullOrWhiteSpace(s) &&
                           s.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public void Stop()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(1500);
                }
            }
            catch
            {
            }
            finally
            {
                if (_process != null)
                {
                    _process.Dispose();
                    _process = null;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }
}
