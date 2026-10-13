# JSR for Z8 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 Jiangtherapee Bridge 2.3.25 的七帧 RAW 超分辨率 Python 后端重写一个 Nikon Z8 专用的 Avalonia GUI 客户端，后端原封不动嵌入单文件 exe 分发。

**Architecture:** C#/.NET 8 前端经无界面 CLI（`--assistant-jsr-job`）调用嵌入的原版后端，流式解析 stdout 协议；前端负责机型/ISO 校验、PTC 满阱计算（显式传 `fullwell`）、顺序任务队列；后端产出的裸 DNG 由复刻自 Save_Z8 的 DngWriter 补全 EXIF/白平衡/镜头校正/预览并以无损 JPEG（Compression=7）重写为 `JSR_<基准帧名>.dng`。

**Tech Stack:** .NET 8.0-windows · Avalonia 11.2.2 · Semi.Avalonia 11.2.1.10 · CommunityToolkit.Mvvm 8.3.2 · xUnit · LibRaw fork（submodule `y-g-jiang/LibRaw@nikon-he-production`）· libjpeg-turbo（submodule）· 原版后端载荷（`reference/Jiangtherapee-Bridge-2.3.25-Windows-x64`）

**Spec:** `docs/superpowers/specs/2026-10-13-jsr4z8-design.md`（含 GUI 定稿 mockup `docs/design/*.html`）

## 已实测钉死的协议事实（2026-10-13 探针，样本 ISO 3200 / RX 7900 XTX）

执行者不可更改这些事实，直接作为常量与测试夹具来源：

1. 调用：`"<BridgeExe>" --assistant-jsr-job <request.json 绝对路径> <resources 绝对路径> <config 绝对路径>`（全 Windows 路径）。
2. stdout 按行：`ASSISTANT_JSR_START|{json}` → N 条 `ASSISTANT_JSR_LOG|<原样日志行>`（内含 `SRHQ_V12_PROGRESS|<整数百分比>|<阶段中文>`、`SRHQ_V12_FRAME|<i>|<EV>|<值>|<路径>`、`SRHQ_EFFECTIVE_ENGINE|v12|v12-wgpu|<名称>`、纯文本行如 `Decoded RAW 3/7`）→ 结束时恰一条 `ASSISTANT_JSR_RESULT|{json: exit_code,state,log,job_id,output,pid,stage,...}`。失败时 stderr 另有 `ASSISTANT_JSR_ERROR|<msg>`。harness 进程退出码：失败=1（成功=0，待 e2e 确认）。
3. harness 内部再 spawn `resources\jsr_backend.exe`——**取消任务必须杀整棵进程树**。
4. `fullwell` 传数值则后端跳过自身 PTC 匹配并回显 `PTC {"selection":"manual_fullwell","effective_fullwell_e":<值>}`（日志可作确认锚点）。后端自身 PTC 匹配在 Z8 上因机型名差异必然失败——所以满阱永远由前端算好传入。
5. 请求 JSON 精确 schema（多余字段勿加）：
   `{"paths":[7 绝对路径],"parameters":{"engine":"stormer7","mode":"full","output":"<目录>","prefix":"<前缀>","manual":false,"dark":"<黑场绝对路径>","fullwell":<数|null>,"ptc":"","color":"libraw","dng":true,"tiff":false,"preview":false,"deghost":false,"memory":<GB int>}}`
6. 后端在 output 目录建 `<prefix>_V15_3_5_K7_<时间戳>_<序号>/` 目录：内含同名 `.dng` + `辅助文件/`（npy 中间产物、run.log、result.json、support.png）。失败/中断残留同名 `.partial` 目录；成功后目录无后缀。
7. 样例实跑：4GB 预算在 45.7MP 对齐阶段即 MemoryError（驱动已用 4.086GB）；**12GB 全链成功，state="complete"、exit_code=0，全程约 5.5 分钟（RX 7900 XTX）**。UI 默认预算 4GB 保留，但错误文案须引导调高。
8. 样例 EXIF：Make=`NIKON CORPORATION`，Model=`NIKON Z 8`（注意空格），ISO=3200，黑电平 1008 / 声明白电平 16383（14-bit DN）。LibRaw cam_mul → AsShotNeutral=[0.560175, 1.0, 0.557734]（探针日志值）。
9. **后端成功产物 DNG 实测结构**（`tmp_run1/out/probe1_.../probe1_....dng`）：IFD0 = 256×171 未压缩缩略图 + Make=Nikon/Model="Z 8" + Software="Transformer-JSR V15.3.4" + Artist="2026 姜少耕 y-g-jiang.github.io"（**最终 DNG 不保留这两个署名标签，法律署名随载荷内 THIRD_PARTY_NOTICES.md 分发，2026-10-13 用户决定**）+ XMP 511B + ColorMatrix1 + AsShotNeutral + 21 字节最小 OpcodeList2，无 EXIF/GPS/预览 IFD；唯一 raw SubIFD = **16560×11040、3 通道 16-bit、Photometric=34892（LinearRaw，已去马赛克线性 RGB，无 gamma 无白平衡）**、Compression=7（tifffile LJ）、BlackLevel=0/WhiteLevel=65535、归一化公式 `round(clip(linear*1.001,0,1)*65535)`；实际条带数据仅 ~31MB，文件预分配 341MB（**紧凑重写可减 90% 体积**）。几何 = 输入 raw 的 2 倍线性超分。
10. 进度行两种：`SRHQ_V12_PROGRESS|<int 0-100>|<阶段>` 与导出期 `SRHQ_ETA_PROGRESS|dng|<已完行>|<总行>`。
11. 探针日志原件：`tmp_run1/stdout2.log`（失败链）、`tmp_run1/stdout3.log`（完整成功链）——Task 6 夹具来源；成功产物与 `辅助文件/result.json`（含 shape/whitepoint 编码描述）留在原处供 Task 9 参考。

