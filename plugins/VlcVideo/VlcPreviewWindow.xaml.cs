using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        private Image _coverImage;
        private LibVLC _libVlc;
        private MediaPlayer _mp;
        private Media _media;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private bool _playing;
        private bool _wasPlayingBeforeSeek;
        private bool _seeking;
        private readonly DispatcherTimer _clickTimer = new DispatcherTimer();
        private bool _pendingClick;
        private int _pendingSeekSeconds;

        public VlcPreviewWindow(string filePath)
        {
            InitializeComponent();
            _filePath = filePath;
            Title = "UniversalConvert";
            _timer.Interval = TimeSpan.FromMilliseconds(200);
            _timer.Tick += OnTimerTick;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TitleText.Text = Path.GetFileName(_filePath);

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
                    Visibility = Visibility.Collapsed,
                    RenderOptions = { BitmapScalingMode = System.Windows.Media.BitmapScalingMode.HighQuality }
                };
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
            OnUi(() =>
            {
                if (_media == null || e.ParsedStatus != MediaParsedStatus.Done && e.ParsedStatus != MediaParsedStatus.Skipped)
                {
                    return;
                }
                var title = _media.Meta(MetadataType.Title);
                var artist = _media.Meta(MetadataType.Artist);
                if (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
                {
                    var shown = string.Join(" - ", new[] { artist, title }.Where(x => !string.IsNullOrEmpty(x)));
                    if (!string.IsNullOrEmpty(shown))
                    {
                        TitleText.Text = shown;
                    }
                }

                // 音频（无视频轨）时显示内嵌封面
                if (_mp != null && _mp.VideoTracksCount <= 0)
                {
                    try
                    {
                        var art = _media.Meta(MetadataType.ArtworkURL);
                        if (!string.IsNullOrEmpty(art) && File.Exists(art))
                        {
                            var bmp = new System.Windows.Media.Imaging.BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.UriSource = new Uri(art);
                            bmp.EndInit();
                            bmp.Freeze();
                            _coverImage.Source = bmp;
                            _coverImage.Visibility = Visibility.Visible;
                        }
                    }
                    catch { }
                }
            });
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
            });
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