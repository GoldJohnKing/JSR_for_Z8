# argv 探测 + 后端测量 dark 表 + 升级 2.3.27 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ① 运行期抓取 jsr_backend.exe 真实 argv（回答能否绕过 Bridge.exe）② 以 2.3.27 z8_presets manifest 再生成预置黑场表 ③ 载荷升级 2.3.27（自动选最高版本 + 过期载荷自动重打包）并以真 GPU E2E 重验。

**Architecture:** Task1=纯实验（进程命令行快照+证据文档）；Task2=新离线工具 regen-dark-table 重生成 `Dark/Embedded/dark_z8.csv`（NEF 压缩码映射 0→3/3→14/4→13，RN=√四相位时域方差均值）；Task3=build.bat 选最高版本 + pack-payload 临时打包哈希比对自动换新 + Embedded 重打包 2.3.27（沿用既有排除清单，z8_presets 整目录排除）+ E2E 全量重验（顺带复抓 argv 验证 2.3.27 契约未变）。

**Tech Stack:** 既有栈，无新依赖；tools/ 新增 dotnet run 工具（同 pack-payload 模式）。

**Spec:** 用户 2026-10-15 第三轮指令（Q1 实验、Q2 方案、引擎升级最新）+ 本计划 Global Constraints。

## Global Constraints（含并行隔离，最高优先）

- **并行隔离（用户明示：同目录另有 Agent 做 UI 改动）**：禁止触碰 `src/Jsr4Z8.UI/**`、`tests/Jsr4Z8.Tests/TaskGroupVmTests.cs`、`TaskGroupCreatorTests.cs`、`ProgressThrottleTests.cs`、`QueueRunnerTests.cs`（UI 链域）；**禁止任何分支操作**（checkout/merge/rebase/reset）；提交一律显式列举文件路径 `git add <paths>`，禁止 `git add -A/-u`；每次提交前 `git status --short` 快照并核对暂存区只含本任务文件；若构建/测试失败定位到本任务 diff 之外的文件（外来 WIP），**停止并 BLOCKED 上报**，绝不修改他人代码；构建遇文件锁（他人同时构建）等待 30s 重试，禁止删 build/ 全目录；E2E 启动前 `tasklist` 确认无并发 testhost/vstest。
- 构建/测试 Windows Host：`cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && dotnet test --nologo ..."`（当前基线 267/267，但外来 WIP 可能污染全量——**本轮任务门禁用聚焦过滤器**，全量仅在 Task C 终验跑且按上述规则处置外来失败）。
- 禁止递归搜索 reference/、external/、samples/、~/.nuget；reference/ 只读。
- A：实验证据文档提交至 `docs/superpowers/notes/2026-10-15-jsr-backend-argv-probe.md`（捕获的完整命令行 + 进程树 + 结论）。
- B：新表格式沿用 `nef_comp,iso,black,rn`（首行起注释保留、来源/公式/映射/manifest sha256 写入头注）；RN=√(per_phase temporal_variance_dn2 均值) 保留 2 位小数；black=black_dn（整数照写）；生成 102 行（34 ISO×3 压缩）；旧表被**替换**（新表为超集，无 fallback 逻辑）；DarkTableTests 更新期望值并补 Lo 档行存在性断言；AGENT.md 决策 3 数据源改写。
- C：build.bat 载荷源改为**目录名降序第一个**（最高版本，注释说明 2.3.x 前提）；pack-payload 改为"打包到临时目录 → 与 Embedded 现有 manifest 哈希比对 → 不同才原子替换两文件，相同则保留并打印跳过"（消灭"存在即跳过"的过期陷阱；.bat 保持 CRLF）；Embedded 从 2.3.27 重打包（排除清单不动，z8_presets 自动排除）；E2E 全量 5 用例真 GPU 重验；E2E 运行期间用 Task 1 的监视器复抓 argv 比对 2.3.25/2.3.27 是否一致；文档同步（AGENT.md §1 版本号、§7 移除"build.bat 载荷跳过仅按存在性"遗留、README 如有版本号）。
- 每任务一 commit；TDD（B 的工具与表、C 的 pack-payload 比对逻辑有测试；build.bat/E2E 为脚本与门禁）。

