using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UniversalConvert.Core.Models;
using UniversalConvert.Core.Plugins;

namespace UniversalConvert.Plugin.VlcVideo
{
    /// <summary>
    /// VLC 视频播放器扩展：接管主程序的视频预览（全格式播放 + 原生 seek 预览帧）。
    /// 需要宿主版本 ≥ 2.4.0（IVideoPreviewProvider 自该版本引入）。
    /// </summary>
    public sealed class VlcVideoPlugin : IConverterPlugin, IMediaPreviewProvider2
    {
        public string Id => "com.universalconvert.vlcvideo";
        public string Name => "VLC 播放器";
        public string Description => "媒体预览增强：基于 VLC 的全格式播放（视频 mkv/webm/hevc/rmvb 及全部音频），拖拽进度条原生帧预览";
        public string Version => "1.2.8";
        public string MinAppVersion => "2.4.0-dev.9";
        public string MaxAppVersion => null;
        public string Author => "UniversalConvert";

        private IPluginContext _context;

        public void Initialize(IPluginContext context) { _context = context; }

        internal IPluginContext Context => _context;

        public bool IsToolAvailable() => true;

        public bool IsUntested => false;

        // 本扩展只提供视频预览，不注册任何格式转换
        public IList<ConversionCapability> GetCapabilities() => new List<ConversionCapability>();

        public Task<ConversionResult> ConvertAsync(
            ConversionRequest request, IProgress<ConversionProgress> progress, CancellationToken ct)
        {
            return Task.FromResult(ConversionResult.Failed("VLC 播放器扩展不提供格式转换", TimeSpan.Zero));
        }

        public bool CanPreviewVideo(string extension) => true;

        // VLC 全能解码：音频预览一并接管（避免内置音频兜底落到系统播放器）
        public bool CanPreviewAudio(string extension) => true;

        public bool ShowPreview(string filePath)
        {
            return ShowPreviewWithName(filePath, null);
        }

        public bool ShowPreviewWithName(string filePath, string displayName)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
            VlcPreviewWindow._pluginRef = new WeakReference(this);
            try
            {
                // 非模态：预览窗口不阻塞主界面（构造异常视为接管失败，回退内置预览）
                var window = new VlcPreviewWindow(filePath, displayName);
                window.Show();
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void Log(string message)
        {
            try { _context?.Log(message); } catch { }
        }
    }
}