# 主界面内联任务组 + 曝光包围 + Save_Z8 风格重构 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 按用户 7 点需求重构 UI：内联任务组（删除新建对话框）、曝光包围基准帧选取、错误信息极简化、Save_Z8 风格主界面、失败即删临时文件 + 启动清扫、后端载荷旧版本目录清理。

**Architecture:** 三层改动：① Core（Validator 文案、TaskQueueRunner 临时目录生命周期、PayloadManager 旧版本清理）；② UI ViewModel（新增 TaskGroupCreator 承接内联建组 + 曝光包围排序，TaskGroupViewModel 卡内换基准/去日志，MainViewModel 直建卡片）；③ Views（MainWindow/TaskCardView 按 Save_Z8 视觉规范重写，删除 NewTaskDialog/LogPanel）。

**Tech Stack:** .NET 8 + Avalonia 11.2.2 + Semi.Avalonia 11.2.1.10 + CommunityToolkit.Mvvm 8.3.2（全部锁版本，不新增依赖）。

**Spec:** 用户 2026-10-15 七点需求（本文件 §Global Constraints 逐条钉死）+ 参考实现 `D:\GitRepos\Save_Z8\src\SaveZ8.UI\Views\MainWindow.axaml`。

## Global Constraints

- 界面纯中文；构建/测试在 Windows Host：`cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet test --nologo"`（当前基线 251/251）。
- 需求 1：仅快门速度不同的 7 帧合法（校验五维不含快门，现状即允许——补测试钉死）；默认基准帧=按曝光取**中间**（7 帧升序第 4 张）；7 帧等曝光时=自然排序第 1 张。
- 需求 2：UI 不再显示日志（删 LogPanel/日志开关/LogLines）；仅失败/报错显示错误信息。
- 需求 3：删除「新建任务组」窗口；拖放 NEF 直接在主界面创建任务组区块，交互（换基准/开始/停止/重试/移除/打开）与错误信息在区块内。
- 需求 4：成功时不显示任何匹配细节（7 个一致/PTC/黑场/ISO/满阱值全部消失）；仅不一致时显示原因并禁止执行（批量自动跳过）。错误原因文案（文件名前缀 + 原因）：`非同一机型拍摄`、`非同一 ISO 拍摄`、`图像尺寸不同`、`非同一位深拍摄`、`非同一 RAW 压缩拍摄`、`不受支持的机型：<机型>`、`不受支持的 ISO：<ISO>`、`不受支持的拍摄格式：<格式>`、`帧数不符`、`无法读取`、`ISO 值无效`。
- 需求 5：主界面风格对齐 Save_Z8（`D:\GitRepos\Save_Z8`）：Feather 线性图标（StreamGeometry）、渐变 logo 徽标 + 标题/副标题行、版本号 + 图标按钮顶栏、状态胶囊（pill+呼吸圆点）、卡片列表 + 总览计数条、空态引导区（三步指引）、拖放悬停遮罩、底栏 StatusText + Primary 动作按钮；窗口 960×640 / Min 760×520；窗口图标复用 Save_Z8 的 app.ico（作者自有资产）。
- 需求 6：失败/停止立即删除本任务临时目录（不再保留 ~3GB 诊断目录）；软件启动时清扫上一轮全部残留（`<TempBase>\Jsr4Z8\*`，不限年龄）。
- 需求 7：已有机制正确（载荷目录按 manifest 哈希前 16 位命名 + 每文件 SHA-256 校验 + 自愈重解压 + 原子释放），仅补强：EnsureExtracted 后清理非当前哈希的兄弟目录（旧版本残留 ~500MB/个）。
- 保持不动：后端调用契约、请求 schema、stdout 协议、取消杀树、DNG 完整化、五维一致性逻辑本身（只改文案）、设置对话框功能。
- 每任务一次 commit；测试临时目录 GUID+finally 清理；禁止全局 find（reference/ 649MB）。

## Review Focus

