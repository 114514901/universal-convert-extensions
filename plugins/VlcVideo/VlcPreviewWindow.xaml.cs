using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

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
        private LibVLC _libVlc;
        private MediaPlayer _mp;
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
                _libVlc = new LibVLC();
                _mp = new MediaPlayer(_libVlc);
                VideoHost.MediaPlayer = _mp;

                _mp.Playing += OnPlaying;
                _mp.Paused += (s, args) => { _playing = false; PlayPauseButton.Content = "播放"; };
                _mp.Stop += (s, args) => { _playing = false; PlayPauseButton.Content = "播放"; };
                _mp.EndReached += (s, args) => { _playing = false; PlayPauseButton.Content = "播放"; };
                _mp.TimeChanged += OnTimeChanged;
                _mp.LengthChanged += OnLengthChanged;

                using (var media = new Media(_libVlc, new Uri(_filePath)))
                {
                    _mp.Play(media);
                }
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
                Core.Initialize(libDir);
                _initialized = true;
            }
        }

        private void OnPlaying(object sender, EventArgs e)
        {
            PlayPauseButton.IsEnabled = true;
            if (_mp.Length > 0)
            {
                ProgressSlider.Maximum = _mp.Length / 1000.0;
                ProgressSlider.IsEnabled = true;
            }
        }

        private void OnLengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        {
            if (e.Length > 0)
            {
                ProgressSlider.Maximum = e.Length / 1000.0;
                ProgressSlider.IsEnabled = true;
            }
        }

        private void OnTimeChanged(object sender, MediaPlayerTimeChangedEventArgs e)
        {
            if (_seeking) return;
            var seconds = e.Time / 1000.0;
            if (seconds >= 0 && ProgressSlider.Maximum > 0)
            {
                ProgressSlider.Value = seconds;
            }
            UpdateTimeText(e.Time / 1000.0);
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

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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