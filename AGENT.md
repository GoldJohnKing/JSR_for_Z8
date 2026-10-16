# AGENT.md — JSR for Z8 项目接手指南

尼康 Z8 专用的七帧 RAW 超分辨率 GUI 客户端：C#/.NET 8 + Avalonia 前端，内嵌 Jiangtherapee 2.3.27 的 Python 计算引擎（WGPU/Vulkan 计算；直调预解包树内 `resources\jsr_backend_runtime\jsr_backend.exe`，`_PYI_*` 三元组注入直跑——零 %TEMP% 自解压，载荷不携带顶层 `Jiangtherapee Bridge.exe` 壳与 onefile 整体引擎），以单文件 exe 分发。后端与前端仅通过 argv + stdout 裸行协议通信。

## 1. 环境与工作方式（必读）

- 开发在 **WSL**，一切构建/测试/运行在 **Windows Host**：`cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && <命令>"`。不要用 WSL 侧 dotnet。
- **cmd 互操作陷阱**（都踩过）：
  - `set VAR=1 && next` 会把尾随空格并进值 → 用 .cmd 包装脚本（CRLF）或 `set "VAR=1"`；
  - `start /b <GUI>` 会握住 stdout 管道导致父命令永不返回 → GUI 进程用 `powershell.exe -Command "Start-Process ..."` 分离启动；
  - 路径一律 Windows 绝对路径传给后端/exe。
- 本机（作者机）：AMD RX 7900 XTX；全幅七帧重建约 6 分钟。不传显存预算（见决策 §5）——4GB 预算会在对齐阶段 MemoryError。

## 2. 构建与测试

| 动作 | 命令（Windows host） |
|---|---|
| 全量测试 | `dotnet test --nologo`（当前 **280/280 全绿**；黑场标定恒为预置表匹配——见 §5 决策 3） |
| 一键发布 | `build.bat` → `out\Jsr4Z8_v<版本>.exe`（单文件实测 249,288,792 B；Step 4 恒跑 pack-payload：预解包 onefile 引擎入 `resources\jsr_backend_runtime\`，自动选 reference/ 下最高版本，manifest 哈希比对自决换入，无需手动删 payload.*） |
| 载荷实测 | 预解包载荷：**1,444 文件/原始 587,974,158 B/payload.bin 202,208,052 B/manifest hash 5c78910676d79955**；构成=引擎本体**薄拷贝** 201,243,142 B（b 体清零的打包期变换，长度不变、盘上树零影响；原件=reference/ onefile 未动；无三变量 spawn 即响亮失败）+CArchive 树余 1,416 文件 386,096,731 B→压缩 193,108,942 B+licenses/元数据 27 文件 634,285 B；薄档案评估与端到端实证 docs/superpowers/notes/2026-10-16-thin-archive-probe.md（注入契约实证 …/2026-10-16-meipass2-experiment.md） |
| 无头自检 | `out\Jsr4Z8_v*.exe --selftest [file.nef]`（释放/校验载荷+验树内引擎 `resources\jsr_backend_runtime\jsr_backend.exe`+LibRaw 冒烟） |
| E2E（真 GPU，~6min/全幅） | cmd 脚本内 `set JSR4Z8_E2E=1` + `dotnet test --filter FullyQualifiedName~E2eTests.<名>`；门控规则见 §4 硬契约末条 |
| 诊断工具 | `tools/finalize-variant/`（离线调用融合器：crop/passthrough/featureSet 剥离矩阵，ACR 排障利器） |

版本号在 `src/Jsr4Z8.UI/Jsr4Z8.UI.csproj` `<Version>`（当前 **0.1.0**，用户指定不用 1.x）。

## 3. 架构

```
Avalonia GUI(Jsr4Z8.UI: Save_Z8 风格卡片流/拖放内联建组/渐进补齐)
   └─ QueueMediator(单飞+IDispatcher UI 线程编组+进度节流200ms)
        └─ TaskQueueRunner(顺序队列: 释放载荷→临时目录→BuildArguments argv→BackendRunner(直调)→JsrDngFinalizer)
             ├─ PayloadManager: exe 内嵌载荷(裸DEFLATE+逐文件SHA-256 manifest) → %LOCALAPPDATA%\JSR4Z8\backend\<hash>\（resources\ 源树 27 文件+预解包引擎树 resources\jsr_backend_runtime\ 1,417 文件含引擎本体薄拷贝（b 体清零，长度不变），合计 1,444 文件/587,974,158 B）释放+逐文件SHA校验+自愈+旧版本目录清理（实测首释 2.3s/复验 0.8s）
             ├─ BackendRunner: 直调树内 resources\jsr_backend_runtime\jsr_backend.exe（注入 _PYI_* 三元组，见 §4）→ stdout 裸行协议解析(ProtocolParser) → taskkill /T 树杀取消
             └─ JsrDngFinalizer: 后端裸DNG → 解码像素 → DngComposer(复刻自Save_Z8) 重写
                  （EXIF/GPS/XMP/三级预览搬迁、镜头校正Opcode、无损JPEG Compress=7、LinearRaw 16bit×3通道）
