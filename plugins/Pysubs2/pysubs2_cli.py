# PyInstaller 入口脚本（不能用 pysubs2/__main__.py 直接打包）：
# __main__.py 内部用相对导入（from .cli import Pysubs2CLI），作为顶层脚本执行时
# 没有父包上下文会报 "attempted relative import with no known parent package"。
# 这里以绝对导入方式加载库再调用 CLI，包上下文完整。
import sys

from pysubs2.cli import Pysubs2CLI

if __name__ == "__main__":
    rv = Pysubs2CLI()(sys.argv[1:])
    sys.exit(rv)