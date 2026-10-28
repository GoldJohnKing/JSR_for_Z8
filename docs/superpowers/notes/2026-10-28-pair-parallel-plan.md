# 方案①：对齐 6 对并行（编排层替换）实现方案（2026-10-28，评审后修订版）

> **对抗性评审结论：有条件通过**（独立子代理，只读攻击，报告全文见会话产物；三项放行条件已全部闭合，见 §6）。

目标：把 V18.1 引擎 `v15_2.masked_alignment.align()` 的 6 对循环并发化（对齐段 58.5s → 目标 ≤30s），**DNG 产物逐字节不变**。本方案引入对上游引擎载荷树的构建期补丁（用户已裁定方向待评审确认）。

## 0. 已核验的机制事实（评审请挑战）

| # | 事实 | 证据 |
|---|---|---|
| F1 | 引导器 `backend.py`（exe 内 's' 条目）启动即 `from v15_2.masked_alignment import install; install()`；`install()` = `pipeline.align = align; pipeline.Geometry = MaskedGeometry; return SCHEMA`（返回值被丢弃） | 反汇编（原始字节已复核：尾部 0x53=RETURN_VALUE，早先"第三处赋值"为反汇编表错，已修正） |
| F2 | `align(rt, data, work, progress)`：串行段（全局 H/`registration.register`、percentile 门限、ctx/ref 构建）→ `for` 6 对逐对 `local.align_pair`（每对完成后经 `progress` 回调与 `print` 双路输出进度行，字符串 `'SRHQ_V12_PROGRESS|'`、`'|高光掩膜四次场对齐 '`、`'Masked quartic pair '`、`'/6: '` 为其常量）→ 循环后 `AnalyticFields(fields…)` + report + `atomic_json` 落盘。**跨对无数值归约** | 反汇编（consts/names/varnames 全表） |
| F3 | `v15_2` 包及子模块经 sys.path 的 `<MEIPASS>/wgpu` 目录从**磁盘树**导入（磁盘遮蔽 PYZ；x 条目缺位即 ModuleNotFoundError——V18.1 升级排障实证）；FileFinder 后缀顺序 extension→`.py`→`.pyc`，同名时 **`.py` 优先**；树内 `__pycache__` 写失败被静默忽略（只读树每次内存编译，~130 行开销可忽略） | Python 3.10 导入机制 + 2026-10-27 排障 |
| F4 | `runtime_source_hashes.json`（树根，39 个 v15_2 源文件名→SHA，**不含 masked_\***）只被 `tuning.pyc` 读取作为调参指纹输入（与 adapter/platform/wgpu 版本/assets manifest 哈希一起组成 payload dict），**不是校验器**——不重算树内文件哈希做比对 | tuning.pyc 反汇编 |
| F5 | 引擎产物运行间逐字节确定：同版本两次全幅运行 backend DNG SHA-256 相同（`b9f0ba7c…`）；DNG 无时间戳类字节 | 2026-10-28 双跑实证 |
| F6 | 对齐耗时遥测（times dict/pair_times）只进 work 目录报告 JSON，不进 DNG | F5 双跑哈希相同即佐证 |

## 1. 实现设计

### 1.1 替换模块 `tools/engine-patches/v15_2/masked_alignment.py`（仓库内新文件）

```python
"""6 对并行编排层：重量级语义全部复用上游原件，仅并发化 align() 的对循环。"""
from concurrent.futures import ThreadPoolExecutor
from . import masked_alignment_upstream as _up     # 上游原件（载荷树内改名保留）
# —— 其余 import 与上游 align() 所用一致（registration/local/np/time/Path/atomic_json…）

SCHEMA = _up.SCHEMA
AnalyticFields = _up.AnalyticFields
MaskedGeometry = _up.MaskedGeometry
# 其余上游符号原样 re-export（模块公共面与上游一致，`--masked-align-export` 路径不受影响）

def align(rt, data, work, progress):
    # 阶段A（串行，语义=上游 align 同名段）：全局 H、times、percentile 门限、ctx/ref 构建
    #   —— 从上游 align() 精确移植（约 25 行，两处 if 分支：data.get('H') 缓存复用、percentile 门限）
    # 阶段B（并行）：ThreadPoolExecutor(max_workers=6).submit(local.align_pair, …) × 6
    #   —— 按 futures 列表序逐个 .result()：天然按对序阻塞，第 i 对进度行在拿到第 i 个
    #      result 后再发射（progress 回调与 print 双路，格式串取上游同款）
    # 阶段C（串行）：AnalyticFields、report 组装、atomic_json、return（语义=上游）
    ...

def install():
    from . import pipeline
    pipeline.align = align
    pipeline.Geometry = MaskedGeometry
    return SCHEMA
```

