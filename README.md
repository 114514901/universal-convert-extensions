# UniversalConvert Extensions

UniversalConvert 的扩展插件仓库。每个插件一个目录，包含插件源码 + `manifest.json`。

## 插件包格式

一个插件包（zip）= `manifest.json` + 插件 DLL（+ 可选 `tools\` 工具二进制）。

- `manifest.json` 字段：id / name / description / version / author / minAppVersion / maxAppVersion / homepage
- 插件实现 `UniversalConvert.Core.Plugins.IConverterPlugin`（或继承 `ExternalToolConverterBase`）
- 编译时引用主仓库 Release 里的 `UniversalConvert.Core.SDK.zip`

## 新增插件步骤

1. 在 `plugins\<Name>\` 下新建 `<Name>Plugin.csproj` + `<Name>Plugin.cs` + `manifest.json`。
2. 更新 `index.json`，加入该插件及其下载地址。
3. 打 tag `{Name}-{版本}`（如 `Pandoc-1.0.0`），CI 自动构建、打包并发布。

## 已有插件

- **Pandoc** — 文档格式转换（md/docx/html/tex/epub 等），工具随包分发。
- **MarkItDown** — Microsoft MarkItDown：PDF/Word/Excel/PPT/HTML/图片 OCR/音频等转 Markdown。Python 运行时与依赖由 CI 用 PyInstaller 打成单文件 `tools\markitdown.exe` 随包分发，无需用户安装 Python。