## Global Constraints

- .NET `net8.0-windows`；NuGet 锁定：Avalonia 11.2.2 / Avalonia.Desktop 11.2.2 / Semi.Avalonia 11.2.1.10 / Avalonia.Fonts.Inter 11.2.2 / CommunityToolkit.Mvvm 8.3.2。
- 原生依赖仅 git submodule：`external/libraw`（y-g-jiang fork，branch nikon-he-production）、`external/libjpeg-turbo`。Save_Z8 代码**复刻源文件**（保留原头注、改命名空间），不引其为 submodule。
- 后端载荷逐字节原样：仅 `"Jiangtherapee Bridge.exe"` + `resources/`（含 licenses）+ `THIRD_PARTY_NOTICES.md`，绝不增删改其中文件。
- 输出文件名固定 `JSR_<基准帧名>.dng`；界面纯中文；主题 Semi.Avalonia 默认暗色、跟随系统+顶栏切换。
- 任务顺序执行一次一组；失败/停止不阻断后续组。
- 本轮仅 Nikon Z8、仅 engine=stormer7、mode=full；无 deghost/ROI/TIFF/预览输出入口。
- 构建与测试在 Windows Host 执行（WSL 内经 `cmd.exe /c "cd /d D:\\GitRepos\\JSR_for_Z8 && ..."`）；不用 WSL 侧 dotnet。
- 禁止：手填黑场参数、手选/手填 PTC、修改后端任何文件。

## Review Focus

1. **协议行边界**：`ASSISTANT_JSR_RESULT` 单行内嵌整段含 `\r\n` 的 log JSON；日志行可能含 `|` 与中文。→ Task 6 夹具测试（真实日志逐行喂入断言事件序）。
2. **ISO 越界**：ISO<50 或 >102400（表外无邻近档）必须判「匹配不到 PTC」拒绝任务，而非外推。→ Task 4 `Lookup(49)`/`Lookup(102401)` 断言 null。
3. **校验负片**：机型/ISO 不一致、截断 NEF、非 NEF 扩展名——错误信息必须点名文件与差异值。→ Task 5（mock RawFileInfo 组合 + samples 截断副本）。
4. **取消与残留**：停止须杀 jsr_backend.exe 子进程；`.partial` 残留在任务临时目录、成功后清理。→ Task 7 stub-exe 子进程树测试；Task 16 真机停止测试。
5. **载荷损坏自愈**：释放目录被用户删掉一半文件 → SHA256 校验失败 → 自动重新完整释放，不半新半旧。→ Task 10 破坏性测试。

## File Structure

```
JSR_for_Z8/
├─ external/libraw, external/libjpeg-turbo      (submodules, Task 1)
├─ native/libraw_wrapper.cpp, dng_ljpeg_wrapper.cpp (Task 2, 复刻自 Save_Z8/native/)
├─ src/Jsr4Z8.Core/
│  ├─ Native/LibRawNative.cs, DngLjpegNative.cs (Task 2)
│  ├─ RawMetadata.cs + RawMetadataReader.cs     (Task 3, 读机型/ISO/电平/白平衡)
│  ├─ NefAssetExtractor.cs + TiffConstants.cs   (Task 3, 复刻 TiffIfdExtractor：EXIF/GPS/XMP/预览/NEFInfo)
│  ├─ Ptc/PtcTable.cs + Embedded/ptc_z8.csv     (Task 4)
│  ├─ Model/TaskGroup.cs, TaskGroupValidator.cs (Task 5)
│  ├─ Backend/BackendRequest.cs, ProtocolParser.cs (Task 6)
│  ├─ Backend/BackendRunner.cs                  (Task 7)
│  ├─ Dng/DngWriter.cs, NikonLensCorrection.cs, Models/ (Task 8, 复刻)
│  ├─ Dng/JsrDngFinalizer.cs                    (Task 9)
│  └─ Payload/PayloadManager.cs + tools/pack-payload/(Task 10)
├─ src/Jsr4Z8.UI/  (App, MainWindow, Views/, ViewModels/, Task 11-14)
├─ tests/Jsr4Z8.Tests/  (每 Task 对应测试文件; fixtures/)
├─ build.bat (Task 15)   out/   Directory.Build.props
```

---

### Task 1: 仓库脚手架与工具链预检

