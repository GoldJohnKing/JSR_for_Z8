# 拖放遮罩修复 + 状态条 + ±EV 胶囊 + 即时建卡 Loading 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用户手测反馈四项：拖放遮罩滞留修复、进度条下移+状态色、非等曝光组 ±EV 偏差胶囊、建卡/补齐即时反馈（Loading）。

**Architecture:** 全部在 UI 层（MainWindow/TaskCardView 视图 + MainViewModel/TaskGroupViewModel/FrameChipViewModel VM）；Core 不动。Task C 把"先校验后建卡"翻转为"先建卡后校验"（卡片自持 IsValidating 生命周期）。

**Tech Stack:** 既有栈，无新依赖。**Spec:** 用户 2026-10-15 第三轮四点反馈（下方 Global Constraints 逐条钉死）。

## Global Constraints

- 构建/测试 Windows Host；基线 267/267；禁止递归搜索 reference/、external/、samples/、~/.nuget；界面纯中文；每任务一 commit；TDD（VM 逻辑）；视图靠 x:CompileBindings+全量套件。
- F1 遮罩：窗口 DragOver 订阅加 `handledEventsToo:true`，处理分支：`e.Handled==true`（卡片等后代接管）→ 立即 `IsDragOver=false`+停表+return（不设 e.Handled）；否则原逻辑（Handled=true/效果/IsDragOver/续期）。新增 DragLeave 处理器立即隐藏。计时器保留为兜底。
- F2 进度条：移至卡内容 StackPanel 最后（贴卡底，同 F1 前顶部贴边手法）；恒可见；VM 新增 `double BarValue => IsRunning ? Progress : 100`（OnProgressChanged/OnStateChanged 通知）；颜色：st-ready→就绪灰（与统计条同资源）、st-done→Success、st-failed→Danger、st-blocked→无法执行色（与统计条同资源，实现者从 MainWindow 统计条样式抄准确键名）、st-running 不设（Semi 默认主色）。
- F3 ±EV：`IsBracketed`（可读且 ExposureTime>0 的帧中任两帧曝光超容差即真，容差同 PickReference）；EV=log2(t/t当前基准)，显示 (ev>0?"+":"")+ev.ToString("0.0")，|ev|<0.05→"0.0"，不可读/缺曝光→"—"；基准帧=0.0。芯片模板：IsBracketed→[EV 标签按钮（独立底色，点击=SetReferenceCommand(chip)）|竖线分隔|文件名|✕]；等曝光→保留现有星标按钮。分体胶囊参考既有 chip/reference 样式；基准芯片整体 Primary 底不变。RebuildChips 与 ApplyReference 均重算 EV。
- F4 即时建卡：MainViewModel.AddFilesAsync=过滤 NEF→post 立即建卡（`new TaskGroupViewModel(new TaskGroup(paths), validation:null, runner)`，接线 RemoveRequested/计数订阅）→`await card.ValidateAsync()`；卡新增 `Task ValidateAsync()`（IsValidating=true→后台读全部未缓存帧+重算+应用→finally false；异常→Blocked+「校验时发生错误：{msg}」）与 `AddFilesAsync` 同用 IsValidating 包裹；`IsEditable => !IsValidating && (Ready or Blocked)`；StateText 在 IsValidating 时="校验中"；ValidatingCount 改为派生 `Tasks.Count(t=>t.IsValidating)`（订阅卡 PropertyChanged）；TaskGroupCreator.CreateAsync 主流程退役（保留 PickReference/类型供复用）。视图：卡根 Border 内容改 Grid[StackPanel, Loading 覆盖层]——覆盖层 IsVisible=IsValidating、SemiColorOverlayBackground、居中旋转 loader 图标（feather Loader path，RotateTransform 0.8s 无限动画）+文字 校验中。
- 测试适配：MainViewModel 系列测试改断言即时建卡语义；新增 ValidateAsync/IsValidating/BarValue/EV 文本/IsBracketed 用例。

## Review Focus

