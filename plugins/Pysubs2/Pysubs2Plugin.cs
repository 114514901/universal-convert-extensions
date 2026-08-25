using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalConvert.Core.Models;
using UniversalConvert.Core.Plugins;
using UniversalConvert.Core.Process;

namespace UniversalConvert.Plugin.Pysubs2
{
    /// <summary>
    /// 字幕格式转换插件，基于 pysubs2（纯 Python 库，CLI 由 CI 用 PyInstaller 打成单文件 tools\pysubs2.exe）。
    ///
    /// 支持 srt/ass/ssa/sub(microdvd)/vtt/sami/ttml/json 互转。
    /// pysubs2 输出文件名不可控（同名换扩展名、--to 指定格式），因此自定义 ConvertAsync：
    /// 输出到临时目录再搬。
    /// </summary>
    public sealed class Pysubs2Plugin : IConverterPlugin
    {
        private IPluginContext _context;

        public string Id => "com.universalconvert.pysubs2";
        public string Name => "Pysubs2";
        public string Description => "字幕格式互转（srt/ass/ssa/vtt/sami/ttml 等），基于 pysubs2";
        public string Version => "1.0.0";
        public string MinAppVersion => "1.7.3";
        public string MaxAppVersion => null;
        public bool IsUntested => false;

        /// <summary>输入扩展名 → pysubs2 格式标识符。</summary>
        private static readonly Dictionary<string, string> InputByExtension =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".srt", "srt" },
                { ".ass", "ass" },
                { ".ssa", "ssa" },
                { ".sub", "microdvd" },
                { ".vtt", "vtt" },
                { ".sami", "sami" },
                { ".smi", "sami" },
                { ".ttml", "ttml" },
                { ".json", "json" }
            };

        /// <summary>输出扩展名 → pysubs2 格式标识符。</summary>
        private static readonly Dictionary<string, string> OutputByExtension =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".srt", "srt" },
                { ".ass", "ass" },
                { ".ssa", "ssa" },
                { ".sub", "microdvd" },
                { ".vtt", "vtt" },
                { ".sami", "sami" },
                { ".ttml", "ttml" },
                { ".json", "json" }
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
            foreach (var pair in InputByExtension)
            {
                caps.Add(new ConversionCapability
                {
                    InputExtension = pair.Key,
                    InputDisplayName = GetDisplayName(pair.Key) + " 字幕",
                    Outputs = OutputByExtension
                        .Where(o => !string.Equals(o.Key, pair.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(o => new OutputFormat
                        {
                            Extension = o.Key,
                            DisplayName = GetDisplayName(o.Key) + " 字幕",
                            Options = new List<OptionDefinition>
                            {
                                new OptionDefinition
                                {
                                    Key = "inputEnc",
                                    Label = "输入编码",
                                    Type = OptionType.Enum,
                                    DefaultValue = "utf-8",
                                    Choices = new List<OptionChoice>
                                    {
                                        new OptionChoice { Value = "utf-8", Label = "UTF-8（默认）" },
                                        new OptionChoice { Value = "gbk", Label = "GBK（中文）" },
                                        new OptionChoice { Value = "big5", Label = "Big5（繁体）" },
                                        new OptionChoice { Value = "latin-1", Label = "Latin-1" }
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
                return ConversionResult.Failed("未找到 pysubs2（扩展 tools\\pysubs2.exe）", DateTime.UtcNow - started);
            }
            if (!File.Exists(request.InputPath))
            {
                return ConversionResult.Failed("输入文件不存在：" + request.InputPath, DateTime.UtcNow - started);
            }

            var outExt = (request.OutputExtension ?? ".srt");
            if (!outExt.StartsWith(".")) outExt = "." + outExt;
            outExt = outExt.ToLowerInvariant();

            string outFormat;
            if (!OutputByExtension.TryGetValue(outExt, out outFormat))
            {
                return ConversionResult.Failed("不支持的字幕输出格式：" + outExt, DateTime.UtcNow - started);
            }

            var outputPath = request.OutputPath;
            if (string.IsNullOrEmpty(outputPath))
            {
                var dir = Path.GetDirectoryName(request.InputPath);
                var name = Path.GetFileNameWithoutExtension(request.InputPath);
                outputPath = Path.Combine(dir ?? "", name + outExt);
            }

            // pysubs2 输出到 -o 目录、文件名=输入同名+新扩展，先转临时目录再搬
            var tempOut = Path.Combine(Path.GetTempPath(), "uc-subs-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempOut);

                progress?.Report(new ConversionProgress(ConversionStage.Running, -1, "正在转换字幕..."));
                var args = BuildArguments(request, tempOut, outFormat);
                var run = await Task.Run(
                    () => ProcessRunner.Run(tool, args, cancellationToken, null, null, request.PauseSignal),
                    cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return ConversionResult.Failed("转换已取消", DateTime.UtcNow - started);
                }
                if (run.ExitCode != 0)
                {
                    var detail = string.IsNullOrEmpty(run.StandardError) ? run.StandardOutput : run.StandardError;
                    return ConversionResult.Failed("pysubs2 转换失败", DateTime.UtcNow - started, Truncate(detail), run.ExitCode);
                }

                var produced = Directory.GetFiles(tempOut)
                    .FirstOrDefault(f => string.Equals(
                        Path.GetExtension(f), outExt, StringComparison.OrdinalIgnoreCase));
                if (produced == null)
                {
                    var detail = string.IsNullOrEmpty(run.StandardError) ? run.StandardOutput : run.StandardError;
                    return ConversionResult.Failed("pysubs2 未产生输出文件", DateTime.UtcNow - started, Truncate(detail), run.ExitCode);
                }

                File.Copy(produced, outputPath, true);
                return ConversionResult.Succeeded(outputPath, DateTime.UtcNow - started);
            }
            finally
            {
                TryDeleteDirectory(tempOut);
            }
        }

        private static string BuildArguments(ConversionRequest request, string tempOut, string outFormat)
        {
            var args = "--to " + outFormat + " --output-dir " + ProcessRunner.Quote(tempOut);

            // 输入编码（可选，处理 GBK 等老字幕）
            string enc;
            if (request.Options != null && request.Options.TryGetValue("inputEnc", out enc)
                && !string.IsNullOrEmpty(enc) && enc != "utf-8")
            {
                args += " --input-enc " + enc;
            }

            args += " " + ProcessRunner.Quote(request.InputPath);
            return args;
        }

        private string FindTool()
        {
            try
            {
                var dir = Path.GetDirectoryName(GetType().Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    var local = Path.Combine(dir, "tools", "pysubs2.exe");
                    if (File.Exists(local)) return local;
                }
            }
            catch
            {
                // 忽略
            }
            return _context != null ? _context.FindTool("pysubs2") : null;
        }

        private static string GetDisplayName(string ext)
        {
            switch (ext)
            {
                case ".srt": return "SRT";
                case ".ass": return "ASS";
                case ".ssa": return "SSA";
                case ".sub": return "MicroDVD";
                case ".vtt": return "WebVTT";
                case ".sami":
                case ".smi": return "SAMI";
                case ".ttml": return "TTML";
                case ".json": return "JSON";
                default: return ext.TrimStart('.').ToUpperInvariant();
            }
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(无输出)";
            const int max = 500;
            return text.Length <= max ? text : text.Substring(0, max);
        }

        private static void TryDeleteDirectory(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}