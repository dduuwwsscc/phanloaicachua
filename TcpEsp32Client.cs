using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace phanLoaiCaChua
{
    public sealed class TcpEsp32Client : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private Task _rxTask;

        public bool IsConnected => _client != null && _client.Connected;

        public event Action<string> LogMessage;
        public event Action<bool> ConnectionChanged;
        public event Action<string> DataReceived;

        public async Task<bool> ConnectAsync(string ip, int port)
        {
            Disconnect();

            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(ip, port);
                _stream = _client.GetStream();
                _cts = new CancellationTokenSource();
                _rxTask = Task.Run(() => ReceiveLoop(_cts.Token));

                RaiseLog($"Đã kết nối TCP tới {ip}:{port}");
                ConnectionChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                RaiseLog("Kết nối thất bại: " + ex.Message);
                Disconnect();
                return false;
            }
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            byte[] buffer = new byte[2048];

            try
            {
                while (!token.IsCancellationRequested && _stream != null)
                {
                    int read = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (read <= 0)
                        break;

                    string text = Encoding.UTF8.GetString(buffer, 0, read);
                    DataReceived?.Invoke(text);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException ex)
            {
                RaiseLog("Mất kết nối: " + ex.Message);
            }
            catch (Exception ex)
            {
                RaiseLog("Receive lỗi: " + ex.Message);
            }
            finally
            {
                Disconnect();
            }
        }

        public async Task<bool> SendAsync(string text)
        {
            if (!IsConnected || _stream == null)
                return false;

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(text);
                await _stream.WriteAsync(data, 0, data.Length);
                return true;
            }
            catch (Exception ex)
            {
                RaiseLog("Send lỗi: " + ex.Message);
                Disconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            try { _cts?.Cancel(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }

            _cts = null;
            _stream = null;
            _client = null;
            ConnectionChanged?.Invoke(false);
        }

        private void RaiseLog(string msg)
        {
            LogMessage?.Invoke(msg);
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
