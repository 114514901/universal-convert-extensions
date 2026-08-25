using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalConvert.Core.Models;
using UniversalConvert.Core.Plugins;
using UniversalConvert.Core.Process;

namespace UniversalConvert.Plugin.MIDI
{
    /// <summary>
    /// MIDI 合成与音频转换插件，基于 FluidSynth + GeneralUser GS 音色库
    /// （工具与音色库随插件包分发在 tools\）。
    ///
    /// 转换流程：FluidSynth 把 .mid/.midi 渲染成 wav；输出 .wav 直接得到，
    /// 其它格式（mp3/ogg/flac/m4a）再交给应用的 ffmpeg 链式转码。
    /// 同时实现 IPreviewProvider：主程序预览 .mid/.midi 时先渲染成 wav 再播放。
    /// </summary>
    public sealed class MIDIPlugin : IConverterPlugin, IPreviewProvider
    {
        private IPluginContext _context;

        public string Id => "com.universalconvert.midi";
        public string Name => "MIDI";
        public string Description => "MIDI 合成与音频转换（.mid/.midi → wav/mp3/ogg/flac/m4a），FluidSynth + GeneralUser GS 音色库随包分发";
        public string Version => "1.0.1";

        /// <summary>需要 IPreviewProvider 与加载时 MinAppVersion 校验（主程序 2.0.2-dev.7 起）。</summary>
        public string MinAppVersion => "2.0.2-dev.7";
        public string MaxAppVersion => null;
        public bool IsUntested => false;

        private static readonly string[] MidiInputs = { ".mid", ".midi" };
        private static readonly string[] MidiOutputs = { ".wav", ".mp3", ".ogg", ".flac", ".m4a" };

        public void Initialize(IPluginContext context)
        {
            _context = context;
        }

        public bool IsToolAvailable()
        {
            return FindFluidSynth() != null && FindSoundFont() != null;
        }

        public IList<ConversionCapability> GetCapabilities()
        {
            var caps = new List<ConversionCapability>();
            foreach (var input in MidiInputs)
            {
                caps.Add(new ConversionCapability
                {
                    InputExtension = input,
                    InputDisplayName = "MIDI 音乐",
                    Outputs = MidiOutputs
                        .Where(o => !string.Equals(o, input, StringComparison.OrdinalIgnoreCase))
                        .Select(ext => new OutputFormat
                        {
                            Extension = ext,
                            DisplayName = ext.TrimStart('.').ToUpperInvariant() + " 音频",
                            Options = new List<OptionDefinition>
                            {
                                new OptionDefinition
                                {
                                    Key = "sampleRate",
                                    Label = "采样率",
                                    Type = OptionType.Enum,
                                    DefaultValue = "44100",
                                    Choices = new List<OptionChoice>
                                    {
                                        new OptionChoice { Value = "22050", Label = "22050 Hz" },
                                        new OptionChoice { Value = "44100", Label = "44100 Hz（默认）" },
                                        new OptionChoice { Value = "48000", Label = "48000 Hz" }
                                    }
                                },
                                new OptionDefinition
                                {
                                    Key = "gain",
                                    Label = "音量增益",
                                    Type = OptionType.Enum,
                                    DefaultValue = "0.5",
                                    Choices = new List<OptionChoice>
                                    {
                                        new OptionChoice { Value = "0.2", Label = "0.2（轻柔，FluidSynth 原默认）" },
                                        new OptionChoice { Value = "0.5", Label = "0.5（默认，推荐）" },
                                        new OptionChoice { Value = "1.0", Label = "1.0（响亮）" },
                                        new OptionChoice { Value = "1.5", Label = "1.5（增强）" },
                                        new OptionChoice { Value = "2.0", Label = "2.0（最大，可能削波）" }
                                    }
                                }
                            }
                        })
                        .ToList()
                });
            }
            return caps;
        }

        // ---------- IPreviewProvider ----------

