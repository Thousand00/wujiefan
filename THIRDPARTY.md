# 第三方组件

## PawnIO (LpcIO.bin 模块)

- 来源: https://github.com/namazso/PawnIO
- 许可: GNU Lesser General Public License v2.1 (LGPL-2.1)
- 本仓库以嵌入资源形式分发 `src/WujieFan/Resources/LpcIO.bin`（嵌入名 `WujieFan.Core.LpcIO.bin`）。

LGPL 2.1 要求：

1. 保留版权与许可声明（见上文来源）。
2. 允许使用者替换该库。本项目以**独立数据文件**形式嵌入，替换方式为：
   用上游发布的同名 `LpcIO.bin` 覆盖 `src/WujieFan/Resources/LpcIO.bin` 后重新构建；
   或将 `dist/app/` 视为可分发包，按 LGPL 要求提供替换途径。
3. 若修改了该模块，必须以 LGPL 2.1 公开修改后的源码。

完整的 LGPL 2.1 文本见 https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt

## 本项目自有代码

MIT License，见仓库根目录 `LICENSE`。