1. **非 NEF / 混合拖放**：不得建组、不得崩溃、光标 None → Task 4 测试（AddFilesAsync 空集 no-op）。
2. **并发校验**（连续两次快速拖放）：两组各自成卡、互不串扰 → Task 4 测试。
3. **曝光并列**（包围集中两帧同快门）：中间帧选取必须确定性（稳定排序）→ Task 4 测试。
4. **不可读文件**：建卡为 Blocked、错误含文件名、批量跳过 → Task 1 文案 + Task 4 测试。
5. **运行中/完成后点芯片换基准**：必须无效（CanExecute 门控）→ Task 5 测试。
6. **崩溃残留**：启动清扫必须删除任意年龄残留（含刚产生的）→ Task 2 测试。
7. **旧版本载荷目录**：EnsureExtracted 后被清理、当前目录完好 → Task 3 测试。

---

### Task 1: Core 校验文案极简化 + 机型一致性维度

**Files:**
- Modify: `src/Jsr4Z8.Core/Model/TaskGroupValidator.cs`
- Test: `tests/Jsr4Z8.Tests/ValidationTests.cs`

**Interfaces:**
- Produces: `ValidationIssue.MessageZh` 新文案（Kind 枚举与判定逻辑不变，仅 NotZ8 拆分两种文案）；`Validate` 签名不变。

- [ ] **Step 1: 更新/新增失败测试**

ValidationTests 中改断言 + 新增用例：
- `IsoMismatch_IssueNamesFileAndBothIsos` → 期望 `"DSC_7580.NEF：非同一 ISO 拍摄：ISO 800（基准帧 400）"`。
- `NonZ8Model_EveryFileFlaggedNotZ8`（全组同 Z6_3）→ 7 条 NotZ8，每条含 `"不受支持的机型：NIKON Z 6_3"` 与 `"仅支持 Nikon Z8"`。
- 新增 `MixedModels_FrameDiffersFromReference_NonSameModelMessage`：仅 FrameNames[3]=Z6_3 → 该帧 NotZ8 文案含 `"非同一机型拍摄"` 与两机型名。
- `NonNikonMake_CameraMismatchInsteadOfNotZ8` → 文案含 `"不受支持的机型：Canon Inc. Canon EOS R5"`。
- `MixedGroup_OneTwelveBitFrame_FlaggedBitDepthMismatch` → 含 `"非同一位深拍摄"`。
- `MixedGroup_OneDxFrame_FlaggedCropMismatch` → 含 `"图像尺寸不同"` 与 `"5488×3712"`。
- `MixedGroup_OneHeFrame_FlaggedCompressionMismatch` → 含 `"非同一 RAW 压缩拍摄"` 与 `"HE"`。
- `OffTableIso_FlaggedCaptureFormatUnsupported` → 单条含 `"不受支持的 ISO：51.5"`。
- `PtcUnavailable_WhenReferenceIsoOutsideTableBounds` → PtcUnavailable 条含 `"不受支持的 ISO：49"`。
- `TwelveBitReadout_FlaggedCaptureFormatUnsupported` → 含 `"不受支持的拍摄格式"` 与 `"12bit"`。
- 新增 `BracketedShutterSevenFrames_CanRun`：7 帧 ExposureTime 各不同（0.001..1/8000）→ CanRun=true、无 issue（钉死需求 1 的校验侧）。
- 新增 `UnreadableFrame_IssueNamesFile` 文案含 `"无法读取"`。

- [ ] **Step 2: 运行确认失败**

Run: `cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet test --nologo --filter FullyQualifiedName~ValidationTests"`
Expected: 新/改断言 FAIL。

- [ ] **Step 3: 实现 TaskGroupValidator 文案**

