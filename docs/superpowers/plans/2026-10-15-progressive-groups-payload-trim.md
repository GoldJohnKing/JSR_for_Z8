# 渐进补齐 + 星标交互 + 悬浮参数 + 载荷裁剪 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 四项新需求：① 任务组渐进补齐（卡内添加按钮 + 拖放到卡）② 芯片星标/X 交互 ③ 悬浮显示关键参数 ④ 打包载荷裁剪（剥离实证不需要的文件）。

**Architecture:** VM 层把"建组时一次性校验"泛化为"组内可变文件集 + 元数据缓存 + 增量重校验"（TaskGroupViewModel 持有 per-frame RawMetadata，兼作悬浮参数数据源）；打包器加显式排除清单，E2E 在真 GPU 上兜底验证裁剪不破坏 assistant 路径。

**Tech Stack:** 同既有（.NET 8 + Avalonia 11.2.2 + Semi.Avalonia 11.2.1.10 + CT.Mvvm 8.3.2），无新增依赖。

**Spec:** 用户 2026-10-15 第二轮四点需求 + 本轮全部裁定（见 Global Constraints）。

## Global Constraints

- 构建/测试 Windows Host：`cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet test --nologo"`（基线 254/254）；禁止递归搜索 reference/、external/、samples/、~/.nuget；界面纯中文。
- 补齐交互（用户裁定）：卡内"添加 RAW…"按钮选择 **或** 拖放到卡片 = 给该组补文件；拖到空白/窗口其余位置 = 新建组。拖入>7 张或补齐后>7 张 → 与"文件数量不足"同样处理（组保留、Blocked、帧数不符文案显示实际数量）。
- 基准帧（用户裁定，回退默认式）：用户点过星标 → 以用户星标为准（星标文件被 X 移除后回退自动）；否则每次增删文件后自动按曝光规则重选（等曝光=自然序第 1 张；包围=曝光升序中间帧）。芯片显示顺序恒为自然序，星标只标记不重排；TaskGroup.FramePaths 契约不变（基准在前，其余按芯片显示序）。
- 星型按钮：默认空心；当前基准帧的星=实心高亮黄色（#F0A020 系）；点击星=设为基准（用户手动）。X 按钮=从组中移除该文件；移除最后一帧=移除整卡。
- 可编辑门控：仅 Ready/Blocked 卡可加文件/点星/X（Running/Done/Failed 锁定，与现有 SetReference 门控对齐）。
- 悬浮参数（无焦段）：机型 / 快门 / 光圈 / ISO / 位深 / 压缩 / 画幅 / 拍摄时间——数据源 RawMetadata（EXIF 已读），ToolTip 多行。
- 载荷裁剪清单（本轮，2.3.25 实际生效项 + 预置项）：`resources/jsr_v98_k14_backend.exe`、`resources/jsr_fast_k14_backend.exe`、`resources/_internal/_tcl_data/**`、`resources/_internal/_tk_data/**`、`resources/_internal/tcl86t.dll`、`resources/_internal/tk86t.dll`、`resources/_internal/cv2/opencv_videoio_ffmpeg*.dll`、`resources/_internal/wgpu/__pyinstaller/**`、`resources/z8_presets/**`（2.3.27 预置）；顶层 THIRD_PARTY_NOTICES.md 退出白名单。保留：Bridge.exe（必需）、jsr_backend.exe（实测 Compressor）、raw_bridge.exe、jxl_worker.exe（小，用途存疑保守保留）。
- build.bat 版本选择**不动**（仍取第一个匹配 2.3.25；切换 2.3.27 留给用户决策）。
- E2E 验证门禁：裁剪载荷重新打包嵌入后，`JSR4Z8_E2E=1` 全流程 E2E + `--selftest` 必须绿（本机 RX 7900 XTX 真 GPU）。
- 每任务一 commit；TDD。

## Review Focus

