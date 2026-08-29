using System;
using System.Collections.Generic;
using System.IO;
using UniversalConvert.Core.Plugins;

namespace UniversalConvert.Plugin.VlcVideo
{
    /// <summary>
    /// VLC 视频播放器扩展：接管主程序的视频预览（全格式播放 + 原生 seek 预览帧）。
    /// 需要宿主版本 ≥ 2.4.0（IVideoPreviewProvider 自该版本引入）。
    /// </summary>
    public sealed class VlcVideoPlugin : IConverterPlugin, IVideoPreviewProvider
    {
        public string Id => "com.universalconvert.vlcvideo";
        public string Name => "VLC 视频播放器";
        public string Description => "视频预览增强：基于 VLC 的全格式播放（mkv/webm/hevc/rmvb 等），拖拽进度条原生帧预览";
        public string Version => "1.0.0";
        public string MinAppVersion => "2.4.0";
        public string MaxAppVersion => null;
        public string Author => "UniversalConvert";

        public IReadOnlyList<ConversionCapability> GetCapabilities() => Array.Empty<ConversionCapability>();

        public bool CanPreviewVideo(string extension) => true;

        public bool ShowPreview(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
            var window = new VlcPreviewWindow(filePath);
            window.ShowDialog();
            return true;
        }
    }
}