## Review Focus

1. 外来 WIP 污染构建——发现即停并上报，不修他人代码 → 全任务约束。
2. B：Lo 档 32/40/50 行在 LibRaw 报告 ISO=64 的 NEF 上可能永不命中（AGENT.md 既有事实：Lo=假ISO）——表包含该三档作为完备性，不宣称匹配行为变化 → B 测试注释钉死。
3. B：黑场值从实测均值（1005.89）变为标称 black_dn（1008）——请求语义仍为 manual black；E2E（C）实测引擎接受 → C 门禁。
4. C：替换 Embedded 两文件的非原子窗口（gitignored、build 间隙可接受）——工具内注释说明先 manifest 后 bin 顺序。
5. C：版本字符串降序选择对 2.3.x 成立、2.10 之类会误序——注释标注前提，不做通用版本解析（YAGNI）。
6. A：监视循环可能错过短命进程——StopMidRun 用例引擎存活秒级，200ms 轮询足够；漏抓则改用 FullRun 用例窗口重试。

---

### Task 1: argv 运行期抓取实验

**Files:**
- Create: `docs/superpowers/notes/2026-10-15-jsr-backend-argv-probe.md`（唯一提交物）
- 脚本与日志：临时目录（.superpowers/ 工作区内），不提交

**Interfaces:**
- Produces: 证据文档（含 jsr_backend.exe 完整命令行、进程树时序、Bridge.exe argv 对照、与直调可行性的初步结论）；监视脚本路径供 Task C 复用。

- [ ] **Step 1:** 写 PowerShell 监视器（200ms 轮询 ≤120s）：`Get-CimInstance Win32_Process` 过滤 Name∈{jsr_backend.exe, jsr_v98_k14_backend.exe, jsr_fast_k14_backend.exe, raw_bridge.exe, jxl_worker.exe, Jiangtherapee Bridge.exe}，捕获 ProcessId/ParentProcessId/CreationDate/CommandLine 去重追加日志；后台启动。
- [ ] **Step 2:** `set JSR4Z8_E2E=1&& dotnet test --filter FullyQualifiedName~E2eTests.StopMidRun`（真实后端、取消式、引擎短命窗口足够）；漏抓则改 FullRun_NoDarkFile 重试一次。
- [ ] **Step 3:** 停监视器；整理证据写文档：每进程完整 argv、父子关系、工作目录若可得、结论段（argv 是否含 request.json/resources/config 之类可自建参数；直调面=重写 harness 的最小集合；2.3.27 复验指向 Task C）。
- [ ] **Step 4:** Commit `docs: jsr_backend 运行期 argv 探测证据与直调可行性初析`（仅该 md 文件）。

### Task 2: regen-dark-table 工具与新表

**Files:**
- Create: `tools/regen-dark-table/Program.cs` + `regen-dark-table.csproj`
- Modify: `src/Jsr4Z8.Core/Dark/Embedded/dark_z8.csv`（再生成为 102 行）
- Modify: `tests/Jsr4Z8.Tests/DarkTableTests.cs`（期望值更新 + Lo 行断言 + 新表来源注释核对）
- Modify: `AGENT.md`（§5 决策 3 数据源句改写）
- Test: `tests/Jsr4Z8.Tests/DarkTableTests.cs`

**Interfaces:**
- Produces: `tools/regen-dark-table <manifest.json 路径> [输出csv路径]`（默认输出到 Core Embedded；解析 JSR_Z8_DARK_LIBRARY_V1：profiles[].{iso,compression,black_dn,per_phase[].temporal_variance_dn2}；映射 0→3、3→14、4→13；输出排序 nef_comp,iso 升序）；头注含 schema、来源 manifest 的 SHA-256、公式、映射表、生成时间。