**Files:**
- Create: `JSR_for_Z8.sln`、`Directory.Build.props`、`src/Jsr4Z8.Core/Jsr4Z8.Core.csproj`、`src/Jsr4Z8.UI/Jsr4Z8.UI.csproj`（空骨架）、`tests/Jsr4Z8.Tests/Jsr4Z8.Tests.csproj`、`README.md`
- Modify: `.gitignore`（补 `build/`、`out/`、`libs/`、`tmp*/`、`*.user`）
- Run: `git submodule add -b nikon-he-production https://github.com/y-g-jiang/LibRaw.git external/libraw`、`git submodule add https://github.com/libjpeg-turbo/libjpeg-turbo external/libjpeg-turbo`

**Interfaces:**
- Produces: 解决液与三个项目（Core=classlib net8.0-windows，UI=Avalonia App，Tests=xUnit 引用 Core）；`Directory.Build.props` 统一 `build\obj`、`build\bin` 路径（照抄 Save_Z8 同名文件改名）。

- [ ] **Step 1: Windows host 预检命令记录**：`cmd.exe /c "dotnet --version"`（需 8.x）与探测 vcvarsall.bat 路径（`C:\Program Files\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvarsall.bat`）。任一缺失→停止并报告，不要自行安装。
- [ ] **Step 2: 建项目骨架**（UI 项目用 Avalonia 11.2.2 模板手写 csproj + Program.cs + App.axaml 最小可启动；引用按 Global Constraints 锁版本），添加 submodule 两条命令如上。
- [ ] **Step 3: 验证**：`cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet build"` Expected: Build succeeded 0 错误。
- [ ] **Step 4: Commit** `chore: solution scaffold, pinned packages, native submodules`

### Task 2: 原生库构建与互操作复刻

**Files:**
- Create: `native/libraw_wrapper.cpp`、`native/dng_ljpeg_wrapper.cpp`（从 `D:\GitRepos\Save_Z8\native\` 复刻，头注保留出处；若 dng_ljpeg_wrapper 依赖 libjpeg-turbo 构建产物路径，同步调整相对路径）
- Create: `src/Jsr4Z8.Core/Native/LibRawNative.cs`、`DngLjpegNative.cs`（复刻改命名空间 `Jsr4Z8.Core.Native`）
- Create: `build.bat` 雏形（仅 Step 0-3：预检/submodule/libraw.dll/jpeg62/wrapper，照抄 Save_Z8 `build.bat` 对应段落，路径改名 JSR_for_Z8）
- Test: `tests/Jsr4Z8.Tests/NativeSmokeTests.cs`

**Interfaces:**
- Produces: `LibRawNative`（extern C wrapper：`OpenRaw(wchar path)` 返回句柄、`GetMake/GetModel/GetIso/GetCamMul/GetLevels/GetDimensions...`——以复刻后的实际签名为准，与 Save_Z8 一致）；`libs\libraw.dll`、`libs\libraw_wrapper.dll`、`libs\jpeg62.dll`（或按 Save_Z8 静态链接方案）。

- [ ] **Step 1: 失败测试** `NativeSmokeTests.OpenSampleNef_ReadsMakeModelIso`：P/Invoke 打开 `samples/DSC_7576.NEF`，断言 Make=`NIKON CORPORATION`、Model 含 `Z 8`、ISO=3200、cam_mul 长度 4。
- [ ] **Step 2: 跑测试确认失败**：`cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet test --filter NativeSmoke"` Expected: FAIL（DllNotFoundException/EntryPointNotFound）。
- [ ] **Step 3: 复刻 wrapper 与 C# 互操作；跑 build.bat 步骤产出 libs 三 DLL**（测试项目 csproj `<None Include="..\..\libs\*.dll" CopyToOutputDirectory="PreserveNewest"/>`，照 Save_Z8.Tests 模式）。
- [ ] **Step 4: 重跑测试通过**（Expected: PASS）。
- [ ] **Step 5: Commit** `feat: libraw/jpeg-turbo wrappers ported from Save_Z8`

### Task 3: NEF 元数据与资产提取

**Files:**
- Create: `src/Jsr4Z8.Core/RawMetadata.cs`（record：Make/Model/Iso/BlackLevel/WhiteLevel/Width/Height/CamMul[4]/CfaPattern/ExposureTime/ApertureDateTime 等）、`RawMetadataReader.cs`（基于 Task 2 句柄封装）
- Create: `src/Jsr4Z8.Core/NefAssetExtractor.cs`、`TiffConstants.cs`（复刻 Save_Z8 `TiffIfdExtractor.cs`：EXIF/GPS/Interop/XMP 源块、三级内嵌 JPEG 预览、NEFInfo(0xC7D5) 提取；命名空间改 `Jsr4Z8.Core`）
- Test: `tests/Jsr4Z8.Tests/RawMetadataTests.cs`

**Interfaces:**
- Produces: `static RawMetadata RawMetadataReader.Read(string nefPath)`（LibRaw 失败抛 `RawReadException`，文件不存在/损坏统一为该异常）；`NefAssetExtractor.Extract(string nefPath) -> NefAssets { byte[] ExifBlock, byte[] GpsBlock, byte[] InteropBlock, byte[] XmpBlock, PreviewImage[] Previews(≤3), byte[] NefInfo }`（PreviewImage: {int W, int H, byte[] Jpeg}）。