**逐字节等价论证**：阶段A 输出与上游一致（同指令序）→ 6 个 `local.align_pair` 各自输入一致、内部指令序不变 → 每对输出 bitwise 相同 → `fields` 列表同序同值 → `AnalyticFields`/恢复/融合/导出链（引擎其余 1380+1440 条目逐字节相同）输入完全一致 → DNG 相同。线程调度只影响墙钟，不影响任何数值路径。

### 1.2 打包注入（`tools/pack-payload` 新增 EnginePatchStep）

staging 树（EnginePreextractor.Extract 之后、Pack 之前）施加：
1. **哈希守卫（评审 #1 修正：守卫对象=源 exe）**：`<srcDir>/resources/jsr_backend.exe`（Extract 的**输入**，未经薄化）的 SHA-256 必须等于 `398b927d…`（V18.1 上游原件哈希）——上游发版或 reference 树换血即响亮失败；源 exe 哈希同时传递性钉住 Extract 解出的 `masked_alignment.pyc` 内容。**不可**守卫 staged 树内 exe（薄化后哈希必然不同，原稿此处自相矛盾，评审指出）；
2. `wgpu/v15_2/masked_alignment.pyc` → 改名 `masked_alignment_upstream.pyc`（上游原件零字节改动，仅文件名；改名后同名 .py/.pyc 冲突消失，`.py` 优先规则不再被考验——评审复核后比原论证更强）；
3. 写入 `wgpu/v15_2/masked_alignment.py`（来自 `tools/engine-patches/`，模块级 `__getattr__` 委托 `_up` re-export，公共面按构造完备——评审建议 #5）；
4. 守卫附带校验 patch 源文件自身的仓库内 SHA（防手滑改动未过验证）。

注入是 staging 内变换：reference/ 原树零触碰（与薄档案变换同一纪律）。载荷 manifest 自然携带两个新文件的哈希。

### 1.3 测试

- `PayloadTests`：注入步骤单测（伪 exe 哈希守卫触发/通过、改名、.py 落位、上游 pyc 内容不变）；patch 文件 SHA 锁定测试。
- 引擎级等价：见 §2。

## 2. 分层验证（评审重点）

1. **中间件对比**（对齐层内等价，强于末端哈希）：手动直调（三变量注入，不经 app，work 目录留存）分别以上游版/补丁版构建各跑一次同组作业，diff 两版的 work 目录对齐报告 JSON 的全部数值字段（times 类除外）+ 最终 DNG 哈希。
2. **哈希谓词**：补丁版手动直调无损组 → backend DNG SHA-256 == `b9f0ba7cf998eeb040f03c5c8cb3795f3a92ded13e1ffc3202b66ce3dfd95102`。
3. **E2E 基线断言**（本轮已落地的新流程）：四格式 Theory 断言最终 `JSR_*.dng` SHA == `tests/Jsr4Z8.Tests/fixtures/e2e/baselines.json` 基线（**基线在补丁实施前以上游版 regen**——即"原引擎输出"）；补丁版全绿后才算通过。
4. **性能验收（评审 #4 修正：段墙钟）**：对齐段墙钟 = 全局配准阶段末行 → 第 6 对完成行的绝对间隔，目标 ≤30s；<2×（>29s）即回退重估（备选 multiprocessing 变体，同数值等价论证，首版不做）。不采用"首末对行间隔"（并行下该值≈最慢对与最先对的完成时刻差，趋近 0，会被线程竞争退化绕过）。

## 2.5 基线时序锚定（评审 #2）

四格式基线已于 2026-10-28 在**未打补丁的上游版构建**上 regen（本方案实施前）：
- regen 构建的载荷 manifest 哈希 = `1fd93ab0862c2064…`（补丁必然改变此哈希——两个新文件入列 manifest，顺序可审计）；
- baselines.json 四键齐备，随本方案同批提交，并由 FixtureIntegrityTests 的 fixtures 锁哈希二次钉住（基线变更=双处提交）；
- 补丁 PR 若发现基线缺失/重建，必须先验明其构建的 manifest 哈希属上游版——§2.1/§2.2 的直调哈希（`b9f0ba7c…`）为非重言完底。

## 3. 风险与对策

