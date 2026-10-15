# 绕过 Bridge 直调 jsr_backend 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** BackendRunner 直调 `resources\jsr_backend.exe`（主算链 argv 模板自建），预览链采样后跳过；载荷移除 Bridge.exe；真 GPU E2E 全量重验；协议契约（AGENT.md §4）与全部相关测试重写为裸行协议。

**Architecture:** Task1 纯实验（预览 JSON 采样 + 无预览直调全流程实证 + 裸 stdout/退出码/双实例行为取证）→ Task2 Core 重构（argv 构建器替代 request.json、BackendRunner 直调+合成 Started/Finished、裸行解析沿用 SRHQ 既有路径、TaskQueueRunner/PayloadManager/selftest 换引擎路径、测试以 Task1 真实捕获为新夹具）→ Task3 载荷白名单去 Bridge + 重打包（哈希比对自动换新已就位）+ E2E 5/5 + 文档契约重写。

**Tech Stack:** 既有栈；无新依赖。

**Spec:** 用户 2026-10-15 第四轮指令（改为绕过 Bridge 直调；预览实测采样后跳过/忽略）+ 证据文档 docs/superpowers/notes/2026-10-15-jsr-backend-argv-probe.md（argv 模板）。

## Global Constraints（含并行隔离，最高优先）

- **并行隔离**（同目录仍有 UI Agent）：不触 `src/Jsr4Z8.UI/**`、TaskGroupVmTests/TaskGroupCreatorTests/ProgressThrottleTests/QueueRunnerTests 之外的本轮必要改动须逐文件显式 add——本轮预计触碰 QueueRunnerTests/ProtocolTests/BackendRunnerTests/E2eTests/PayloadTests（Core 域，与 UI Agent 无冲突，仍逐文件 add）；无分支操作；提交前 `git status --short` 核对暂存集；外来失败→BLOCKED 上报；E2E 前查无并发 testhost；MSBuild 锁→30s 重试。
- Windows Host 构建/测试：`cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet test --nologo"`（基线 283/283）；禁止递归搜索 reference/、external/、samples/、~/.nuget。
- 主算链 argv 模板（证据文档 §2，逐字）：`--output-dir <jobDir> --out-prefix <jobId> --dng-compression auto --raw-frame <path>×7 --developer-mode --read-noise-dn <RN> --black-level-dn <BL> --full-well-electrons <FW> --deghost off --out-dng on --out-png off --out-tiff off --out-format jpeg --jpeg-quality 95 --camera-profile-source libraw`（deghost 按 request 预留 off/on 映射；值全部来自既有 PtcMatch/DarkMatch/TaskGroup 数据）。
- 预览链：实测采样 preview-input.json/preview.json 内容入证据文档；产品代码**不实现**预览（用户裁定：GUI 不支持预览，跳过/忽略）。直调成功性必须以"无预览、无 cfg 目录"的裸主算链实证。
- 协议：直调后 stdout=引擎裸行（SRHQ_V12_PROGRESS/SRHQ_ETA_PROGRESS/SRHQ_V12_FRAME/INFO/PTC/DONE 等），无 ASSISTANT_* 信封；Started 事件由我们合成（spawn 即知 pid；stage/compressor/engine 字段自填）；Finished 由进程退出合成（退出码+日志尾人话化沿用既有 SRHQ_NET_ABORT/MemoryError 提取，跳过信封行的逻辑删除）。取消仍杀整棵树（引擎自再 spawn 双实例）。
- 载荷：打包白名单改为 `resources/` 全树（减既有排除）——顶层不再含 Bridge.exe（文件移除+存在性校验改为 resources\jsr_backend.exe）；`_internal` 整树保留（引擎对 _internal 的依赖面未知——本轮**不做**激进裁剪，留待后续矩阵实验；Bridge.exe -14MB 是本轮唯一移除项）。
- AGENT.md §4 硬契约整节重写（后端调用/请求 schema→argv 模板/stdout 协议→裸行/取消/产物不变）；README 后端描述同步；证据文档追加预览采样与直调实证节。
- 每任务一 commit；TDD（Task2 协议/argv 构建器/Task3 白名单）；E2E 门禁真 GPU。

## Review Focus

