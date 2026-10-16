# 崩溃孤儿清理 + 载荷裁剪探寻 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ① 主进程异常退出时自动击杀全部 Python 子进程（Job Object kill-on-close 兜底）② 分阶段探寻并实施载荷进一步裁剪（_internal 依赖矩阵：静态分析 + 拷贝目录短冒烟 + 一次完整验证）。

**Architecture:** Task1=P/Invoke Job Object 包装（main 启动即自assign）+ 可测性（子进程 assign→关句柄→断言死亡）；Task2=纯实验（PyInstaller TOC/strings 静态依赖分析 + 提取目录副本的改名-冒烟矩阵，40s 级，不跑完整 E2E）；Task3=按结论实施 packer 排除+重打包+selftest+一次完整直调验证（用户"继续探寻"指令的必要组成）+文档。

**Tech Stack:** 既有栈；Job Object 用 kernel32 P/Invoke（Win10+ 嵌套作业）。

**Spec:** 用户 2026-10-16 两点指令。

## Global Constraints

- Windows Host 构建/测试；基线 **272/272（main @6b5523a）**；禁止递归搜索 reference/、external/、~/.nuget；samples/ 只读目标子目录。
- 完整 GPU 直调运行（~6min）仅两处：Task3 最终验证一次（用户"继续探寻"指令授权范围内，报告显著标注）；其余一切冒烟 ≤60s。
- 载荷裁剪**只动 packer 排除清单**，不改 jsr_backend.exe 及其任何文件内容（字节纪律）；实验在 %LOCALAPPDATA% 提取目录的**副本**上进行。
- Job Object：`Assign 当前进程` 于 Program 启动最早处（含 --selftest 路径）；创建失败静默继续（绝不阻断启动）；正常取消仍走 taskkill /T（Job 为崩溃兜底而非替代）；Win8+ 嵌套作业前提注明。
- 每任务一 commit；TDD（Task1 的作业包装/Task3 的排除清单）。

## Review Focus

1. Job Object 句柄生命周期（静态引用防 GC 回收 SafeHandle→提前全灭！）→ Task1 测试+代码审查重点。
2. 测试进程污染：作业测试杀的是自 spawn 的 sleeper，不波及 testhost → Task1 测试设计约束。
3. 冒烟矩阵假阴性（晚期依赖在 40s 内不触发——weights/对齐期）→ Task2 结论分级（import 级/晚期级），晚期级必须进 Task3 完整验证。
4. 裁剪后 selftest/包管理器排除/版本清剪三链不破 → Task3 PayloadTests+盘上抽查。
5. 嵌套作业环境（用户从带作业的启动器运行）→ Task1 静默失败路径。

---

### Task 1: Job Object 崩溃兜底

**Files:**
- Create: `src/Jsr4Z8.Core/Native/JobObject.cs`（CreateJobObjectW/SetInformationJobObject/AssignProcessToJobObject P/Invoke；`static bool TryEnableKillOnCloseForCurrentProcess()`——创建 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 作业、assign 自身、静态字段持句柄；`internal static bool TryAssign(Process)` 测试缝）
- Modify: `src/Jsr4Z8.UI/Program.cs`（Main 最早处调用 TryEnableKillOnCloseForCurrentProcess()，含 --selftest 路径；失败静默）
- Modify: `AGENT.md`（§4 取消节补一句：崩溃兜底=Job Object kill-on-close；正常取消=taskkill /T）
- Test: `tests/Jsr4Z8.Tests/JobObjectTests.cs`

**Interfaces:**
- Produces: 上述两个静态方法；测试：spawn `cmd /c timeout /t 60 >nul` → TryAssign(子) → 释放作业句柄 → 断言子进程 ≤5s 内退出（WaitForExit）；TryEnable 对 testhost 自身的调用**不做**（防误杀测试宿主——只测 TryAssign 路径+作业创建返回非空）。

- [ ] **Step 1: 失败测试**（JobObjectTests：作业创建非空+子进程 assign→dispose 句柄→5s 内死亡）
- [ ] **Step 2: 红** → **Step 3: 实现**（SafeHandle 静态持有；SetInformationJobObject JOBOBJECT_EXTENDED_LIMIT_INFORMATION）
- [ ] **Step 4: 全量 272+新增 绿；UI 构建 0 警告** → **Step 5: Commit** `feat(core): Job Object kill-on-close——主进程崩溃自动清理全部子进程`

### Task 2: 载荷裁剪探寻（实验）

**Files:**
- Create: `docs/superpowers/notes/2026-10-16-payload-trim-probe.md`（唯一提交物）
- 脚本/日志：SDD 工作区

**Interfaces:**
- Produces: ①静态依赖分析（jsr_backend.exe 的 PyInstaller TOC/strings：引用哪些 _internal 模块/DLL/权重路径）②拷贝目录冒烟矩阵结论表（候选目录×40s 冒烟结果+分级：import 级可剥/晚期依赖/必需）③推荐裁剪清单+预估节省 ④完整验证需求清单（晚期级候选）。

- [ ] **Step 1:** 静态分析：对提取目录 `resources\jsr_backend.exe` 做 strings/TOC 提取（python 脚本读 PyInstaller CArchive 目录或 strings-grep 模块名 cv2/numpy/PIL/lxml/cryptography/wgpu/权重文件名/spynet/weights.npz 等）；对照 _internal 实际清单归类"引擎引用/无引用"。
- [ ] **Step 2:** 冒烟矩阵：复制提取目录→逐候选改名（`_internal\cv2`、`_internal\cryptography`、`_internal\lxml`、`_internal\PIL`、`_internal\numpy*`、`raw_bridge.exe`、`jxl_worker.exe`、`libjxl.dll` 等按 Step1 结论定）→ 每项 spawn 直调（无损 7 帧，独立输出目录）跑 ~40s（过 import+解码进入对齐）→ 记录：启动即崩=import 级可剥；40s 存活=晚期/必需分级；`_internal\wgpu` 权重类**不进冒烟**（晚期必验清单）。
- [ ] **Step 3:** 证据文档：矩阵表+分级+推荐清单（预期候选：cv2 117MB/cryptography 10MB/lxml 7MB/PIL 11MB/raw_bridge 5MB/jxl_worker 3MB/libjxl 6MB——以实测为准）+预估 payload/exe 缩减。
- [ ] **Step 4: Commit**（仅证据 md）`docs: _internal 依赖矩阵与载荷裁剪探寻结论`

### Task 3: 实施裁剪 + 一次完整验证

**Files:**
- Modify: `src/Jsr4Z8.Core/Payload/PayloadPacker.cs`（按 Task2 清单扩排除——每项带依据注释）
- Modify: `tests/Jsr4Z8.Tests/PayloadTests.cs`（新排除项断言）
- Modify: `AGENT.md`/`README.md`（数字同步）
- 产物：Embedded 重打包（哈希比对自动换新）

- [ ] **Step 1: 失败测试** → **Step 2: 红** → **Step 3: 实现**。
- [ ] **Step 4:** 重打包（记录统计）+ selftest。
- [ ] **Step 5:** **一次完整直调验证**（无损 7 帧全流程 ~6min；用户探寻指令授权；输出 DNG+退出码 0；含晚期依赖复核——若冒烟矩阵有晚期级候选被裁，此跑即终审）。失败→回退该排除重打包重验。
- [ ] **Step 6:** 全量套件+文档 → **Step 7: Commit** `feat(payload): _internal 依赖矩阵裁剪——cv2/… 移除，完整直调验证`