逐 Kind 新 MessageZh（`{name}`=GetFileName(path)，Fmt 沿用）：
- FrameCount：`帧数不符：任务组需要 7 帧，实际 {n} 帧`（不变）
- Unreadable：`{name}：无法读取（{ex.Message}）`
- CameraMismatch：`{name}：不受支持的机型：{make} {model}`
- NotZ8：model≠基准帧 model → `{name}：非同一机型拍摄：{model}（基准帧 {refModel}）`；model=基准帧 model（全组同非 Z8）→ `{name}：不受支持的机型：{model}（仅支持 Nikon Z8）`。基准帧自身非 Z8 走后支。实现：先读基准帧 model（不可读则退回旧逻辑按"未支持"报），每文件比对。
- NotFiniteIso：`{name}：ISO 值无效`
- IsoMismatch：`{name}：非同一 ISO 拍摄：ISO {iso}（基准帧 {base}）`
- BitDepthMismatch：`{name}：非同一位深拍摄：{bits}bit（基准帧 {base}bit）`
- CompressionMismatch：`{name}：非同一 RAW 压缩拍摄：{compName}（基准帧 {baseName}）`
- CropMismatch：`{name}：图像尺寸不同：{w}×{h}（基准帧 {w}×{h}）`
- CaptureFormatUnsupported(画像)：`基准帧 {name}：不受支持的拍摄格式：{bits}bit · {compName}（仅支持 14bit 无损/HE/HE*）`
- CaptureFormatUnsupported(ISO 表外)：`基准帧 {name}：不受支持的 ISO：{iso}`
- PtcUnavailable：`基准帧 {name}：不受支持的 ISO：{iso}`

- [ ] **Step 4: 运行测试通过**

Run 同 Step 2。Expected: ValidationTests 全 PASS。

- [ ] **Step 5: Commit**

`git add -A src/Jsr4Z8.Core tests/Jsr4Z8.Tests/ValidationTests.cs && git commit -m "feat(core): 校验失败文案极简化——仅原因+文件名，机型一致性独立提示"`

### Task 2: Core 队列——失败即删临时目录 + 启动清扫

**Files:**
- Modify: `src/Jsr4Z8.Core/Queue/TaskQueueRunner.cs`（TaskRunOutcome 删 DiagnosticDir 字段；失败/取消路径删 jobDir；`SweepStaleDiagnosticDirs`→`SweepTempRoot(AppSettings)` 删除全部子目录；删 DiagnosticDirMaxAgeDays/_startupSweepDone/Kept()）
- Modify: `src/Jsr4Z8.UI/ViewModels/QueueMediator.cs`（删 DiagnosticPrefix 拼接）
- Test: `tests/Jsr4Z8.Tests/QueueRunnerTests.cs`

**Interfaces:**
- Produces: `TaskRunOutcome(Success, Cancelled, Message, OutputPath, Elapsed)`（5 字段）；`public static void TaskQueueRunner.SweepTempRoot(AppSettings settings)`——删除 `<TempBase>\Jsr4Z8` 下所有子目录，吞掉一切 IO 异常。

- [ ] **Step 1: 更新失败测试**

- `BackendFailure_KeepsTempDir_HumanizedMessageWithDiagnosticHint` → 改名 `BackendFailure_DeletesTempDir_HumanizedMessage`：ErrorText 含 `"模拟失败原因"`、不含 `"诊断目录："`；`Assert.Empty(Directory.GetDirectories(h.JsrTempRoot))`。
- `NoDng…`（offset≈150 的用例，含 job.log/request.json 断言）→ 改为：ErrorText 含 `"后端未产出 DNG"`、不含 `"诊断目录："`；临时根下无残留目录。
- `SweepStaleDiagnosticDirs_DeletesOld_KeepsFreshAndExcluded` → 改名 `SweepTempRoot_DeletesAllLeftovers_AnyAge`：old/fresh/current 三目录全部被删。
- 新增 `CancelledRun_DeletesTempDir`：stub_kill + 队列取消 → Failed("已停止") 且临时根为空。
- QueueMediator 相关：`EventMapping…`/`MissingPtc…` 中 LogLines 断言本任务先保留（Task 5 才删）——若编译报 DiagnosticDir 则同步删引用。

- [ ] **Step 2: 运行确认失败**

Run: `cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet test --nologo --filter FullyQualifiedName~QueueRunnerTests"`
Expected: FAIL（旧语义）。

- [ ] **Step 3: 实现**

- TaskQueueRunner：`BackendLogLine` 失败返回、NoDng、OperationCanceled、Exception 四处 → 返回前 best-effort `TryDeleteDir(jobDir)`（复用现有 try/catch IOException/UnauthorizedAccess 模式，抽小私有方法）；TaskRunOutcome 去 DiagnosticDir。
- `SweepTempRoot`：EnumerateDirectories(root) 逐个 best-effort 递归删（同上吞异常）；`root` 不存在直接返回。
- QueueMediator：ApplyOutcome 里 `message = outcome.Message`（删 DiagnosticDir 分支与常量）。

