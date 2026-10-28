# CPU 对齐（V18.1 masked 四次项场）GPU 移植可行性评估（2026-10-28）

方法：① 静态解剖载荷树 `wgpu/v15_2/masked_{local,fit,alignment}.pyc`（3.10 marshal 反汇编，内核/数据形态图谱）；② 全幅七帧真机计时（无损组 DSC_7576-7582，三变量注入直调，逐行时间戳捕获 stdout 协议，`tmp_e2e/timing-run.ps1`，exit 0 / DONE total 394.4s）。

## 实测分阶段（XTX，全幅无损组，总 394.4s）

| 阶段 | 时长 | 占比 | 执行域 |
|---|---|---|---|
| 解码七张 RAW | 7.7s | 2.0% | CPU |
| 全局配准+guide（"WGPU 对齐…"前段） | 18.2s | 4.6% | CPU 特征匹配 |
| **高光掩膜四次场对齐（6 对 ×10.7s）** | **58.5s** | **14.8%** | **CPU** |
| **Transformer 恢复** | **297.1s** | **75.3%** | **GPU (WGPU)** |
| DNG 写出+无损压缩+校验 | 12.7s | 3.2% | CPU |

**Amdahl 上限：对齐全部归零也只省 58.5s（394→336s，−15%）；连全局段一起归零 −19%。**
（修正：上一轮"~9min/组"为口误——E2E 36m37s÷6 用例≈6.1min/组；本计时 6.6min，与 2.3.27 GPU 时代"约 6 分钟"基本持平，CPU 对齐净代价仅 ~+0.6min。）

## 内核级可移植性（masked_local 图谱）

| 内核 | 形态 | GPU 适配 | 备注 |
|---|---|---|---|
| `local_measurements` 瓦片循环 | BS192/STRIDE128 网格（全幅 ~2.7k 瓦片/对），**纯串行 Python 循环**（ThreadPoolExecutor 导入未用） | ★★★ 瓦片间 embarrassingly parallel | 每瓦片：`matchTemplate`(NCC±6)→`minMaxLoc`→逐瓦片 `findTransformECC`(平移)→Sobel 相干性 |
| `masked_ncc` 种子搜索 | cv2.matchTemplate TM_CCORR + 掩膜归一 | ★★★ 批量相关 | 最规整的 GPU 形态 |
| 逐瓦片 ECC 精化 | cv2.findTransformECC MOTION_TRANSLATION（迭代） | ★★ 需复现 cv2 数值语义 | 移植即数值偏差来源 |
| `masked_fit` 四次项场 | 每瓦片 WLS(lstsq)+SVD 模型选择（15 项基） | ★★★ 批量小线性系统 | 但 CPU 上本就轻量 |
| `global_homography` 金字塔 ECC | 每对 1 次、小矩阵迭代 | ★ 低强度 | 留 CPU 合理 |
| `AnalyticFields` 场合成 | numpy 逐查询 | ★★ | 与融合阶段衔接，动了影响面大 |

## 结论

1. **技术上可行，但不建议 GPU 移植**：收益上限 15%，而代价是——数值偏离上游"sealed V9.8 fitter"基线（cv2 ECC/NCC 语义在 WGSL 里复现必然有浮点差异，七帧输出与上游 V18.1 不再可比）、每次上游发版需重移植重验证、E2E 基线重锚。
2. **若确要压对齐时间，性价比排序**：
   - a. **6 对并行**（进程/线程级）：数值逐对不变（每对内部计算顺序不动）→ 输出应逐字节等价，工程量小，58.5s→~15-20s；但需以载荷树替换 'x' 条目方式维护 fork（磁盘树遮蔽 PYZ 的机制使替换生效），**违反"上游原样"现行约定，需用户裁定**；
   - b. 瓦片级并行/向量化（numpy 批量 matchTemplate）：同 fork 代价，数值可能有规约顺序差异；
   - c. GPU 移植：见上，不建议。
3. **真正的时间大头（75.3%）是 Transformer 恢复**——上游封印的 WGSL 网络，属上游优化空间，本项目不可动。

计时日志：`tmp_e2e/timing-out/timed.log`（未入库）；对齐器内核反汇编产物：会话 `/tmp/gpu-eval/`（临时）。
