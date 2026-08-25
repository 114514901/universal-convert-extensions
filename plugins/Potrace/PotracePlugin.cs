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

namespace UniversalConvert.Plugin.Potrace
{
    /// <summary>
    /// 位图转矢量图插件，基于 Potrace（C 工具，由扩展 CI 用 MinGW 编译源码产出 tools\potrace.exe）。
    ///
    /// 输入：BMP/PBM/PGM/PPM 位图；输出：SVG/PDF/EPS/PostScript 矢量图。
    /// potrace 只处理黑白（内部先按阈值二值化）。
    /// </summary>
    public sealed class PotracePlugin : IConverterPlugin
    {
        private IPluginContext _context;

        public string Id => "com.universalconvert.potrace";
        public string Name => "Potrace";
        public string Description => "位图转矢量图（BMP/PBM → SVG/PDF/EPS/PS），工具由 CI 编译随包分发";
        public string Version => "1.0.0";
        public string MinAppVersion => "1.7.3";
        public string MaxAppVersion => null;
        public bool IsUntested => false;

        private static readonly string[] Inputs = { ".bmp", ".pbm", ".pgm", ".ppm" };

        /// <summary>输出扩展名 → potrace 后端参数（-b）。</summary>
        private static readonly Dictionary<string, string> OutputBackends =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".svg", "svg" },
                { ".pdf", "pdf" },
                { ".eps", "eps" },
                { ".ps", "ps" }
            };

        public void Initialize(IPluginContext context)
        {
            _context = context;
        }

        public bool IsToolAvailable()
        {
            return FindTool() != null;
        }

        public IList<ConversionCapability> GetCapabilities()
        {
            var caps = new List<ConversionCapability>();
            foreach (var input in Inputs)
            {
                caps.Add(new ConversionCapability
                {
                    InputExtension = input,
                    InputDisplayName = input.TrimStart('.').ToUpperInvariant() + " 位图",
                    Outputs = OutputBackends
                        .Where(o => !string.Equals(o.Key, input, StringComparison.OrdinalIgnoreCase))
                        .Select(o => new OutputFormat
                        {
                            Extension = o.Key,
                            DisplayName = GetDisplayName(o.Key) + " 矢量图",
                            Options = new List<OptionDefinition>
                            {
                                new OptionDefinition
                                {
                                    Key = "threshold",
                                    Label = "二值化阈值",
                                    Type = OptionType.Enum,
                                    DefaultValue = "0.5",
                                    Choices = new List<OptionChoice>
                                    {
                                        new OptionChoice { Value = "0.25", Label = "0.25（更亮转黑，细节多）" },
                                        new OptionChoice { Value = "0.5", Label = "0.5（默认）" },
                                        new OptionChoice { Value = "0.75", Label = "0.75（更暗转黑，轮廓少）" }
                                    }
                                }
                            }
                        })
                        .ToList()
                });
            }
            return caps;
        }

        public async Task<ConversionResult> ConvertAsync(
            ConversionRequest request,
            IProgress<ConversionProgress> progress,
            CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;

            var tool = FindTool();
            if (tool == null)
            {
                return ConversionResult.Failed("未找到 Potrace（扩展 tools\\potrace.exe）", DateTime.UtcNow - started);
            }
            if (!File.Exists(request.InputPath))
            {
                return ConversionResult.Failed("输入文件不存在：" + request.InputPath, DateTime.UtcNow - started);
            }

            var outExt = (request.OutputExtension ?? ".svg");
            if (!outExt.StartsWith(".")) outExt = "." + outExt;
            outExt = outExt.ToLowerInvariant();

            string backend;
            if (!OutputBackends.TryGetValue(outExt, out backend))
            {
                return ConversionResult.Failed("不支持的矢量输出格式：" + outExt, DateTime.UtcNow - started);
            }

            var outputPath = request.OutputPath;
            if (string.IsNullOrEmpty(outputPath))
            {
                var dir = Path.GetDirectoryName(request.InputPath);
                var name = Path.GetFileNameWithoutExtension(request.InputPath);
                outputPath = Path.Combine(dir ?? "", name + outExt);
            }

            try
            {
                progress?.Report(new ConversionProgress(ConversionStage.Running, -1, "正在矢量化..."));
                var args = BuildArguments(request, outputPath, backend);
                var run = await Task.Run(
                    () => ProcessRunner.Run(tool, args, cancellationToken, null, null, request.PauseSignal),
                    cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return ConversionResult.Failed("转换已取消", DateTime.UtcNow - started);
                }
                if (run.ExitCode != 0 || !File.Exists(outputPath))
                {
                    var detail = string.IsNullOrEmpty(run.StandardError) ? run.StandardOutput : run.StandardError;
                    return ConversionResult.Failed("Potrace 转换失败", DateTime.UtcNow - started, Truncate(detail), run.ExitCode);
                }

                return ConversionResult.Succeeded(outputPath, DateTime.UtcNow - started);
            }
            finally
            {
            }
        }

        private static string BuildArguments(ConversionRequest request, string outputPath, string backend)
        {
            var sb = new StringBuilder();
            sb.Append("-b ").Append(backend);

            // 二值化阈值（0-1）
            string threshold;
            if (request.Options != null && request.Options.TryGetValue("threshold", out threshold)
                && !string.IsNullOrEmpty(threshold))
            {
                sb.Append(" -t ").Append(threshold);
            }

            sb.Append(" ").Append(ProcessRunner.Quote(request.InputPath));
            sb.Append(" -o ").Append(ProcessRunner.Quote(outputPath));
            return sb.ToString();
        }

        private string FindTool()
        {
            try
            {
                var dir = Path.GetDirectoryName(GetType().Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    var local = Path.Combine(dir, "tools", "potrace.exe");
                    if (File.Exists(local)) return local;
                }
            }
            catch
            {
                // 忽略
            }
            return _context != null ? _context.FindTool("potrace") : null;
        }

        private static string GetDisplayName(string ext)
        {
            switch (ext)
            {
                case ".svg": return "SVG";
                case ".pdf": return "PDF";
                case ".eps": return "EPS";
                case ".ps": return "PostScript";
                default: return ext.TrimStart('.').ToUpperInvariant();
            }
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(无输出)";
            const int max = 500;
            return text.Length <= max ? text : text.Substring(0, max);
        }
    }
}