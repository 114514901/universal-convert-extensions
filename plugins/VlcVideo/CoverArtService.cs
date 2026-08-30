using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UniversalConvert.Plugin.VlcVideo
{
    /// <summary>
    /// 封面加载：libvlc 的 ArtworkURL（file:/// URI）转本地路径，落盘可能晚于解析回调，轮询加载。
    /// </summary>
    internal static class CoverArtService
    {
        /// <summary>在 target 上显示封面（无则保持隐藏）。</summary>
        public static void ShowCover(Image target, string artworkUrl)
        {
            if (target == null || string.IsNullOrEmpty(artworkUrl)) return;

            var local = ToLocalPath(artworkUrl);
            PollLoad(target, local, 5, TimeSpan.FromMilliseconds(500));
        }

        private static string ToLocalPath(string artworkUrl)
        {
            try { return new Uri(artworkUrl).LocalPath; }
            catch { return artworkUrl; }
        }

        private static void PollLoad(Image target, string localPath, int attempts, TimeSpan interval)
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += (s, e) =>
            {
                if (Load(target, localPath) || --attempts <= 0)
                {
                    timer.Stop();
                }
            };
            timer.Start();
        }

        private static bool Load(Image target, string localPath)
        {
            try
            {
                if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return false;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(localPath);
                bmp.EndInit();
                bmp.Freeze();
                target.Source = bmp;
                target.Visibility = Visibility.Visible;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}