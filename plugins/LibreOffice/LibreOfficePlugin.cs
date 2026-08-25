using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalConvert.Core.Models;
using UniversalConvert.Core.Plugins;
using UniversalConvert.Core.Process;

namespace UniversalConvert.Plugin.LibreOffice
{
    /// <summary>
    /// Office 文档渲染转换插件，基于 LibreOffice（soffice.exe --headless，完整版随包分发在 tools\）。
    ///
    /// 与 Pandoc（结构转换）互补：本插件做渲染式转换——doc/docx/xls/xlsx/ppt/pptx 等
    /// 直接渲染成排版保真的 PDF，以及 Office 家族互转、老格式（doc/xls/ppt）转新格式。
    ///
    /// soffice --convert-to 的输出文件名/目录不可控（固定输出到 --outdir、同名换扩展名），
    /// 因此自定义 ConvertAsync：先渲染到临时目录，再把产物搬到目标路径。
    /// 每次转换使用独立 UserInstallation（临时 profile），避免多个 soffice 进程抢锁用户配置。
    /// </summary>
    public sealed class LibreOfficePlugin : IConverterPlugin
    {
        private IPluginContext _context;

        public string Id => "com.universalconvert.libreoffice";
        public string Name => "LibreOffice";
        public string Description => "Office 文档渲染转换（doc/docx/xls/xlsx/ppt/pptx → PDF 等），LibreOffice 完整版随包分发（约 400MB）";
        public string Version => "1.0.0";
        public string MinAppVersion => "1.7.3";
        public string MaxAppVersion => null;
        public bool IsUntested => false;

        /// <summary>文档类输入：输出 PDF/文档格式。</summary>
        private static readonly string[] DocInputs =
            { ".doc", ".docx", ".odt", ".rtf", ".html", ".htm", ".txt", ".ott" };

        /// <summary>表格类输入：输出 PDF/表格格式。</summary>
        private static readonly string[] SheetInputs =
            { ".xls", ".xlsx", ".ods", ".csv", ".ots" };

        /// <summary>演示类输入：输出 PDF/演示格式。</summary>
        private static readonly string[] SlideInputs =
            { ".ppt", ".pptx", ".odp", ".otp" };

        private static readonly string[] DocOutputs =
            { ".pdf", ".docx", ".odt", ".rtf", ".html", ".txt" };
        private static readonly string[] SheetOutputs =
            { ".pdf", ".xlsx", ".ods", ".csv" };
        private static readonly string[] SlideOutputs =
            { ".pdf", ".pptx", ".odp" };

        public void Initialize(IPluginContext context)
        {
            _context = context;
        }

        public bool IsToolAvailable()
        {
            return FindSoffice() != null;
        }

        public IList<ConversionCapability> GetCapabilities()
        {
            var caps = new List<ConversionCapability>();

            foreach (var input in DocInputs)
            {
                caps.Add(BuildCapability(input, "文档", DocOutputs));
            }
            foreach (var input in SheetInputs)
            {
                caps.Add(BuildCapability(input, "表格", SheetOutputs));
            }
            foreach (var input in SlideInputs)
            {
                caps.Add(BuildCapability(input, "演示", SlideOutputs));
            }
            return caps;
        }

        private static ConversionCapability BuildCapability(string input, string kind, string[] outputs)
        {
            return new ConversionCapability
            {
                InputExtension = input,
                InputDisplayName = input.TrimStart('.').ToUpperInvariant() + " " + kind,
                Outputs = outputs
                    .Where(o => !string.Equals(o, input, StringComparison.OrdinalIgnoreCase))
                    .Select(ext => new OutputFormat
                    {
                        Extension = ext,
                        DisplayName = ext == ".pdf" ? "PDF 文档" : ext.TrimStart('.').ToUpperInvariant() + " 文档"
                    })
                    .ToList()
            };
        }

