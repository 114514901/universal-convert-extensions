using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace UniversalConvert.Plugin.VlcVideo
{
    /// <summary>
    /// VLC 视频预览窗口：直接播放任意格式；拖拽进度条 VLC 原生渲染 seek 帧；
    /// 双击画面播放/暂停，左右 1/3 单击 ±5 秒；音量指数曲线。
    /// </summary>
    public partial class VlcPreviewWindow : Window
    {
        private static readonly object InitializeLock = new object();
        private static bool _initialized;

        private readonly string _filePath;
        private VideoView VideoHost;
        internal static WeakReference _pluginRef;
        private Image _coverImage;
        private LibVLC _libVlc;
        private MediaPlayer _mp;
        private Media _media;
        private float _staticBitrateKbps = -1;
        private string _staticInfoSuffix = "";
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly DispatcherTimer _infoTimer = new DispatcherTimer();
        private bool _playing;
        private bool _wasPlayingBeforeSeek;
        private bool _seeking;
        private readonly DispatcherTimer _clickTimer = new DispatcherTimer();
        private bool _pendingClick;
        private int _pendingSeekSeconds;

        public VlcPreviewWindow(string filePath, string displayName = null)
        {
            InitializeComponent();
            _filePath = filePath;
            Title = "UniversalConvert";
            TitleText.Text = displayName ?? Path.GetFileName(filePath);
            _timer.Interval = TimeSpan.FromMilliseconds(200);
            _timer.Tick += OnTimerTick;
            _infoTimer.Interval = TimeSpan.FromMilliseconds(500);
            _infoTimer.Tick += OnInfoTimerTick;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureInitialized();
                // 动态创建 VideoView（XAML 引用会因程序集探测路径问题加载失败）
                VideoHost = new VideoView { Background = System.Windows.Media.Brushes.Black };
                VideoHost.PreviewMouseLeftButtonDown += OnVideoMouseLeftDown;
                HostGrid.Children.Add(VideoHost);

                // 封面层（音频预览显示内嵌封面；置于 VideoView 之上）
                _coverImage = new Image
                {
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    Visibility = Visibility.Collapsed
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(_coverImage, System.Windows.Media.BitmapScalingMode.HighQuality);
                HostGrid.Children.Add(_coverImage);

                _libVlc = new LibVLC();
                _mp = new MediaPlayer(_libVlc);
                VideoHost.MediaPlayer = _mp;
                // 显式初始音量对齐滑块 100%（libvlc 初始音量未设置时可能非 1.0，拉一下才变）
                ApplyVolume();

                _mp.Playing += OnPlaying;
                _mp.Paused += OnPaused;
                _mp.Stopped += OnStopped;
                _mp.EndReached += OnEndReached;
                _mp.TimeChanged += OnTimeChanged;
                _mp.LengthChanged += OnLengthChanged;

                // 持有 Media 以便异步解析元数据/封面（libvlc 自动导出内嵌封面到临时文件）
                _media = new Media(_libVlc, new Uri(_filePath));
                _media.ParsedChanged += OnMediaParsedChanged;
                _mp.Play(_media);
                _media.Parse(MediaParseOptions.ParseLocal);
                _playing = true;
                PlayPauseButton.Content = "暂停";
                _timer.Start();
                _infoTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("VLC 播放器初始化失败：" + ex.Message, "VLC 视频播放器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        }

        /// <summary>初始化 libvlc（进程内一次）：定位到本扩展目录下的 tools\libvlc.dll。</summary>
        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (InitializeLock)
            {
                if (_initialized) return;
                var dllDir = Path.GetDirectoryName(typeof(VlcVideoPlugin).Assembly.Location);
                var libDir = Path.Combine(dllDir ?? string.Empty, "tools");
                LibVLCSharp.Shared.Core.Initialize(libDir);
                _initialized = true;
            }
        }

        // LibVLCSharp 事件回调在 VLC 内部线程触发，所有 UI 更新必须回到 UI 线程。
        // 注意：此处不能访问任何依赖属性（如 IsLoaded）——非 UI 线程读取同样抛跨线程异常。
        private void OnUi(Action action)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { action(); }
                    catch { /* 窗口已关闭等场景：忽略 */ }
                }));
            }
            catch { }
        }

        // 元数据/封面解析完成（VLC 内部线程，UI 更新经 OnUi）
        private void OnMediaParsedChanged(object sender, MediaParsedChangedEventArgs e)
        {
            var status = e.ParsedStatus;
            OnUi(() =>
            {
                var plugin = _pluginRef?.Target as VlcVideoPlugin;
                if (plugin == null) return;
                if (_media == null)
                {
                    plugin.Log("VLC 封面解析：media 为空");
                    return;
                }
                plugin.Log(string.Format("VLC 封面解析：status={0}", status));
                if (status != MediaParsedStatus.Done && status != MediaParsedStatus.Skipped)
                {
                    plugin.Log("VLC 封面解析：跳过（非 Done/Skipped）");
                    return;
                }
                plugin.Log("VLC 封面解析：title=" + (_media.Meta(MetadataType.Title) ?? "(空)") +
                    ", artist=" + (_media.Meta(MetadataType.Artist) ?? "(空)"));
                var artwork = _media.Meta(MetadataType.ArtworkURL);
                plugin.Log("VLC 封面解析：artworkURL=" + (artwork ?? "(空)") +
                    ", 存在=" + (!string.IsNullOrEmpty(artwork) && File.Exists(artwork)));
                plugin.Log(string.Format("VLC 封面解析：videoTrackCount={0}", _mp == null ? -1 : _mp.VideoTrackCount));
                var title = _media.Meta(MetadataType.Title);
                var artist = _media.Meta(MetadataType.Artist);
                if (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
                {
                    var shown = string.Join(" - ", new[] { artist, title }.Where(x => !string.IsNullOrEmpty(x)));
                    if (!string.IsNullOrEmpty(shown))
                    {
                        MetaText.Text = shown;
                        MetaText.Visibility = Visibility.Visible;
                    }
                }

                // 音频（无视频轨）时显示内嵌封面
                if (_mp != null && _mp.VideoTrackCount <= 0)
                {
                    var art = _media.Meta(MetadataType.ArtworkURL);
                    if (!string.IsNullOrEmpty(art))
                    {
                        // ArtworkURL 为 file:/// URI 形式，需转本地路径
                        string local = art;
                        try { local = new Uri(art).LocalPath; } catch { }
                        TryShowCover(local);
                    }
                }
            });
        }

        /// <summary>
        /// 显示封面（libvlc 导出 artwork 可能晚于 ParsedChanged 落盘：轮询几次）。
        /// </summary>
        private void TryShowCover(string localPath)
        {
            var attempts = 5;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (s, e) =>
            {
                if (LoadCover(localPath) || --attempts <= 0)
                {
                    timer.Stop();
                }
            };
            timer.Start();
        }

        private bool LoadCover(string localPath)
        {
            try
            {
                if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return false;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(localPath);
                bmp.EndInit();
                bmp.Freeze();
                _coverImage.Source = bmp;
                _coverImage.Visibility = Visibility.Visible;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void OnPlaying(object sender, EventArgs e)
        {
            OnUi(() =>
            {
                PlayPauseButton.IsEnabled = true;
                if (_mp != null && _mp.Length > 0)
                {
                    ProgressSlider.Maximum = _mp.Length / 1000.0;
                    ProgressSlider.IsEnabled = true;
                }
            });
            LoadStreamInfoAsync();
        }

        private void OnPaused(object sender, EventArgs e)
        {
            _playing = false;
            OnUi(() => PlayPauseButton.Content = "播放");
        }

        private void OnStopped(object sender, EventArgs e)
        {
            _playing = false;
            OnUi(() => PlayPauseButton.Content = "播放");
        }

        private void OnEndReached(object sender, EventArgs e)
        {
            _playing = false;
            OnUi(() => PlayPauseButton.Content = "播放");
        }

        private void OnLengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        {
            OnUi(() =>
            {
                if (e.Length > 0)
                {
                    ProgressSlider.Maximum = e.Length / 1000.0;
                    ProgressSlider.IsEnabled = true;
                }
            });
        }

        private void OnTimeChanged(object sender, MediaPlayerTimeChangedEventArgs e)
        {
            OnUi(() =>
            {
                if (_seeking) return;
                var seconds = e.Time / 1000.0;
                if (seconds >= 0 && ProgressSlider.Maximum > 0)
                {
                    ProgressSlider.Value = seconds;
                }
                UpdateTimeText(e.Time / 1000.0);
                // 动态码率实时刷新（kHz/声道保持静态）
                if (InfoText != null)
                {
                    var br = CurrentBitrateKbps();
                    InfoText.Text = (br > 0 ? string.Format("{0:0} kbps", br) : "—") + _staticInfoSuffix;
                }
            });
        }

        private void OnInfoTimerTick(object sender, EventArgs e)
        {
            // 独立轮询刷新流信息（不依赖 TimeChanged 事件链）
            if (InfoText != null)
            {
                try
                {
                    var br = CurrentBitrateKbps();
                    InfoText.Text = (br > 0 ? string.Format("{0:0} kbps", br) : "—") + _staticInfoSuffix;
                }
                catch { }
            }
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (!_seeking && _mp != null)
            {
                UpdateTimeText(_mp.Time / 1000.0);
            }
        }

        private void UpdateTimeText(double seconds)
        {
            var pos = TimeSpan.FromSeconds(seconds);
            var total = _mp != null && _mp.Length > 0
                ? TimeSpan.FromMilliseconds(_mp.Length)
                : TimeSpan.Zero;
            TimeText.Text = string.Format("{0:hh\\:mm\\:ss} / {1:hh\\:mm\\:ss}", pos, total);
        }


        /// <summary>流信息：码率（kbps）/ 采样率（kHz）/ 声道。LibVLCSharp 3.x 无实时统计 API，
        /// 用宿主的 ffprobe 一次性探测；未知值显示 —。</summary>
        private void LoadStreamInfoAsync()
        {
            var plugin = _pluginRef?.Target as VlcVideoPlugin;
            var ffprobe = plugin?.Context?.FindTool("ffprobe");
            LogDebug("ffprobe 路径: " + (ffprobe ?? "(未找到)"));
            if (string.IsNullOrEmpty(ffprobe) || InfoText == null)
            {
                return;
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = "-v error -select_streams a:0 -show_entries stream=bit_rate,sample_rate,channels " +
                                "-of default=noprint_wrappers=1 " + Quote(_filePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        if (proc == null) return string.Empty;
                        var text = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(5000);
                        LogDebug("ffprobe exit=" + proc.ExitCode + " 输出=[" + text + "]");
                        return text;
                    }
                });

                _ = task.ContinueWith(t =>
                {
                    var text = t.Result ?? string.Empty;
                    var values = new System.Collections.Generic.Dictionary<string, string>();
                    foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            values[line.Substring(0, eq)] = line.Substring(eq + 1);
                        }
                    }

                    string bitrate = null, sampleRate = null, channels = null;
                    values.TryGetValue("bit_rate", out bitrate);
                    values.TryGetValue("sample_rate", out sampleRate);
                    values.TryGetValue("channels", out channels);

                    var parts = new System.Collections.Generic.List<string>();
                    long br;
                    long.TryParse(bitrate, out br);
                    _staticBitrateKbps = br > 0 ? br / 1000f : -1;
                    long sr;
                    long.TryParse(sampleRate, out sr);
                    parts.Add(sr > 0 ? string.Format("{0:0.#} kHz", sr / 1000.0) : "—");
                    long ch;
                    long.TryParse(channels, out ch);
                    if (ch == 1) parts.Add("单声道");
                    else if (ch == 2) parts.Add("立体声");
                    else if (ch > 2) parts.Add(string.Format("{0}声道", ch));
                    else parts.Add("—");

                    _staticInfoSuffix = parts.Count > 0 ? " · " + string.Join(" · ", parts) : "";
                    var text1 = (CurrentBitrateKbps() > 0 ? string.Format("{0:0} kbps", CurrentBitrateKbps()) : "—") + _staticInfoSuffix;
                    LogDebug(string.Format("解析完成: staticBitrate={0:0.#} suffix=[{1}] 显示=[{2}]",
                        _staticBitrateKbps, _staticInfoSuffix, text1));
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (InfoText != null) InfoText.Text = text1; } catch { }
                    }));
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
            catch
            {
                // 探测失败：保持 — 不打扰
            }
        }
        // ---------- 控制 ----------

        private void OnPlayPause(object sender, RoutedEventArgs e)
        {
            if (_mp == null) return;
            if (_playing)
            {
                _mp.Pause();
                _playing = false;
                PlayPauseButton.Content = "播放";
            }
            else
            {
                _mp.Play();
                _playing = true;
                PlayPauseButton.Content = "暂停";
            }
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            if (_mp == null) return;
            _mp.Stop();
            _playing = false;
            PlayPauseButton.Content = "播放";
            ProgressSlider.Value = 0;
            TimeText.Text = string.Empty;
        }

        // ---- libvlc 实时统计（LibVLCSharp 3.x 未封装，直接 P/Invoke） ----

        [StructLayout(LayoutKind.Sequential)]
        private struct MediaStats
        {
            public int ReadBytes;
            public float InputBitrate;
            public int DemuxReadBytes;
            public float DemuxBitrate;
            public int DemuxCorrupted;
            public int DemuxDiscontinuity;
            public int DecodedVideo;
            public int DecodedAudio;
            public int DisplayedPictures;
            public int LostPictures;
            public int LostABuffers;
            public int PlayedABuffers;
        }

        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_get_stats(IntPtr media, out MediaStats stats);

        /// <summary>实时码率（kbps）：优先 libvlc 统计的 demux bitrate（播放中动态变化），
        /// 不可用回退 ffprobe 静态音频码率。</summary>
        private float CurrentBitrateKbps()
        {
            try
            {
                if (_media != null)
                {
                    MediaStats st;
                    var ok = libvlc_media_get_stats(_media.NativeReference, out st);
                    if (ok != 0 && st.DemuxBitrate > 0)
                    {
                        return st.DemuxBitrate;
                    }
                    LogDebug(string.Format("stats: ok={0} demux={1:0.#} readBytes={2}", ok, st.DemuxBitrate, st.ReadBytes));
                }
                else
                {
                    LogDebug("stats: media 为空");
                }
            }
            catch (Exception ex)
            {
                LogDebug("stats P/Invoke 异常: " + ex);
            }
            LogDebug(string.Format("stats 回落静态码率: {0:0.#} kbps", _staticBitrateKbps));
            return _staticBitrateKbps;
        }

        private DateTime _lastDebugLog;
        /// <summary>debug 日志（节流：至少间隔 3 秒，避免刷屏）。</summary>
        private void LogDebug(string message)
        {
            var now = DateTime.Now;
            if ((now - _lastDebugLog).TotalSeconds < 3) return;
            _lastDebugLog = now;
            try
            {
                var plugin = _pluginRef?.Target as VlcVideoPlugin;
                plugin?.Log("VLC debug: " + message);
            }
            catch { }
        }

        private static string Quote(string path)
        {
            return "\"" + path.Replace("\"", "\\\"") + "\"";
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ---------- 进度条：拖拽时临时暂停，VLC 原生渲染 seek 帧 ----------

        private void OnProgressPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _seeking = true;
            _wasPlayingBeforeSeek = _playing;
            if (_playing)
            {
                _mp.Pause();
                _playing = false;
                PlayPauseButton.Content = "播放";
            }
        }

        private void OnProgressPreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _seeking = false;
            if (_mp != null)
            {
                _mp.Time = (long)(ProgressSlider.Value * 1000);
                if (_wasPlayingBeforeSeek)
                {
                    _mp.Play();
                    _playing = true;
                    PlayPauseButton.Content = "暂停";
                }
            }
        }

        private void OnProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_seeking || _mp == null) return;
            // 拖动中：VLC 暂停态可直接渲染 seek 帧
            _mp.Time = (long)(ProgressSlider.Value * 1000);
            UpdateTimeText(ProgressSlider.Value);
        }

        // ---------- 画面：双击任意位置播放/暂停，左右 1/3 单击 ±5 秒 ----------
        // 单击延迟 300ms 执行（等待可能的双击）；300ms 内再次点击 → 播放/暂停

        private void OnVideoMouseLeftDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_mp == null || VideoHost.ActualWidth <= 0) return;

            if (_pendingClick && _clickTimer.IsEnabled)
            {
                _clickTimer.Stop();
                _pendingClick = false;
                OnPlayPause(sender, e);
                e.Handled = true;
                return;
            }

            var x = e.GetPosition(VideoHost).X;
            var region = x / VideoHost.ActualWidth;
            _pendingClick = false;
            _pendingSeekSeconds = region < 1.0 / 3.0 ? -5 : region > 2.0 / 3.0 ? 5 : 0;
            StartClickTimer();
            e.Handled = true;
        }

        private void StartClickTimer()
        {
            _pendingClick = true;
            _clickTimer.Interval = TimeSpan.FromMilliseconds(300);
            _clickTimer.Tick -= OnClickTimerTick;
            _clickTimer.Tick += OnClickTimerTick;
            _clickTimer.Stop();
            _clickTimer.Start();
        }

        private void OnClickTimerTick(object sender, EventArgs e)
        {
            _clickTimer.Stop();
            if (!_pendingClick) return;
            _pendingClick = false;
            if (_pendingSeekSeconds != 0)
            {
                SeekRelative(_pendingSeekSeconds);
            }
        }

        private void SeekRelative(int seconds)
        {
            var target = _mp.Time + seconds * 1000L;
            if (target < 0) target = 0;
            if (_mp.Length > 0 && target > _mp.Length) target = _mp.Length;
            _mp.Time = target;
            ProgressSlider.Value = target / 1000.0;
            UpdateTimeText(target / 1000.0);
        }

        // ---------- 音量（指数曲线，人耳对数感知） ----------

        private void ApplyVolume()
        {
            if (_mp != null)
            {
                _mp.Volume = (int)(VolumeToAmplitude(VolumeSlider.Value) * 100);
            }
            if (VolumeText != null)
            {
                VolumeText.Text = string.Format("{0:0}%", VolumeSlider.Value * 100);
            }
        }

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ApplyVolume();
        }

        private static double VolumeToAmplitude(double sliderValue)
        {
            if (sliderValue <= 0) return 0;
            if (sliderValue >= 1) return 1;
            return Math.Pow(sliderValue, 2.0);
        }

        // ---------- 关闭清理 ----------

        private void OnClosed(object sender, EventArgs e)
        {
            _timer.Stop();
            _infoTimer.Stop();
            try
            {
                if (_mp != null)
                {
                    _mp.Stop();
                    _mp.Dispose();
                    _mp = null;
                }
                if (_media != null)
                {
                    _media.Dispose();
                    _media = null;
                }
                if (_libVlc != null)
                {
                    _libVlc.Dispose();
                    _libVlc = null;
                }
            }
            catch { }
        }
    }
}