- [ ] **Step 4: 运行测试通过**

Run 同 Step 2。Expected: QueueRunnerTests 全 PASS。

- [ ] **Step 5: Commit**

`git commit -m "feat(core): 失败/停止即删任务临时目录；SweepTempRoot 启动清扫全部残留"`

### Task 3: PayloadManager 清理旧版本载荷目录

**Files:**
- Modify: `src/Jsr4Z8.Core/Payload/PayloadManager.cs`
- Test: `tests/Jsr4Z8.Tests/PayloadTests.cs`

**Interfaces:**
- Produces: EnsureExtracted 既有签名；新行为——成功定位/释放 target 后，best-effort 删除 `_backendRoot` 下所有名字≠当前 `hashDir` 的子目录（旧版本整目录 + 遗留 tmp）。

- [ ] **Step 1: 失败测试**

新增 `EnsureExtracted_PrunesStaleSiblingVersions`：同一 backendRoot 先用 payload A EnsureExtracted（dirA），再用 payload B（内容不同→hashDir 不同）EnsureExtracted → 返回 dirB、`Directory.Exists(dirA)`==false。复用 MakeFakeTree/PackFake 测试设施（改一个文件字节生成 B）。

- [ ] **Step 2: 确认失败**（旧代码不删 dirA）。

- [ ] **Step 3: 实现**

