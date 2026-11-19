# SnugWin PR #1 验收报告

[应用 PR](https://github.com/nicocart/snug-win/pull/1) · [跟踪 Issue（fork）](https://github.com/WilliamLambertCN/snug-win/issues/1) · [被测提交 68750dc](https://github.com/WilliamLambertCN/snug-win/commit/68750dcfaa01c0d42441cdb16ef7e9f366f593f6)

**结论：新增固定像素模式，Release 构建及 298 项自动化测试通过；这些结果不能代替 Windows 显示模式回读或手机通过 UU 的实连验收。** 上游未开启 Issues，因此 PR 关联的是贡献者 fork 的 Issue。

| 验收项 | 结果 | 依据 |
| --- | --- | --- |
| Release 构建和全量测试 | 通过 | Windows x64、.NET SDK 10.0.404。将警告视为错误：零警告、零错误；298/298 项通过、零跳过，其中新增 27 项。 |
| 固定与默认模式区分 | 自动化通过 | 小米 15 Ultra 竖屏启用 `fixedNativePixels` 时只注册 1440×3200 @ 60 Hz；内容变宽或收到旧宽度请求不改像素。不启用时仍按原 UU 适配从 1080×1908 开始，不影响其他屏。[新增测试](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/tests/SnugWin.Core.Tests/FixedNativePixelTests.cs)覆盖两种驱动协议、配置保存及错误回读时只撤回本次目标。 |
| 建屏身份、失败回滚、会话采样 | 自动化通过 | 已有屏和身份不明的屏不被认领或删除；撤屏失败可重试；慢采样与孤立写入分别覆盖。见[到达与撤屏测试](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/tests/SnugWin.Core.Tests/DisplayArrivalAndRetirementTests.cs)及[会话采样测试](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/tests/SnugWin.Core.Tests/SessionSamplingTests.cs)。 |
| 固定像素的 Windows 实际显示路径 | 待验证 | 不能用机型名、配置值或协议 fixture 代替真实显示模式回读。 |
| 手机 UU 实连、视频流、边缘点击、滚动、断开恢复 | 未验证 | 应用只控制 Windows 虚拟屏，不设置 UU 编码分辨率或码率。 |
| 长时间运行、驱动内存 | 未验收 | [驱动 PR #236](https://github.com/MolotovCherry/virtual-display-rs/pull/236)独立跟踪驱动引用泄漏；应用回归数不计入驱动测试。 |

## 历史对照

[上一版验收报告（dd5fb66，localfix.2）](https://github.com/WilliamLambertCN/snug-win/blob/89bdc10b990b1654fb5bf52f1d74fd68596aad2a/docs/acceptance/SnugWin-PR1.md)：271 项回归及两轮原版 Cherry v0.3.1 的实际显示测试通过。默认适配回读 1080×1908、1120×1980、1080×1848，原有三块屏的设置不变。该结果不属于本次固定 1440×3200 模式。

## 复核方法

在被测提交的仓库根目录运行：

```powershell
dotnet build SnugWin.sln -c Release -warnaserror
dotnet test SnugWin.sln -c Release --no-build
```

给一个 1440×3200 手机竖屏配置单独启用 `fixedNativePixels`。先记录已有屏幕的分辨率、位置、缩放和主屏标记。用手机通过 UU 连入，选中新出现的屏，检查 Windows 是否实际回读 1440×3200，用无敏感内容的窗口检查边缘点击和滚动；断开后等重连宽限期结束，再确认手机屏撤除、窗口归位、原有显示设置不变。
