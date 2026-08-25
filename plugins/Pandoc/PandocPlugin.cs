using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniversalConvert.Core.Models;
using UniversalConvert.Core.Plugins;
using UniversalConvert.Core.Process;

namespace UniversalConvert.Plugin.Pandoc
{
    /// <summary>
    /// 文档格式转换插件，基于 Pandoc（工具随插件包分发在 tools\pandoc.exe）。
    /// 输入交给 Pandoc 自动识别；输出显式指定 writer（不依赖扩展名猜测）。
    /// </summary>
    public sealed class PandocPlugin : ExternalToolConverterBase
    {
        public override string Id => "com.universalconvert.pandoc";
        public override string Name => "Pandoc";
        public override string Description => "文档格式转换（md/docx/html/tex/epub 等），基于 Pandoc";
        public override string Version => "1.1.0";
        protected override string ToolName => "pandoc";

        private static readonly string[] DocInputs =
        {
            ".md", ".markdown", ".docx", ".html", ".htm", ".tex", ".latex", ".epub",
            ".rst", ".odt", ".txt", ".org", ".rtf", ".fb2", ".opml", ".mediawiki",
            ".wiki", ".muse", ".t2t", ".textile", ".typst", ".ipynb", ".pptx",
            ".xlsx", ".csv", ".tsv", ".bib", ".ris", ".adoc", ".asciidoc",
            ".xml", ".man", ".pod"
        };

        private static readonly string[] DocOutputs =
        {
            ".md", ".markdown", ".docx", ".html", ".htm", ".tex", ".latex", ".epub",
            ".rst", ".odt", ".org", ".rtf", ".fb2", ".opml", ".mediawiki", ".muse",
            ".t2t", ".textile", ".typst", ".ipynb", ".pptx", ".adoc", ".asciidoc",
            ".man", ".texi", ".texinfo", ".bib"
        };

        /// <summary>输出扩展名 → Pandoc writer 名（输出必须显式 -t，避免扩展名猜测出错）。</summary>
        private static readonly Dictionary<string, string> WriterByExtension =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".md", "markdown" },
                { ".markdown", "markdown" },
                { ".docx", "docx" },
                { ".html", "html" },
                { ".htm", "html" },
                { ".tex", "latex" },
                { ".latex", "latex" },
                { ".epub", "epub" },
                { ".rst", "rst" },
                { ".odt", "odt" },
                { ".org", "org" },
                { ".rtf", "rtf" },
                { ".fb2", "fb2" },
                { ".opml", "opml" },
                { ".mediawiki", "mediawiki" },
                { ".muse", "muse" },
                { ".t2t", "t2t" },
                { ".textile", "textile" },
                { ".typst", "typst" },
                { ".ipynb", "ipynb" },
                { ".pptx", "pptx" },
                { ".adoc", "asciidoc" },
                { ".asciidoc", "asciidoc" },
                { ".man", "man" },
                { ".texi", "texinfo" },
                { ".texinfo", "texinfo" },
                { ".bib", "bibtex" }
            };

        public override IList<ConversionCapability> GetCapabilities()
        {
            var caps = new List<ConversionCapability>();
            foreach (var input in DocInputs)
            {
                caps.Add(new ConversionCapability
                {
                    InputExtension = input,
                    InputDisplayName = input.TrimStart('.').ToUpperInvariant() + " 文档",
                    Outputs = DocOutputs
                        .Where(o => !string.Equals(o, input, StringComparison.OrdinalIgnoreCase))
                        .Select(BuildOutput)
                        .ToList()
                });
            }
            return caps;
        }

        private static OutputFormat BuildOutput(string ext)
        {
            var output = new OutputFormat
            {
                Extension = ext,
                DisplayName = ext.TrimStart('.').ToUpperInvariant()
            };

            // markdown 家族共享 .md/.markdown：提供「Markdown 变体」选项显式选 writer
            if (ext == ".md" || ext == ".markdown")
            {
                output.Options = new List<OptionDefinition>
                {
                    new OptionDefinition
                    {
                        Key = "mdDialect",
                        Label = "Markdown 变体",
                        Type = OptionType.Enum,
                        DefaultValue = "auto",
                        Choices = new List<OptionChoice>
                        {
                            new OptionChoice { Value = "auto", Label = "自动（默认 markdown）" },
                            new OptionChoice { Value = "gfm", Label = "gfm（GitHub Flavored Markdown）" },
                            new OptionChoice { Value = "commonmark", Label = "commonmark" },
                            new OptionChoice { Value = "commonmark_x", Label = "commonmark_x（扩展）" },
                            new OptionChoice { Value = "markdown_strict", Label = "markdown_strict（原版）" },
                            new OptionChoice { Value = "markdown_mmd", Label = "markdown_mmd（MultiMarkdown）" },
                            new OptionChoice { Value = "markdown_phpextra", Label = "markdown_phpextra" }
                        }
                    }
                };
            }
            return output;
        }

        protected override string BuildArguments(ConversionRequest request, string outputPath)
        {
            var sb = new StringBuilder();

            // 输入不指定 -f：交给 pandoc 自动识别
            sb.Append(ProcessRunner.Quote(request.InputPath));

            var outExt = request.OutputExtension ?? string.Empty;
            if (!outExt.StartsWith(".")) outExt = "." + outExt;

            // 输出显式指定 writer（不依赖扩展名猜测，避免 .mediawiki 等被默认成 markdown）
            string writer;
            if (WriterByExtension.TryGetValue(outExt, out writer))
            {
                // markdown 变体选项可覆盖默认 writer
                if ((outExt == ".md" || outExt == ".markdown")
                    && request.Options != null
                    && request.Options.TryGetValue("mdDialect", out var dialect)
                    && !string.IsNullOrEmpty(dialect)
                    && !string.Equals(dialect, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    writer = dialect;
                }
                sb.Append(" -t ").Append(writer);
            }

            sb.Append(" -o ").Append(ProcessRunner.Quote(outputPath));
            return sb.ToString();
        }
    }
}
