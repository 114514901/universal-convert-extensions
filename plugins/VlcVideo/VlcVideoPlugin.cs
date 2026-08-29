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
    public sealed class VlcVideoPlugin : IConverterPlugin, IMediaPreviewProvider
    {
        public string Id => "com.universalconvert.vlcvideo";
        public string Name => "VLC 播放器";
        public string Description => "媒体预览增强：基于 VLC 的全格式播放（视频 mkv/webm/hevc/rmvb 及全部音频），拖拽进度条原生帧预览";
        public string Version => "1.0.7";
        public string MinAppVersion => "2.4.0-dev.9";
        public string MaxAppVersion => null;
        public string Author => "UniversalConvert";

        private IPluginContext _context;

        public void Initialize(IPluginContext context) { _context = context; }

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
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
            var window = new VlcPreviewWindow(filePath);
            window.ShowDialog();
            return true;
        }
    }
}