- [ ] **Step 1: 失败测试**：①`Read_SampleNef` 断言 ISO 3200、BlackLevel 1008、WhiteLevel 16383、尺寸 8256×5504（Z8 全幅 raw，以实际读出为准写死）；②`Extract_SampleNef_HasExifPreviewsNefInfo` 断言 ExifBlock 非空且含 Make、Previews.Count>=2、NefInfo 非空；③`Read_TruncatedNef_Throws`（fixture：samples NEF 前 64KB 截断副本，测试内生成到临时目录）。
- [ ] **Step 2: 确认失败 → 实现/复刻 → 确认通过**（命令同 Task 2 模式，filter `RawMetadata`）。
- [ ] **Step 3: Commit** `feat: NEF metadata + asset extraction (ported TiffIfdExtractor)`

### Task 4: PTC 表与双段拟合

**Files:**
- Create: `tools/extract-ptc.py`（从 `reference/.../resources/_internal/ptc_index.csv` 过滤 `brand==Nikon && model==Z8` 12 行 → `src/Jsr4Z8.Core/Ptc/Embedded/ptc_z8.csv`（EmbeddedResource），列保持原样）
- Create: `src/Jsr4Z8.Core/Ptc/PtcTable.cs`
- Test: `tests/Jsr4Z8.Tests/PtcTests.cs`

**Interfaces:**
- Produces: `sealed class PtcTable { static PtcTable LoadDefault(); PtcMatch? Lookup(double iso); }`；`record PtcMatch { double FullwellE; bool Exact; string Source; }`（Source: "table"/"fit-low"/"fit-high"）。表内 ISO（含 1e-6 容差）→ 1/alpha 精确值；ISO∈(50,102400) 且表外 → 分段最小二乘：iso<500 用 {50,100,200,400}、iso>=500 用 {800,...,102400}，对 (iso, 1/alpha) 直线拟合取值；iso<=50 或 >=102400 之外（<50 或 >102400 的表外值）→ null。

- [ ] **Step 1: 失败测试**（值从 CSV 计算写死，保留 4 位有效数字）：`Lookup(3200)` → Exact=true, FullwellE≈1102.2；`Lookup(50)`→Exact；`Lookup(4000)`→fit-high 且有值；`Lookup(499)`/`Lookup(500)` 用各自段；`Lookup(49)`/`Lookup(102401)`→null；留一验证：`Lookup(1600)`（该点不参与拟合的对照断言：拟合值与表值相对误差 <5%）。
- [ ] **Step 2: 确认失败 → 运行 `python3 tools/extract-ptc.py` 生成资源 → 实现 `PtcTable`（最小二乘手写，无外部数学库）→ 确认通过。**
- [ ] **Step 3: Commit** `feat: Z8 PTC table + two-segment fullwell fit`

### Task 5: 任务组模型与校验

**Files:**
- Create: `src/Jsr4Z8.Core/Model/TaskGroup.cs`（`IReadOnlyList<string> FramePaths`（已按文件名自然升序）、`string DarkPath`、`string ReferencePath => FramePaths[0]`）、`TaskGroupValidator.cs`
- Test: `tests/Jsr4Z8.Tests/ValidationTests.cs`

**Interfaces:**
- Consumes: `RawMetadataReader.Read`、`PtcTable.Lookup`。
- Produces: `static ValidationResult Validate(TaskGroup g)`；`record ValidationResult { bool CanRun; IReadOnlyList<ValidationIssue> Issues; PtcMatch? Ptc; }`；`record ValidationIssue { string File; IssueKind Kind; string MessageZh; }`（Kind: CameraMismatch/IsoMismatch/NotZ8/Unreadable/PtcUnavailable/FrameCount）。规则：帧数恰 7；8 文件全部 Model 规范化后 == Nikon Z8（`Z 8`/`Z8`/大小写/空格归一）；ISO 两两一致（1e-6）；PTC Lookup 非 null；黑场必填。CanRun=false 时 Issues 至少 1 条且中文信息点名文件。

- [ ] **Step 1: 失败测试**：mock `RawMetadata`（注入读函数重载 `Validate(TaskGroup, Func<string,RawMetadata>)` 便于无文件测试）：ISO 不一致组、非 Z8 组（`NIKON Z 6_3`）、7 帧齐全 Z8/ISO3200 + samples 黑场真文件 → CanRun=true 且 Ptc.Exact；非 Z8 → Issue 含「仅支持 Nikon Z8」；帧数 6/8 → FrameCount。
- [ ] **Step 2: 确认失败 → 实现 → 确认通过。**
- [ ] **Step 3: Commit** `feat: task group model + 8-file consistency validation`

### Task 6: 后端请求构造与协议解析

**Files:**
- Create: `src/Jsr4Z8.Core/Backend/BackendRequest.cs`、`ProtocolParser.cs`
- Create: `tests/Jsr4Z8.Tests/fixtures/protocol/`（从 `tmp_run1/stdout2.log`、`stdout3.log` 拷贝，e2e 后补成功日志）
- Test: `tests/Jsr4Z8.Tests/ProtocolTests.cs`