1. 并发补齐与运行中锁定：Running 卡上拖放/按钮必须无效 → Task 3 测试。
2. 补齐后 Ptc/Dark 刷新：Blocked→Ready 转换时 runner 拿到新匹配 → Task 3 测试（Ptc/Dark 可变）。
3. 手动基准回退：星标帧被 X → 自动规则接管，无悬空引用 → Task 3 测试。
4. 重复文件补齐：同一路径二进制大小写重复加入不得重复帧 → Task 3 测试。
5. 裁剪回归：排除清单改动后 PayloadTests 断言每个排除项不进 manifest、保留项仍进 → Task 1 测试；E2E 真机兜底 → Task 2。
6. 卡片拖放与窗口拖放冲突：卡上 Drop 必须 e.Handled，不得同时新建组 → Task 4（编译+绑定；行为靠路由处理序）。

---

### Task 1: 打包器排除清单

**Files:**
- Modify: `src/Jsr4Z8.Core/Payload/PayloadPacker.cs`
- Test: `tests/Jsr4Z8.Tests/PayloadTests.cs`
- Modify: `AGENT.md`（决策 2 修订：载荷不再携带 THIRD_PARTY_NOTICES.md——用户裁定 2026-10-15；§3 PayloadPacker 行补排除清单一句）

**Interfaces:**
- Produces: `public static readonly IReadOnlyList<string> ExcludedPayloadGlobs`（或等价私有清单+`IsExcluded(string relPath)` internal seam）；`EnumerateSourceFiles` 过滤排除项；顶层白名单变为 {bridge exe} ∪ resources/(减排除) 。

- [ ] **Step 1: 失败测试**：PayloadTests 新增 `Pack_ExcludesUnneededBackendFiles`——MakeFakeTree 伪造 `resources/jsr_v98_k14_backend.exe`、`resources/_internal/_tcl_data/x.enc`、`resources/_internal/cv2/opencv_videoio_ffmpeg500_64.dll`、`resources/z8_presets/Z8_0_ISO000064.dng`、顶层 `THIRD_PARTY_NOTICES.md` → Pack 后 manifest 无这些路径；bridge exe/resources 正常文件/`resources/licenses/*` 仍在。既有用例中 THIRD_PARTY_NOTICES 相关断言同步改（枚举源不再含它）。
- [ ] **Step 2: 红**（dotnet test --filter PayloadTests）。
- [ ] **Step 3: 实现**：排除清单常量（精确路径 + 两段 glob：`_internal/cv2/opencv_videoio_ffmpeg*.dll`、目录前缀匹配 `_internal/_tcl_data/`、`_internal/_tk_data/`、`_internal/wgpu/__pyinstaller/`、`z8_presets/`）；注释逐条写明依据（assistant 单形态 7 帧实测 Compressor=jsr_backend.exe；manual 标定→z8_presets 不查；Tcl/Tk=GUI 库；ffmpeg=视频库；wgpu/__pyinstaller=测试残留；notices=用户裁定）。
- [ ] **Step 4: 绿 + 全量 254 保持**。
- [ ] **Step 5: Commit** `feat(payload): 打包排除 k14 引擎/Tcl-Tk/ffmpeg/测试残留/notices，预置 z8_presets 排除`

### Task 2: 真 GPU E2E 验证裁剪载荷

**Files:**
- 无源码改动；产物：重打包的 `src/Jsr4Z8.Core/Payload/Embedded/payload.*`（gitignored）+ 验证记录
- Test: 现有 `E2eTests`（JSR4Z8_E2E=1 门控）+ `--selftest`

- [ ] **Step 1:** 删除 `src/Jsr4Z8.Core/Payload/Embedded/payload.bin` + `payload.manifest.json`，运行 `dotnet run --project tools\pack-payload -- <2.3.25 src> <Embedded>` 生成裁剪载荷；记录 manifest 文件数/原始字节对比（旧 1285 文件/633MB → 预期 ~430 文件/~378MB）。
- [ ] **Step 2:** `cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && set JSR4Z8_E2E=1&& dotnet test --nologo --filter FullyQualifiedName~E2eTests"`——全流程（真 GPU ~6min/全幅）必须绿。失败 → 定位被剥文件 → 从排除清单回退该项 → 重打包重跑。
- [ ] **Step 3:** 构建冒烟 `out\Jsr4Z8_v*.exe --selftest`（载荷释放/校验+LibRaw 冒烟）。
- [ ] **Step 4:** 验证记录写入本任务报告（manifest 对比 + E2E/selftest 输出节选）；如需源码回退则按 Step 2 修正并 commit，否则无代码提交，报告即交付物。