EnsureExtracted 的 `_verifiedDir = target; return target; 之前插入 `PruneStaleVersions(hashDir)`：`foreach dir in EnumerateDirectories(_backendRoot) where name != hashDir → DeleteDirectory(dir)`（复用现有 DeleteDirectory，整体 try/catch 吞异常——清理失败不阻断运行）。注意在**校验通过/释放完成之后**执行，避免误删并发实例正在用的当前版本；旧版本不再被任何进程引用（单实例场景），风险可控。

- [ ] **Step 4: 全量 PayloadTests PASS。**

- [ ] **Step 5: Commit** `git commit -m "feat(payload): 释放后清理旧版本载荷目录，防 %LOCALAPPDATA% 堆积 ~500MB/版"`

### Task 4: TaskGroupCreator——内联建组 + 曝光包围基准帧

**Files:**
- Create: `src/Jsr4Z8.UI/ViewModels/TaskGroupCreator.cs`
- Delete: `src/Jsr4Z8.UI/ViewModels/NewTaskViewModel.cs`、`src/Jsr4Z8.UI/Views/NewTaskDialog.axaml(.cs)`、`src/Jsr4Z8.UI/App.axaml.cs` 中 ShowNewTaskDialog
- Modify: `src/Jsr4Z8.UI/ViewModels/MainViewModel.cs`（AddFilesAsync 内联建卡 + IsValidating/StatusText/IsDragOver/计数）
- Test: `tests/Jsr4Z8.Tests/NewTaskVmTests.cs` → 重写为 `tests/Jsr4Z8.Tests/TaskGroupCreatorTests.cs`；`tests/Jsr4Z8.Tests/TaskGroupVmTests.cs`（MainViewModel 部分适配）

**Interfaces:**
- Consumes: `TaskGroupValidator.Validate(TaskGroup, Func<string,RawMetadata>)`、`RawMetadataReader.Read`、`MainViewModel.AddValidated`。
- Produces:
  - `public sealed record CreatedTaskGroup(TaskGroup Group, ValidationResult Validation)`（从 NewTaskViewModel 移入 TaskGroupCreator.cs）。
  - `public static class TaskGroupCreator { public static Task<CreatedTaskGroup?> CreateAsync(IReadOnlyList<string> paths, Func<string, RawMetadata>? read = null) }`——后台线程执行；内部：.NEF 过滤（忽略大小写）→ OrdinalIgnoreCase 去重 → NaturalFileNameComparer 自然排序 → memoized 读取全部元数据（不可读文件记集合）→ 基准帧选取 → `TaskGroupValidator.Validate`（传 memoized reader，避免双读）。空 NEF 集 → 返回 null（调用方 no-op）。
  - `internal sealed class NaturalFileNameComparer`（自 NewTaskViewModel 原样迁入本文件）。
  - MainViewModel：`public Task AddFilesAsync(IReadOnlyList<string> paths)`（计数器 + StatusText + post 回 UI 线程 AddValidated）；ctor 新参 `Action<Action>? post = null`（默认 inline，App 传 Dispatcher.UIThread.Post）；`[ObservableProperty] bool IsDragOver / int ValidatingCount / string StatusText`；派生 `ReadyCount/DoneCount/FailedCount/BlockedCount`（订阅卡片 PropertyChanged 刷新）；`ClearAllCommand`——移除全部非 Running 卡（Running 卡不动）。

- [ ] **Step 1: 重写测试文件**

`TaskGroupCreatorTests.cs`（沿用 NewTaskVmTests 的 Meta/Reader 设施）：
- `Create_ShuffledSeven_EqualExposure_ReferenceIsNaturalFirst`：7 帧同 ExposureTime → Group.FramePaths==自然排序、ReferencePath==第 1 张。
- `Create_BracketedSeven_MiddleExposureIsReference`：7 帧 ExposureTime = {1/8000,1/4000,1/2000,1/1000,1/500,1/250,1/125}（乱序输入）→ ReferencePath==1/1000 那张；其余 6 帧保持自然顺序在后。
- `Create_BracketedTiedExposures_DeterministicPick`： exposures {1,1,2,2,4,4,8} → 基准=第 4 位（升序稳定排序后 index 3，即 2 中自然序第 1 张）。
- `Create_FilterDedupeNatural`：混合 .jpg/.nrW/大小写重复 → 仅 NEF 去重自然序（原 Drop_KeepsOnlyNef… 断言移植）。
- `Create_NonNefOnly_ReturnsNull`：`CreateAsync(["a.jpg"])` → null（Review Focus 1）。
- `Create_EightFrames_BlockedWithFrameCount`：8 帧 → Validation.CanRun=false、Issues 含 `"帧数不符"`（卡仍创建，错误在区块内）。
- `Create_UnreadableFrame_BlockedNamingFile`：1 帧抛 RawReadException → CanRun=false、MessageZh 含文件名与 `"无法读取"`。
- `Create_BracketedButIsoDiffers_Blocked`：包围 + 第 5 帧 ISO 不同 → IsoMismatch（钉死"仅快门"边界）。
- `Create_RealSamples_CanRun`（集成）：真样本 7 帧（含真实同曝光）→ CanRun=true、ReferencePath==DSC_7576.NEF。
- MainViewModel：`AddFilesAsync_CreatesCardViaPost`（inline post）：Ready 卡入 Tasks；`AddFilesAsync_EmptyNef_NoCardNoStatusFlicker`；`AddFilesAsync_ConcurrentDrops_TwoCards`（Review Focus 2，两条 Task 未 await 完成前再发第二条）；`ClearAll_KeepsRunningRemovesRest`（Ready+Running+Done 三卡 → 清除后仅 Running 留存）。

- [ ] **Step 2: 确认编译失败/测试失败**（TaskGroupCreator 不存在、NewTaskViewModel 删除后旧引用断裂——本步骤与 Step 3 同批改完才可编译，按 TDD 先写测试再实现，运行时以"找不到类型"为失败形态）。

- [ ] **Step 3: 实现**

- 新建 TaskGroupCreator（Interfaces 契约）：曝光并列判定 `Math.Abs(a-b) <= 1e-6 * Math.Max(1,|b|)`；全等→序不动；否则按 (ExposureTime, NaturalFileNameComparer(fileName)) 升序取 `Count/2`（7→3）为基准，其余帧保持自然序，基准提到 FramePaths[0]。快门=0（EXIF 缺失）视为相等维度参与比较。
- MainViewModel：删 DropFilesRequested/AddTaskRequested/AddTask 命令与 DropFiles；Add(TaskGroupViewModel) 保留并补 PropertyChanged 订阅刷新计数；`AddFilesAsync`：过滤后空→no-op；`ValidatingCount++` → `await Task.Run(CreateAsync)` → `post(加卡 + 计数--)`；StatusText 计算：ValidatingCount>0→`正在校验 {n} 张 RAW…`，runner busy→`正在处理…`，无任务→`拖放 7 张 RAW 开始`，否则 `共 {N} 个任务组`。
- App.axaml.cs：删 ShowNewTaskDialog 与两处事件接线；MainViewModel 构造传 `post: a => Dispatcher.UIThread.Post(a)`；新增启动清扫：`_ = Task.Run(() => TaskQueueRunner.SweepTempRoot(new SettingsServiceProvider(settings).CurrentSettings));`（settings=共享 SettingsService 实例）。

- [ ] **Step 4: 运行**

Run: `cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet test --nologo --filter "FullyQualifiedName~TaskGroupCreatorTests|FullyQualifiedName~TaskGroupVmTests""`
Expected: PASS（TaskGroupVmTests 中 LogLines 用例尚未删——本任务先仅适配 MainViewModel 相关，日志用例在 Task 5 删）。

