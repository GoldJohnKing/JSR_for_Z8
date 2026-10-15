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

**有条件可行（倾向可行）**：主算入口的 argv 完全由「我们已持有的请求语义 + 可按观测值硬编码的 Bridge 默认开关」构成，不存在不透明句柄/管道等不可自建依赖；harness 功能面在本项目里几乎全部已有对应物（裸行解析、树杀、产物核验、排他串行）。剩余未知集中在两处：① 预览阶段（`--bridge-preview` 两个 JSON 的内容与必要性）；② 未文档化 CLI 的版本稳定性——**2.3.27 复验已通过（§5）**。

**更新（2026-10-15 晚，§6/§7 实证后）**：两个剩余未知均已实跑关闭——预览产物仅为 Bridge 自身 UI 生成缩略图/元数据（§6 采样），主算链对 cfg/预览零依赖（§7 无 cfg 无预览真机直调全绿）。**用户已裁定：绕过 Bridge、直调立项实施；预览链按用户裁定永久跳过（本 GUI 无预览面）**——即本文 §4 的“spike 后再议”已升级为“已立项实施”，预览非必需由 §6/§7 实证背书。**已实施（Task 3）：载荷不再携带 Bridge.exe（排除清单防回潮；直调端到端 E2E 已全量重验，见 task-3-report）**。

## 5. 2.3.27 复验：argv 契约一致（2026-10-15，Task 3）

载荷升级 2.3.27（裁剪后 manifest hash `fa541adf…`）后，以同一监视器（argv-monitor.ps1，本次日志 `argv-capture-233.log`，工作区）在 E2E 真机运行中复抓：

- **进程树与双实例自重复模式不变**：testhost → Bridge → 预览链（父→子同 argv，间隔 2.1s）与主算链（父→子同 argv，间隔 2.1s）；预览先于主算 ~4s，与 §2 时序一致。
- **Bridge.exe / 预览 argv 逐字同构**：`--assistant-jsr-job <request.json> <resources> <cfg>`、`--bridge-preview <cfg>\jsr\preview-input.json <cfg>\jsr\preview.json`（仅路径/哈希目录不同）。
- **主算 argv 开关序列完全一致**（`--output-dir … --camera-profile-source libraw` 逐一比对）。值差异仅两处，均为预期：`--black-level-dn 1005.89→1008`、`--read-noise-dn 14.54→14.07`（预置黑场表换成后端 2.3.27 z8_presets manifest 导出的 102 行表，(无损, ISO3200) 行值）；`--full-well-electrons 1102.2` 不变（两版 ptc_index.csv 字节一致）。

结论：§3/§4 的直调可行性分析对 2.3.27 继续成立，无需修正。

## 6. 预览链采样：两个 JSON 的实际内容（2026-10-15 23:08–23:14，Task 1）

**方法**：E2E `FullRun_NoDarkFile_PresetAutoMatch_ProducesValidJsrDng`（通过，1/1，6m02s）真机运行期间，PowerShell 监视器以 200ms 轮询 `%TEMP%\Jsr4Z8\*\cfg\jsr\*`，文件集一变即整目录快照到工作区——`preview-input.json` 落盘后 215ms（23:08:33.375 写入 → 23:08:33.590 快照）即被捕获，早于预览链进程创建（23:08:33.376）之后的主算阶段；`preview.json` 于预览完成时（23:08:36.947）由第二次快照补齐。共捕获 5 文件：`preview-input.json`(570B)、`preview.json`(426,215B)、`preview.log`(0B)、`EXECUTION.lock`(0B)、`job-1792076917463.log`(15,891B，引擎自写的主算 stdout 副本，尾行 `DONE total 335.9s`)。全文存工作区 `preview-sample/cefbbf9d27934527be62908fa9055254/`（不提交）。

**preview-input.json（570B，逐字）**：

```json
{"engine":"stormer7","paths":["D:\\GitRepos\\JSR_for_Z8\\samples\\test_frames\\FX\\Lossless\\DSC_7576.NEF","D:\\GitRepos\\JSR_for_Z8\\samples\\test_frames\\FX\\Lossless\\DSC_7577.NEF","D:\\GitRepos\\JSR_for_Z8\\samples\\test_frames\\FX\\Lossless\\DSC_7578.NEF","D:\\GitRepos\\JSR_for_Z8\\samples\\test_frames\\FX\\Lossless\\DSC_7579.NEF","D:\\GitRepos\\JSR_for_Z8\\samples\\test_frames\\FX\\Lossless\\DSC_7580.NEF","D:\\GitRepos\\JSR_for_Z8\\samples\\test_frames\\FX\\Lossless\\DSC_7581.NEF","D:\\GitRepos\\JSR_for_Z8\\samples\\test_frames\\FX\\Lossless\\DSC_7582.NEF"]}
```

