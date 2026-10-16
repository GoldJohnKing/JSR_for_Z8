# 薄档案实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** pack 工具在预解包后对树内引擎 exe 施加 b-体零填充变换（同字节长度；z/m/s/stub/TOC/cookie 原样）——payload.bin/exe 回落至 ~202/~249MB，保留零解压/零 `_MEI` 全部收益。

**Architecture:** Task1=EnginePreextractor 管线增薄变换步骤（合成夹具测）；Task2=失败确认+文档披露面（部署形态已天然对齐：BackendRunner 三变量本就自指树内 exe——评估实证形态）；Task3=重打包+selftest+一次完整验证（含无三变量失败冒烟）+文档数字。

**Tech Stack:** 既有；评估配方=docs/superpowers/notes/2026-10-16-thin-archive-probe.md（F1 形态+两机制事实）。

**Spec:** 用户 2026-10-16 实施令（基于评估结论）。

## Global Constraints

- 忠实评估 F1 形态：仅 `b` 类型条目体区间清零；z/m/s 体、stub 头、TOC、cookie 逐字节原样；**文件总长度不变**（偏移全部保持有效）。
- 机制事实 A 遵守：部署=薄 exe 在树根、三变量自指——现有 BackendRunner 实现已对齐（`_PYI_ARCHIVE_FILE=spec.EngineExe` 全路径），Task2 仅锁定不回归。
- 变换确定性（同输入同输出字节）；参考树 onefile exe 不动（变换只作用于打包 staging 内拷贝）；AGENT.md 披露"随包引擎 exe 为薄化改造副本（b 体清零，打包期变换，原件在 reference/）"。
- Windows Host；基线 282/282（feat/thin-archive @b17fbae）；完整 GPU 验证一次+无三变量失败冒烟一次（Task3）；E2E 套件禁跑；隔离规则延续；每任务一 commit；TDD。

## Review Focus

1. 零填充区间精确性（仅 b 体；越界一格=坏 TOC/z 体→启动崩）→ Task1 合成夹具逐区间断言。
2. 变换后 exe 的 manifest 哈希链（新 hash→新释放目录→旧目录清剪）→ Task3 实况+selftest。
3. 无三变量 spawn 失败确认（评估审查注记的未测推断——Task3 一次廉价冒烟定性）。
4. 文档披露完备（改造副本事实+新数字）→ Task3。
5. 合成夹具真实性（复用第六轮 BuildMiniOnefile，勿自造格式）。

---

### Task 1: EnginePreextractor 薄变换

**Files:**
- Modify: `tools/pack-payload/EnginePreextractor.cs`（`ThinTransform(string engineExe)` 或并入 Extract 管线：树内 exe 拷贝后即变换；internal seam 可测）
- Modify: `tests/Jsr4Z8.Tests/PayloadTests.cs`（合成夹具断言：b 体区间全零/其余逐字节不变/长度同/TOC 偏移有效）
- Test: 同上

- [ ] Step 1: 失败测试（复用 BuildMiniOnefile 构造含 b/z/m 条目归档→Extract+Thin→断言）→ Step 2: 红 → Step 3: 实现（流式：拷贝后按 TOC 区间覆写零）→ Step 4: 聚焦+全量 282+新增 绿。
- [ ] Step 5: Commit `feat(payload): 打包期薄档案变换——引擎 exe b 体清零（评估 F1 形态）`

### Task 2: 部署形态锁定 + 披露

**Files:**
- Modify: `tests/Jsr4Z8.Tests/BackendRunnerTests.cs`（若需：断言 _PYI_ARCHIVE_FILE==被 spawn exe 自身全路径——评估事实 A 的防回归锁；已有断言核对后补强）
- Modify: `src/Jsr4Z8.Core/Payload/PayloadPacker.cs`（披露注释：随包 exe 为薄化副本）

- [ ] Step 1: 测试补强（若现有已锁则记录免改）→ Step 2: 绿 → Step 3: Commit `test(backend): 事实 A 防回归锁；载荷薄化披露注`

### Task 3: 重打包 + 完整验证 + 文档

- [ ] Step 1: 重打包（实测：预计 bin ~202MB/exe ~249MB——只录入实测）+ selftest 绿 + 旧释放目录清剪实况。
- [ ] Step 2: **一次完整验证**（薄 exe 形态直跑：契约三证+退出码正确捕获+零 _MEI 轮询）+ **无三变量失败冒烟**（对薄 exe 裸 spawn→定性失败形态，补评估未测推断）。
- [ ] Step 3: 全量套件+文档（AGENT.md/README 新数字+改造披露；评估文档 §尾追加"已实施"一句）→ Step 4: Commit `feat(payload): 薄档案端到端验证；文档同步`
