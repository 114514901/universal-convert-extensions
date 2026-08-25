using System;
using System.Collections.Generic;
using UniversalConvert.Core.Models;
using UniversalConvert.Core.Plugins;
using UniversalConvert.Core.Process;

namespace UniversalConvert.Plugin.MarkItDown
{
    /// <summary>
    /// Microsoft MarkItDown 文档转 Markdown 插件（外部 Python 工具）。
    /// 工具随包分发（PyInstaller 打包的单文件 tools\markitdown.exe），也可回退到系统 PATH。
    /// </summary>
    public sealed class MarkItDownPlugin : ExternalToolConverterBase
    {
        public override string Id => "com.universalconvert.markitdown";
        public override string Name => "MarkItDown";
        public override string Description => "Microsoft MarkItDown：PDF/Word/Excel/PPT/HTML/图片 OCR/音频等转 Markdown（Python 运行时随包分发）";
        public override string Version => "1.1.0";
        protected override string ToolName => "markitdown";

        private static readonly string[] Inputs =
        {
            ".pdf", ".docx", ".pptx", ".xlsx", ".html", ".htm",
            ".csv", ".json", ".xml", ".txt",
            ".jpg", ".jpeg", ".png",
            ".mp3", ".wav", ".epub"
        };

        public override IList<ConversionCapability> GetCapabilities()
        {
            var caps = new List<ConversionCapability>();
            foreach (var input in Inputs)
            {
                caps.Add(new ConversionCapability
                {
                    InputExtension = input,
                    InputDisplayName = GetDisplayName(input),
                    Outputs = new List<OutputFormat>
                    {
                        new OutputFormat { Extension = ".md", DisplayName = "Markdown" }
                    }
                });
            }
            return caps;
        }

        private static string GetDisplayName(string ext)
        {
            switch (ext)
            {
                case ".pdf": return "PDF 文档";
                case ".docx": return "Word 文档";
                case ".pptx": return "PowerPoint 演示";
                case ".xlsx": return "Excel 表格";
                case ".html":
                case ".htm": return "HTML 网页";
                case ".csv": return "CSV 表格";
                case ".json": return "JSON 文件";
                case ".xml": return "XML 文件";
                case ".txt": return "文本文件";
                case ".jpg":
                case ".jpeg": return "JPG 图片";
                case ".png": return "PNG 图片";
                case ".mp3": return "MP3 音频";
                case ".wav": return "WAV 音频";
                case ".epub": return "EPUB 电子书";
                default: return ext.TrimStart('.').ToUpperInvariant() + " 文件";
            }
        }

        protected override string BuildArguments(ConversionRequest request, string outputPath)
        {
            // markitdown <输入> -o <输出.md>
            return ProcessRunner.Quote(request.InputPath) + " -o " + ProcessRunner.Quote(outputPath);
        }
    }
}
