# PyInstaller 入口脚本（不能用 markitdown/__main__.py 直接打包）：
# __main__.py 内部的相对导入（如 from .__about__ import __version__）在作为顶层
# 脚本执行时没有父包上下文，会报 "attempted relative import with no known parent package"。
# 这里以绝对导入方式把 markitdown 作为库加载，再调用其 main()，包上下文完整。
import sys

from markitdown.__main__ import main

if __name__ == "__main__":
    sys.exit(main())