**preview.json（426,215B；除 `preview` 缩略图字段外逐字，缩略图为 data-URI，全文见工作区）**：

```json
{
  "paths": ["D:\\…\\DSC_7576.NEF", "…", "DSC_7582.NEF"],   /* 7×与 preview-input 相同的绝对路径 */
  "frames": [                                            /* 7 帧，每帧结构同下（此处展开第 0/3 帧） */
    {"path": "D:\\…\\DSC_7576.NEF", "name": "DSC_7576.NEF", "iso": "3200", "shutter": 0.01,
     "camera": "NIKON CORPORATION NIKON Z 8", "ev": 0.0, "reference": false},
    …
    {"path": "D:\\…\\DSC_7579.NEF", "name": "DSC_7579.NEF", "iso": "3200", "shutter": 0.01,
     "camera": "NIKON CORPORATION NIKON Z 8", "ev": 0.0, "reference": true},
    …
  ],
  "reference": 3,
  "width": 8280,
  "height": 5520,
  "reference_name": "DSC_7579.NEF",
  "exposure_confirmation_required": false,
  "ptc_auto_available": false,
  "ptc_issue": "没有匹配的 PTC：NIKON CORPORATION NIKON Z 8 ISO 3200.0。请在 PTC 下拉框选择单像素满阱容量相近的机型/ISO作为替代；不要只按画幅或总像素数选。读出噪声仍来自你的黑场。",
  "warnings": [],
  "preview": "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCA…<423,659 字符，全文见工作区>",
  "preview_error": null,
  "orientation": "sensor-unrotated"
}
```

**一段话分析**：预览链是 Bridge 自身 UI 的“取景/确认”服务——`preview-input.json` 只有引擎名（`stormer7`）与 7 帧路径（请求方早已持有的信息原样回显）；`preview.json` 则是 Bridge 面向用户的帧清单（逐帧 EXIF：ISO/快门/机身/EV）、参考帧选定（`reference`=3 → DSC_7579）、8280×5520 传感器尺寸、曝光确认/PTC 自动匹配提示文案，外加一张约 415KB 的 base64 JPEG 缩略图（占该文件 99% 体积）。主算 argv 从不引用这两个文件（§2/§5 契约），其“消费方”只可能是 Bridge 的图形界面（曝光确认对话框、预览图）；本 GUI 无预览面，故按用户裁定永久跳过合理且无损——§7 的无 cfg、无预览直调双组全绿进一步实证主算链对预览零依赖。附带收益：跳过预览每任务省 ~4.2s（23:08:33.376→23:08:37.551）与一整对引擎双实例（省 2 进程）。

## 7. 无预览直调实证：裸行协议、退出码归属与杀树完整性（2026-10-15 23:15–23:30，Task 1）

**方法**：以 §2/§5 捕获的 argv 契约逐字手工拼装（cmd/ps1 脚本，工作区 `scripts/`），直接 spawn `…backend\\fa541adff100e78e\\resources\\jsr_backend.exe`——**不建 cfg、不跑预览、不经 Bridge**；job 目录在独立工作区临时目录，stdout/stderr 重定向到文件，argv-monitor（§1 同款）全程伴跑；RN=14.07 / BL=1008 / FW=1102.2（ISO3200 无损行，z8_presets 102 行表）。

### 7.1 运行结果总表

| 运行 | 帧组（samples/test_frames/FX/） | 退出码 | 墙钟（实测） | 引擎自报 | DNG 产物 | 字节数 |
|---|---|---|---|---|---|---|
| run1 无损 | `Lossless\DSC_7576..7582.NEF` | **0** | 342.3s（23:17:33.85→23:23:16.13） | `DONE total 339.2s` | `jobs/run1-lossless/run1lossless_V15_3_5_K7_20261015_231736_534682/run1lossless_V15_3_5_K7_20261015_231736_534682.dng` | 358,130,979 |
| run2 HE | `he_frames\DSC_7652..7658.NEF` | **0** | 339.9s（23:23:44.88→23:29:24.80） | `DONE total 336.8s` | `jobs/run2-he/run2he_V15_3_5_K7_20261015_232347_569923/run2he_V15_3_5_K7_20261015_232347_569923.dng` | 360,236,480 |
| run3 杀树 | 无损 argv，+30.2s 处 `taskkill /T /F` | 1（强杀） | 杀于 23:30:12.022 | 杀时 `SRHQ_V12_PROGRESS\|32\|估计跨帧运动` | （预期无产物，实测无） | — |

注：run2 沿用无损 ISO3200 行的 RN/BL——该组仅验证 HE 解码路径与管线形状，不标定精度。run0（未入表）：首版 harness 把 7 帧误拼成单个逗号串参数，引擎预检即拒（运行时观测，该次日志未留存）：stdout 裸行 `SRHQ_NET_ABORT:preflight|此 Transformer 权重需要七张互不重复的 RAW` + stderr Python `ValueError` 回溯，无 DNG——同时给出失败形态证据：**裸 abort 行走 stdout、诊断回溯走 stderr**，主算对 cfg/预览仍未索取任何前置。

