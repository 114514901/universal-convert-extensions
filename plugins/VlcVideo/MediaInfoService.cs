using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UniversalConvert.Plugin.VlcVideo
{
    /// <summary>
    /// 媒体流信息服务：采样率/声道（ffprobe 静态）+ 码率（ffprobe 包级时间线，1 秒滑动窗口动态 VBR）。
    /// 完全脱离 UI：计算结果经回调（需由调用方切到 UI 线程）。
    /// </summary>
    internal sealed class MediaInfoService
    {
        private readonly string _ffprobe;
        private readonly string _filePath;
        private readonly Func<double> _currentSeconds;
        private readonly Action<string> _onInfoText;

        private BitrateTimeline _timeline;
        private float _staticBitrateKbps = -1;
        private string _staticSuffix = "";

        public MediaInfoService(string ffprobe, string filePath, Func<double> currentSeconds, Action<string> onInfoText)
        {
            _ffprobe = ffprobe;
            _filePath = filePath;
            _currentSeconds = currentSeconds;
            _onInfoText = onInfoText;
        }

        /// <summary>启动后台探测（静态流信息 + 码率时间线）。</summary>
        public void Start()
        {
            Task.Run(() =>
            {
                LoadStaticStreamInfo();
                LoadBitrateTimeline();
                Publish();
            });
        }

        /// <summary>播放中定时调用：按当前时间刷新动态码率。</summary>
        public void Refresh()
        {
            Publish();
        }

        private void Publish()
        {
            var kbps = CurrentBitrateKbps();
            var text = (kbps > 0 ? string.Format("{0} kbps", kbps) : "—") + _staticSuffix;
            try { _onInfoText(text); } catch { }
        }

        private float CurrentBitrateKbps()
        {
            try
            {
                if (_timeline != null)
                {
                    var br = _timeline.GetBitrateKbps(_currentSeconds());
                    if (br > 0) return br;
                }
            }
            catch { }
            return _staticBitrateKbps;
        }

        private void LoadStaticStreamInfo()
        {
            try
            {
                var text = RunFfprobe(
                    "-v error -select_streams a:0 -show_entries stream=bit_rate,sample_rate,channels " +
                    "-of default=noprint_wrappers=1 " + Quote(_filePath), 5000);
                if (string.IsNullOrEmpty(text)) return;

                var values = ParseKeyValues(text);

                long br, sr, ch;
                long.TryParse(GetValue(values, "bit_rate"), out br);
                long.TryParse(GetValue(values, "sample_rate"), out sr);
                long.TryParse(GetValue(values, "channels"), out ch);

                _staticBitrateKbps = br > 0 ? br / 1000f : -1;

                var parts = new List<string>();
                parts.Add(sr > 0 ? string.Format("{0:0.#} kHz", sr / 1000.0) : "—");
                if (ch == 1) parts.Add("单声道");
                else if (ch == 2) parts.Add("立体声");
                else if (ch > 2) parts.Add(string.Format("{0}声道", ch));
                else parts.Add("—");

                _staticSuffix = " · " + string.Join(" · ", parts);
            }
            catch { }
        }

        private void LoadBitrateTimeline()
        {
            try
            {
                var json = RunFfprobe(
                    "-v error -select_streams a:0 -show_entries packet=pts_time,size -of json " + Quote(_filePath),
                    15000);
                var timeline = BitrateTimeline.Parse(json);
                if (timeline != null && timeline.IsValid)
                {
                    _timeline = timeline;
                }
            }
            catch { }
        }

        private string RunFfprobe(string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffprobe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var proc = Process.Start(psi))
            {
                if (proc == null) return string.Empty;
                var text = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(timeoutMs);
                return text;
            }
        }

        private static Dictionary<string, string> ParseKeyValues(string text)
        {
            var result = new Dictionary<string, string>();
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) result[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return result;
        }

        private static string GetValue(Dictionary<string, string> map, string key)
        {
            string v;
            return map.TryGetValue(key, out v) ? v : null;
        }

        private static string Quote(string path)
        {
            return "\"" + path.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>ffprobe 包级码率时间线：累计字节曲线，按时间差估算 1 秒滑动瞬时码率。</summary>
        private sealed class BitrateTimeline
        {
            private readonly List<double> _times = new List<double>();
            private readonly List<long> _cumBytes = new List<long>();

            public bool IsValid => _times.Count > 0;

            public void Add(double time, long cumulativeBytes)
            {
                _times.Add(time);
                _cumBytes.Add(cumulativeBytes);
            }

            public int GetBitrateKbps(double seconds)
            {
                if (!IsValid) return 0;
                int end = FindIndex(seconds);
                if (end < 0) return 0;
                int start = FindIndex(seconds - 1.0);
                if (start < 0) start = 0;

                double dt = _times[end] - _times[start];
                long db = _cumBytes[end] - _cumBytes[start];
                if (dt <= 0) return 0;
                return (int)(db * 8.0 / dt / 1000.0);
            }

            private int FindIndex(double seconds)
            {
                int lo = 0, hi = _times.Count - 1, ans = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (_times[mid] <= seconds) { ans = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                return ans;
            }

            public static BitrateTimeline Parse(string json)
            {
                if (string.IsNullOrEmpty(json)) return null;

                var timeline = new BitrateTimeline();
                var regex = new Regex(
                    "\"pts_time\":\\s*([0-9.]+)[^}]*?\"size\":\\s*(\\d+)",
                    RegexOptions.Singleline);
                long cum = 0;
                foreach (Match m in regex.Matches(json))
                {
                    double time;
                    long size;
                    if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out time)) continue;
                    if (!long.TryParse(m.Groups[2].Value, out size)) continue;
                    cum += size;
                    timeline.Add(time, cum);
                }
                return timeline;
            }
        }
    }
}