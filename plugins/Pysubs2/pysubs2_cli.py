# PyInstaller 入口脚本（不能用 pysubs2/__main__.py 直接打包）：
# __main__.py 内部用相对导入（from .cli import Pysubs2CLI），作为顶层脚本执行时
# 没有父包上下文会报 "attempted relative import with no known parent package"。
#
# 另外扩展了 pysubs2 不支持的 LRC 歌词格式（--to lrc 或输入为 .lrc）：
# lrc 只有开始时间 [mm:ss.xx]，转字幕时结束时间取下一条开始（最后一条 +2 秒）。
import os
import re
import sys

from pysubs2 import SSAFile, SSAEvent
from pysubs2.cli import Pysubs2CLI

LRC_TIME_RE = re.compile(r"\[(\d+):(\d+)(?:[.:](\d{1,3}))?\]\s*(.*)")


def parse_lrc_file(path, encoding):
    """解析 lrc 为 SSAFile（结束时间 = 下一条开始，最后一条 +2s；单位：毫秒）。"""
    subs = SSAFile()
    entries = []
    with open(path, "r", encoding=encoding, errors="replace") as f:
        for line in f:
            m = LRC_TIME_RE.search(line)
            if not m:
                continue
            minutes = int(m.group(1))
            seconds = int(m.group(2))
            frac_text = m.group(3) or "0"
            # 兼容 .xx / :xx / :xxx（毫秒取前两位）
            frac = int(frac_text[:2].ljust(2, "0"))
            start_ms = (minutes * 60 + seconds) * 1000 + frac * 10
            text = m.group(4).strip()
            if text:
                entries.append((start_ms, text))
    for i, (start_ms, text) in enumerate(entries):
        end_ms = entries[i + 1][0] if i + 1 < len(entries) else start_ms + 2000
        subs.append(SSAEvent(start=start_ms, end=end_ms, text=text))
    return subs


def write_lrc_file(subs, path, encoding):
    """把字幕写成 lrc（每条取开始时间，多行文本合并为空格分隔；时间单位：毫秒）。"""
    lines = []
    for ev in subs:
        total_ms = int(ev.start)
        minutes, rem = divmod(total_ms, 60000)
        seconds, ms = divmod(rem, 1000)
        lines.append("[%02d:%02d.%02d]%s" % (minutes, seconds, ms // 10, ev.text.replace("\n", " ").strip()))
    with open(path, "w", encoding=encoding) as f:
        f.write("\n".join(lines) + "\n")


def find_arg(argv, name):
    for i, a in enumerate(argv):
        if a == name and i + 1 < len(argv):
            return argv[i + 1]
    return None


def find_input_path(argv):
    """输入文件 = 最后一个非选项参数（跳过 --xxx 选项及其值）。"""
    value_options = {"--to", "--output-dir", "--input-enc", "--output-enc", "-f", "-t", "-o"}
    result = None
    skip_next = False
    for a in argv:
        if skip_next:
            skip_next = False
            continue
        if a in value_options:
            skip_next = True
            continue
        if a.startswith("-"):
            continue
        result = a
    return result


def main():
    argv = sys.argv[1:]

    # 定位输入文件（最后一个非选项参数）
    input_path = find_input_path(argv)

    to_format = find_arg(argv, "--to")
    output_dir = find_arg(argv, "--output-dir")
    input_enc = find_arg(argv, "--input-enc") or "utf-8"
    output_enc = find_arg(argv, "--output-enc") or "utf-8"

    is_lrc_input = input_path is not None and input_path.lower().endswith(".lrc")
    is_lrc_output = to_format == "lrc"

    if not (is_lrc_input or is_lrc_output):
        # 常规字幕格式：原样交给 pysubs2 CLI
        rv = Pysubs2CLI()(argv)
        sys.exit(rv)

    if not input_path:
        print("错误：缺少输入文件", file=sys.stderr)
        sys.exit(1)
    if not os.path.isfile(input_path):
        print("错误：输入文件不存在: %s" % input_path, file=sys.stderr)
        sys.exit(1)

    out_ext = "lrc" if is_lrc_output else to_format
    if not out_ext:
        print("错误：--to 缺失或无效", file=sys.stderr)
        sys.exit(1)

    out_dir = output_dir if output_dir else os.path.dirname(input_path) or "."
    os.makedirs(out_dir, exist_ok=True)
    base = os.path.splitext(os.path.basename(input_path))[0]
    out_path = os.path.join(out_dir, base + "." + out_ext)

    try:
        if is_lrc_output:
            subs = SSAFile.load(input_path)  # 输入为 pysubs2 支持的字幕格式
            write_lrc_file(subs, out_path, output_enc)
        else:
            subs = parse_lrc_file(input_path, input_enc)
            subs.save(out_path)  # 按扩展名自动选格式（srt/ass/vtt 等）
    except Exception as ex:
        print("LRC 转换失败: %s" % ex, file=sys.stderr)
        sys.exit(1)

    sys.exit(0)


if __name__ == "__main__":
    main()