using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace UniversalConvert.Plugin.VlcVideo
{
    /// <summary>
    /// VLC 预览窗口：全格式播放、拖拽进度条原生 seek 帧、双击播放/暂停、左右 1/3 单击 ±5 秒、
    /// 指数音量、音频封面与流信息（码率/采样率/声道）。
    /// 复杂逻辑拆到 MediaInfoService / CoverArtService。
    /// </summary>
    public partial class VlcPreviewWindow : Window
    {
        private static readonly object InitializeLock = new object();
        private static bool _libVlcInitialized;

        internal static WeakReference PluginRef;

        private readonly string _filePath;
        private readonly string _displayName;

        private VideoView _videoHost;
        private Image _coverImage;
        private LibVLC _libVlc;
        private MediaPlayer _mp;
        private Media _media;
        private MediaInfoService _mediaInfo;

        private readonly DispatcherTimer _uiTimer = new DispatcherTimer();
        private readonly DispatcherTimer _infoTimer = new DispatcherTimer();
        private readonly DispatcherTimer _clickTimer = new DispatcherTimer();

        private bool _ready;
        private bool _playing;
        private bool _seeking;
        private bool _wasPlayingBeforeSeek;
        private bool _pendingClick;
        private int _pendingSeekSeconds;

        public VlcPreviewWindow(string filePath, string displayName = null)
        {
            InitializeComponent();
            _filePath = filePath;
            _displayName = displayName;
            Title = "UniversalConvert";
            TitleText.Text = displayName ?? Path.GetFileName(filePath);

            _uiTimer.Interval = TimeSpan.FromMilliseconds(200);
            _uiTimer.Tick += OnUiTimerTick;
            _infoTimer.Interval = TimeSpan.FromMilliseconds(500);
            _infoTimer.Tick += OnInfoTimerTick;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureLibVlcInitialized();
                BuildMediaElements();
                StartPlayback();
                _ready = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("VLC 播放器初始化失败：" + ex.Message, "VLC 播放器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        }

        // ---------- 初始化 ----------

        private static void EnsureLibVlcInitialized()
        {
            if (_libVlcInitialized) return;
            lock (InitializeLock)
            {
                if (_libVlcInitialized) return;
                var dllDir = Path.GetDirectoryName(typeof(VlcVideoPlugin).Assembly.Location);
                LibVLCSharp.Shared.Core.Initialize(Path.Combine(dllDir ?? string.Empty, "tools"));
                _libVlcInitialized = true;
            }
        }

        private void BuildMediaElements()
        {
            // VideoView 代码动态创建（XAML 引用扩展目录程序集在 BAML 加载时无法解析）
            _videoHost = new VideoView { Background = System.Windows.Media.Brushes.Black };
            _videoHost.PreviewMouseLeftButtonDown += OnVideoMouseLeftDown;
            HostGrid.Children.Add(_videoHost);

            // 封面层（音频预览显示内嵌封面，置于 VideoView 之上）
            _coverImage = new Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                Visibility = Visibility.Collapsed
            };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                _coverImage, System.Windows.Media.BitmapScalingMode.HighQuality);
            HostGrid.Children.Add(_coverImage);
        }

        private void StartPlayback()
        {
            _libVlc = new LibVLC();
            _mp = new MediaPlayer(_libVlc);
            _videoHost.MediaPlayer = _mp;

            _mp.Playing += OnPlaying;
            _mp.Paused += OnPaused;
            _mp.Stopped += OnStopped;
            _mp.EndReached += OnEndReached;
            _mp.TimeChanged += OnTimeChanged;
            _mp.LengthChanged += OnLengthChanged;

            // 音量：恢复上次记录（0-1）；无记录默认满音量
            var savedVolume = LoadSavedVolume();
            if (savedVolume.HasValue)
            {
                VolumeSlider.Value = savedVolume.Value;
            }
            else
            {
                ApplyVolume();
            }

            _media = new Media(_libVlc, new Uri(_filePath));
            _media.ParsedChanged += OnMediaParsedChanged;
            _mp.Play(_media);
            _media.Parse(MediaParseOptions.ParseLocal);

            _playing = true;
            PlayPauseButton.Content = "暂停";
            _uiTimer.Start();
            _infoTimer.Start();
        }

        // ---------- 线程调度 ----------

        /// <summary>LibVLCSharp 事件在 VLC 内部线程触发，UI 更新统一切回 UI 线程。
        /// 注意不能在此访问依赖属性（如 IsLoaded）——非 UI 线程读取同样抛异常。</summary>
        private void OnUi(Action action)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { action(); } catch { }
                }));
            }
            catch { }
        }

        // ---------- 元数据 / 封面 ----------

        private void OnMediaParsedChanged(object sender, MediaParsedChangedEventArgs e)
        {
            if (e.ParsedStatus != MediaParsedStatus.Done && e.ParsedStatus != MediaParsedStatus.Skipped) return;

            OnUi(() =>
            {
                if (_media == null) return;

                // 第二行：艺术家 - 标题（无则不显示）
                var artist = _media.Meta(MetadataType.Artist);
                var title = _media.Meta(MetadataType.Title);
                var shown = string.Join(" - ", new[] { artist, title }.Where(x => !string.IsNullOrEmpty(x)));
                if (!string.IsNullOrEmpty(shown))
                {
                    MetaText.Text = shown;
                    MetaText.Visibility = Visibility.Visible;
                }

                // 音频（无视频轨）显示内嵌封面
                if (_mp != null && _mp.VideoTrackCount <= 0)
                {
                    CoverArtService.ShowCover(_coverImage, _media.Meta(MetadataType.ArtworkURL));
                }
            });
        }

        // ---------- 播放状态事件 ----------

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
            StartMediaInfo();
        }

        private void OnPaused(object sender, EventArgs e) { OnPlaybackStateChanged(false); }
        private void OnStopped(object sender, EventArgs e) { OnPlaybackStateChanged(false); }
        private void OnEndReached(object sender, EventArgs e) { OnPlaybackStateChanged(false); }

        private void OnPlaybackStateChanged(bool playing)
        {
            _playing = playing;
            OnUi(() => PlayPauseButton.Content = playing ? "暂停" : "播放");
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
                UpdateTimeText(seconds);
            });
        }

        // ---------- 流信息（码率/采样率/声道） ----------

        private void StartMediaInfo()
        {
            if (_mediaInfo != null) return;
            var plugin = PluginRef?.Target as VlcVideoPlugin;
            var ffprobe = plugin?.Context?.FindTool("ffprobe");
            if (string.IsNullOrEmpty(ffprobe)) return;

            _mediaInfo = new MediaInfoService(
                ffprobe, _filePath,
                () => _mp != null ? _mp.Time / 1000.0 : 0.0,
                text => OnUi(() => { if (InfoText != null) InfoText.Text = text; }));
            _mediaInfo.Start();
        }

        private void OnInfoTimerTick(object sender, EventArgs e)
        {
            _mediaInfo?.Refresh();
        }

        private void OnUiTimerTick(object sender, EventArgs e)
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

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ---------- 进度条：拖拽临时暂停，VLC 原生渲染 seek 帧 ----------

        private void OnProgressPreviewMouseDown(object sender, MouseButtonEventArgs e)
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

        private void OnProgressPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _seeking = false;
            if (_mp == null) return;
            _mp.Time = (long)(ProgressSlider.Value * 1000);
            if (_wasPlayingBeforeSeek)
            {
                _mp.Play();
                _playing = true;
                PlayPauseButton.Content = "暂停";
            }
        }

        private void OnProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_seeking || _mp == null) return;
            _mp.Time = (long)(ProgressSlider.Value * 1000);
            UpdateTimeText(ProgressSlider.Value);
        }

        // ---------- 画面快捷操作：双击播放/暂停，左右 1/3 单击 ±5 秒 ----------

        private void OnVideoMouseLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (_mp == null || _videoHost.ActualWidth <= 0) return;

            if (_pendingClick && _clickTimer.IsEnabled)
            {
                _clickTimer.Stop();
                _pendingClick = false;
                OnPlayPause(sender, e);
                e.Handled = true;
                return;
            }

            var x = e.GetPosition(_videoHost).X;
            var region = x / _videoHost.ActualWidth;
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

        // ---------- 音量（指数曲线） ----------

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
            // 仅窗口就绪（OnLoaded 完成后）持久化，避免 XAML 初始化/恢复阶段误写
            if (_ready)
            {
                SaveVolume(VolumeSlider.Value);
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

        // ---------- 音量记忆（与内置播放器共享同一配置文件） ----------

        private static string VolumeFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UniversalConvert", "preview-volume.txt");

        private static double? LoadSavedVolume()
        {
            try
            {
                var path = VolumeFilePath;
                if (!File.Exists(path)) return null;
                double v;
                var text = File.ReadAllText(path).Trim();
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v)) return null;
                if (v < 0) v = 0;
                if (v > 1) v = 1;
                return v;
            }
            catch { return null; }
        }

        private static void SaveVolume(double value)
        {
            try
            {
                var path = VolumeFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
        }

        // ---------- 关闭清理 ----------

        private void OnClosed(object sender, EventArgs e)
        {
            _uiTimer.Stop();
            _infoTimer.Stop();
            _clickTimer.Stop();
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