**Interfaces:**
- Produces: `static string BackendRequest.BuildJson(TaskGroup g, PtcMatch ptc, int vramGb, string outputDir, string prefix)`（输出与协议事实 #5 逐字段一致，`fullwell=ptc.FullwellE` 保留 1 位小数，JSON UTF-8 无 BOM，键序无关但字段集精确）；`ProtocolParser.ParseLine(string raw) -> ProtocolEvent`（子类型：`Start(JsrStart Json)`、`Log(string Text, ProgressLine? Progress)`、`Result(JsrResult Json)`、`ErrorOnStderr(string)`、`Unrecognized(string)`）；`ProgressLine { int Percent; string Stage; }` 由 `SRHQ_V12_PROGRESS|int|stage` 提取；导出期 `SRHQ_ETA_PROGRESS|dng|done|total` → `ProgressLine(Percent=95+5*done/total, Stage="DNG 导出")`；RESULT 行 JSON 反序列化为 `JsrResult { int ExitCode; string State; string Log; string JobId; ... }`。

- [ ] **Step 1: 失败测试**：①BuildJson 断言完整 JSON（示例值写死：7 路径/黑场/fullwell 1102.2/memory 12 → 与请求事实 #5 结构全等）；②ParseLine 用 fixture 真实行：`ASSISTANT_JSR_START|{...}`→Start、`ASSISTANT_JSR_LOG|SRHQ_V12_PROGRESS|57|Transformer 恢复`→Log(Progress(57,"Transformer 恢复"))、RESULT 行→Result 且（stdout2 夹具）ExitCode==3/State==failed、（stdout3 夹具）ExitCode==0/State==complete；`SRHQ_ETA_PROGRESS|dng|10880|11040`→Progress(95+5*done/total, "DNG 导出")；无前缀行→Unrecognized；③整份 stdout3.log 逐行喂入：事件总数=行数、恰 1 条 Result(State=complete)、无 Unrecognized 丢失文本。
- [ ] **Step 2: 确认失败 → 实现（System.Text.Json）→ 确认通过。**
- [ ] **Step 3: Commit** `feat: backend request builder + stdout protocol parser`

### Task 7: 后端执行器

**Files:**
- Create: `src/Jsr4Z8.Core/Backend/BackendRunner.cs`、`RunnerEvent.cs`（record 事件：Started/LogLine/Progress/Finished(RunnerResult { bool Success; string Message; TimeSpan Elapsed; })）
- Create: `tests/Jsr4Z8.Tests/fixtures/stub_backend/`（stub.bat：echo START 行 → 循环 echo LOG/PROGRESS 行 → spawn `cmd /c ping -n 30 127.0.0.1`（模拟子进程长跑）→ echo RESULT 行 exit 0；stub_kill.bat 变体验证树杀）
- Test: `tests/Jsr4Z8.Tests/BackendRunnerTests.cs`

**Interfaces:**
- Consumes: `ProtocolParser`、请求 JSON（由调用方生成落盘）。
- Produces: `sealed class BackendRunner { Task<RunnerResult> RunAsync(BackendRunSpec spec, Action<RunnerEvent> onEvent, CancellationToken ct); }`；`record BackendRunSpec { string BridgeExe; string ResourcesDir; string ConfigDir; string RequestJsonPath; string LogFile; }`。行为：UTF-8 读 stdout/stderr；`ct` 取消 → `taskkill /T /F /PID`杀 harness 及 jsr_backend 子树 → 返回 Finished(cancelled)；RESULT state!="complete" 或退出码!=0 → Success=false 且 Message 提取日志尾行人话（`SRHQ_NET_ABORT:xxx|` 或 MemoryError 句）；所有 LogLine 追加写 LogFile。**每个任务独立 ConfigDir（复刻排他锁规避，事实 #3）。**

- [ ] **Step 1: 失败测试**：①stub 跑通：收到 Started→Progress(≥1)→Finished(Success=true)；②取消：启动后 500ms 取消 → Finished 在 3s 内返回且 ping 子进程消失（`tasklist` 查无 PID，或用唯一 marker 文件由子进程心跳写入、取消后停止心跳）；③stub_kill 杀树断言。
- [ ] **Step 2: 确认失败 → 实现（Process + 异步行读，不丢缓冲）→ 确认通过。**
- [ ] **Step 3: Commit** `feat: backend runner with process-tree cancel + log persistence`

### Task 8: DngWriter 复刻（NEF→完整 DNG 直转）

**Files:**
- Create: `src/Jsr4Z8.Core/Dng/DngWriter.cs`、`NikonLensCorrection.cs`、`Models/`、`TiffConstants.cs`（如 Task 3 未含）（复刻 Save_Z8 对应文件，命名空间 `Jsr4Z8.Core.Dng`，**不移植 BlackLevelFixer**）
- Create: `src/Jsr4Z8.Core/Dng/DngComposer.cs`（低层入口：像素+元数据+资产 → 写 DNG）、`NefToDngConverter.cs`（编排：LibRaw 解码 NEF 像素 → 域映射 → DngComposer；参考 Save_Z8 `NefProcessor.cs` 但无修复步骤）
- Test: `tests/Jsr4Z8.Tests/DngWriterTests.cs`（参考 Save_Z8 `tests/SaveZ8.Tests` 的 DNG 结构回读断言模式）