```

- `src/Jsr4Z8.Core`：RawMetadataReader(LibRaw)、NefAssetExtractor(TiffIfdExtractor 复刻)、Ptc(双段log-log拟合)、Dark(Embedded/dark_z8.csv 102行=34档ISO×3压缩（后端 z8_presets manifest 离线导出） 预置黑场表：双键(压缩,ISO)精确匹配不插值——ISO500=DCG切换点；画幅不参与匹配)、TaskGroupValidator(五维一致性：机型/ISO/位深/压缩/画幅 两两校验+预置黑场双键匹配，画像外=CaptureFormatUnsupported"拍摄格式不支持"；无手动黑场路径；曝光包围：仅快门速度不同的 7 帧合法，UI 层默认取中间曝光为基准帧)、Backend(argv 构建器 BuildArguments+裸行协议+直调执行器)、Dng(写盘器)、Payload(白名单=resources/ 树减排除清单+打包期预解包引擎树映射 resources\jsr_backend_runtime\（tools/pack-payload EnginePreextractor：CArchive b 条目→树+引擎本体薄拷贝（b 体清零，薄档案形态））；排除：顶层 Bridge.exe/K14 引擎/onefile 整体 jsr_backend.exe（ExactPath 防回潮）/_internal 整树（引擎从不读盘上树）/raw_bridge+jxl_worker 工具与其 libjxl-DLL 家族（从未 spawn）/z8_presets；THIRD_PARTY_NOTICES.md 不打包)、Queue。
- `external/`：git submodule ×2（y-g-jiang/LibRaw@nikon-he-production、libjpeg-turbo）——**唯一允许的源码依赖方式**；.NET 库一律 NuGet 锁版本（Avalonia 11.2.2 / Semi.Avalonia 11.2.1.10 / CT.Mvvm 8.3.2，与 Save_Z8 对齐）。
- 复刻纪律：Save_Z8 代码（`D:\GitRepos\Save_Z8`，作者自有项目）按「复刻源文件+保留头注」方式移植，**不**作为 submodule。参考产物：`D:\GitRepos\Save_Z8\samples\DSC_7576_fixed.dng`（ACR 实证可开的 CFA 对照基准）。

## 4. 硬契约（实测钉死，勿凭直觉改）

- **后端调用（直调）**：spawn 载荷内 `resources\jsr_backend_runtime\jsr_backend.exe`（预解包树内引擎，载荷唯一入口；onefile `resources\jsr_backend.exe` 与顶层 `Jiangtherapee Bridge.exe` 均不在载荷内，排除清单防回潮）。argv 模板一行化（`BackendRequest.BuildArguments`；探测实证 docs/superpowers/notes/2026-10-15-jsr-backend-argv-probe.md §2/§5/§7）：
  `jsr_backend.exe --output-dir <jobDir> --out-prefix <jobId> --dng-compression auto --raw-frame <f1> … --raw-frame <f7> --developer-mode --read-noise-dn <RN> --black-level-dn <BL> --full-well-electrons <FW> --deghost off --out-dng on --out-png off --out-tiff off --out-format jpeg --jpeg-quality 95 --camera-profile-source libraw`
  **无 request.json、无 cfg 目录、无预览链**（§7 无 cfg/无预览真机直调 exit 0 实证）；**memory 预算彻底不传 = 不限额**；FW=PTC 匹配值 1 位小数，RN/BL=预置黑场表值（`tests/…/ProtocolTests.cs` 逐开关锁定）。
- **引擎注入契约（_PYI_* 三元组，2026-10-16 实证）**：spawn 树内引擎时必须整组字符级精确注入 `_PYI_ARCHIVE_FILE=<引擎 exe 全路径>`（它自身即 CArchive）、`_PYI_APPLICATION_HOME_DIR=<树目录>`（无尾分隔符）、`_PYI_PARENT_PROCESS_LEVEL=0`——引擎以「子实例」身份直跑树：**零 %TEMP% _MEI 自解压、单实例、树零写入**（端到端实测：全程 1s 轮询零 _MEI、单 pid 贯穿、树 1,444 文件前后零变化）。fallback 语义：缺 `_PYI_ARCHIVE_FILE` 时引导器复位块剥除另两个 → 静默回退 per-run 自解压（每跑泄一个 386MB `_MEI`+父子双实例）——`BackendRunnerTests` stub 回显逐字符锁三值，路径回退由 `QueueRunnerTests` 防退化锁拦截。
- **stdout 协议（裸行，无信封）**：引擎 stdout 即协议本体，零 `ASSISTANT_*` 信封（那是 Bridge 外加的，已随直调消失）；结构行仅 `SRHQ_V12_PROGRESS|int|阶段` 与导出期 `SRHQ_ETA_PROGRESS|dng|done|total`（映射 95+5*done/total，尾三行 = `SRHQ_V12_PROGRESS|100|导出完成` → `V12 output: <dir>` → `DONE total <s>s`，P100 恰一次）；其余（`PTC {…}` 回显、`SRHQ_V12_FRAME|…`、Proxy 噪声）按原文透传；stderr 不参与协议（诊断回溯走 stderr，成功时 0 字节）。夹具：`tests/Jsr4Z8.Tests/fixtures/protocol/direct_success.log`（真实直调日志）。
- **取消**：注入生效后引擎单实例直跑（历史未注入形态每次自重复为父子双实例、~2s 间隔，实际工作在子进程，父转述退出码；树内还可能出现未名孙进程）——无论单双实例，都必须 `taskkill /T /F` 杀整棵树（持有自己 spawn 的父 pid 即可，防御性语义不变）。崩溃兜底：主进程启动即加入 kill-on-close Job Object，异常退出时 OS 自动终止全部子进程；正常取消仍为 taskkill /T 树杀。
- **后端产物**：`<outputDir>/<prefix>_V15_3_5_K7_<ts>/<同名>.dng` + 辅助文件；失败残留 `.partial` 目录（成功 glob 需排除）。退出码取父实例（正常完成 0）。
- **后端 DNG 形态**：LinearRaw(34892) 16bit×3通道、无 EXIF/预览/校正、BL=0/WL=65535、尺寸=输入 raw 填充尺寸×2（8280×5520→16560×11040）。
- **PTC**：`Ptc/Embedded/ptc_z8.csv`（自原包提取）；表内 ISO 直取 1/alpha，表外双段 **log-log 最小二乘**（<500 / ≥500 两段）——字面线性拟合违反计划自身留一检验且 ISO499 得负满阱，勿改回。
- **E2E 门禁**：完整 E2E（使用 samples RAW 的 GPU 重建）默认不跑，仅用户明示要求时运行；例行验收 = 全量单测 + `build.bat` + `--selftest`（无 NEF 参数，仅载荷释放/校验）。

## 5. 关键决策（用户裁定，勿推翻）

1. **不传显存预算**（用户明确要求"始终不限制"）。**无设置功能**：恒默认行为——输出=基准帧同目录、临时=%TEMP%，无配置持久化。
2. **最终 DNG 的 IFD0 无 Artist、Software="JSR for Z8"**——后端算法署名不进 IFD0（载荷不携带 THIRD_PARTY_NOTICES.md；第三方许可仍在 resources/licenses 内随载荷分发）；NEF EXIF 区内摄影者自有标签照常迁移。
3. **黑场标定恒为预置自动匹配（无手动黑场文件路径）**。7 帧在 **机型/ISO/位深/压缩/画幅** 五维两两一致（机型逐文件验 Z8；位深=TIFF BitsPerSample、压缩=MakerNote NEFCompression、画幅=raw 尺寸；**陷阱**：Z8 全部 NEF 的 TIFF Compression 恒 34713，HE(13)/HE*(14) 与无损(3) 只能靠 NEFCompression 判别），任一不一致禁止执行。PTC 按机型+ISO 匹配（单像素参数，读出维度不影响归一化 shot 斜率）；预置黑场 `Dark/Embedded/dark_z8.csv`（102 行 = 34 档 ISO × 3 压缩，后端 2.3.27 z8_presets manifest 离线导出：black_dn 标称、RN=√四相位时域方差均值、映射 0→3/3→14/4→13；Lo 档 32/40/50 为完备性纳入）按 **(压缩,ISO) 双键**精确匹配——画幅不参与（读噪与画幅无关，DX vs FX ≤1%@高ISO，任意画幅取 FX 行）。画像外（12bit）或表外 ISO → `CaptureFormatUnsupported`"拍摄格式不支持"。物理事实（口径唯一来源 DarkTable.cs 类注释）：HE 读噪比无损 +5~13%（Lo 档最大），HE\* −1.5~+3%（有损编码量化噪声叠加）；ISO500=DCG 切换点故不插值；Lo 扩展档=假ISO（数据≡ISO64，自动命中 64 行）；常规快门不影响标定。
4. 输出名固定 `JSR_<基准帧名>.dng`；基准帧=等曝光时文件名自然排序第 1 张、曝光包围（仅快门不同）时按曝光升序取中间帧（第 4 张）。任务组可先少后补（卡内「添加 RAW…」按钮或再拖放到卡）；芯片★星标=手动基准（仅就绪/无法执行态可设，星标帧被移除则回退自动选取）；悬浮文件名显示关键参数（机型/快门/光圈/ISO/位深/压缩/画幅/拍摄时间）；>7 或 <7 帧均阻断为「帧数不符」。非等曝光组芯片显示 ±EV 偏差胶囊（点击设基准，等曝光组保留星标；EV 恒以中间曝光为 0EV 锚点，不随基准切换）；拖入/补齐即时建卡并显示校验中 Loading，校验由卡片自持（ValidateAsync），状态条随分组状态着色于卡底。
5. 顺序队列一次一组；失败/停止不阻断后续；失败/停止立即删除本任务临时目录（不留诊断目录），启动时清扫临时根（TempBase）下 Jsr4Z8 的全部残留（应对崩溃残留；单实例互斥；清扫跳过 2 分钟内新写的目录）。
6. 布局默认 **Tiles**（`DngWriteOptions.RawLayout` 的 Strips 选项为 ACR 排障变体，测试与 finalize-variant 工具在用）。
7. 界面纯中文；Semi.Avalonia 默认暗色、跟随系统+切换；以 `D:\GitRepos\Save_Z8` 的视觉规范为设计权威。
8. 不签名、单 exe 分发；README 有 SmartScreen/杀软指引。

## 6. 经验教训（血泪，浓缩）

1. **DefaultCropOrigin 是 ActiveArea 相对坐标**（DNG 规范），不是绝对坐标。本项目曾因此被 ACR 全尺寸拒开：×2 几何路径 ActiveArea 原点非零 (16,24) 而暴露；Save_Z8 的 CFA（原点 0,0）与所有裁剪探针（回退几何）都侥幸合法——**绝对=相对的巧合掩盖了 bug**。回归测试已锁（`CropOrigin_MustBeActiveAreaRelative_*`）。排障方法论：单变量判别件（tools/finalize-variant 的 featureSet/crop/passthrough）+ 用户 ACR 做终审；本地裁判（rawpy/tifffile/pylibjpeg）只能证伪不能证实 ACR 行为。
2. **兼容性问题的排查顺序**：先字节级对照三方（实证可开件/失败件/同源参考），再单变量二分；警惕自建工具引入未验证变量（手搓 wrapper 曾污染一轮结论）。
3. **LibRaw fork（含 mainline）不读 LinearRaw DNG**（-2）——像素验证走 `ljd_decode_stream`（原生解码导出，libjpeg 错误遏制已接 setjmp，勿动）。rawpy 对 LinearRaw 的读取行为不可靠，别当裁判。
4. **编码器/解码器对称偏误**：自家编解码往返全绿 ≠ 规范正确；必须有独立血统解码器交叉验证（pylibjpeg=经典 IJG）。
5. **测试不留垃圾**：测试临时目录统一 GUID 根+finally 清理（TestTemp helper）；曾有 34 个 `jsr4z8_*` 泄漏到用户 %TEMP%。
6. **UI 进度必须节流**：后端进度 ~20 行/秒、子任务名轮换，直通 UI 会闪烁（200ms 合并节流，终态必达）。
7. 派 subagent 审查时**禁止全局 find**——reference/（约 854MB）、external/、~/.nuget 会超时；限定 grep 路径。
8. 运行期错误文案要人话化：直调引擎后直接从裸行提取 `SRHQ_NET_ABORT:stage|msg` / MemoryError 行（ASSISTANT_* 信封已随 Bridge 移除而消失，无需再跳信封行）。
9. 进入 Running/Done 必须清 ErrorText（用户踩过"重试后旧失败提示不消失"）。
10. **E2E 门控用例长期不跑会腐烂**：中文路径副本拍平、抓取流写句柄两缺陷即例（长期被 `JSR4Z8_E2E=1` 门控跳过所掩盖）——完整 E2E 仅用户明示时运行，该窗口到来时应全绿后再交付。

## 7. 已知遗留（低风险，勿顺手重构）

- 编码侧 `lje_encode_tile`/`lje_selftest` 仍用 libjpeg 默认 error_exit（输入自生成，风险低）。
- DngWriter 潜在 ≥2GiB 载荷的 int/uint 算术隐患 5 处（W8/W12/W13/W14/R6；审计明细在作者本地 `.superpowers/sdd-postrelease-fix6-report.md`，未入库；当前最大 1.1GB 安全）。
- build.bat Step4 的 cmake 路径硬编码（载荷段自动选版+哈希比对换新）。
- StartAll 与用户单卡点击交错时可能跳过一张就绪卡（需重点击）；卡片 VM 订阅共享 runner 事件不退订（有界）。
- 星标/✕ 命令绑定经 ReflectionBinding（运行时解析，编译期不校验）——UI 冒烟（人工手测）覆盖。

## 8. 文档地图

- 活性契约参考（argv 探测/裸行协议/直调实证，硬契约的事实出处）：`docs/superpowers/notes/2026-10-15-jsr-backend-argv-probe.md`
- 注入契约/预解包实证（_PYI_* 三元组机制、零 _MEI、持久目录只读性）：`docs/superpowers/notes/2026-10-16-meipass2-experiment.md`
- 排障/修复记录（ACR 攻坚全程）：作者本地 `.superpowers/sdd-postrelease-fix*.md`（fix1-11，未入库；可执行结论已浓缩进 §6 经验教训）
- 清理决策与防误删清单（2026-10-16 全库清理审计）：`docs/cleanup-audit-2026-10-16.md`
- 样例：`samples/test_frames/`（`lossless`=DSC_7576-7582 七帧无损等曝光组；`he_compressed`=DSC_7652-7658 七帧 HE 组；`unequal_exposure`=DSC_7855-7861 七帧不等曝光/包围组（1/3EV 快门梯 1/200…1/50，中间曝光 1/100 为基准帧），均未入 git；黑场标定无样本文件——预置黑场表来源为后端 z8_presets manifest 离线导出，见 §5 决策 3）；后端原包：`reference/` 下仅 2.3.27 一树在（2.3.25 已移除）；勿改一字节；载荷取 2.3.27（build.bat 自动选最高版本；黑场表/argv 契约随版重验）