# SnugWin PR #1 验收报告

[应用 PR](https://github.com/nicocart/snug-win/pull/1) · [跟踪 Issue（fork）](https://github.com/WilliamLambertCN/snug-win/issues/1) · [被测提交 68750dc](https://github.com/WilliamLambertCN/snug-win/commit/68750dcfaa01c0d42441cdb16ef7e9f366f593f6)

**结论：本机显示安全验收未通过；不要把此构建当作已解决主屏卡住的版本。** Release 构建及 298 项自动化测试通过，但在原版驱动上观察到电脑不再向原物理主屏输出。未确定由哪个组件切换了输出，未把异常期间的竖屏误算成本应用成功创建的屏。上游未开启 Issues，因此 PR 关联的是贡献者 fork 的 Issue。

| 验收项 | 结果 | 依据 |
| --- | --- | --- |
| Release 构建和全量测试 | 通过 | Windows x64、.NET SDK 10.0.404。将警告视为错误：零警告、零错误；298/298 项通过、零跳过，其中新增 27 项。 |
| 固定与默认模式区分 | 自动化通过 | 小米 15 Ultra 竖屏启用 `fixedNativePixels` 时只注册 1440×3200 @ 60 Hz；内容变宽或收到旧宽度请求不改像素。不启用时仍按原 UU 适配从 1080×1908 开始，不影响其他屏。[新增测试](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/tests/SnugWin.Core.Tests/FixedNativePixelTests.cs)覆盖两种驱动协议、配置保存及错误回读时只撤回本次目标。 |
| 建屏身份、失败回滚、会话采样 | 自动化通过 | 已有屏和身份不明的屏不被认领或删除；撤屏失败可重试；慢采样与孤立写入分别覆盖。见[到达与撤屏测试](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/tests/SnugWin.Core.Tests/DisplayArrivalAndRetirementTests.cs)及[会话采样测试](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/tests/SnugWin.Core.Tests/SessionSamplingTests.cs)。 |
| 交付与配置 | 本机通过 | 已实际运行 `1.3.0-localfix.3`。设置页读取到目标小米竖屏已启用；逐字段比较只有该屏的固定像素选项改变，自启和快捷方式保留。此项不代表建屏成功。 |
| 原主屏保持活动、固定像素双屏共存 | **现场未通过，停止测试** | 原来唯一的 3840×2160 @ 60 Hz、200% 物理主屏不在活动桌面中时，Windows 只报告另一条 1440×3200 @ 144 Hz、150% 的输出路径。应用请求的是 60 Hz；只看分辨率不能把这个目标归为本应用创建。随后回读又与完整原桌面快照一致，用户另行确认实际操作已经恢复。恢复不是对固定像素建屏的验收；谁发起了输出切换仍未确定。 |
| 手动模式下的建屏日志 | 未就绪 | 在问题前记录到手动模式和四次建屏未上线的失败，未取得可用的已确认目标。独立原生基线测试在提交建屏请求前停止；没有成功的前后对比轮次。 |
| 手机 UU 实连、视频流、边缘点击、滚动、断开恢复 | 未验证 | 应用只控制 Windows 虚拟屏，不设置 UU 编码分辨率或码率。 |
| 长时间运行、驱动内存 | 未验收 | [驱动 PR #236](https://github.com/MolotovCherry/virtual-display-rs/pull/236)独立跟踪驱动引用泄漏；应用回归数不计入驱动测试。 |

## 现场复盘：设备是谁的，和谁切了主屏，是两个问题

- **四次失败不是四次空操作。** 应用记录的是手动模拟控制端，不是真实手机握手。在约 83 秒内，首次尝试后分别等 5、15、30 秒重试；每次开始前仍能读到 4K 屏。四次“未上线”前后各有一条 Windows Kernel-PnP 1010 事件，标的是 Cherry 监视器离开总线。事件证明设备发生了变化，不告诉我们是谁下令移除，也不能把它们算成四次成功建屏。
- **后来只剩下的竖屏更像 UU/GameViewer 的输出。** 故障快照中的 GDI 显示名，事后映射到 GameViewer 适配器。该快照约 30 秒后，Windows 记录了一块 GameViewer 监视器移除；其父适配器的硬件类型也与上述映射一致。随后原 4K 桌面恢复，用户确认能操作。历史快照没保存目标接口，GDI 名称可以复用；两份证据支持设备来源判断，但不能证明是 UU、SnugWin 还是系统发起了切换。不能从中推出整机死锁、触控被接管或驱动内存泄漏。

**源码为什么没兜住：**

1. [建屏及重试](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/src/SnugWin.Core/PhoneScreen/PhoneDesk.cs#L588-L638)对所有异常套用同一重试策略。新目标身份不明，和写驱动前的临时失败，不该同样处理。[桌面恢复](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/src/SnugWin.Core/PhoneScreen/PhoneDesk.cs#L723-L749)只比较仍能找到的旧屏；已消失的物理屏不会被算作变化。建屏抛异常时也不走成功分支的这次比较。
2. [等待目标出现](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/src/SnugWin.Windows/Display/PhoneDisplayService.cs#L138-L207)的两个阶段共用“未上线”：可能没确认新设备身份，也可能设备已出现、但没有成为独立副屏。现有日志分不出来。[模式回读](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/src/SnugWin.Windows/Display/PhoneDisplayService.cs#L323-L346)核查宽高和缩放，却没有把刷新率也作为就绪条件。
3. [撤屏](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/src/SnugWin.Windows/Display/PhoneDisplayService.cs#L276-L292)核查目标确实归本次创建，但发送删除前，不核查它是否已变成唯一活动输出。[已有测试](https://github.com/WilliamLambertCN/snug-win/blob/68750dcfaa01c0d42441cdb16ef7e9f366f593f6/tests/SnugWin.Core.Tests/DisplayArrivalAndRetirementTests.cs#L68-L84)甚至允许删除已成为主屏的自有目标。这是独立查到的风险，不是认定这次删掉了唯一屏。

**下次验收的硬条件（尚未实现或验证）：**

- 先用 fixture 模拟主屏中途消失、UU 独占一屏、目标刷新率不符、自有屏变成唯一输出。首次出现这些情况，要停止本次会话的建屏重试，不接管输入，不删除唯一输出或外部屏。错误要区分“请求已发送”“设备已识别”“已成为副屏”“模式已回读”；只记录阶段和显示参数，不记录私有设备标识和连接地址。
- 先在有独立恢复手段的测试机上，用便携版本、自启关闭、单次建屏；分别测仅 UU、仅 SnugWin、最后两者一起。只允许一个组件负责建屏；如果 UU 已接管桌面，SnugWin 应停下，不尝试夺回主屏。旧版 Cherry 管道只允许一个客户端，监测工具不应为取样另占一条连接。不能把测试版本直接切入用户已启用的自启路径再做首轮验证。
- 真机通过必须同时看到原物理主屏保持原模式、位置、缩放和主屏身份，**另有**一条可确认归属的 1440×3200 @ 60 Hz 非主屏；手机边缘点按、滚动正确；断开后只撤自己的副屏并恢复窗口。中途丢主屏或出现唯一输出即停，不靠最后一次快照与起点一致冒充全程正常。

## 历史对照

[上一版验收报告（dd5fb66，localfix.2）](https://github.com/WilliamLambertCN/snug-win/blob/89bdc10b990b1654fb5bf52f1d74fd68596aad2a/docs/acceptance/SnugWin-PR1.md)：271 项回归及两轮原版 Cherry v0.3.1 的实际显示测试通过。默认适配回读 1080×1908、1120×1980、1080×1848，原有三块屏的设置不变。该结果不属于本次固定 1440×3200 模式。

## 复核方法

在被测提交的仓库根目录运行：

```powershell
dotnet build SnugWin.sln -c Release -warnaserror
dotnet test SnugWin.sln -c Release --no-build
```

现场已停止建屏及驱动替换。下一次端到端验收应使用有独立本地控制台的测试机，不在受影响的主力机上反复试。先记录已有屏幕的模式、位置、缩放和主屏标记，再给一个手机竖屏单独启用 `fixedNativePixels`。要求原物理主屏全程保留，额外出现一条身份可确认、回读为 1440×3200 @ 60 Hz 的路径；原屏消失即中止。之后才检查手机 UU 点按、滚动、断开撤屏与窗口归位。
