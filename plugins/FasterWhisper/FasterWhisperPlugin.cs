using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniversalConvert.Core.Models;
using UniversalConvert.Core.Plugins;
using UniversalConvert.Core.Process;

namespace UniversalConvert.Plugin.FasterWhisper
{
    /// <summary>
    /// 语音转文字/字幕插件，基于 faster-whisper（CTranslate2 推理，CLI 由 CI 用 PyInstaller
    /// 打成单文件 tools\whisper.exe，Whisper base 模型随包分发在 tools\whisper-base）。
    ///
    /// 流程：先用应用自带的 ffmpeg 把任意音频转成 16kHz 单声道 wav（免去在 Python 侧打包解码器），
    /// 再交给 whisper 转写，输出 txt/srt/vtt；进度由 CLI 的 "PROGRESS x" 行解析上报。
    /// </summary>
    public sealed class FasterWhisperPlugin : IConverterPlugin
    {
        private IPluginContext _context;

        public string Id => "com.universalconvert.fasterwhisper";
        public string Name => "FasterWhisper";
        public string Description => "语音转文字/字幕（音频 → txt/srt/vtt），Whisper base 模型随包分发";
        public string Version => "1.0.0";
        public string MinAppVersion => "1.7.3";
        public string MaxAppVersion => null;
        public bool IsUntested => false;

        private static readonly string[] AudioInputs =
            { ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".opus", ".aac", ".wma" };

        private static readonly string[] TextOutputs =
            { ".txt", ".srt", ".vtt" };