**Interfaces:**
- Consumes: `RawMetadataReader`、`NefAssetExtractor`（Task 3）、`DngLjpegNative`（Task 2）。
- Produces: `DngComposer.Compose(RawPayload payload, RawMetadata meta, NefAssets assets, string destDngPath)`（供 Task 9 复用，像素源可替换）；`RawPayload { ushort[] Data; int W, H; int Samples; bool IsCfa; string? CfaPattern; }`。便捷入口 `NefToDngConverter.Convert(string nefPath, string destDngPath)`（CFA 路径：LibRaw 解码 NEF 像素 → 域映射 → DngComposer，验证与 Save_Z8 同构能力）。输出 DNG 含 IFD0 缩略图+基线/色彩标签+EXIF/GPS+SubIFDs（raw+全尺寸预览+中图预览）、raw IFD ActiveArea/DefaultCrop 成对、OpcodeList2/3（NEFInfo 映射）、Compression=7（CFA 路径 14-bit 原生域 BL=1008/WL=15892）、AsShotNeutral=1/cam_mul。

- [ ] **Step 1: 失败测试**（samples `DSC_7576.NEF`）：Convert 后用测试内 TIFF 遍历器断言：SubIFD raw Compression==7、WhiteLevel==15892、BlackLevel==1008、OpcodeList2/3 存在且非空、IFD0 EXIF 含 Make/Model/ISOSpeedRatings==3200、预览 IFD≥2 个（JPEG magic FF D8）、LibRaw（Task 2 句柄）能打开产物读出 8256×5504（以实际为准）。
- [ ] **Step 2: 确认失败 → 复刻+实现 → 确认通过。**
- [ ] **Step 3: Commit** `feat: DngWriter ported - full metadata lossless DNG from NEF`

### Task 9: 后端 DNG 融合器（JSR_基准名.dng 落盘）

**Files:**
- Create: `src/Jsr4Z8.Core/Dng/JsrDngFinalizer.cs`、`BackendDngReader.cs`（读后端 DNG 的 raw IFD：像素数组、BL/WL、CFA、尺寸；未压缩与压缩两种读取容忍）
- Create: `tests/Jsr4Z8.Tests/fixtures/backend-dng/`（e2e ROI 小样产物——见 Step 0）
- Test: `tests/Jsr4Z8.Tests/FinalizerTests.cs`

**Interfaces:**
- Consumes: Task 8 的 `DngComposer`（LinearRaw RGB 模式，事实 #9：后端产物为 16560×11040×3×16bit LinearRaw、BL=0/WL=65535、Compression=7 LJ 条带）、Task 3 资产提取。
- Produces: `static string JsrDngFinalizer.Finalize(string backendDngPath, string referenceNefPath, string destDir)` → 返回写出的 `JSR_<基准帧名>.dng` 绝对路径。行为：解 LJ 条带读像素 → 紧凑重写（体积 -90%）；几何 ×2 适配：ActiveArea/DefaultCrop 按 2 倍缩放成对写入，WarpRectilinear/FixVignetteRadial 为归一化系数直接沿用；EXIF/GPS/Interop/XMP/预览来自基准帧 NEF；AsShotNeutral/ColorMatrix 沿用后端值（与 LibRaw cam_mul 交叉校验，偏差>1e-4 记日志）；**不写入后端 Software/Artist，Software 写 "JSR for Z8"**（事实 #9）。

- [ ] **Step 0: 生成 fixture**：以 `mode:"roi", roi:[512,512,1024,768]`（SDK 语法 `roi=@(x,y,w,h)` → JSON 数组）跑一次真实后端（Task 7 未就绪前可手动，复用 tmp_run1 流程），产物小 DNG 存 fixtures/backend-dng/；同时记录其 raw IFD 标签值写进本文件注释。
- [ ] **Step 1: 失败测试**：Finalize(小 fixture DNG, samples/DSC_7576.NEF, tempdir) → 文件名 `JSR_DSC_7576.dng`；结构断言：raw SubIFD Compression=7、Samples=3、Photometric=34892、宽高与输入一致；IFD0 含 EXIF（ISO 3200）与≥2 预览；OpcodeList2/3 非空；**无 Artist 标签且 Software=="JSR for Z8"**；像素往返：LJ 解码回读均值与输入偏差<0.01%（无损验证）；体积 < 输入的 20%。
- [ ] **Step 2: 确认失败 → 实现 → 确认通过。**
- [ ] **Step 3: Commit** `feat: JSR finalizer fuses backend pixels + reference NEF metadata`

### Task 10: 载荷管理（嵌入-释放-校验）

**Files:**
- Create: `tools/pack-payload/Program.cs`（小控制台：扫 `reference/Jiangtherapee-Bridge-2.3.25-Windows-x64/{exe,resources,THIRD_PARTY_NOTICES.md}` → 逐文件 SHA256 清单 `payload.manifest.json` + LZMA（`System.IO.Compression` 无 LZMA → 用 zlib deflate 或引入 `SharpCompress`——**决定：zlib `DeflateStream`，无新依赖**）打包 `payload.bin` → 生成 `src/Jsr4Z8.Core/Payload/Embedded/payload.bin+manifest` 为 EmbeddedResource（>100MB 资源，csproj `LogicalName` 固定））
- Create: `src/Jsr4Z8.Core/Payload/PayloadManager.cs`
- Test: `tests/Jsr4Z8.Tests/PayloadTests.cs`（用 <1MB 假载荷同管线，不嵌真载荷进测试）

