# SnugWin PR #1 验收报告

[应用 PR](https://github.com/nicocart/snug-win/pull/1) · [跟踪 Issue（fork）](https://github.com/WilliamLambertCN/snug-win/issues/1) · [被测提交 dd5fb66](https://github.com/WilliamLambertCN/snug-win/commit/dd5fb668627789fc2596956787ba4070c37f00b7)

**结论：应用构建、自动化回归和真实 Windows 建屏链路通过；手机通过 UU 的端到端体验未验收。** 本报告针对 `1.3.0-localfix.2`，不是对外部驱动补丁的验收。

| 验收项 | 结果 | 依据 |
| --- | --- | --- |
| Release 构建及回归测试 | 通过 | Windows x64、.NET SDK 10.0.404；零警告、零错误，271/271 项通过，零跳过。 |
| 屏幕身份、失败回滚、会话采样 | 自动化通过 | 已有屏和身份不明的屏不被认领或删除；撤屏失败可重试；慢采样与孤立写入分别覆盖。见[到达与撤屏测试](https://github.com/WilliamLambertCN/snug-win/blob/dd5fb668627789fc2596956787ba4070c37f00b7/tests/SnugWin.Core.Tests/DisplayArrivalAndRetirementTests.cs)及[会话采样测试](https://github.com/WilliamLambertCN/snug-win/blob/dd5fb668627789fc2596956787ba4070c37f00b7/tests/SnugWin.Core.Tests/SessionSamplingTests.cs)。 |
| 建屏、调整尺寸、换比例、撤屏 | 真驱动通过，2 轮 | 直接加载自包含发布包里的 Core/Windows 程序集，连接原版 Cherry v0.3.1。按顺序回读到 1080×1908、1120×1980、1080×1848，缩放 175%。撤屏后桌面路径约 0.3 秒收敛；原有三块屏的模式、位置、缩放和主屏标记保持不变。 |
| 竖屏分辨率 | 不满足原生像素要求 | 本次使用 1440×3200 手机配置，但该提交的 UU 适配算法得到 1080×1908。不能据此声称 Windows 虚拟屏或手机视频流为 1440×3200。 |
| 手机 UU 实连、边缘点击、滚动、断开恢复 | 未验证 | 上面的原生测试驱动了 Windows 建屏服务，不是一次真实手机会话。 |
| 驱动长期内存 | 未验证 | 原版驱动的短测仍有内存增长。外部驱动的修复另由[驱动 PR #236](https://github.com/MolotovCherry/virtual-display-rs/pull/236)跟踪；应用测试不证明驱动泄漏已修复。 |

## 复核方法

在上述提交的仓库根目录，用项目指定的 SDK 运行：

```powershell
dotnet build SnugWin.sln -c Release -warnaserror
dotnet test SnugWin.sln -c Release --no-build
```

手工复核时，先记下已有屏幕的分辨率、位置、缩放和主屏标记。手机通过 UU 连入，选中新出现的手机屏，用无敏感内容的窗口检查边缘点击与滚动；断开，等重连宽限期结束，再确认手机屏消失、窗口归位、原有显示设置不变。若评估原生像素，还需直接回读虚拟屏模式，不能用机型名称、输入框数值或 DPI 代替。
