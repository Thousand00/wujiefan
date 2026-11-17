# 第三方组件

## PawnIO (LpcIO.bin 模块)

- 来源: https://github.com/namazso/PawnIO
- 许可: GNU Lesser General Public License v2.1 (LGPL-2.1)
- 本仓库以嵌入资源形式分发 `src/WujieFan/Resources/LpcIO.bin`（嵌入名 `WujieFan.Core.LpcIO.bin`）。

LGPL 2.1 要求：

1. 保留版权与许可声明（见上文来源）。
2. 允许使用者替换该库。需要如实说明的是：本项目把它作为 **EmbeddedResource** 编进
   `WujieFan.Core.dll`（见 `src/WujieFan.Core/WujieFan.Core.csproj`，逻辑名
   `WujieFan.Core.LpcIO.bin`），因此 `dist/app/` 里**没有**可替换的 `LpcIO.bin` 文件，
   替换该模块必须替换 `src/WujieFan/Resources/LpcIO.bin` 后**重新编译**。
   LGPL 2.1 的合规通过「提供源码 + 构建脚本（`WujieFan.sln` / `tools/make-release.ps1`）
   使任何人都能重建并替换该组件」来满足。
3. 若修改了该模块，必须以 LGPL 2.1 公开修改后的源码。

完整的 LGPL 2.1 文本见 https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt

## 本项目自有代码

MIT License，见仓库根目录 `LICENSE`。