        public async Task<ConversionResult> ConvertAsync(
            ConversionRequest request,
            IProgress<ConversionProgress> progress,
            CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;

            var soffice = FindSoffice();
            if (soffice == null)
            {
                return ConversionResult.Failed("未找到 LibreOffice（扩展 tools\\program\\soffice.exe）", DateTime.UtcNow - started);
            }
            if (!File.Exists(request.InputPath))
            {
                return ConversionResult.Failed("输入文件不存在：" + request.InputPath, DateTime.UtcNow - started);
            }

            var outExt = (request.OutputExtension ?? ".pdf");
            if (!outExt.StartsWith(".")) outExt = "." + outExt;
            outExt = outExt.ToLowerInvariant();

            var filter = GetFilter(outExt);
            if (filter == null)
            {
                return ConversionResult.Failed("不支持的输出格式：" + outExt, DateTime.UtcNow - started);
            }

            // 输出路径（与引擎默认规则一致：显式指定优先，否则同目录同名换扩展名）
            var outputPath = request.OutputPath;
            if (string.IsNullOrEmpty(outputPath))
            {
                var dir = Path.GetDirectoryName(request.InputPath);
                var name = Path.GetFileNameWithoutExtension(request.InputPath);
                outputPath = Path.Combine(dir ?? "", name + outExt);
            }

            // soffice --convert-to 输出到 --outdir、文件名=输入同名+目标扩展，故先渲染到临时目录再搬
            var tempOut = Path.Combine(Path.GetTempPath(), "uc-lo-" + Guid.NewGuid().ToString("N"));
            var tempProfile = Path.Combine(Path.GetTempPath(), "uc-lo-profile-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempOut);
                Directory.CreateDirectory(tempProfile);

                progress?.Report(new ConversionProgress(ConversionStage.Running, -1, "正在用 LibreOffice 转换..."));
                var args = BuildArguments(request.InputPath, tempOut, tempProfile, filter);
                var run = await Task.Run(
                    () => ProcessRunner.Run(soffice, args, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return ConversionResult.Failed("转换已取消", DateTime.UtcNow - started);
                }
                if (run.ExitCode != 0)
                {
                    var detail = string.IsNullOrEmpty(run.StandardError) ? run.StandardOutput : run.StandardError;
                    return ConversionResult.Failed("LibreOffice 转换失败", DateTime.UtcNow - started, Truncate(detail), run.ExitCode);
                }

                // 找产物：输入同名 + 目标扩展（soffice 可能保留大写扩展名，忽略大小写匹配）
                var produced = Directory.GetFiles(tempOut)
                    .FirstOrDefault(f => string.Equals(
                        Path.GetExtension(f), outExt, StringComparison.OrdinalIgnoreCase));
                if (produced == null)
                {
                    var detail = string.IsNullOrEmpty(run.StandardError) ? run.StandardOutput : run.StandardError;
                    return ConversionResult.Failed("LibreOffice 未产生输出文件", DateTime.UtcNow - started, Truncate(detail), run.ExitCode);
                }

                File.Copy(produced, outputPath, true);
                return ConversionResult.Succeeded(outputPath, DateTime.UtcNow - started);
            }
            finally
            {
                TryDeleteDirectory(tempOut);
                TryDeleteDirectory(tempProfile);
            }
        }

        /// <summary>--convert-to 的 filter 名；返回 null 表示不支持。</summary>
        private static string GetFilter(string outExt)
        {
            switch (outExt)
            {
                case ".pdf": return "pdf";
                case ".docx": return "docx:Office Open XML Text";
                case ".xlsx": return "xlsx:Calc MS Excel 2007 XML";
                case ".pptx": return "pptx:Impress MS PowerPoint 2007 XML";
                case ".odt": return "odt:writer8";
                case ".ods": return "ods:calc8";
                case ".odp": return "odp:impress8";
                case ".rtf": return "rtf:Rich Text Format";
                case ".html": return "html:HTML (StarWriter)";
                case ".txt": return "txt:Text (encoded)";
                case ".csv": return "csv:Text - txt - csv (StarCalc)";
                default: return null;
            }
        }

        private static string BuildArguments(string inputPath, string tempOut, string tempProfile, string filter)
        {
            // -env:UserInstallation 用独立临时 profile：避免多个 soffice 并发抢锁用户配置
            return "--headless --norestore --convert-to " + ProcessRunner.Quote(filter)
                + " --outdir " + ProcessRunner.Quote(tempOut)
                + " -env:UserInstallation=" + ProcessRunner.Quote("file:///" + tempProfile.Replace('\\', '/'))
                + " " + ProcessRunner.Quote(inputPath);
        }

        private string FindSoffice()
        {
            try
            {
                var dir = Path.GetDirectoryName(GetType().Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    var local = Path.Combine(dir, "tools", "program", "soffice.exe");
                    if (File.Exists(local)) return local;
                    // 兼容直接把 exe 放 tools 根目录的情况
                    var flat = Path.Combine(dir, "tools", "soffice.exe");
                    if (File.Exists(flat)) return flat;
                }
            }
            catch
            {
                // 忽略
            }
            return _context != null ? _context.FindTool("soffice") : null;
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
