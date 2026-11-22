# v0.5.5 — 修复窗口变成透明的问题

这一版只修一个界面缺陷，没有新功能。

## 症状

有时候把窗口从托盘叫回来，只剩下左上角一小条可见 —— 标题、三张数据卡、两行开关、
状态栏全部不见了，像一块透明玻璃。必须完全退出程序再启动才能恢复。

用户实际遇到时的截图：外框和「WujieFan」品牌标还在，最小化按钮挤在标题左边，
下面一片空白。

## 根因

窗口是无边框的，四个圆角靠自己给窗口设 `Region` 裁出来。原来算这个 Region 用的
是窗口当时的 `Width` / `Height`：

```csharp
using var path = Round.Path(new RectangleF(0, 0, Width, Height), Dpi.PxF(this, 12));
Region = new Region(path);
```

问题在于**窗口最小化时 `Width` / `Height` 不是窗口大小**。实测最小化后普通窗体的
`Width=237`、`Height=39`（系统给的占位矩形），`ClientSize` 直接是 0。

而状态栏每秒都在刷新（`SetStatus` → `Relayout` → 重设 Region）。只要有一拍赶上
「已经最小化/已收进托盘」的瞬间，就会把 237×39 这个小矩形写成窗口的裁剪区域。
恢复时没有任何地方重算它，于是窗口永久只剩左上角一条 —— 这就是为什么症状时有时无、
必须重启才好。

## 修法

**Region 不再用 `Width` / `Height`。** `Relayout` 本来就已经算出这一帧的目标尺寸
（`w` 和累加出来的 `y`），直接把它传下去：

```csharp
UpdateRegion(w, y);
```

这样 Region 永远不可能来自系统占位矩形。另外保留一个不带参数的重载，供「刚恢复、
还没走 Relayout」的路径使用 —— 它取 `ClientSize` 与 `RestoreBounds` 里**较大的那个**：
稳定状态下两者相等，过渡期较小的那个一定是过时值。最小化时 `RestoreBounds` 仍是
真实大小，所以这条路也裁不错。

配套两处：

- `OnFormResize` 在切回 `Normal` 时补一次 `UpdateRegion()`，因为恢复本身不一定触发
  `Relayout`。
- 新增 `MainForm.RefreshChrome()`，`TrayHost.Restore()` 在把窗口叫回来之后调用它。
  `ShowInTaskbar`、`Show()`、`WindowState` 这三步都可能重建窗口句柄，而 DWM 原生圆角
  是**绑在句柄上的** —— 新句柄默认是直角，且 Region 可能还是最小化期间算的那个尺寸。

## 验证

- `dotnet build WujieFan.sln -c Release` → 0 警告 0 错误。
- 单测 222/222 通过（本版未新增用例，`MinTestCount` 未变）。
- `pwsh tools/verify.ps1` → 5/5 全过，EXITCODE=0。
- **反射探针实测产品路径**（收进托盘 → 隐藏期间连刷 8 次状态栏 → 托盘叫回）：
  - 修复前：`Region` 从 `432x390` 变成 `237x39`，恢复后窗口 `432x390` 但 Region 仍是
    `237x39` → 只剩左上角一条，与用户截图一致。
  - 修复后：隐藏期间 Region 稳定为 `432x372`（状态栏变高后的真实高度），恢复后
    Region 与窗口尺寸**逐像素一致**。
- `DrawToBitmap` 像素级对照：恢复后的窗口完整画出标题、三张数据卡、两行开关、
  状态栏与脚注小字，与正常态无差异。

## 未覆盖

- 没有在用户真机上复现「托盘反复收放 + 电源事件」的长时间循环，只跑了单次路径。
- 探针用 `Application.DoEvents` 泵消息，不走完整消息循环；窗口状态转换本身在探针里
  不完全可靠，所以探针的结论只对 Region 值本身负责（这恰好就是本次修的东西）。