- [ ] **Step 5: Commit** `git commit -m "feat(ui): 内联建组替代新建对话框——TaskGroupCreator + 曝光包围中间帧基准"`

### Task 5: TaskGroupViewModel 卡内换基准 + 去日志

**Files:**
- Modify: `src/Jsr4Z8.UI/ViewModels/TaskGroupViewModel.cs`、`src/Jsr4Z8.UI/ViewModels/QueueMediator.cs`
- Test: `tests/Jsr4Z8.Tests/TaskGroupVmTests.cs`、`tests/Jsr4Z8.Tests/ProgressThrottleTests.cs`、`tests/Jsr4Z8.Tests/QueueRunnerTests.cs`（QueueMediatorTests 部分）

**Interfaces:**
- Produces:
  - TaskGroupViewModel：删 `LogLines`/`IsLogExpanded`/`DarkName`/MetaText 中 PTC 与黑场；`Group` 改 `{ get; private set; }` + `OnPropertyChanged`；`ReferenceName`/`OutputName` 改可观察（换基准后更新）；新命令 `SetReference(FrameChipViewModel chip)`（Ready/Blocked 才可用；与现任互换槽位，Group.FramePaths 同步重排）；`FrameChipViewModel` 增 `IsError` 不需要（删）。MetaText：Ready/Blocked→`待输出 JSR_x.dng`；Done→`已输出 <完整路径>`；Running→空。
  - QueueMediator：`BackendLogLine` 处理只留进度提取 + 门重开 flush（删 `vm.LogLines.Add`）；ApplyOutcome 删 `[完成]` 行。

- [ ] **Step 1: 更新测试**

- TaskGroupVmTests：删 `LogLinesAppend_And_ToggleLogFlipsExpanded`；`MetaText` 相关断言改（Ready 卡=`待输出 JSR_DSC_7576.dng`，无 PTC/黑场字样：`DoesNotContain("PTC")`、`DoesNotContain("黑场")`）；新增：
  - `SetReference_SwapsGroupOrderAndOutputName`：Ready 卡点第 4 芯片 → Group.FramePaths[0]==该帧、OutputName==`JSR_<新基准名>.dng`、FrameChips[0].IsReference。
  - `SetReference_DisabledWhenRunningOrDone`：Running/Done/Failed 卡 `SetReferenceCommand.CanExecute(null)`==false（Review Focus 5）。
- ProgressThrottleTests：所有 `vm.LogLines` 断言删除/替换——(d) 语义改为"非进度行不产生 UI 变更"：`ChangeCounter` 计数不增；`FixtureReplay_DefaultGate…` 删行数断言；`GateDisabled…` baseline 重放删 `bare.LogLines.Add` 与两处行数断言。
- QueueRunnerTests：`EventMapping…` 删 `Assert.Contains("模拟后端启动", vm.LogLines)` 与 `"已输出"` 日志断言（Done 状态已由 State/MetaText 覆盖）；`BackendFailure…` 已在 Task 2 改。

- [ ] **Step 2: 运行确认失败**（LogLines 已不存在→编译错即失败形态；行为断言先红）。