### Task 3: VM——渐进补齐/移除/星标/悬浮参数

**Files:**
- Modify: `src/Jsr4Z8.UI/ViewModels/TaskGroupCreator.cs`（返回值扩展 per-path 元数据）
- Modify: `src/Jsr4Z8.UI/ViewModels/TaskGroupViewModel.cs`（文件集可变 + 缓存 + 重校验 + 星标/X + 悬浮文本）
- Modify: `src/Jsr4Z8.UI/ViewModels/MainViewModel.cs`（AddValidated 适配新创建产物；卡级添加入口）
- Test: `tests/Jsr4Z8.Tests/TaskGroupVmTests.cs`、`tests/Jsr4Z8.Tests/TaskGroupCreatorTests.cs`

**Interfaces:**
- Produces:
  - `CreatedTaskGroup(TaskGroup Group, ValidationResult Validation)` → 扩展为 `CreatedTaskGroup(TaskGroup Group, ValidationResult Validation, IReadOnlyDictionary<string, RawMetadata> Metadata)`（路径→元数据，大小写不敏感键）。
  - TaskGroupViewModel 新 API：`Task AddFilesAsync(IReadOnlyList<string> paths)`（过滤 .nef/去重 vs 现有/后台读新文件并入缓存/重排重校验/应用）；`RemoveFile(FrameChipViewModel chip)`；`SetReference`（语义改：标记不重排——chips 恒自然序显示，Group=[基准,其余显示序]，置 `_manualReference=true`）；自动路径 `_manualReference=false` 时增删后重算（曝光中间帧规则复用 creator 的选取函数，抽公共静态 `TaskGroupCreator.PickReference(IReadOnlyList<string> paths, Func<string,RawMetadata> read)`）。
  - `FrameChipViewModel`：改 class（ObservableObject）：`FileName/Path/IsReference/InfoText`（悬浮多行文本：机型\n快门\n光圈\nISO\n位深\n压缩\n画幅\n拍摄时间，缺失项显示 —）。
  - Ptc/Dark：`private set` + 应用时刷新（Blocked↔Ready 转换随重校验结果走 GoReady/GoBlocked，更新 IssuesText）。
  - 可编辑门控 `IsEditable => State is Ready or Blocked`——AddFiles/星标/X/添加按钮全部用它（Running/Done/Failed 锁定）。
  - 移除最后一帧 → 触发 RemoveRequested（卡消失）。
- Consumes: Task 1 无依赖；MainViewModel.AddFilesAsync（新建组路径）复用扩展后的 creator。

- [ ] **Step 1: 失败测试**（TaskGroupVmTests 新增，用可注入 reader 的 AddFilesAsync 测试缝——VM 构造函数加 `Func<string,RawMetadata>? read = null` 测试参数）：
  - `AddBelowSeven_BlockedThenTopUp_Ready`（3 帧建组 Blocked 帧数不符 → 加 4 帧 → Ready 且 Ptc/Dark 非空）
  - `AddBeyondSeven_BlockedWithFrameCount`（7 帧 Ready → 再加 1 → Blocked 帧数不符，实际 8 帧）
  - `AddDuplicatePath_NoDupFrame`（大小写重复路径 → 仍 7 帧）
  - `RemoveReference_ManualStar_FallsBackToAuto`（星标第 5 帧 → X 它 → 自动规则接管）
  - `Star_NoReorder_GroupContractKept`（星标后 chips 顺序不变、Group.FramePaths[0]=星标帧）
  - `RemoveLastFile_RemovesCard`（RemoveRequested 触发）
  - `LockedStates_RejectEdits`（Running/Done/Failed：AddFiles/RemoveFile/SetReference 无效）
  - `InfoText_ContainsShutterIsoModel`（含 1/8000s、ISO3200、Z 8、f/4 格式）
  - `AutoReference_RecomputedOnAdd`（3 帧包围 → 加至 7 帧后基准=新集合曝光中间帧）
  - TaskGroupCreatorTests：`CreateAsync_ReturnsMetadata`（返回字典含 7 路径）；`PickReference_PublicContract`（等曝光/包围/并列三分支）
