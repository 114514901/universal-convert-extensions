# faster-whisper 语音转文字 CLI（PyInstaller 入口）。
# 用法：
#   whisper_cli <input.wav> -o <output> --model <ct2模型目录> [--format txt|srt|vtt] [--language zh|en|...]
#
# 进度通过 stderr 输出 "PROGRESS <0-100>" 行，由插件解析上报。
import argparse
import sys

from faster_whisper import WhisperModel


def fmt_ts(seconds, decimal=False):
    ms = int(seconds * 1000)
    h, rem = divmod(ms, 3600000)
    m, rem = divmod(rem, 60000)
    s, ms = divmod(rem, 1000)
    if decimal:
        return "%02d:%02d:%02d.%03d" % (h, m, s, ms)
    return "%02d:%02d:%02d,%03d" % (h, m, s, ms)


def main():
    parser = argparse.ArgumentParser(prog="whisper")
    parser.add_argument("input", help="输入音频（16kHz 单声道 wav）")
    parser.add_argument("-o", "--output", required=True, help="输出文件路径")
    parser.add_argument("--model", required=True, help="CTranslate2 模型目录")
    parser.add_argument("--format", choices=["txt", "srt", "vtt"], default="txt")
    parser.add_argument("--language", default=None, help="语言代码；缺省自动检测")
    args = parser.parse_args()

    model = WhisperModel(args.model, device="cpu", compute_type="int8")
    segments, info = model.transcribe(args.input, language=args.language)
    total = info.duration or 0.0

    txt_parts = []
    srt_parts = []
    vtt_parts = []
    for seg in segments:
        if total > 0:
            sys.stderr.write("PROGRESS %.1f\n" % (seg.end / total * 100.0))
            sys.stderr.flush()
        text = seg.text.strip()
        if not text:
            continue
        txt_parts.append(text)
        srt_parts.append(
            "%d\n%s --> %s\n%s\n" % (seg.id + 1, fmt_ts(seg.start), fmt_ts(seg.end), text))
        vtt_parts.append(
            "%s --> %s\n%s\n" % (fmt_ts(seg.start, True), fmt_ts(seg.end, True), text))

    with open(args.output, "w", encoding="utf-8") as f:
        if args.format == "txt":
            f.write("\n".join(txt_parts))
        elif args.format == "srt":
            f.write("\n".join(srt_parts))
        else:  # vtt
            f.write("WEBVTT\n\n" + "\n".join(vtt_parts))

    sys.stderr.write("PROGRESS 100\n")
    sys.stderr.flush()


if __name__ == "__main__":
    main()