- [ ] **Step 3: 实现**

按 Interfaces 修改两个 VM。SetReference 实现：`int i = FrameChips.IndexOf(chip); if (i <= 0 || State is not (Ready or Blocked)) return;` 交换 FrameChips[0]/[i] 与 IsReference；`Group = new TaskGroup(新序列)`；更新 ReferenceName/OutputName（OnPropertyChanged）。Ptc/Dark 保留（组内五维一致 ⇒ 换基准不变；Blocked 组本就 null，不重校验——文案对 ISO/压缩一致组不随基准变化）。

- [ ] **Step 4: 运行三个测试文件全 PASS**（命令同前，filter 加 ProgressThrottleTests|QueueRunnerTests）。

- [ ] **Step 5: Commit** `git commit -m "feat(ui): 卡内点击芯片换基准；移除日志面板与匹配细节展示"`

### Task 6: Views——Save_Z8 风格主界面与任务卡

**Files:**
- Rewrite: `src/Jsr4Z8.UI/Views/MainWindow.axaml(.cs)`、`src/Jsr4Z8.UI/Views/TaskCardView.axaml(.cs)`
- Delete: `src/Jsr4Z8.UI/Views/LogPanel.axaml(.cs)`
- Create: `src/Jsr4Z8.UI/Assets/app.ico`（复制自 `D:\GitRepos\Save_Z8\src\SaveZ8.UI\Assets\app.ico`）
- Modify: `src/Jsr4Z8.UI/Jsr4Z8.UI.csproj`（ApplicationIcon + AvaloniaResource）
- Test: 编译 + 全量 dotnet test（视图无 headless 测试，靠 Task 4/5 的 VM 测试与编译期绑定校验 x:CompileBindings）

**Interfaces:**
- Consumes: MainViewModel（IsDragOver/ValidatingCount/StatusText/计数/StartAllCommand/ToggleThemeCommand/AddFilesAsync）、TaskGroupViewModel（StateText/IsRunning/Progress/FrameChips/SetReferenceCommand/StartCommand/StopCommand/RetryCommand/OpenFolderCommand/RemoveCommand/IssuesText/ErrorText/MetaText）。

- [ ] **Step 1: 资产与 csproj**

复制 app.ico；csproj PropertyGroup 加 `<ApplicationIcon>Assets\app.ico</ApplicationIcon>`，新增 `<AvaloniaResource Include="Assets\app.ico" />`。

- [ ] **Step 2: 重写 MainWindow.axaml**