        /// <summary>支持的转写语言（值：faster-whisper 语言代码）。</summary>
        private static readonly Dictionary<string, string> Languages =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "auto", "自动检测（默认）" },
                { "zh", "中文" },
                { "en", "英语" },
                { "ja", "日语" },
                { "ko", "韩语" },
                { "fr", "法语" },
                { "de", "德语" },
                { "es", "西班牙语" },
                { "ru", "俄语" }
            };

        public void Initialize(IPluginContext context)
        {
            _context = context;
        }

        public bool IsToolAvailable()
        {
            return FindWhisper() != null && FindModelDirectory() != null;
        }

        public IList<ConversionCapability> GetCapabilities()
        {
            var caps = new List<ConversionCapability>();
            foreach (var input in AudioInputs)
            {
                caps.Add(new ConversionCapability
                {
                    InputExtension = input,
                    InputDisplayName = input.TrimStart('.').ToUpperInvariant() + " 音频",
                    Outputs = TextOutputs.Select(ext => new OutputFormat
                    {
                        Extension = ext,
                        DisplayName = GetDisplayName(ext),
                        Options = new List<OptionDefinition>
                        {
                            new OptionDefinition
                            {
                                Key = "language",
                                Label = "语言",
                                Type = OptionType.Enum,
                                DefaultValue = "auto",
                                Choices = Languages
                                    .Select(l => new OptionChoice { Value = l.Key, Label = l.Value })
                                    .ToList()
                            }
                        }
                    }).ToList()
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

            var whisper = FindWhisper();
            if (whisper == null)
            {
                return ConversionResult.Failed("未找到 whisper（扩展 tools\\whisper.exe）", DateTime.UtcNow - started);
            }
            var modelDir = FindModelDirectory();
            if (modelDir == null)
            {
                return ConversionResult.Failed("未找到 Whisper 模型（扩展 tools\\whisper-base）", DateTime.UtcNow - started);
            }
            var ffmpeg = _context != null ? _context.FindTool("ffmpeg") : null;
            if (string.IsNullOrEmpty(ffmpeg))
            {
                return ConversionResult.Failed("未找到 ffmpeg（音频转 wav 需要）", DateTime.UtcNow - started);
            }
            if (!File.Exists(request.InputPath))
            {
                return ConversionResult.Failed("输入文件不存在：" + request.InputPath, DateTime.UtcNow - started);
            }

            var outExt = (request.OutputExtension ?? ".txt");
            if (!outExt.StartsWith(".")) outExt = "." + outExt;
            outExt = outExt.ToLowerInvariant();
            if (!TextOutputs.Contains(outExt, StringComparer.OrdinalIgnoreCase))
            {
                return ConversionResult.Failed("不支持的文字输出格式：" + outExt, DateTime.UtcNow - started);
            }

            var outputPath = request.OutputPath;
            if (string.IsNullOrEmpty(outputPath))
            {
                var dir = Path.GetDirectoryName(request.InputPath);
                var name = Path.GetFileNameWithoutExtension(request.InputPath);
                outputPath = Path.Combine(dir ?? "", name + outExt);
            }

            var tempWav = Path.Combine(Path.GetTempPath(), "uc-wav-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                // 1. ffmpeg 转 16kHz 单声道 wav（whisper 的标准输入格式）
                var format = outExt.TrimStart('.');
                var language = GetLanguage(request);

                progress?.Report(new ConversionProgress(ConversionStage.Running, -1, "正在转音频格式..."));
                var ffArgs = "-y -hide_banner -loglevel error -i " + ProcessRunner.Quote(request.InputPath)
                    + " -ac 1 -ar 16000 " + ProcessRunner.Quote(tempWav);
                var trans = await Task.Run(
                    () => ProcessRunner.Run(ffmpeg, ffArgs, cancellationToken, null, null, request.PauseSignal),
                    cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return ConversionResult.Failed("转换已取消", DateTime.UtcNow - started);
                }
                if (trans.ExitCode != 0 || !File.Exists(tempWav))
                {
                    var detail = string.IsNullOrEmpty(trans.StandardError) ? trans.StandardOutput : trans.StandardError;
                    return ConversionResult.Failed("音频转 wav 失败", DateTime.UtcNow - started, Truncate(detail), trans.ExitCode);
                }

                // 2. whisper 转写（进度：解析 "PROGRESS x" 行）
                progress?.Report(new ConversionProgress(ConversionStage.Running, 0, "正在语音识别..."));
                var whisperArgs = ProcessRunner.Quote(tempWav)
                    + " -o " + ProcessRunner.Quote(outputPath)
                    + " --model " + ProcessRunner.Quote(modelDir)
                    + " --format " + format;
                if (!string.IsNullOrEmpty(language))
                {
                    whisperArgs += " --language " + language;
                }

                var run = await Task.Run(
                    () => ProcessRunner.Run(whisper, whisperArgs, cancellationToken,
                        line => OnWhisperLine(line, progress), null, request.PauseSignal),
                    cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return ConversionResult.Failed("转换已取消", DateTime.UtcNow - started);
                }
                if (run.ExitCode != 0 || !File.Exists(outputPath))
                {
                    var detail = string.IsNullOrEmpty(run.StandardError) ? run.StandardOutput : run.StandardError;
                    return ConversionResult.Failed("语音识别失败", DateTime.UtcNow - started, Truncate(detail), run.ExitCode);
                }

                progress?.Report(new ConversionProgress(ConversionStage.Completed, 100, "完成"));
                return ConversionResult.Succeeded(outputPath, DateTime.UtcNow - started);
            }
            finally
            {
                TryDelete(tempWav);
            }
        }

        /// <summary>解析 whisper 的 "PROGRESS x" 进度行。</summary>
        private static void OnWhisperLine(string line, IProgress<ConversionProgress> progress)
        {
            if (line == null) return;
            const string prefix = "PROGRESS ";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                double pct;
                if (double.TryParse(line.Substring(prefix.Length).Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                {
                    progress?.Report(new ConversionProgress(ConversionStage.Running, pct, "正在语音识别 " + (int)pct + "%"));
                }
            }
        }

        private static string GetLanguage(ConversionRequest request)
        {
            string language;
            if (request.Options != null && request.Options.TryGetValue("language", out language)
                && !string.IsNullOrEmpty(language) && language != "auto")
            {
                return language;
            }
            return null;
        }

        private string FindWhisper()
        {
            try
            {
                var dir = Path.GetDirectoryName(GetType().Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    var local = Path.Combine(dir, "tools", "whisper.exe");
                    if (File.Exists(local)) return local;
                }
            }
            catch
            {
                // 忽略
            }
            return _context != null ? _context.FindTool("whisper") : null;
        }

        /// <summary>模型目录：tools\whisper-base（含 model.bin / config.json / tokenizer.json 等）。</summary>
        private string FindModelDirectory()
        {
            try
            {
                var dir = Path.GetDirectoryName(GetType().Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    var local = Path.Combine(dir, "tools", "whisper-base");
                    if (Directory.Exists(local) && File.Exists(Path.Combine(local, "model.bin")))
                    {
                        return local;
                    }
                }
            }
            catch
            {
                // 忽略
            }
            return null;
        }

        private static string GetDisplayName(string ext)
        {
            switch (ext)
            {
                case ".txt": return "纯文本";
                case ".srt": return "SRT 字幕";
                case ".vtt": return "WebVTT 字幕";
                default: return ext.TrimStart('.').ToUpperInvariant();
            }
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(无输出)";
            const int max = 500;
            return text.Length <= max ? text : text.Substring(0, max);
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}