**Interfaces:**
- Produces: `sealed class PayloadManager { static readonly string Root = %LOCALAPPDATA%\JSR4Z8\backend; string EnsureExtracted(IProgress<double>? progress); }`。行为：目标 `%LOCALAPPDATA%\JSR4Z8\backend\<manifest SHA256 前 16hex>\`；存在且清单全过 → 直接返回（<50ms）；任一文件缺/哈希不符 → 整目录删掉重建（自愈）；释放期间写 `.incomplete` 标记防中断半态。

- [ ] **Step 1: 失败测试**：假载荷（3 文件）打包→EnsureExtracted 到临时根 → 文件齐；删 1 文件再 EnsureExtracted → 自愈完整；二次调用跳过（计时断言）。
- [ ] **Step 2: 确认失败 → 实现 → 确认通过。真载荷仅进最终发布（Task 15），测试不依赖真载荷。**
- [ ] **Step 3: Commit** `feat: backend payload pack/extract/self-heal manager`

### Task 11: UI 骨架·主题·卡片流主窗口

**Files:**
- Create: `src/Jsr4Z8.UI/App.axaml(.cs)`（Semi 主题，`RequestedThemeVariant=Dark` 默认）、`Views/MainWindow.axaml(.cs)`、`Views/TaskCardView.axaml`、`ViewModels/MainViewModel.cs`、`ViewModels/TaskGroupViewModel.cs`、`Services/ThemeService.cs`
- Test: `tests/Jsr4Z8.Tests/TaskGroupVmTests.cs`

**Interfaces:**
- Consumes: Core 的 TaskGroup/ValidationResult（ViewModel 持纯状态，UI 绑定）。
- Produces: `MainViewModel { ObservableCollection<TaskGroupViewModel> Tasks; ICommand AddTask, StartAll, OpenSettings, ToggleTheme; }`；`TaskGroupViewModel` 状态机 `TaskState { Ready, Running, Done, Failed, Blocked }` + `int Progress; string StageText; ObservableCollection<string> LogLines; ICommand Start/Stop/Remove/Retry/OpenFolder/ToggleLog`。布局按 `docs/design/layout.html`：顶栏（logo+「Nikon Z8 专用」tag+添加/全部开始/⚙/主题切换）+ 卡片纵列 + 空状态投放提示。

- [ ] **Step 1: 失败测试**（ViewModel 纯逻辑）：初始无任务 → StartAll.CanExecute=false；插入 Ready 任务 → true；状态转移 Ready→Running→Done 后 Progress=100 且 OpenFolder 可用。
- [ ] **Step 2: 确认失败 → 实现（CommunityToolkit.Mvvm [ObservableProperty]/[RelayCommand]）→ 确认通过 + Windows host 手动启动 UI 冒烟（`dotnet run` 截图目检，无自动化）。**
- [ ] **Step 3: Commit** `feat: UI shell - card flow main window with theme service`

### Task 12: 新建任务组面板

**Files:**
- Create: `src/Jsr4Z8.UI/Views/NewTaskDialog.axaml(.cs)`、`ViewModels/NewTaskViewModel.cs`
- Test: `tests/Jsr4Z8.Tests/NewTaskVmTests.cs`

**Interfaces:**
- Consumes: `TaskGroupValidator`（输入变更即重校验，防抖 300ms 后台线程）。
- Produces: `NewTaskViewModel { ObservableCollection<FrameChipVm> Frames; string? DarkPath; ValidationState State; ICommand Create; }`；`FrameChipVm { string Path; bool IsReference; ICommand SetReference/Remove; }`。行为（mockup `add-task-flow-v3.html`）：拖入/选择 → 文件名自然升序去重、仅收 `.NEF/.nef`；7 帧满后仍可替换（重拖覆盖提示）；点芯片设基准（与现任互换）；黑场独立按钮 OpenFileDialog；`Create.CanExecute = State.CanRun`；创建产出 `TaskGroup` 交 MainViewModel。异常展示：Unreadable 文件红色芯片+错误条。

- [ ] **Step 1: 失败测试**：拖入乱序 7 文件（假路径注入 mock 读函数）→ 排序+基准=首个；选非 Z8 → Create 禁用+Issue 文案含机型名；黑场缺失 → 禁用；移除一帧 → 帧数 Issue。
- [ ] **Step 2: 确认失败 → 实现 → 确认通过 + UI 手动冒烟（真实拖放 samples）。**
- [ ] **Step 3: Commit** `feat: new-task dialog with live validation`

### Task 13: 队列执行引擎与卡片运行态接线

**Files:**
- Create: `src/Jsr4Z8.Core/Settings/AppSettings.cs`（纯 record 定义+默认值，持久化在 Task 14）、`src/Jsr4Z8.Core/Queue/TaskQueueRunner.cs`（顺序执行、失败继续、单飞）、`src/Jsr4Z8.UI/ViewModels/QueueMediator.cs`（VM↔Core 桥）、`Views/LogPanel.axaml`
- Test: `tests/Jsr4Z8.Tests/QueueRunnerTests.cs`

**Interfaces:**
- Consumes: `BackendRunner`、`BackendRequest.BuildJson`、`JsrDngFinalizer`、`PayloadManager`、`AppSettings`（本任务创建定义，Task 14 建持久化）。
- Produces: `TaskQueueRunner.Run(TaskGroup, AppSettings, CancellationToken)`：①`PayloadManager.EnsureExtracted`（首次释放进度直接映射卡片进度条 0-5%）；②临时目录 `<TempBase>\Jsr4Z8\<jobId>\`（TempBase=设置）；③config=request 同目录 `cfg\`；④BuildJson(memory=设置 VramGb, fullwell=校验 Ptc) → spawn → 事件上抛；⑤RESULT 成功 → 在 output dir 找 `prefix_*.dng` → Finalize → 拷/写到 `<输出目录>\JSR_<基准名>.dng`（SameAsInput=基准帧目录）；⑥成功清理临时目录，失败保留。`TaskState` 全链路映射（Blocked/Ready→Running(进度=PROGRESS 行)→Done/Failed）。

- [ ] **Step 1: 失败测试**（stub 后端，Task 7 fixtures）：两个任务顺序执行断言串行（第二个 Started 在第一个 Finished 之后）；第一个失败 → 第二个仍执行；取消队列 → 当前任务 3s 内 Finished(cancelled)、后续不启动。
- [ ] **Step 2: 确认失败 → 实现 → 确认通过。**
- [ ] **Step 3: Commit** `feat: sequential queue + backend wiring + finalizer integration`

### Task 14: 高级设置与持久化

**Files:**
- Create: `src/Jsr4Z8.Core/Settings/SettingsService.cs`（JSON `%LOCALAPPDATA%\JSR4Z8\settings.json`，持久化 Task 13 定义的 `AppSettings`）、`src/Jsr4Z8.UI/Views/SettingsDialog.axaml(.cs)`、`ViewModels/SettingsViewModel.cs`
- Test: `tests/Jsr4Z8.Tests/SettingsTests.cs`

**Interfaces:**
- Produces: `record AppSettings { double VramGb = 4.0; OutputMode = SameAsInput; string? CustomOutputDir; TempMode = Default; string? CustomTempDir; string? LastDarkPath; }`（LastDarkPath 顺带记住上次黑场目录仅作文件对话框初始目录——不自动沿用）；`SettingsService.Load()/Save()`（损坏 JSON → 默认值重写）。UI 按 mockup `settings.html` B 版：滑块 1-16 步进 0.5 + 数值框、单选+浏览、底部示例输出绑定（选基准帧演示路径 `\JSR_xxx.dng`）。

- [ ] **Step 1: 失败测试**：默认值；往返序列化；损坏文件→默认；VramGb clamp [1,16]。
- [ ] **Step 2: 确认失败 → 实现 → 确认通过 + UI 冒烟。**
- [ ] **Step 3: Commit** `feat: settings dialog + persistence`

### Task 15: 一键构建与单文件发布

**Files:**
- Modify: `build.bat`（补全 Step 4-5：`tools/pack-payload` 生成真载荷资源 → `dotnet publish src/Jsr4Z8.UI -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`（原生 DLL 内嵌压缩，照 Save_Z8 模式）→ `out\Jsr4Z8_v<版本>.exe`）
- Create: `src/Jsr4Z8.UI/Program.cs` 增加 `--selftest` 无头模式（载荷校验+LibRaw 冒烟+退出，照 Save_Z8 先例）
- Test: 手动验证（无单测）

- [ ] **Step 1: Windows host 跑 `build.bat` 全链**，Expected: out 下单一 exe；记录体积。
- [ ] **Step 2: 冒烟**：`out\Jsr4Z8_v*.exe --selftest`（在无 dotnet 的干净 PATH 环境下，如 `cmd /c set PATH=C:\Windows\System32 && out\...exe --selftest`）Expected: 自检通过 exit 0。
- [ ] **Step 3: Commit** `build: one-shot build to single-file exe with embedded payload`

### Task 16: 端到端验收（Windows Host）

- [ ] **Step 1: GUI 全流程**：添加 samples 组（黑场 DSC_7575 + 7576-7582）→ 校验行显示 Z8·ISO3200·满阱 1102.2（表值）→ 开始 → 观察进度（SRHQ_V12_PROGRESS 驱动）→ 完成卡显示输出路径/大小/耗时 → 打开文件夹确认 `JSR_DSC_7576.dng`。记录总耗时与显存报错样例（4GB 跑一次取证错误文案链路）。
- [ ] **Step 2: 产物 DNG 全项检查**：LibRaw 回读像素非恒零；Compression=7；EXIF（ISO 3200/ExposureTime/焦距）、OpcodeList2/3、≥2 预览、AsShotNeutral=[0.560175,1,0.557734]（探针事实 #8）；体积 vs 未压缩明显缩小；与 Save_Z8 输出结构抽查对照。
- [ ] **Step 3: 鲁棒性**：中文+空格路径副本全流程；运行中点停止 → tasklist 无 jsr_backend 残留、卡片回 Ready；二次启动跳过载荷释放（日志时间戳）；删载荷半目录再启动 → 自愈。
- [ ] **Step 4: 成功 stdout 完整日志回存** `tests/fixtures/protocol/success.log` 并补 ProtocolParser 全量测试通过。
- [ ] **Step 5: Commit + 更新 README（含 SmartScreen/杀软说明、使用说明、体积）**，全绿后交付。
