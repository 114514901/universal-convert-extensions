using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniversalConvert.Core.Models;
using UniversalConvert.Core.Plugins;
using UniversalConvert.Core.Process;

namespace UniversalConvert.Plugin.ImageMagick
{
    /// <summary>
    /// 图像格式转换与处理插件，基于 ImageMagick（magick.exe，工具随包分发在 tools\）。
    ///
    /// 覆盖 200+ 格式：常规图片（与内置 FFmpeg 重叠时由应用弹窗让用户选择用哪个）、
    /// ICO/CUR/SVG/PSD/TIFF/EXR/HDR 等 FFmpeg 不覆盖的格式、以及相机 RAW（cr2/nef/arw/dng 等，
    /// 依赖官方二进制的 libraw delegate）。
    /// </summary>
    public sealed class ImageMagickPlugin : ExternalToolConverterBase
    {
        public override string Id => "com.universalconvert.imagemagick";
        public override string Name => "ImageMagick";
        public override string Description => "图像格式转换与处理（ICO/SVG/PSD/TIFF/相机 RAW 等 200+ 格式），工具随包分发";
        public override string Version => "1.0.0";
        protected override string ToolName => "magick";

        private static readonly string[] ImageInputs =
        {
            // 常规（与内置 FFmpeg 重叠，冲突时由应用弹窗选择）
            ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tiff", ".tif",
            ".heic", ".heif", ".avif",
            // ImageMagick 特有
            ".ico", ".cur", ".svg", ".psd", ".tga", ".pcx", ".xbm", ".xpm",
            ".pbm", ".pgm", ".ppm", ".pnm", ".dds", ".exr", ".hdr", ".sgi", ".ras",
            ".jng", ".miff", ".palm", ".pict", ".wpg", ".mng",
            // 相机 RAW（官方二进制带 libraw delegate 时可用）
            ".cr2", ".crw", ".nef", ".nrw", ".arw", ".dng", ".orf", ".rw2", ".raf", ".pef", ".srw", ".x3f"
        };

        private static readonly string[] ImageOutputs =
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tiff", ".tif",
            ".ico", ".svg", ".psd", ".tga", ".pcx", ".xbm", ".xpm",
            ".pbm", ".pgm", ".ppm", ".pnm", ".dds", ".exr", ".hdr", ".sgi",
            ".avif", ".heic", ".heif", ".pdf"
        };

        public override IList<ConversionCapability> GetCapabilities()
        {
            var caps = new List<ConversionCapability>();
            foreach (var input in ImageInputs)
            {
                caps.Add(new ConversionCapability
                {
                    InputExtension = input,
                    InputDisplayName = GetDisplayName(input),
                    Outputs = ImageOutputs
                        .Where(o => !string.Equals(o, input, StringComparison.OrdinalIgnoreCase))
                        .Select(ext => new OutputFormat
                        {
                            Extension = ext,
                            DisplayName = ext.TrimStart('.').ToUpperInvariant() + " 图片",
                            Options = new List<OptionDefinition>
                            {
                                new OptionDefinition
                                {
                                    Key = "quality",
                                    Label = "质量",
                                    Type = OptionType.Int,
                                    DefaultValue = "90"
                                },
                                new OptionDefinition
                                {
                                    Key = "resize",
                                    Label = "缩放",
                                    Type = OptionType.Enum,
                                    DefaultValue = "100%",
                                    Choices = new List<OptionChoice>
                                    {
                                        new OptionChoice { Value = "100%", Label = "原始尺寸（默认）" },
                                        new OptionChoice { Value = "50%", Label = "50%" },
                                        new OptionChoice { Value = "25%", Label = "25%" },
                                        new OptionChoice { Value = "10%", Label = "10%" }
                                    }
                                }
                            }
                        })
                        .ToList()
                });
            }
            return caps;
        }

        protected override string BuildArguments(ConversionRequest request, string outputPath)
        {
            var sb = new StringBuilder();
            sb.Append(ProcessRunner.Quote(request.InputPath));

            // 质量（0-100；jpeg/png/ico 等格式有效）
            string quality;
            if (request.Options != null && request.Options.TryGetValue("quality", out quality)
                && !string.IsNullOrEmpty(quality))
            {
                sb.Append(" -quality ").Append(quality);
            }

            // 缩放（百分比）
            string resize;
            if (request.Options != null && request.Options.TryGetValue("resize", out resize)
                && !string.IsNullOrEmpty(resize) && resize != "100%")
            {
                sb.Append(" -resize ").Append(resize);
            }

            sb.Append(" ").Append(ProcessRunner.Quote(outputPath));
            return sb.ToString();
        }

        private static string GetDisplayName(string ext)
        {
            switch (ext)
            {
                case ".ico": return "ICO 图标";
                case ".cur": return "CUR 光标";
                case ".svg": return "SVG 矢量图";
                case ".psd": return "PSD 图像";
                case ".tga": return "TGA 图像";
                case ".pcx": return "PCX 图像";
                case ".xbm": return "XBM 位图";
                case ".xpm": return "XPM 位图";
                case ".pbm":
                case ".pgm":
                case ".ppm":
                case ".pnm": return "PNM 位图";
                case ".dds": return "DDS 纹理";
                case ".exr": return "EXR 图像";
                case ".hdr": return "HDR 图像";
                case ".sgi": return "SGI 图像";
                case ".ras": return "SUN Raster 图像";
                case ".jng": return "JNG 图像";
                case ".miff": return "MIFF 图像";
                case ".palm": return "Palm 位图";
                case ".pict": return "PICT 图像";
                case ".wpg": return "WPG 图像";
                case ".mng": return "MNG 动画";
                case ".cr2": return "Canon RAW";
                case ".crw": return "Canon RAW (CRW)";
                case ".nef": return "Nikon RAW";
                case ".nrw": return "Nikon RAW (NRW)";
                case ".arw": return "Sony RAW";
                case ".dng": return "DNG RAW";
                case ".orf": return "Olympus RAW";
                case ".rw2": return "Panasonic RAW";
                case ".raf": return "Fujifilm RAW";
                case ".pef": return "Pentax RAW";
                case ".srw": return "Samsung RAW";
                case ".x3f": return "Sigma RAW";
                case ".pdf": return "PDF 文档";
                default: return ext.TrimStart('.').ToUpperInvariant() + " 图片";
            }
        }
    }
}