| 风险 | 等级 | 对策 |
|---|---|---|
| 阶段A 移植误差（~25 行两分支） | 高 | 反编译辅助 + 逐常量/逐分支字节码对照；中间件 diff（§2.1）+ 哈希谓词兜底；四格式含不等曝光组覆盖 percentile 分支 |
| 进度行乱序/pct 回退 | 中 | 按序收集后发射（§1.1 阶段B）；E2E 已补进度单调断言（评审 #7 的强选项） |
| 线程异常语义偏差：上游第 i 对抛异常时后续对未跑；并行版其余对可能已完成（仅多写 work 中间件） | 低 | 按序 `.result()` 传播首个异常（时机与上游一致）；等价路径（E2E 四格式）无异常；偏差面=已跑对的中间文件，无全局副作用 |
| cv2 并发线程安全 | 低 | OpenCV 调用级独立；哈希谓词实证兜底 |
| masked_local 隐藏可变全局 | 已闭合 | 评审 #3 定性：`cache` 唯一命中为报错字符串常量 `'Registration reference cache belongs to another burst'`（align_pair 参数校验），模块级名字无 cache，无并发写面 |
| GIL 折损超预期 | 中 | §2.4 性能门；备选 multiprocessing |
| 上游发版 | 中 | 哈希守卫响亮失败 → 重移植编排层（diff 面小）+ 基线 regen（显式环境变量，防无意识漂移） |
| `.py` 引入 `__pycache__`（评审 #6）：释放树可写，首跑会写 `wgpu/v15_2/__pycache__/masked_alignment.cpython-310.pyc`——"引擎对 MEIPASS 零写入"的旧实证不再覆盖补丁树 | 低（已核无护栏被绊） | 记录为已知差异：运行后树=manifest 集合+一个 `__pycache__`；VerifyExisting 只查 manifest 条目不受影响；未来若加树完整性护栏须豁免 `__pycache__` |
| 进度行突发（6 行近乎同时到达） | 低 | UI ProgressThrottle 折叠中间值，纯展示，百分比仍单调（评审 #8 记录） |

## 4. 工期与交付物

实现 0.5–1 天 + 验证 0.5–1 天（含四格式 E2E 两轮）。交付物：patch 模块、pack-payload 注入步骤+单测、E2E 基线断言流程（本轮先行落地）、AGENT.md/文档更新、两次构建的 DNG 哈希等价记录。

## 5. 评审请重点攻击的断言（已评审，结论见 §6）

1. F3 的 `.py` 优先与磁盘遮蔽是否足以保证我们的模块被导入、上游原件被 `masked_alignment_upstream` 名义导入（Python 3.10 / PyInstaller 子模式下的任何反例）；
2. 逐字节等价论证中"线程调度不影响数值路径"是否存在隐藏的全局状态（cv2 全局、numpy/BLAS 线程交互、masked_local 模块级可变量）；
3. 阶段A 移植策略的完备性（反编译 vs 手写，如何证明分支语义零偏差）；
4. 哈希守卫+基线 regen 流程是否存在让"无意识漂移通过"的缝隙；
5. 任何更简单的等效方案（不 fork 达到同样并行收益的路径）。

## 6. 对抗性评审结果与处置（2026-10-28，独立子代理）

**判定：有条件通过 → 三项放行条件已全部闭合。**

| 级别 | 发现 | 处置 |
|---|---|---|
| 致命 #1 | 守卫对象（薄化后 staged exe）与守卫值（上游原件哈希 398b927d…）错配，按原稿实施必然全线失败 | §1.2 已改守卫源 exe（Extract 输入） |
| 重大 #2 | 四格式基线时序是承载性条件；评审时仅 1/4 键在（regen 进行中），且 regen 无构建来源校验 | §2.5 已锚定：四键在上游版（manifest `1fd93ab0…`）regen 齐 + fixtures 锁二次钉 + 直调哈希非重言完底 |
| 重大 #3 | `masked_local.pyc` 含 `cache` 字符串，静态面未闭合 | 已定性：唯一命中=报错字符串常量（参考缓存归属校验），无模块级可变名字 |
| 建议 #4–#9 | 性能门度量/`__getattr__` re-export/`__pycache__` 已知差异/单调断言/突发节流/替代方案引证 | 已全部纳入 §1.2、§2.4、§3 风险表与 E2E 单调断言 |

评审另独立强化了两点：改名后同名 .py/.pyc 冲突消失（导入链比原论证更强）；子模式不会重解 x 条目覆盖磁盘树（由 2026-10-27 缺位报错实证反证）。「未发现」清单：导入机制链反例、协议测试被时序绊倒、载荷自愈冲突、更简等效方案。
