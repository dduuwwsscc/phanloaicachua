using OpenCvSharp;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace phanLoaiCaChua
{
    internal sealed class AiTomatoClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _classifyUrl;
        private bool _disposed;

        public AiTomatoClient(string classifyUrl)
        {
            _classifyUrl = classifyUrl;
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromMilliseconds(12000);
        }

        public string CheckHealth(string healthUrl)
        {
            try
            {
                string text = _http.GetStringAsync(healthUrl).GetAwaiter().GetResult();
                return "OK: " + text;
            }
            catch (Exception ex)
            {
                return "FAIL: " + ex.Message;
            }
        }

        public AiTomatoResult Analyze(Mat frame)
        {
            if (frame == null || frame.Empty())
                return AiTomatoResult.Fail("Frame rỗng");

            try
            {
                byte[] jpgBytes;
                Cv2.ImEncode(".jpg", frame, out jpgBytes, new int[]
                {
                    (int)ImwriteFlags.JpegQuality, 65
                });

                using (var content = new MultipartFormDataContent())
                using (var fileContent = new ByteArrayContent(jpgBytes))
                {
                    fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
                    content.Add(fileContent, "image", "frame.jpg");

                    HttpResponseMessage resp = _http.PostAsync(_classifyUrl, content).GetAwaiter().GetResult();
                    string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!resp.IsSuccessStatusCode)
                        return AiTomatoResult.Fail("AI server lỗi HTTP " + (int)resp.StatusCode + ": " + text);

                    return AiTomatoResult.Parse(text);
                }
            }
            catch (TaskCanceledException)
            {
                return AiTomatoResult.Fail("AI timeout");
            }
            catch (Exception ex)
            {
                return AiTomatoResult.Fail("AI offline: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _http?.Dispose();
            _disposed = true;
        }
    }

    internal sealed class AiTomatoResult
    {
        public bool Success { get; private set; }
        public bool Found { get; private set; }
        public int X { get; private set; }
        public int Y { get; private set; }
        public int W { get; private set; }
        public int H { get; private set; }
        public string Label { get; private set; }
        public string DisplayLabel { get; private set; }
        public float Confidence { get; private set; }
        public string ErrorMessage { get; private set; }

        public static AiTomatoResult Fail(string error)
        {
            return new AiTomatoResult
            {
                Success = false,
                ErrorMessage = error,
                Label = "none",
                DisplayLabel = "Không xác định"
            };
        }


        public static AiTomatoResult FromValues(bool success, bool found, int x, int y, int w, int h, string label, string displayLabel, float confidence, string errorMessage)
        {
            return new AiTomatoResult
            {
                Success = success,
                Found = found,
                X = x,
                Y = y,
                W = w,
                H = h,
                Label = label,
                DisplayLabel = displayLabel,
                Confidence = confidence,
                ErrorMessage = errorMessage
            };
        }

        public static AiTomatoResult Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Fail("AI trả rỗng");

            string[] parts = raw.Split(new[] { '|' }, 9);
            if (parts.Length < 9)
                return Fail("AI trả sai định dạng: " + raw);

            bool success = parts[0] == "1";
            bool found = parts[1] == "1";

            int x, y, w, h;
            float conf;
            int.TryParse(parts[2], out x);
            int.TryParse(parts[3], out y);
            int.TryParse(parts[4], out w);
            int.TryParse(parts[5], out h);
            float.TryParse(parts[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out conf);

            string label = parts[6] ?? "none";
            string displayLabel = parts[8] ?? label;

            return new AiTomatoResult
            {
                Success = success,
                Found = found,
                X = x,
                Y = y,
                W = w,
                H = h,
                Label = label,
                DisplayLabel = displayLabel,
                Confidence = conf,
                ErrorMessage = success ? string.Empty : displayLabel
            };
        }
    }
}