- [ ] **Step 2: 红**。
- [ ] **Step 3: 实现**（重校验复用 TaskGroupValidator.Validate + memoized reader 读缓存；新文件读取放 Task.Run；UI 线程应用：chips 重建=自然序、IsReference 标记、InfoText 生成；重排仅影响 Group 路径序）。
- [ ] **Step 4: 绿 + 全量**。
- [ ] **Step 5: Commit** `feat(ui): 任务组渐进补齐/移除/星标基准/悬浮参数——VM 层`

### Task 4: Views——卡内添加按钮、星标/X、悬浮、拖放补齐

**Files:**
- Modify: `src/Jsr4Z8.UI/Views/TaskCardView.axaml(.cs)`、`src/Jsr4Z8.UI/Views/MainWindow.axaml.cs`（若需）
- Test: 编译 + x:CompileBindings + 全量 suite（行为已由 Task 3 VM 测试覆盖）

**Interfaces:**
- Consumes: Task 3 的 AddFilesAsync/RemoveFile/SetReference/IsEditable/FrameChipViewModel.InfoText。

- [ ] **Step 1:** 芯片模板：`[★星标按钮][文件名 TextBlock(ToolTip=InfoText)][✕按钮]`——星标用 feather Star path（`M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z`）：非基准=Stroke Text3 空心、IsHitTestVisible；基准=Fill #F0A020 实心（Blocked 橙警告色系一致）；点击→SetReferenceCommand(chip)。✕=row-action-danger 小按钮→RemoveFileCommand(chip)，ToolTip 移除此帧。文件名 ToolTip.Tip={Binding InfoText}（多行纯文本）。
- [ ] **Step 2:** 头部动作区新增 `添加 RAW…`（Small+FolderPlus，IsEditable 时可见）——Click 打开 NEF 多选选择器→`await vm.AddFilesAsync(paths)`（选择器逻辑放 code-behind，同 MainWindow 既有模式）。
- [ ] **Step 3:** 卡片拖放补齐：TaskCardView 根 Border AllowDrop + DragOver（含 NEF 且 IsEditable → Copy + st-droptarget 高亮类；否则 None）+ Drop（e.Handled=true；NEF→AddFilesAsync）。窗口级 Drop 处理保持（卡未处理时冒泡新建组）。DispatcherTimer 悬停消隐同窗口模式或简化为 DragLeave 清除。
- [ ] **Step 4:** 移除旧的"点击芯片本体换基准"PointerPressed 转发（星标按钮取代）；chips 宿主 IsEnabled 绑 IsEditable。
- [ ] **Step 5:** 全量 suite 绿 + 编译零警告。
- [ ] **Step 6: Commit** `feat(ui): 卡内添加按钮+星标/X 芯片+悬浮参数+拖放补齐`

### Task 5: 文档同步 + 终验

**Files:**
- Modify: `AGENT.md`（§5 决策 4 补充星标/补齐/回退语义；决策 2 修订已在 Task 1；§8 加本轮计划行；§3 架构 UI 行补"渐进补齐"）、`README.md`（使用说明：可先拖少于 7 张再补齐、星标换基准、悬浮看参数；构建节注明载荷裁剪与验证方式）

- [ ] **Step 1:** 两文档同步（沿用既定行文；不写未实测的 exe 体积数字，写载荷原始字节缩减与 E2E 验证结论——数字取 Task 2 报告）。
- [ ] **Step 2:** 全量 `dotnet test` 绿。
- [ ] **Step 3: Commit** `docs: 渐进补齐/星标交互/载荷裁剪同步文档`
