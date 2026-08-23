using System;
using System.Collections.Generic;
using System.Linq;
using UniversalConvert.Core.Plugins;
using UniversalConvert.Core.Process;

namespace UniversalConvert.Plugin.Pandoc
{
    /// <summary>
    /// 文档格式转换插件，基于 Pandoc（工具随插件包分发在 tools\pandoc.exe）。
    /// </summary>
    public sealed class PandocPlugin : ExternalToolConverterBase
    {
        public override string Id => "com.universalconvert.pandoc";
        public override string Name => "Pandoc";
        public override string Description => "文档格式转换（md/docx/html/tex/epub 等），基于 Pandoc";
        public override string Version => "1.0.0";
        protected override string ToolName => "pandoc";

        private static readonly string[] DocInputs =
            { ".md", ".markdown", ".docx", ".html", ".htm", ".tex", ".epub", ".rst", ".odt", ".txt" };
        private static readonly string[] DocOutputs =
            { ".md", ".docx", ".html", ".tex", ".epub", ".rst", ".odt" };

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
                        .Select(o => new OutputFormat
                        {
                            Extension = o,
                            DisplayName = o.TrimStart('.').ToUpperInvariant()
                        })
                        .ToList()
                });
            }
            return caps;
        }

        protected override string BuildArguments(ConversionRequest request, string outputPath)
        {
            // Pandoc 自动识别输入格式，按输出扩展名确定输出格式
            return ProcessRunner.Quote(request.InputPath) + " -o " + ProcessRunner.Quote(outputPath);
        }
    }
}
