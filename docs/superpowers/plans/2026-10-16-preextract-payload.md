# 预解包载荷 + _PYI_* 注入直跑 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 打包时预解包引擎（CArchive→1,416 文件树，含引擎 exe 本体）替代 onefile 整体嵌入；运行时 PayloadManager 照常释放+复用，BackendRunner 以 `_PYI_*` 三重注入直跑树内引擎——零 `_MEI`、零每轮解压、版本内容寻址。

**Architecture:** Task1=pack 工具预解包（C# 移植实验解包器；树入白名单、onefile exe 出白名单；合成 CArchive 夹具测试）；Task2=BackendRunner 引擎路径改树内+三变量注入（stub 回显环境断言）+TaskQueueRunner/selftest 路径同步；Task3=重打包+selftest+一次完整验证+文档。

**Tech Stack:** 既有；实验配方=docs/superpowers/notes/2026-10-16-meipass2-experiment.md + 工作区 Python 解包器（忠实移植基准）。

**Spec:** 用户 2026-10-16 预解包提议+批准实施。

## Global Constraints

- 忠实复刻实验验证形态：树布局与注入三变量**逐字对齐**证据文档（`_PYI_ARCHIVE_FILE=<树>\jsr_backend.exe`、`_PYI_APPLICATION_HOME_DIR=<树>`、`_PYI_PARENT_PROCESS_LEVEL=0`）——布局偏差=实验失效面。
- Windows Host；基线 274/274（feat/jobobject-payload-trim @52406eb）；完整 GPU 直调验证仅 Task3 一次（实施门禁，用户实施令授权）；E2E 套件仍默认禁跑。
- 载荷源=reference\Jiangtherapee-Bridge-2.3.27-Windows-x64（只读）；树内引擎 exe 必须与源 onefile exe 逐字节一致（提取自归档的 exe 条目或原样拷贝——以实验配方为准）。
- pack 工具的 CArchive 解包器必须有**合成夹具测试**（构造微型归档→解包→树断言），不得只靠真 201MB 归档冒烟。
- 隔离规则延续（外来 UI 域不碰、显式 add、无分支操作）；每任务一 commit；TDD。

## Review Focus

1. 树布局保真（实验形态 vs 实现偏差——尤其 exe 在树内的位置与来源）→ Task1/2 测试+Task3 全跑。
2. 注入值逐字（错一个反斜杠=复位块剥除→静默回退自解压，症状隐蔽）→ Task2 stub 回显断言。
3. 白名单翻转完备（onefile exe 不进 manifest、树全进、旧排除不回潮）→ Task1 测试。
4. 升级链语义（源引擎变→树 hash 变→EmbeddedUpdater 换新——现有机制应自然工作，验证之）→ Task1 测试。
5. 尺寸对账（payload.bin 预计 ~200MB 量级；实测为准入文档，不得写预估数）→ Task3。

---

### Task 1: pack-payload 预解包

**Files:**
- Modify: `tools/pack-payload/Program.cs`（或新文件 EnginePreextractor.cs 于工具项目）+ `src/Jsr4Z8.Core/Payload/PayloadPacker.cs`（白名单/排除调整）
- Modify: `tests/Jsr4Z8.Tests/PayloadTests.cs`
- Test: 同上

**Interfaces:**
- Produces: 工具内 `EnginePreextractor.Extract(string onefileExe, string destDir)`（CArchive：EOF MEI cookie/TOC/类型码/zlib——逐逻辑移植实验 Python 脚本，脚本在 `.superpowers/sdd/2026-10-16-meipass2-experiment/ws/`）；pack 流程：源树 resources\jsr_backend.exe → 临时解包 → 树以 `resources\jsr_backend_runtime\` 前缀入载荷（含引擎 exe 本体），源 onefile exe 加 ExactPath 排除（注释：预解包替代——2026-10-16 用户裁定）。

- [ ] Step 1: 失败测试：合成微型 CArchive 夹具（构造器 helper：header+TOC+b/z/m 条目+zlib）→ Extract→树/字节/类型断言；PayloadTests：pack 后 manifest 含 jsr_backend_runtime\ 树、不含 resources\jsr_backend.exe、计数对。
- [ ] Step 2: 红 → Step 3: 实现 → Step 4: 聚焦绿（PayloadTests）+全量 274 保持。
- [ ] Step 5: Commit `feat(payload): 打包预解包引擎——CArchive 树替代 onefile 整体嵌入`

### Task 2: BackendRunner 树内引擎 + 三变量注入

**Files:**
- Modify: `src/Jsr4Z8.Core/Backend/BackendRunner.cs`（引擎路径=<释放根>\resources\jsr_backend_runtime\jsr_backend.exe；psi.EnvironmentVariables 注入三变量）、`src/Jsr4Z8.Core/Queue/TaskQueueRunner.cs`（EngineFileName→树内路径常量）、`src/Jsr4Z8.UI/Program.cs`（selftest 引擎存在性检查改树内路径）
- Modify: `tests/Jsr4Z8.Tests/BackendRunnerTests.cs`（stub 回显 %_PYI_% 三变量→逐字断言）、`QueueRunnerTests.cs`（Harness 路径适配）
- Test: 同上

- [ ] Step 1: 失败测试（stub 回显注入断言+路径解析断言）→ Step 2: 红 → Step 3: 实现 → Step 4: 聚焦+全量绿。
- [ ] Step 5: Commit `feat(backend): _PYI 三重注入直跑预解包引擎——零 _MEI 运行`

### Task 3: 重打包 + selftest + 完整验证 + 文档

**Files:**
- 产物：Embedded 重打包；`AGENT.md`/`README.md` 数字与形态同步
- Test: 全量套件 + 单次完整直调

- [ ] Step 1: 重打包（实测 manifest 文件数/原始字节/payload.bin/hash；抽查树全进/onefile 0 命中/旧排除全 0）。
- [ ] Step 2: selftest 绿（树内引擎检查）。
- [ ] Step 3: **单次完整验证**：直跑（7 帧无损，新释放载荷）→ exit 0 + DONE + 契约 DNG + **全程 %TEMP% 零 _MEI 轮询观测**（注入生效的运行时铁证）。
- [ ] Step 4: 全量套件 + 文档（尺寸/形态/注入契约/升级重证注记）→ Step 5: Commit `feat(payload): 预解包直跑端到端验证；文档同步`