1. 无预览直调是否真等价（DNG 产出+退出码 0+像素经 finalizer）→ Task1 实验即门禁；Task3 E2E 终验。
2. 引擎失败模式裸行化（SRHQ_NET_ABORT/MemoryError 无信封包装后提取逻辑仍有效）→ Task2 测试（失败夹具=真实或复刻裸行）。
3. 双实例自 respawn：取消杀树覆盖孙进程；退出码取父进程是否可靠 → Task1 取证 + Task2 BackendRunnerTests(stub 复刻双实例)。
4. 预览跳过的副作用（引擎是否在某阶段读 preview.json）→ Task1 多次直调（含 HE 帧）验证。
5. 载荷白名单变更不破坏 PayloadManager 释放/校验/自愈/selftest → Task3 PayloadTests+E2E。
6. 外来 UI WIP 污染 → 全任务隔离规则。

---

### Task 1: 预览采样与直调实证（实验）

**Files:**
- Modify: `docs/superpowers/notes/2026-10-15-jsr-backend-argv-probe.md`（追加 §6 预览采样、§7 直调实证；结论段更新为"已实施直调"基调）
- 脚本/日志：SDD 工作区，不提交

**Interfaces:**
- Produces: ①preview-input.json/preview.json 实际内容（代码块全文）②≥2 次无预览直调成功记录（无损+HE，DNG 产出路径+字节数+退出码+耗时）③裸 stdout 形状记录（前 30 行+尾 10 行，确认无信封）④双实例时序与退出码归属观察 ⑤cfg 目录不需要的证据 ⑥新协议夹具素材：一次完整直调 stdout 存档（供 Task2 提交为 tests/fixtures/protocol/direct_success.log）。