        public IList<string> SupportedPreviewExtensions => new List<string> { ".mid", ".midi" };

        /// <summary>把 MIDI 渲染成临时 wav 供预览；失败返回 null（调用方回退其它预览方式）。</summary>
        public async Task<string> RenderPreviewAsync(string inputPath, CancellationToken cancellationToken)
        {
            var fluidsynth = FindFluidSynth();
            var soundFont = FindSoundFont();
            if (fluidsynth == null || soundFont == null || !File.Exists(inputPath)) return null;

            var wav = Path.Combine(Path.GetTempPath(), "uc-midi-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                // 预览固定用推荐增益 0.5（FluidSynth 原默认 0.2 偏小）
                var args = BuildRenderArguments(soundFont, inputPath, wav, "44100", "0.5");
                var result = await Task.Run(() => ProcessRunner.Run(fluidsynth, args, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0 || !File.Exists(wav))
                {
                    TryDelete(wav);
                    return null;
                }
                return wav;
            }
            catch
            {
                TryDelete(wav);
                return null;
            }
        }

        // ---------- 转换 ----------

        public async Task<ConversionResult> ConvertAsync(
            ConversionRequest request,
            IProgress<ConversionProgress> progress,
            CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;

            var fluidsynth = FindFluidSynth();
            if (fluidsynth == null)
            {
                return ConversionResult.Failed("未找到 FluidSynth（扩展 tools\\fluidsynth.exe）", DateTime.UtcNow - started);
            }
            var soundFont = FindSoundFont();
            if (soundFont == null)
            {
                return ConversionResult.Failed("未找到音色库（扩展 tools\\*.sf2）", DateTime.UtcNow - started);
            }
            if (!File.Exists(request.InputPath))
            {
                return ConversionResult.Failed("输入文件不存在：" + request.InputPath, DateTime.UtcNow - started);
            }

            // 1. FluidSynth 渲染成临时 wav
            var sampleRate = GetOption(request, "sampleRate", "44100");
            var gain = GetOption(request, "gain", "0.5");
            var tempWav = Path.Combine(Path.GetTempPath(), "uc-midi-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                progress?.Report(new ConversionProgress(ConversionStage.Running, -1, "正在渲染 MIDI..."));
                var render = await Task.Run(
                    () => ProcessRunner.Run(fluidsynth, BuildRenderArguments(soundFont, request.InputPath, tempWav, sampleRate, gain), cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return ConversionResult.Failed("转换已取消", DateTime.UtcNow - started);
                }
                if (render.ExitCode != 0 || !File.Exists(tempWav))
                {
                    var detail = string.IsNullOrEmpty(render.StandardError) ? render.StandardOutput : render.StandardError;
                    return ConversionResult.Failed("FluidSynth 渲染失败", DateTime.UtcNow - started, Truncate(detail), render.ExitCode);
                }

                // 2. 输出路径
                var outExt = (request.OutputExtension ?? ".wav");
                if (!outExt.StartsWith(".")) outExt = "." + outExt;
                outExt = outExt.ToLowerInvariant();

                var outputPath = request.OutputPath;
                if (string.IsNullOrEmpty(outputPath))
                {
                    var dir = Path.GetDirectoryName(request.InputPath);
                    var name = Path.GetFileNameWithoutExtension(request.InputPath);
                    outputPath = Path.Combine(dir ?? "", name + outExt);
                }

                // 3. wav 直接给；其它格式链式 ffmpeg 转码
                if (string.Equals(outExt, ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(tempWav, outputPath, true);
                }
                else
                {
                    var ffmpeg = _context != null ? _context.FindTool("ffmpeg") : null;
                    if (string.IsNullOrEmpty(ffmpeg))
                    {
                        return ConversionResult.Failed("未找到 ffmpeg（转码为 " + outExt + " 需要）", DateTime.UtcNow - started);
                    }

                    progress?.Report(new ConversionProgress(ConversionStage.Running, -1, "正在转码为 " + outExt + " ..."));
                    var encode = await Task.Run(
                        () => ProcessRunner.Run(ffmpeg, BuildFfmpegArguments(tempWav, outputPath, outExt), cancellationToken),
                        cancellationToken).ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return ConversionResult.Failed("转换已取消", DateTime.UtcNow - started);
                    }
                    if (encode.ExitCode != 0 || !File.Exists(outputPath))
                    {
                        var detail = string.IsNullOrEmpty(encode.StandardError) ? encode.StandardOutput : encode.StandardError;
                        return ConversionResult.Failed("ffmpeg 转码失败", DateTime.UtcNow - started, Truncate(detail), encode.ExitCode);
                    }
                }

                return ConversionResult.Succeeded(outputPath, DateTime.UtcNow - started);
            }
            finally
            {
                TryDelete(tempWav);
            }
        }

        // ---------- 工具定位 ----------

        private static string PluginToolsDirectory()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(MIDIPlugin).Assembly.Location);
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "tools");
            }
            catch
            {
                return null;
            }
        }

        private string FindFluidSynth()
        {
            try
            {
                var tools = PluginToolsDirectory();
                if (!string.IsNullOrEmpty(tools))
                {
                    var local = Path.Combine(tools, "fluidsynth.exe");
                    if (File.Exists(local)) return local;
                }
            }
            catch
            {
                // 忽略
            }
            return _context != null ? _context.FindTool("fluidsynth") : null;
        }

        /// <summary>音色库：优先插件自带 tools\*.sf2（含 tools\soundfont\ 子目录），否则查数据目录。</summary>
        private string FindSoundFont()
        {
            try
            {
                var tools = PluginToolsDirectory();
                if (!string.IsNullOrEmpty(tools))
                {
                    var found = FindSf2(tools);
                    if (found != null) return found;
                }
                if (_context != null && !string.IsNullOrEmpty(_context.DataDirectory))
                {
                    var found = FindSf2(_context.DataDirectory);
                    if (found != null) return found;
                }
            }
            catch
            {
                // 忽略
            }
            return null;
        }

        private static string FindSf2(string directory)
        {
            var direct = Directory.GetFiles(directory, "*.sf2").FirstOrDefault();
            if (direct != null) return direct;

            var sub = Directory.GetDirectories(directory)
                .SelectMany(d => Directory.GetFiles(d, "*.sf2"))
                .FirstOrDefault();
            return sub;
        }

        // ---------- 命令行 ----------

        private static string BuildRenderArguments(string soundFont, string inputPath, string wavPath, string sampleRate, string gain)
        {
            // -F 渲染到文件；-i 非交互；-r 采样率；-g 音量增益（FluidSynth 原默认 0.2 偏小，文件渲染推荐 0.5）
            return "-F " + ProcessRunner.Quote(wavPath)
                + " -i -r " + sampleRate
                + " -g " + gain
                + " " + ProcessRunner.Quote(soundFont)
                + " " + ProcessRunner.Quote(inputPath);
        }

        private static string BuildFfmpegArguments(string wavPath, string outputPath, string outExt)
        {
            var args = "-y -hide_banner -loglevel error -i " + ProcessRunner.Quote(wavPath) + " ";
            switch (outExt)
            {
                case ".mp3":
                    args += "-c:a libmp3lame -b:a 192k";
                    break;
                case ".ogg":
                    args += "-c:a libvorbis -q:a 5";
                    break;
                case ".flac":
                    args += "-c:a flac";
                    break;
                case ".m4a":
                    args += "-c:a aac -b:a 192k";
                    break;
                default:
                    args += "-c:a pcm_s16le";
                    break;
            }
            return args + " " + ProcessRunner.Quote(outputPath);
        }

        private static string GetOption(ConversionRequest request, string key, string defaultValue)
        {
            if (request.Options != null && request.Options.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
            return defaultValue;
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
