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
- **MarkItDown** — Microsoft MarkItDown：PDF/Word/Excel/PPT/HTML/图片 OCR/电子书等转 Markdown。Python 运行时与依赖由 CI 用 PyInstaller 打成单文件 `tools\markitdown.exe` 随包分发，无需用户安装 Python。
- **MIDI** — MIDI 合成与音频转换（.mid/.midi → wav/mp3/ogg/flac/m4a）。FluidSynth + GeneralUser GS 音色库随包分发；实现 `IPreviewProvider`，主程序可直接预览 .mid/.midi。需要主程序 ≥ 2.0.2-dev.7。
- **ImageMagick** — 图像格式转换与处理（ICO/CUR/SVG/PSD/TIFF/EXR/HDR/相机 RAW 等 200+ 格式），支持质量与缩放参数。官方 portable 包（magick.exe + 依赖）随包分发；与内置 FFmpeg 重叠的格式（png/jpg/webp 等）由应用弹窗让用户选择用哪个。
- **LibreOffice** — Office 文档渲染转换（doc/docx/xls/xlsx/ppt/pptx → PDF 等）。完整版 LibreOffice 随包分发（约 500MB）；每次转换用独立临时 profile，可并发。
- **Pysubs2** — 字幕格式互转（srt/ass/ssa/vtt/sami/ttml/json），支持输入编码选择。pysubs2 为纯 Python，CI 用 PyInstaller 打成单文件随包分发。
- **Potrace** — 位图转矢量（BMP/PBM → SVG/PDF/EPS/PS），支持二值化阈值。官方无 Windows 预编译包，CI 用 MinGW 编译源码随包分发。
- **WebPdf** — 网页转 PDF（本地 HTML → PDF）。复用系统 Microsoft Edge 的无头模式渲染（现代 Chromium 内核），零体积不随包分发。
- **FasterWhisper** — 语音转文字/字幕（音频 → txt/srt/vtt）。Whisper base 模型（CTranslate2 int8）随包分发（约 140MB）；音频先经应用 ffmpeg 转 16kHz wav 再识别。