- [ ] **Step 1: 失败测试**（DarkTableTests）：`BackendPresetTable_Covers34Stops`（LoadDefault 后行数==102；Lookup(32,3)/(40,3)/(50,3) 非空）；`BackendPresetTable_Iso3200Values`（Lookup(3200,3)→(1008,14.07±0.01)；(3200,13)→(1008,15.10)；(3200,14)→(1008,14.18)）；`BackendPresetTable_HeaderRecordsProvenance`（首注释行含 z8_presets manifest 字样与 SHA-256）。工具侧：`Regen_WritesExpectedCsv`（测试内建迷你 manifest JSON → 临时输出 → 行断言）——工具逻辑以内部 API 或进程方式测（参照 pack-payload 的 PayloadTests 做法，用项目内静态方法 + 测试引用）。
- [ ] **Step 2: 红。**
- [ ] **Step 3: 实现**工具 + 用真实 `reference/Jiangtherapee-Bridge-2.3.27-Windows-x64/resources/z8_presets/manifest.json` 再生成 Embedded 表；更新 DarkTableTests 硬编码期望；AGENT.md 决策 3 改写（数据源=后端 2.3.27 z8_presets manifest 离线导出；black_dn 标称；RN=√四相位时域方差均值；旧自测表弃用；Lo 档三行为完备性纳入，LibRaw 若报 ISO64 则自然命中 64 行——匹配行为不变）。
- [ ] **Step 4: 聚焦绿**（--filter FullyQualifiedName~DarkTableTests）+ `--filter FullyQualifiedName~ValidationTests|FullyQualifiedName~PtcTests` 无回归。
- [ ] **Step 5: Commit**（显式列举 5 个文件）`feat(dark): 预置黑场表改由后端 z8_presets manifest 离线导出（102 档，含 Lo 三档）`

### Task 3: 载荷升级 2.3.27 + 自动选版/自动换新 + E2E 重验

**Files:**
- Modify: `build.bat`（选最高版本；Step 4 注释更新；保持 CRLF）
- Modify: `tools/pack-payload/Program.cs`（临时打包+哈希比对+条件替换；测试以进程级或静态法覆盖）
- Modify: `tests/Jsr4Z8.Tests/PayloadTests.cs`（比对/替换逻辑测试）
- Modify: `AGENT.md`（§1 2.3.25→2.3.27；§7 删除"build.bat 载荷跳过仅按存在性"行）、`README.md`（如有版本号处）
- 产物：`src/Jsr4Z8.Core/Payload/Embedded/payload.*`（gitignored，重打包自 2.3.27）

**Interfaces:**
- Produces: pack-payload 新行为——输出到 `<Embedded>.tmp-pack-<pid>/`，与现有 `payload.manifest.json` 逐字节（或 SHA-256）比对：相同→删临时目录、打印"payload 未变，跳过"；不同→替换 manifest 与 bin（先 manifest 后 bin，注释注明间隙）并打印新哈希。build.bat Step 4 改为无条件调用 pack-payload（工具自决），版本选择 `dir /b /ad /o-n` 取第一个。

- [ ] **Step 1: 失败测试**：`Pack_UnchangedSource_KeepsExistingEmbedded`（伪造源两次打包第二次不替换——以静态法/临时目录法测）；`Pack_ChangedSource_ReplacesEmbedded`。
- [ ] **Step 2: 红。**
- [ ] **Step 3: 实现**工具行为 + build.bat 两处修改（CRLF 校验 `file build.bat` 不得变 CRLF→LF）。
- [ ] **Step 4: 实际重打包**：跑 `dotnet run --project tools\pack-payload -- reference\Jiangtherapee-Bridge-2.3.27-Windows-x64 src\Jsr4Z8.Core\Payload\Embedded`；记录新旧 manifest（文件数/字节/哈希）；抽查排除项仍 0 命中（含 z8_presets/THIRD_PARTY_NOTICES）。
- [ ] **Step 5: E2E 全量重验**（启动前 tasklist 查并发）：`set JSR4Z8_E2E=1&& dotnet test --filter FullyQualifiedName~E2eTests` 全绿（新载荷 + 新 dark 表端到端：manual DN=1008/RN=后端值直喂引擎）；同时后台运行 Task 1 监视器复抓 argv，与 Task 1 文档比对（2.3.27 是否一致——不一致则更新文档结论段并显著标注）。
- [ ] **Step 6: 全量套件**（按并行隔离规则处置外来失败）+ 文档同步 + Commit（显式列举文件）`feat(payload): 升级 2.3.27，自动选最高版本与载荷哈希比对换新；E2E 重验`