1. F4 建卡→校验失败（读取异常）不悬死 Loading → Task C 测试。
2. F3 换基准后 EV 全组重算（含负值变 0.0）→ Task B 测试。
3. F1 卡片接管时窗口不再置 IsDragOver（handledEventsToo 分支顺序）→ Task A 代码走查+人工复测。
4. F2 就绪卡进度条满宽灰、Done 满宽绿 → Task A 测试（BarValue）+样式核对统计条同键。
5. Loading 覆盖层期间编辑入口全锁（IsEditable）→ Task C 测试。

---

### Task 1: 拖放遮罩即时隐藏 + 进度条下移状态色

**Files:** Modify `src/Jsr4Z8.UI/Views/MainWindow.axaml.cs`、`src/Jsr4Z8.UI/Views/TaskCardView.axaml(.cs)`、`src/Jsr4Z8.UI/ViewModels/TaskGroupViewModel.cs`（BarValue）；Test `tests/Jsr4Z8.Tests/TaskGroupVmTests.cs`。
**Interfaces:** Produces `double TaskGroupViewModel.BarValue`（IsRunning?Progress:100，通知齐）；窗口 OnDragOver 新分支+OnDragLeave。
- [ ] Step1 失败测试：`BarValue_RunningTracksProgress_OtherStatesFull`（Ready→0?否——Ready→100；Running ReportProgress(42)→42；Done→100；Blocked→100）。先红（BarValue 不存在）。
- [ ] Step2 红 → Step3 实现（F1 code-behind 分支/ DragLeave；F2 XAML 挪位置+样式，色键抄统计条）→ Step4 绿+全量 → Step5 Commit `fix(ui): 遮罩随卡片接管即时隐藏+DragLeave隐藏；进度条下移+状态色`。

### Task 2: ±EV 偏差胶囊

**Files:** Modify `TaskGroupViewModel.cs`（IsBracketed/FrameChipViewModel.EvText/重算）；`TaskCardView.axaml`（芯片模板分体+星标互斥）；Test `TaskGroupVmTests.cs`。
**Interfaces:** Produces `bool IsBracketed`、`string FrameChipViewModel.EvText`。
- [ ] Step1 失败测试：`Bracketed_EvTexts_RelativeToReference`（曝光 1/125..1/8000 组：基准=0.0、两倍=+1.0、一半=-1.0、1/3 档=+0.3/-0.3 取舍按 0.0 格式）；`EqualExposure_NotBracketed`；`SetReference_RecomputesAllEv`；`UnreadableFrame_EvDash`。
- [ ] Step2 红 → Step3 实现（EV 计算+IsBracketed 判定+两处重算+模板）→ Step4 绿+全量 → Step5 Commit `feat(ui): 非等曝光组 ±EV 偏差胶囊替代星标（点击设基准）`。

### Task 3: 即时建卡 + 校验 Loading

**Files:** Modify `MainViewModel.cs`、`TaskGroupViewModel.cs`、`TaskCardView.axaml(.cs)`；Test `TaskGroupCreatorTests.cs`、`TaskGroupVmTests.cs`。
**Interfaces:** Produces `Task ValidateAsync()`、IsValidating 生命周期、派生 ValidatingCount、MainViewModel.AddFilesAsync 新流程、Loading 覆盖层。
- [ ] Step1 失败测试：`AddFiles_CardAppearsImmediately_ThenValidates`（read 缝 TCS 阻塞：建卡即 Tasks.Count==1 且 IsValidating；释放后 Ready）；`ValidateAsync_ReadThrows_BlockedWithFriendlyMessage_NotStuckLoading`；`ValidatingCount_DerivedFromCards`；`IsEditable_FalseWhileValidating`；`AddFilesAsync_CreatesCardViaPost` 等既有用例适配。
- [ ] Step2 红 → Step3 实现（VM 流程+覆盖层+旋转动画；MainViewModel 重写 AddFilesAsync/计数派生；CreateAsync 退役仅注释标注）→ Step4 绿+全量 → Step5 Commit `feat(ui): 拖入即建卡+卡片级校验 Loading`。

### Task 4: 文档 + 终验

**Files:** Modify `AGENT.md`（§5 决策 4 补 EV 胶囊/即时建卡/Loading 语义一句）、`README.md`（使用说明两行）。
- [ ] Step1 两文档同步 → Step2 全量绿 → Step3 Commit `docs: EV 胶囊与即时建卡 Loading 同步`。