### 7.2 裸 stdout 协议（run1 全量 324 行，run2 同构；存档 `direct_success.log` 供 Task 2 夹具）

- **零信封**：两跑 `ASSISTANT_JSR` 计数均为 0——裸行即引擎本体输出，与 §3.2 判断一致。
- 行型清单（run1 计数）：`PTC {…}` 1、`SRHQ_V12_FRAME\|i\|ev\|shutter\|path` 7、ColorMatrix/camera profile 各 1、`SRHQ_EFFECTIVE_ENGINE\|v12\|v12-wgpu\|…` 1、`SENSOR_LEVELS [{…}]` 1、Proxy n/7 若干、`SRHQ_V12_PROGRESS\|pct\|desc` 110、`SRHQ_ETA_PROGRESS\|stage\|done\|total` 87、`V12 output: <dir>` 1、`DONE total <sec>s` 1。
- 首行为 `PTC {…}`（手动 RN/满阱回显），尾三行 `SRHQ_V12_PROGRESS\|100\|导出完成` → `V12 output: <绝对路径>` → `DONE total 339.2s`；`SRHQ_V12_PROGRESS\|100\|` 恰一次。前 30 行+尾 10 行全文见工作区 `direct_success.log`（15,882B，Task 2 将提交为 `tests/fixtures/protocol/direct_success.log`）。
- stderr：run1/run2 均为 0 字节（诊断全部走 stdout；stderr 不参与协议）。

### 7.3 双实例自重复：时序与退出码归属（Phase A + run0–3 共 6 对观测）

| 观测 | 父实例（pid@创建） | 子实例（pid@创建） | 间隔 | 退出顺序 |
|---|---|---|---|---|
| PhaseA 预览对 | 34516@23:08:33.376 | 35956@23:08:35.419 | 2.043s | 子 37.268 → 父 37.551（Δ0.28s） |
| PhaseA 主算对 | 29200@23:08:37.464 | 12452@23:08:39.501 | 2.037s | 子 23:14:16.206 → 父 ≤16.724 |
| run0 失败对 | 19916@23:15:45.256 | 29732@23:15:47.540 | 2.284s | 子 48.252 → 父 48.529（Δ0.28s） |
| run1 | 8516@23:17:33.852 | 13088@23:17:35.866 | 2.014s | 子 23:23:15.802 → 父 ≤16.13（转述 0） |
| run2 | 26284@23:23:45.229 | 11092@23:23:47.075 | 1.846s | 子 23:29:24.502 → 父 ≤24.80（转述 0） |
| run3 | 24736@23:29:41.849 | 35008@23:29:43.950 | 2.101s | 同被 taskkill 终结 |

结论：每次引擎调用恒为「父 spawn → ~1.8–2.3s 后同 argv 子（run3 父/子 argv md5 一致）」，实际工作在**子**进程；正常完成时**子先退、父随后 0.3–0.5s 内退出并转述退出码**（run1/run2 cmd 侧 `%ERRORLEVEL%`=0，即等父进程即得任务结果）——Bridge 当年等的就是父实例；直调 harness 同样只需持有父 pid。argv 中不含任何 cfg/预览路径引用，主算对 cfg 目录零依赖实证成立。

### 7.4 杀树完整性（run3）

父 pid 24736 于 +30.2s（stdout 已至 `SRHQ_V12_PROGRESS\|32\|估计跨帧运动`，解码/运动估计进行中）被 `taskkill /PID 24736 /T /F`：返回码 0；**终止清单 3 个进程**——24736（父）、35008（同 argv 子）、25896（**另一未名后代**：按名过滤的监视器从未观测到它，或为临终前 <200ms 内新起、或为非监视名进程；`/T` 全树枚举照样覆盖——直调取消必须保持 `/T`，不可按名杀）。杀后 631ms 内 `jsr_backend.exe` 全清（≤5s 要求达标）；被强杀父进程退出码 1（TerminateProcess 语义）。无孤儿。

### 7.5 小结

argv 契约（§2/§5）在真机直调下成立：无损+HE 双组 exit 0、`<prefix>_V15_3_5_K7_*` 目录与 DNG 如期产出、裸行协议完整（P100+DONE、零信封）、退出码取父实例、杀树靠 `/T` 全清。§3.4 风险清单中“预览必要性/退出码归属/裸行与经 Bridge 转述一致性”三项全部实测关闭；未文档化 CLI 的版本间稳定性仍属上游固有风险，以载荷哈希锁定 + 契约夹具（direct_success.log）监控。
