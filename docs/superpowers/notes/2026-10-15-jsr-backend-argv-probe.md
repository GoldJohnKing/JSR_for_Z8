# jsr_backend.exe 运行期 argv 探测与直调可行性初析（2026-10-15）

> **版本标注**：本文捕获基于 **2.3.25** 载荷（`%LOCALAPPDATA%\JSR4Z8\backend\3e1f6aa175be089e\`）。Task 3 已以同一监视器对 **2.3.27** 载荷（backend\fa541adff100e78e\）复抓比对：**argv 契约一致**（见 §5，2026-10-15）。

回答的问题：JSR for Z8 能否绕过 `Jiangtherapee Bridge.exe` 直调 `resources\jsr_backend.exe`？——Bridge 交给引擎的 argv 模板藏在 PyInstaller 字节码里无法静态读出，运行期抓取是最短事实路径。

## 1. 实验方法

- **监视器**：PowerShell `Get-CimInstance Win32_Process` 200ms 轮询、上限 180s，过滤 Name∈{jsr_backend.exe, jsr_v98_k14_backend.exe, jsr_fast_k14_backend.exe, raw_bridge.exe, jxl_worker.exe, Jiangtherapee Bridge.exe}，按 (pid, commandline) 去重追加日志。脚本与日志在任务工作区（未提交，Task 3 直接复用）：`.superpowers/sdd/2026-10-15-argv-probe-dark-regen-233/argv-monitor.ps1` + `argv-capture.log`。
- **载具**：E2E `StopMidRun_CancelsWithin5Seconds_NoBackendLeftover_TempDeleted`（真实后端、真机 GPU；运行 30s 进入对齐阶段后取消并树杀）。命令：`set JSR4Z8_E2E=1&& dotnet test --nologo --filter FullyQualifiedName~E2eTests.StopMidRun` —— 通过（1/1，35s）。**本次即抓全，未动用 FullRun_NoDarkFile 重试。**
- **时间**：2026-10-15 20:30–20:33 本地。监视窗口 20:30:40.242–20:33:40.494，661 次轮询、6 条唯一观测；每次进程创建后 ≤240ms 即被首次观测到（最大 237ms，200ms 轮询无漏抓迹象）。

## 2. 捕获结果

进程树（PPID 链）：`testhost(28040) → Jiangtherapee Bridge.exe(28152) → jsr_backend.exe preview 链(32116→30700) 与主算链(36784→22980)`。

**Bridge.exe（PID 28152，PPID 28040=testhost，创建 20:32:10.000）**——即 BackendRunner 发出的原始命令，逐字一致（协议事实 #1 实测复现）：

```
"C:\Users\GoldJohnKing\AppData\Local\JSR4Z8\backend\3e1f6aa175be089e\Jiangtherapee Bridge.exe" --assistant-jsr-job C:\Users\GoldJohnKing\AppData\Local\Temp\Jsr4Z8\fe104f6667ef4b0cb468652d4537d9fc\request.json C:\Users\GoldJohnKing\AppData\Local\JSR4Z8\backend\3e1f6aa175be089e\resources C:\Users\GoldJohnKing\AppData\Local\Temp\Jsr4Z8\fe104f6667ef4b0cb468652d4537d9fc\cfg
```

**jsr_backend.exe 预览调用（PID 32116，PPID 28152=Bridge，创建 20:32:10.015；2.1s 后出现同 argv 子实例 PID 30700，PPID 32116）**：

```
"C:\Users\GoldJohnKing\AppData\Local\JSR4Z8\backend\3e1f6aa175be089e\resources\jsr_backend.exe" --bridge-preview C:\Users\GoldJohnKing\AppData\Local\Temp\Jsr4Z8\fe104f6667ef4b0cb468652d4537d9fc\cfg\jsr\preview-input.json C:\Users\GoldJohnKing\AppData\Local\Temp\Jsr4Z8\fe104f6667ef4b0cb468652d4537d9fc\cfg\jsr\preview.json
```

**jsr_backend.exe 主算调用（PID 36784，PPID 28152=Bridge，创建 20:32:14.121；2.1s 后出现同 argv 子实例 PID 22980，PPID 36784）**：

```
"C:\Users\GoldJohnKing\AppData\Local\JSR4Z8\backend\3e1f6aa175be089e\resources\jsr_backend.exe" --output-dir C:\Users\GoldJohnKing\AppData\Local\Temp\Jsr4Z8\fe104f6667ef4b0cb468652d4537d9fc --out-prefix fe104f6667ef4b0cb468652d4537d9fc --dng-compression auto --raw-frame D:\GitRepos\JSR_for_Z8\samples\test_frames\FX\Lossless\DSC_7576.NEF --raw-frame D:\GitRepos\JSR_for_Z8\samples\test_frames\FX\Lossless\DSC_7577.NEF --raw-frame D:\GitRepos\JSR_for_Z8\samples\test_frames\FX\Lossless\DSC_7578.NEF --raw-frame D:\GitRepos\JSR_for_Z8\samples\test_frames\FX\Lossless\DSC_7579.NEF --raw-frame D:\GitRepos\JSR_for_Z8\samples\test_frames\FX\Lossless\DSC_7580.NEF --raw-frame D:\GitRepos\JSR_for_Z8\samples\test_frames\FX\Lossless\DSC_7581.NEF --raw-frame D:\GitRepos\JSR_for_Z8\samples\test_frames\FX\Lossless\DSC_7582.NEF --developer-mode --read-noise-dn 14.54 --black-level-dn 1005.89 --full-well-electrons 1102.2 --deghost off --out-dng on --out-png off --out-tiff off --out-format jpeg --jpeg-quality 95 --camera-profile-source libraw
```

**时序**：

| 时刻(本地) | 事件 |
|---|---|
| 20:32:10.000 | Bridge 由 testhost 拉起（argv 如上） |
| 20:32:10.015 | 预览#1 jsr_backend（PID 32116，父=Bridge） |
| 20:32:12.131 | 预览#2 jsr_backend（PID 30700，父=32116，argv 与预览#1 逐字相同） |
| 20:32:14.121 | 主算#1 jsr_backend（PID 36784，父=Bridge） |
| 20:32:16.199 | 主算#2 jsr_backend（PID 22980，父=36784，argv 与主算#1 逐字相同） |
| 20:32:40.539 | 取消树杀时刻：PID 22980 观测到空 CommandLine（进程拆除中；与 StopMidRun 的 30s 延迟取消吻合，测试断言 15s 内无残留通过） |

**未出现的进程**：raw_bridge.exe、jxl_worker.exe、jsr_v98_k14_backend.exe、jsr_fast_k14_backend.exe ——本流程只走 stormer 主后端。

## 3. 分析

### 3.1 argv 中我们可自建的部分（值全部来自 request.json，即 `BackendRequest.BuildJson` 已持有的数据）

| request.json 字段（本次值） | 主算 argv 开关 |
|---|---|
| `paths`（7×NEF） | `--raw-frame` ×7（逐帧展开） |
| `output` / `prefix` | `--output-dir` / `--out-prefix` |
| `manual`+`read_noise`=14.54 | `--read-noise-dn 14.54` |
| `black`=1005.89 | `--black-level-dn 1005.89` |
| `fullwell`=1102.2 | `--full-well-electrons 1102.2` |
| `deghost`=false | `--deghost off` |
| `color`="libraw" | `--camera-profile-source libraw` |
| `dng`=true / `tiff`=false | `--out-dng on` / `--out-tiff off` |

### 3.2 Bridge 填充项与内部产物

- **请求里没有、由 Bridge 填的开关**：`--dng-compression auto`、`--out-png off`、`--out-format jpeg`、`--jpeg-quality 95`、`--developer-mode`。可按观测值硬编码复放，但语义无上游文档（`--developer-mode` 疑似开启裸行进度/日志输出，即 Bridge 转发的那套 SRHQ_* 裸行）。
- **预览阶段是 Bridge 内部产物**：请求 `preview=false`，Bridge 仍先跑 `--bridge-preview <cfg>\jsr\preview-input.json <cfg>\jsr\preview.json`。两个 JSON 由 Bridge 写入 cfg 目录，内容未采样（取消后临时目录即被测试断言删除）；主算 argv **未引用**其输出，预览是否为必需前置未知。
- **目录布局**：cfg 内 `jsr\` 子目录、jobId 命名（`fe104f66…` 同时是 out-prefix 与目录名）由 Bridge 规划；直调时需自拟等价布局（仅当引擎隐式依赖 cfg 布局时才有影响，未见证据）。
- **自重复执行**：每次引擎调用都以 父→子 两个同 argv 实例落地（间隔 ~2.1s；预览与主算皆然），疑似引导/监管层。现有树杀（`taskkill /T /F`）已覆盖；直调时退出码取哪一实例需 spike 确认。
- **stdout 协议**：ASSISTANT_JSR_* 信封是 Bridge 外加的；引擎裸行（SRHQ_V12_PROGRESS / SRHQ_ETA_PROGRESS / 日志）我们已在 ProtocolParser 解析（AGENT.md §4）。工作目录：Win32_Process 无 cwd 字段不可得；观测到的 argv 全为绝对路径，cwd 依赖可能性低但未验证。

### 3.3 直调需重新实现的最小 harness（对照 2.3.21 harness 功能清单：原生工具类型校验、持久操作回执、重复启动保护、中断核查、实际任务跟踪、导出文件检查）

headless 单队列场景的最小集合——多数已有对应物：

1. 进程生成 + stdout/stderr 泵 + 退出码判定（裸行解析已有：ProtocolParser；信封层不需要）；
2. 取消 = 进程树杀（已有：`cmd /c taskkill /T /F /PID`）；
3. pid 跟踪（直调后 pid 自持，无需 START 信封转述）；
4. 产物核验（已有：DNG 存在性 + LibRaw 回读）；
5. 重复启动保护/中断核查 → 已由 config 目录排他锁 + 队列串行化承担。

### 3.4 风险

- **未文档化 CLI**：开关集与语义无上游文档，版本间可变；2.3.27 复验见 Task 3。
- **预览阶段未知**：preview-input.json/preview.json 内容未采样、是否可省未验证（后续 spike 可在取消前快照 `%TEMP%\Jsr4Z8\<jobId>\cfg\jsr\`）。
- `--developer-mode` 等默认开关的副作用未知；退出码归属（双实例）未验证。
- 直调后引擎裸行/退出码语义是否与经 Bridge 转述时一致，需实跑比对。

## 4. 结论

**有条件可行（倾向可行）**：主算入口的 argv 完全由「我们已持有的请求语义 + 可按观测值硬编码的 Bridge 默认开关」构成，不存在不透明句柄/管道等不可自建依赖；harness 功能面在本项目里几乎全部已有对应物（裸行解析、树杀、产物核验、排他串行）。剩余未知集中在两处：① 预览阶段（`--bridge-preview` 两个 JSON 的内容与必要性）；② 未文档化 CLI 的版本稳定性——**2.3.27 复验已通过（§5）**。建议：生产路径维持 Bridge 不动；直调作为独立 spike（复放本文捕获的主算 argv，比对 stdout 与产物）验证后再议替换。

## 5. 2.3.27 复验：argv 契约一致（2026-10-15，Task 3）

载荷升级 2.3.27（裁剪后 manifest hash `fa541adf…`）后，以同一监视器（argv-monitor.ps1，本次日志 `argv-capture-233.log`，工作区）在 E2E 真机运行中复抓：

- **进程树与双实例自重复模式不变**：testhost → Bridge → 预览链（父→子同 argv，间隔 2.1s）与主算链（父→子同 argv，间隔 2.1s）；预览先于主算 ~4s，与 §2 时序一致。
- **Bridge.exe / 预览 argv 逐字同构**：`--assistant-jsr-job <request.json> <resources> <cfg>`、`--bridge-preview <cfg>\jsr\preview-input.json <cfg>\jsr\preview.json`（仅路径/哈希目录不同）。
- **主算 argv 开关序列完全一致**（`--output-dir … --camera-profile-source libraw` 逐一比对）。值差异仅两处，均为预期：`--black-level-dn 1005.89→1008`、`--read-noise-dn 14.54→14.07`（预置黑场表换成后端 2.3.27 z8_presets manifest 导出的 102 行表，(无损, ISO3200) 行值）；`--full-well-electrons 1102.2` 不变（两版 ptc_index.csv 字节一致）。

结论：§3/§4 的直调可行性分析对 2.3.27 继续成立，无需修正。