- [ ] **Step 1:** 预览采样：以当前代码跑一次真实任务（E2E FullRun 或手动 runner），监视器轮询 `%TEMP%\Jsr4Z8\<job>\cfg\jsr\` 出现即整目录复制到工作区（成功后源目录会被删除）；若监视窗口错过，改为临时将设置指向自定义 TempBase 并手动跑一次。
- [ ] **Step 2:** 直调实验（PowerShell/cmd 手工）：从已释放载荷直接 spawn `resources\jsr_backend.exe`，argv 按模板逐字构造（7 帧=samples 无损组；jobDir=独立临时目录；RN/BL=新表 ISO3200 值 14.07/1008；FW=1102.2）；**不建 cfg、不跑预览**；重定向 stdout 到日志；等待完成；验证：退出码 0、`<prefix>_V15_3_5_K7_*` 目录与 DNG 出现、日志无 ASSISTANT_ 信封。HE 组（samples 的 HE 帧）重复一次。
- [ ] **Step 3:** 双实例观察（监视器同 Task1/上轮 argv-monitor）：父/子 pid、退出顺序、父退出码；再跑一次中途 taskkill /T 验证杀树完整性（无孤儿 jsr_backend）。
- [ ] **Step 4:** 证据文档追加两节+结论更新；stdout 存档命名 direct_success.log 暂放工作区（Task2 提交）。
- [ ] **Step 5:** Commit（仅证据 md）`docs: 预览链采样与无预览直调实证——裸行协议与双实例取证`

### Task 2: Core 直调实现（argv 构建器 + 裸行协议）

**Files:**
- Modify: `src/Jsr4Z8.Core/Backend/BackendRequest.cs`（BuildJson→BuildArguments：TaskGroup+PtcMatch+DarkMatch+jobDir+jobId → IReadOnlyList<string>；deghost 映射 off/on）
- Modify: `src/Jsr4Z8.Core/Backend/BackendRunner.cs`（直调 spawn：engine 路径+argv；stdout 逐行=裸行：SRHQ 解析沿用、信封解析删除、Started 合成（pid/stage/engine/compressor 自填）、Finished 合成（ExitCode+人话化：SRHQ_NET_ABORT/MemoryError 从裸行尾提取）；杀树/作业日志保留）
- Modify: `src/Jsr4Z8.Core/Backend/ProtocolParser.cs`（删 ASSISTANT_* 信封解析，保留 SRHQ_V12_PROGRESS/SRHQ_ETA_PROGRESS 裸行；或信封代码保留但不再被调用——以最小删除为准）
- Modify: `src/Jsr4Z8.Core/Queue/TaskQueueRunner.cs`（BridgeFileName→EngineFileName="jsr_backend.exe"；payload 解析 join resources\jsr_backend.exe；request.json 写盘删除；cfg 目录创建删除）
- Create: `tests/fixtures/protocol/direct_success.log`（Task1 存档）+ 失败直调夹具（复刻 SRHQ_NET_ABORT 裸行）
- Modify: `tests/Jsr4Z8.Tests/ProtocolTests.cs`（逐字段锁定改为 argv 构建器+裸行解析；删除 request.json schema 锁）+ `BackendRunnerTests.cs`（stub .bat 改造：裸行输出、双实例 respawn 复刻、失败模式）+ `QueueRunnerTests.cs`（Harness 解析路径适配；仅必要断言调整）
- Test: 上述测试文件

**Interfaces:**
- Produces: `BackendRequest.BuildArguments(TaskGroup g, PtcMatch ptc, DarkMatch dark, string outputDir, string prefix)`；`BackendRunSpec(EngineExe, ResourcesDir, RequestArguments, LogFile)`（或等价——保持 ITaskRunner 以上层不变）；QueueEvent 形状不变（Started/LogLine/Finished 语义保留）。

- [ ] **Step 1: 失败测试**：argv 构建器逐开关断言（顺序/值/7×--raw-frame/deghost 映射）；裸行解析（direct_success.log 夹具→进度序列）；BackendRunner 直调 stub（裸行+exit0→Done；SRHQ_NET_ABORT 裸行→人话化失败；双实例 stub（父 spawn 子同 argv）→取消杀树无孤儿；信封行出现→按普通行处理不再解析信封）。
- [ ] **Step 2: 红** → **Step 3: 实现** → **Step 4: 聚焦绿**（Protocol|BackendRunner|QueueRunner 过滤器）。
- [ ] **Step 5: Commit**（显式列举）`feat(backend): 直调 jsr_backend.exe——argv 构建器替代 request.json，裸行协议替代信封`

### Task 3: 载荷去 Bridge + E2E 全量重验 + 契约文档

**Files:**
- Modify: `src/Jsr4Z8.Core/Payload/PayloadPacker.cs`（白名单=resources 全树（减排除）；顶层 Bridge.exe 移除；必需源校验改 resources\jsr_backend.exe；排除清单追加顶层 bridge 精确路径以防回潮）
- Modify: `src/Jsr4Z8.Core/Payload/PayloadManager.cs`（如注释提及 bridge）+ `src/Jsr4Z8.UI/Program.cs` selftest（bridge 检查→engine 检查）——注意 Program.cs 属 UI 项目但非 UI Agent 域（Program/selftest），逐文件 add 并在报告说明
- Modify: `tests/Jsr4Z8.Tests/PayloadTests.cs`（白名单断言：Bridge.exe 不进 manifest、engine 进）
- Modify: `AGENT.md`（§4 硬契约整节重写；§1/§3 架构图 Payload 行与调用链更新）、`README.md`（后端调用描述）
- 产物：Embedded 重打包（gitignored；哈希比对自动换新）
- Test: PayloadTests + E2eTests（真 GPU 5/5）

**Interfaces:**
- Consumes: Task 2 的 EngineFileName/直调路径。
- Produces: 载荷无 Bridge.exe；selftest 验 engine；E2E 全绿基线。

- [ ] **Step 1: 失败测试**（PayloadTests：bridge 缺席/engine 在/计数）→ **Step 2: 红** → **Step 3: 实现**（packer+manager+selftest）。
- [ ] **Step 4: 重打包**（pack-payload 对 2.3.27 源；记录统计——bridge -14MB；抽查 bridge 0 命中、engine 在）。
- [ ] **Step 5: E2E 全量 5/5 真 GPU**（直调新协议端到端；含中文路径/取消/信封捕获用例改造——SuccessStdoutCapture 改为直调裸流断言）。
- [ ] **Step 6: 全量套件 + 文档（AGENT.md §4 重写/README）** → **Step 7: Commit** `feat(payload): 载荷移除 Bridge.exe——直调引擎端到端 E2E 重验；契约文档重写`
