# 载荷裁剪探寻：静态依赖矩阵 + 冒烟实验（2026-10-16）

> 探针对象：现行嵌入载荷 Bridge-free 2.3.27（257 文件 / 406,018,492 B raw / manifest hash `063a6873…`，
> 提取于 `%LOCALAPPDATA%\JSR4Z8\backend\063a6873028edc42\`）。
> 工作区（脚本+日志，未提交）：`.superpowers/sdd/2026-10-16-jobobject-payload-trim/` 下静态/冒烟子目录。
> 结论先行：**jsr_backend.exe 是 PyInstaller onefile 自解压构建——每次启动把完整 1,416 文件档案（386,096,731 B）解压到 `%TEMP%\_MEI*` 并从那里运行，盘上 `_internal` 目录从不被读取。整个 `_internal`（185.1 MB）+ 从未 spawn 的工具/其 DLL 家族（19.1 MB）合计 ~204.1 MB（50.3%）可从载荷剥离（以单次完整验证兜底）。**

## 1. 方法

### 1.1 静态分析（Phase A）

对提取目录 `resources\jsr_backend.exe`（201,243,142 B）做四层交叉，静态探针脚本（工作区 static_probe{,2,3}.py）：

1. **CArchive TOC 解析**：读 exe 尾部 MEI cookie（`MEI\014\013\012\013\016`，pkg_len=200,895,494，toc@200,813,150+82,256，pyver=310）→ 1,434 条目：5×`m`（引导模块）、11×`s`（入口 `backend` + 10 个 pyi_rth 运行时钩子）、1,416×`b`（二进制，压缩后合计 192.3 MB）、1×`z`（PYZ.pyz 8.46 MB）、1×`o`（`pyi-contents-directory _internal`）。
2. **PYZ TOC**：1,448 个纯 Python 模块名清单。
3. **PYZ 逐模块解压扫描**：zlib 解压 1,446 个条目，按 marshal 短 ASCII 名编码（tag 0x7A/0xFA/0x5A/0xDA + 长度前缀）做**精确名匹配**（朴素子串会同时漏报/误报——首次实现以 `(?![A-Za-z0-9_])` 断言被 marshal 相邻类型字节破坏，`dng_repack` 类引用漏检，已修正并复扫），得到"引擎自有模块（backend/v15_1/v15_2/sensor/color_profile/output_chain/…）引用了谁"。
4. **字面量/资产名与 DLL 导入表交叉**：全 PYZ 扫 `raw_bridge`/`jxl_worker`/`libjxl`/权重路径等字面量；`objdump -p` 读 `_internal` 内全部 pyd/dll/exe 的 PE 导入表，找 libjxl 家族的实际消费方；对照源分发目录 `reference\Jiangtherapee-Bridge-2.3.27-Windows-x64\`（_internal 含 1,140 文件）与提取目录实盘清单（216 文件）。

### 1.2 冒烟矩阵（Phase B）

- **副本**：`trim-probe-backend\`（robocopy 自提取目录，257 文件 / 406,018,492 B 与 manifest 逐字节一致；实验只动副本，提取目录零触碰——事后复核仍 257 文件）。
- **协议**（`task2-smoke/smoke-run.ps1`）：候选改名（`X`→`X._REMOVED`）→ 以直调 argv（argv-probe §2/§5/§7 契约：7×`--raw-frame` 无损帧 `samples\test_frames\lossless\DSC_7576-7582.NEF`，`--read-noise-dn 14.07 --black-level-dn 1008 --full-well-electrons 1102.2`，其余开关按观测值硬编码）spawn 副本引擎 → 每秒轮询至多 **60 s**（历次直调显示 ~30 s 已进入对齐/恢复阶段）→ `taskkill /T /F` 树杀父 pid → `tasklist` 复核 `jsr_backend.exe` 归零才放行下一轮 → 还原改名 → 记录 stdout/stderr 尾部、退出码、存活时长、隐藏项是否被重建。
- **控制组**：r00 空隐藏（必须存活）；**阴性对照**：r03 隐藏 numpy（必须死——用于证明改名确实生效、引擎确实读副本）。GPU 合计 6 跑 × ≤60 s ≈ 6.5 min，全部树杀验证无残留。

## 2. 静态分析表（引擎引用 vs 无引用）

引擎自有模块 = 入口 `backend` + PYZ 中的 backend/batch_bridge/bridge_preview/color_profile/dng_repack/native_dng/output_chain/profile_metadata/ptc/selftest/sensor/srhq_joraw2/v124_dng/v15_1.*/v15_2.*/v82_lca/v82_lossless_dng/whitepoint/developer。

| 候选（_internal 下） | 磁盘字节 | 引擎自有模块引用 | 第三方（非自树）引用 | 静态判定 |
|---|---:|---|---|---|
| numpy（+numpy.libs+dist-info） | 28,010,034 | 入口与几乎全部 v15_2 模块 | PIL/cffi/imagecodecs/tifffile/wgpu/threadpoolctl | **必需** |
| wgpu（含 assets_v15_2 权重）+ wgpu_reconstruction | 19,204,049 | 入口、v15_1.*/v15_2.pipeline/runtime/selftest/tuning | — | **必需（晚期关键，权重）** |
| rawpy | 2,330,240 | sensor、v15_2.raw_input、color_profile、profile_metadata、batch_bridge | — | **必需（解码）** |
| exifread | （PYZ 内） | 入口、backend、v15_2.raw_input 等 6 处 | — | 必需（元数据；无独立盘目录） |
| tifffile | （PYZ 内） | color_profile、output_chain、v15_2.industrial_tiff 等 | imagecodecs | 必需（间接） |
| cv2 | 85,862,273 | **10 处**：v15_2.registration/scene_policy/industrial_tiff、output_chain、color_profile、selftest、v124_dng、batch_bridge、bridge_preview、v82_lca | — | 有引用（但见 §3：全为懒加载/非本路径） |
| PIL | 11,251,712 | output_chain、v15_2.industrial_tiff | pip（文档串） | 有引用（同上） |
| imagecodecs（盘上仅 _imcd/_jpeg8/_shared） | 1,393,152 | dng_repack、native_dng、v82_lossless_dng | tifffile | 有引用（dng_repack 由入口引用） |
| psutil | 70,656 | v15_2.gui/memory/pipeline | numpy.testing | 有引用（内存预算） |
| win32（win32api 等） | 318,464 | profile_metadata、sensor、v15_2.gui/memory/tuning | — | 有引用 |
| lxml | 6,870,200 | **无** | bs4、tifffile（均为可选懒加载） | **无引用** |
| cryptography（+dist-info） | 10,116,425 | **无** | pip._vendor（未随行） | **无引用**（pyi_rth 钩子仅设环境变量，不 import） |
| bs4 / yaml / charset_normalizer / win32com / pythonwin / tcl8 | ~2.5 MB 合计 | **无** | — | **无引用**（tkinter 仅 v15_2.gui 引用；tcl 数据已在上轮裁剪剥离） |

**资源根（resources\）候选**：

| 候选 | 字节 | 引擎字面量引用 | PE 导入表消费方 | 静态判定 |
|---|---:|---|---|---|
| raw_bridge.exe | 5,208,181 | 0（全 PYZ 无一处） | 自身仅系统 DLL（PyInstaller 壳） | **无引用**；既往取证：我们的路径从不 spawn |
| jxl_worker.exe | 3,043,232 | 0 | libjxl.dll、libjxl_threads.dll、libwinpthread-1.dll | **无引用**；同上 |
| libjxl.dll / _cms / _threads | 6,392,296 | 仅 docstring（imagecodecs 版本清单、v82_lossless_dng 文档串），非 DLL 加载 | 仅 jxl_worker.exe；_internal 内**零**导入方（`_jpegxl.pyd` 本就不在盘上） | **无引用** |
| libhwy / libbrotli×3 | 1,082,869 | 0 | libjxl.dll 的依赖链 | **无引用**（随 libjxl 家族整体死重） |
| libstdc++-6 / libgcc_s_seh-1 / libwinpthread-1 | 2,884,727 | 0 | jxl_worker.exe、libjxl.dll；_internal 内零导入方 | **无引用** |
| liblcms2-2.dll | 456,345 | 0（docstring 一处） | libjxl_cms 链 | **无引用** |
| jsr_backend_manifest.json / jsr_options.json / libraw-build.json / rawvault.ico | 289,812 | 0 | —（本仓 C# 侧 grep 亦零引用；PTC 表由 Core 自嵌 `ptc_z8.csv`） | 无引用（保守起见见 §5） |

## 3. 冒烟矩阵与**方法论转折**

首轮矩阵（r00 空 / r01 cv2 / r03 numpy）结果：**三跑全部存活 60 s**。numpy 阴性对照本应必死（入口即 import）——存活即裁决：**改名在 `_internal` 上无效，引擎根本不读盘上 `_internal`**。

定位（r03 之后立即取证）：

1. `%TEMP%` 出现本轮专属 `_MEI<pid>2` 目录，内含**恰好 1,416 个文件 / 386,096,731 B**——与 exe CArchive 的 1,416 条 `b` 条目一一对应，含已被上轮裁剪剥掉的 `_tcl_data`/`_tk_data`/全套 imagecodecs。即：引擎每次启动把自身内嵌档案**完整**自解压到临时目录并从那里运行。
2. 历史泄漏的 16 个 `_MEI*` 目录（2026-10-14～16，含 10-15 20:32 argv-probe E2E 跑、10-15 23:29 直调杀树跑——即**线上载荷同样如此**）全部同为 1,416 文件 / 386,096,731 B，无一例外。
3. 由此 §7.3 的"父 spawn → ~2 s 后同 argv 子、实际工作在子进程"双实例之谜同时得解：PyInstaller onefile 引导——父实例解压 `_MEI`、经 `_MEIPASS2` 转生同 argv 子实例执行本体（每跑仅一个 `_MEI`，子实例复用）。
4. 顺带解释：上轮 tcl/ffmpeg 裁剪"无碍"并非因为引擎读裁剪后的 `_internal`，而是引擎从不读它；`_internal` 里仅 `ptc_index.csv`、`wgpu_reconstruction\` 两处不在 exe 档案内（源分发补充文件），r10（见下）证明本路径也用不到。
5. TOC 中 `pyi-contents-directory _internal` 选项条目存在但本构建/环境下引导器未启用盘上复用（16/16 观测均走 `_MEI`）；机制层面无需深究——对裁剪而言"自解压恒发生"即充分事实。

**修正后的冒烟矩阵**（6 跑；"结构一致"= 与 r00 控制组逐行同型，仅时间/计数数值不同；控制组推进至 `SRHQ_V12_PROGRESS|39|Transformer 恢复`，其余五跑均至 `|40|`）：

| 跑 | 隐藏集 | 结果 | 分级 |
|---|---|---|---|
| r00-control | 无 | 60.3 s 存活→树杀，0 残留 | 控制组 ✓ |
| r01-cv2 | `_internal\cv2`（85.9 MB） | 60.2 s 存活，结构一致（66 行，较控制组更远） | 证据价值转为"自解压"实证之一 |
| r03-numpy | `_internal\numpy*`（28.0 MB） | 60.2 s 存活，结构一致 | **阴性对照失效 → 方法论转折触发器** |
| r10-no-internal | **整个 `_internal`（185.1 MB / 216 文件）** | 60.4 s 存活，结构一致 | 一级证据：`_internal` 整树对本路径非必需（60 s 窗口内） |
| r11-tools-dlls | raw_bridge.exe + jxl_worker.exe + 11 个 libjxl/mingw/lcms DLL（19.1 MB） | 60.2 s 存活，结构一致 | 一级证据：工具/DLL 家族非必需 |
| r12-max-trim | r10+r11 全集（204.1 MB） | 60.2 s 存活，结构一致（64 行） | **目标配置的 60 s 预验通过** |

原始 r02(PIL)/r04(lxml)/r05(cryptography)/r06-r09 等单项冒烟被方法论转折取代（单项改名对不读 `_internal` 的引擎无信息量），未执行；静态表（§2）承接其分类职责。每跑后副本 257 文件完整复原（`_REMOVED` 归零），本轮 6 个 `_MEI` 泄漏已清理（历史 10 个非本任务产物，未动）。

## 4. 推荐裁剪清单（预估节省对照 manifest 字节）

| # | 剔除项 | 字节 | 一行依据 |
|---|---|---:|---|
| 1 | `resources/_internal/` 整树 | 185,073,415 | r10 整树隐除 60 s 存活且输出结构一致；r03 阴性对照揭示引擎 onefile 自解压（16×`_MEI` 全量观测）；引擎/本仓 C# 双侧零引用 |
| 2 | `raw_bridge.exe` | 5,208,181 | 全 PYZ 零字面量引用；既往取证从不 spawn；r11/r12 存活 |
| 3 | `jxl_worker.exe` | 3,043,232 | 同上；其 PE 导入表是 libjxl 家族唯一消费方 |
| 4 | `libjxl.dll + _cms + _threads` | 6,392,296 | 引擎仅 docstring 提及；`_internal` 内零导入方（`_jpegxl.pyd` 不在盘上）；r11/r12 存活 |
| 5 | `libhwy.dll + libbrotli×3` | 1,082,869 | libjxl.dll 依赖链，随 #4 整体死重；r11/r12 存活 |
| 6 | `libstdc++-6 + libgcc_s_seh-1 + libwinpthread-1 + liblcms2-2` | 3,341,072 | 仅 jxl_worker/libjxl 链消费；`_internal` 内零导入方；r11/r12 存活 |
| | **合计** | **204,141,065（50.28%）** | 载荷 406,018,492 → **201,877,427 B / 28 文件**（exe + 4 元数据文件 + licenses 23 文件） |

保守保留（零引用但价值/体积比不值一验）：`jsr_backend_manifest.json`、`jsr_options.json`、`libraw-build.json`、`rawvault.ico`（合计 289,812 B）；`licenses\`（344,473 B，法务义务，上轮裁定继续分发）。`jsr_backend.exe` 本体不动（字节纪律；其内嵌档案仍含全部依赖，自解压即运行时来源）。

附带效应（无需行动，记录备查）：每跑仍向 `%TEMP%` 解压 386 MB `_MEI`（现有行为，裁剪不改变）；正常退出自清理、强杀泄漏——取消/崩溃路径已由 Job Object 兜底与树杀覆盖。

## 5. 完整验证需求（晚期级清单）

60 s 冒烟未覆盖的唯一区段是**尾部**（~340 s 全程的导出/收尾阶段——DNG 写出、`--out-format jpeg` 缩略、`V12 output:`/`DONE` 收尾）。因此：

1. **单次完整直调 E2E**（用户已授权一次）：max-trim 配置（§4 全集剔除）跑完整 7 帧无损直调，断言退出码 0、`DONE total`、DNG 产物存在且 LibRaw 回读字节级比对照（或与现行 E2E 断言同构）；同跑校验 `_MEI` 自解压照常。若失败，按 §2 静态表回滚最小嫌疑项（首查 PIL/cv2/imagecodecs 的懒加载导出用法——`output_chain` 引用 cv2/PIL/tifffile，`dng_repack`/`native_dng` 引用 imagecodecs，均可能是晚期 import）。
2. **排除清单扩项**（`PayloadPacker.ExcludedPayloadEntries`）：`(DirectoryPrefix, "resources/_internal/")` + 工具/DLL 家族 ExactPath×13；`PayloadTests` 排除断言同构扩项；manifest hash 变更自然触发嵌入对换新。
3. **回归面**：selftest/包管理器排除/版本清剪三链（现有 PayloadTests 全量）+ 盘上抽查（提取目录无 `_internal`、28 文件）。
4. **风险敞口**：未来后端版本若改用盘上 `_internal`（contents-directory 复用真正生效或转 onedir-only），本结论失效——版本升级时以工作区 static_probe*.py 重跑静态+单轮 60 s 冒烟即可重证（r12 模板现成）。

## 6. 诚实分级声明

静态"无引用"仅是弱证据（动态 import 可字符串构造）——本次以 r03 阴性对照的意外存活为镜：**冒烟才是一级证据，且冒烟结论本身也被自解压事实改写**。r10/r11/r12 的"存活"证明的是"引擎运行时不需要盘上这些文件"（因为运行时来源是 `_MEI` 自解压），而非"这些模块在计算上无用"；cv2/numpy/wgpu/rawpy 等**仍是运行时真依赖**（只是由 exe 内嵌档案供给）。对载荷体积而言二者等价，对"理解引擎"而言必须区分。