结构照抄 Save_Z8MainWindow.axaml 的分区与样式类（icon/icon-white/label-dim/icon-btn/pill/dot/dot-pulse/row-action/empty-zone/empty-stroke/link-inline），图标 StreamGeometry 改为：Layers（logo+空态）、FolderPlus、Trash、Zap、Play、Square(stop)、RotateCw、External、Moon/Sun（沿用 Save_Z8 的路径数据；Play=`M5 3l14 9-14 9V3z`、Square=`M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z`、RotateCw=`M23 4v6h-6M20.49 15a9 9 0 1 1-2.12-9.36L23 10`、Layers=`M12 2 2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5`）：
- 顶栏：渐变徽标(38×38,#2990FF→#135CB8)+Layers 白图标；`JSR for Z8` 19 Bold；副标题 `尼康 Z8 七帧 RAW 超分重建 · 内嵌 Jiangtherapee Bridge 后端 · 输出 Adobe DNG`（12 Text2）；右侧 `v{版本}`（程序集版本）+ 主题 icon-btn（Moon/Sun 随 Theme.Mode 切换可见，ToolTip 暗色/亮色/跟随系统文案含当前模式）。
- 空态（HasNoTasks）：empty-zone 卡片：Layers 44 图标、`将 7 张 RAW 拖到此处` 16 SemiBold、副文 `支持曝光包围（7 帧仅快门速度不同，默认以中间曝光为基准）· 输出 DNG 写入基准帧所在目录`、Primary 按钮 `选择 RAW 文件…`（FolderPlus 图标，Click 打开选择器）；三步指引：`1 拖入或选择 7 张 RAW`/`2 自动校验 · 一键开始`/`3 在基准帧旁得到 JSR DNG`。
- 工作区（有任务）：列表卡（Background1+border+CornerRadius 12）：顶部计数条 `共 {N} 组` + 四个 dot+计数（就绪/运行中/已完成/失败 或 无法执行——Failed 与 Blocked 各一）、右侧 `添加 RAW…`（Small+FolderPlus，Click 打开选择器）与 `清除全部`（Small+Trash→MainViewModel.ClearAllCommand）；下方 ScrollViewer→ItemsControl(TaskCardView)。
- 底栏：StatusText（12 Text2）+ Primary `全部开始`（Zap 白图标，StartAllCommand）。
- 全窗拖放遮罩：IsDragOver → 半透明 Background + 虚线主色描边 + 圆角胶囊 `松开鼠标，添加 RAW 文件`（FolderPlus）。

- [ ] **Step 3: 重写 MainWindow.axaml.cs**

照 Save_Z8：DispatcherTimer(600ms) 悬停消隐模式（DragOver 续期）；OnDrop→过滤 NEF→`await vm.AddFilesAsync(paths)`；`OnBrowseFiles`（空态与顶栏添加共用，FilePicker FileTypeFilter=*.nef）；主题按钮 Click→`vm.ToggleThemeCommand`+图标可见性同步（Moon=非 Light 时显示…按 ThemeService.Mode：Dark/Default→Moon+提示切亮色，Light→Sun）；OnOpened 读版本号显示。

- [ ] **Step 4: 重写 TaskCardView.axaml(.cs)**

- 根 Border：Background1、border、CornerRadius 12、Padding 14,12；顶部 4px 进度条（IsRunning 时贴顶，同 Save_Z8 总进度样式）。
- 头行：`基准 {ReferenceName}`（13.5 SemiBold Text0，绑定可观察 ReferenceName）+ 状态 pill（五态：就绪灰/运行中 Primary+呼吸点/已完成 Success/失败 Danger/无法执行 Warning，纯展示）+ 右侧操作按钮组（Small 风格带图标：`开始`Play / `停止`Square / `重试`RotateCw / `打开`External(CanOpenFolder) / `移除`Trash——CanExecute 自动启停，处理中组仅停止可用）。
- 芯片行：WrapPanel；chip=pill 样式（Fill0 底 12px），基准=Primary 底白字加 `★`；`PointerPressed`→SetReferenceCommand(chip)（ToolTip `点击设为基准帧`；Running/Done/Failed 时 IsEnabled=false）。
- Meta 行（11.5 Text2）+ 错误条（IssuesText/ErrorText：DangerLight 底、Danger 字、圆角 6、TextWrapping）。
- code-behind：保留状态类维护模式（st-* 类改为驱动 pill/进度条样式的选择器），新增芯片点击转发。

- [ ] **Step 5: 编译 + 全量测试**

Run: `cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet test --nologo"`
Expected: 全 PASS（视图改动不改行为）。

- [ ] **Step 6: Commit** `git commit -m "feat(ui): 主界面/任务卡按 Save_Z8 视觉规范重构；移除日志面板"`

### Task 7: 文档同步 + 全量验证

**Files:**
- Modify: `AGENT.md`（§5 决策 4/5：基准帧规则改为曝光包围中间帧、失败即删+启动清扫；§3 架构图 UI 行；§8 设计权威注明本轮用户需求覆盖 docs/design 旧 mockup）、`README.md`（使用说明：曝光包围、拖放即建组、失败不留诊断目录→删除"3GB 诊断目录"段）

- [ ] **Step 1: 更新两文档**（内容如上，保持既有行文风格）。
- [ ] **Step 2: 全量测试** `dotnet test --nologo` 全 PASS。
- [ ] **Step 3: 构建冒烟** `cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet build src\Jsr4Z8.UI\Jsr4Z8.UI.csproj -c Debug --nologo"` 成功（发布 exe 属用户打包动作，不在本计划）。
- [ ] **Step 4: Commit** `git commit -m "docs: 同步内联建组/曝光包围/临时目录新策略到 AGENT.md 与 README"`
