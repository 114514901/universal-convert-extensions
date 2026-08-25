using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniversalConvert.Core.Models;
using UniversalConvert.Core.Plugins;
using UniversalConvert.Core.Process;

namespace UniversalConvert.Plugin.WebPdf
{
    /// <summary>
    /// 网页转 PDF 插件：复用系统 Microsoft Edge 的无头模式渲染本地 HTML 为 PDF。
    ///
    /// 不随包分发任何二进制（零体积）——Edge 是 Windows 10/11 预装的 Chromium 内核浏览器，
    /// 渲染质量与 Chrome 一致（现代 CSS/JS）。
    /// 命令：msedge --headless=new --print-to-pdf="out.pdf" input.html
    /// </summary>
    public sealed class WebPdfPlugin : IConverterPlugin
    {
        private IPluginContext _context;

        public string Id => "com.universalconvert.webpdf";
        public string Name => "WebPdf";
        public string Description => "网页转 PDF（本地 HTML → PDF），复用系统 Edge 无头模式渲染，无需随包分发";
        public string Version => "1.0.0";
        public string MinAppVersion => "1.7.3";
        public string MaxAppVersion => null;
        public bool IsUntested => false;

        /// <summary>Edge 常见的安装路径（按顺序探测）。</summary>
        private static readonly string[] EdgeCandidates =
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
        };

        public void Initialize(IPluginContext context)
        {
            _context = context;
        }

        public bool IsToolAvailable()
        {
            return FindEdge() != null;
        }

        public IList<ConversionCapability> GetCapabilities()
        {
            return new List<ConversionCapability>
            {
                new ConversionCapability
                {
                    InputExtension = ".html",
                    InputDisplayName = "HTML 网页",
                    Outputs = new List<OutputFormat>
                    {
                        new OutputFormat { Extension = ".pdf", DisplayName = "PDF 文档" }
                    }
                },
                new ConversionCapability
                {
                    InputExtension = ".htm",
                    InputDisplayName = "HTML 网页",
                    Outputs = new List<OutputFormat>
                    {
                        new OutputFormat { Extension = ".pdf", DisplayName = "PDF 文档" }
                    }
                }
            };
        }

        public async Task<ConversionResult> ConvertAsync(
            ConversionRequest request,
            IProgress<ConversionProgress> progress,
            CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;

            var edge = FindEdge();
            if (edge == null)
            {
                return ConversionResult.Failed("未找到 Microsoft Edge（网页转 PDF 需要系统 Edge）", DateTime.UtcNow - started);
            }
            if (!File.Exists(request.InputPath))
            {
                return ConversionResult.Failed("输入文件不存在：" + request.InputPath, DateTime.UtcNow - started);
            }

            var outputPath = request.OutputPath;
            if (string.IsNullOrEmpty(outputPath))
            {
                var dir = Path.GetDirectoryName(request.InputPath);
                var name = Path.GetFileNameWithoutExtension(request.InputPath);
                outputPath = Path.Combine(dir ?? "", name + ".pdf");
            }

            try
            {
                progress?.Report(new ConversionProgress(ConversionStage.Running, -1, "正在用 Edge 渲染 PDF..."));
                var args = BuildArguments(request.InputPath, outputPath);
                var run = await Task.Run(
                    () => ProcessRunner.Run(edge, args, cancellationToken, null, null, request.PauseSignal),
                    cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return ConversionResult.Failed("转换已取消", DateTime.UtcNow - started);
                }
                if (run.ExitCode != 0 || !File.Exists(outputPath))
                {
                    var detail = string.IsNullOrEmpty(run.StandardError) ? run.StandardOutput : run.StandardError;
                    return ConversionResult.Failed("Edge 渲染 PDF 失败", DateTime.UtcNow - started, Truncate(detail), run.ExitCode);
                }

                return ConversionResult.Succeeded(outputPath, DateTime.UtcNow - started);
            }
            finally
            {
            }
        }

        private static string BuildArguments(string inputPath, string outputPath)
        {
            var sb = new StringBuilder();
            sb.Append("--headless=new --disable-gpu --no-pdf-header-footer");
            sb.Append(" --print-to-pdf=").Append(ProcessRunner.Quote(outputPath));
            // 输入 HTML 用 file:// URL（Edge 需要 URL 形式）
            var fileUrl = "file:///" + inputPath.Replace('\\', '/');
            sb.Append(" ").Append(ProcessRunner.Quote(fileUrl));
            return sb.ToString();
        }

        private string FindEdge()
        {
            try
            {
                foreach (var candidate in EdgeCandidates)
                {
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // 忽略
            }
            return _context != null ? _context.FindTool("msedge") : null;
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(无输出)";
            const int max = 500;
            return text.Length <= max ? text : text.Substring(0, max);
        }
    }
}