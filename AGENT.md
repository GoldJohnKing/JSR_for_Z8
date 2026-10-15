# AGENT.md — JSR for Z8 项目接手指南

尼康 Z8 专用的七帧 RAW 超分辨率 GUI 客户端：C#/.NET 8 + Avalonia 前端，**原封不动内嵌** Jiangtherapee Bridge 2.3.25 的 Python 后端（WGPU/Vulkan 计算），以单文件 exe 分发。后端与前端仅通过命令行 + stdout 协议通信。

## 1. 环境与工作方式（必读）

- 开发在 **WSL**，一切构建/测试/运行在 **Windows Host**：`cmd.exe /c "cd /d D:\GitRepos\JSR_for_Z8 && <命令>"`。不要用 WSL 侧 dotnet。
- **cmd 互操作陷阱**（都踩过）：
  - `set VAR=1 && next` 会把尾随空格并进值 → 用 .cmd 包装脚本（CRLF）或 `set "VAR=1"`；
  - `start /b <GUI>` 会握住 stdout 管道导致父命令永不返回 → GUI 进程用 `powershell.exe -Command "Start-Process ..."` 分离启动；
  - 路径一律 Windows 绝对路径传给后端/exe。
- 本机（作者机）：AMD RX 7900 XTX；全幅七帧重建约 6 分钟（无预算限制时）。4GB 预算会在对齐阶段 MemoryError——现在已不传预算（见决策 §5）。

## 2. 构建与测试

| 动作 | 命令（Windows host） |
|---|---|
| 全量测试 | `dotnet test --nologo`（当前 **277/277**，含规范不变量/外来流真值/模糊/金丝雀等经验驱动套件） |
| 一键发布 | `build.bat` → `out\Jsr4Z8_v<版本>.exe`（~317MB 单文件（载荷裁剪后）；Step 4 恒跑 pack-payload：自动选 reference/ 下最高版本，manifest 哈希比对自决换入，无需手动删 payload.*） |
| 无头自检 | `out\Jsr4Z8_v*.exe --selftest [file.nef]`（释放/校验载荷+LibRaw 冒烟） |
| E2E（真 GPU，~6min/全幅） | cmd 脚本内 `set JSR4Z8_E2E=1` + `dotnet test --filter FullyQualifiedName~E2eTests.<名>`；门控见 `tests/Jsr4Z8.Tests/E2eTests.cs` |
| 诊断工具 | `tools/finalize-variant/`（离线调用融合器：crop/passthrough/featureSet 剥离矩阵，ACR 排障利器） |

版本号在 `src/Jsr4Z8.UI/Jsr4Z8.UI.csproj` `<Version>`（当前 **0.1.0**，用户指定不用 1.x）。

## 3. 架构

```
Avalonia GUI(Jsr4Z8.UI: Save_Z8 风格卡片流/拖放内联建组/渐进补齐/设置)
   └─ QueueMediator(单飞+IDispatcher UI 线程编组+进度节流200ms)
        └─ TaskQueueRunner(顺序队列: 释放载荷→临时目录→请求JSON→BackendRunner→JsrDngFinalizer)
             ├─ PayloadManager: exe 内嵌载荷(zlib) → %LOCALAPPDATA%\JSR4Z8\backend\<hash>\ 释放+SHA校验+自愈+旧版本目录清理
             ├─ BackendRunner: spawn 原版exe --assistant-jsr-job → stdout协议解析(ProtocolParser) → 杀进程树取消
             └─ JsrDngFinalizer: 后端裸DNG → 解码像素 → DngComposer(复刻自Save_Z8) 重写
                  （EXIF/GPS/XMP/三级预览搬迁、镜头校正Opcode、无损JPEG Compress=7、LinearRaw 16bit×3通道）
```

- `src/Jsr4Z8.Core`：RawMetadataReader(LibRaw)、NefAssetExtractor(TiffIfdExtractor 复刻)、Ptc(双段log-log拟合)、Dark(Embedded/dark_z8.csv 93行=31档ISO×3压缩画像 预置黑场表：双键(压缩,ISO)精确匹配不插值——ISO500=DCG切换点；画幅不参与匹配)、TaskGroupValidator(五维一致性：机型/ISO/位深/压缩/画幅 两两校验+预置黑场双键匹配，画像外=CaptureFormatUnsupported"拍摄格式不支持"；无手动黑场路径；曝光包围：仅快门速度不同的 7 帧合法，UI 层默认取中间曝光为基准帧)、Backend(单形态手动标定请求+协议+执行器)、Dng(写盘器)、Payload(打包排除清单：K14 引擎/Tcl-Tk/ffmpeg 视频 DLL/wgpu 测试残留/z8_presets；THIRD_PARTY_NOTICES.md 不再打包)、Queue。
- `external/`：git submodule ×2（y-g-jiang/LibRaw@nikon-he-production、libjpeg-turbo）——**唯一允许的源码依赖方式**；.NET 库一律 NuGet 锁版本（Avalonia 11.2.2 / Semi.Avalonia 11.2.1.10 / CT.Mvvm 8.3.2，与 Save_Z8 对齐）。
- 复刻纪律：Save_Z8 代码（`D:\GitRepos\Save_Z8`，作者自有项目）按「复刻源文件+保留头注」方式移植，**不**作为 submodule。参考产物：`D:\GitRepos\Save_Z8\samples\DSC_7576_fixed.dng`（ACR 实证可开的 CFA 对照基准）。

## 4. 硬契约（实测钉死，勿凭直觉改）

- **后端调用**：`"Jiangtherapee Bridge.exe" --assistant-jsr-job <request.json> <resources目录> <config目录>`；config 目录即排他锁 → 每任务独立目录。**`memory` 字段彻底不传 = 不限额**（实测全流程通过）。
- **请求 schema（单形态，`tests/…/ProtocolTests.cs` 逐字段锁定）**：手动标定形态——`manual:true + read_noise + black`（载预置表值）+ fullwell 等 14 键，无 `dark` 键（真机实证：后端日志出现 `manual DN`/`manual RN(DN)`）。无 memory 键。
- **stdout 协议**：`ASSISTANT_JSR_START|{json}` → N×`ASSISTANT_JSR_LOG|<行>` → 恰一条 `ASSISTANT_JSR_RESULT|{json: exit_code/state/log}`；**SRHQ 进度行大多是裸行**（无 LOG 前缀）；两种进度格式 `SRHQ_V12_PROGRESS|int|阶段` 与导出期 `SRHQ_ETA_PROGRESS|dng|done|total`。夹具：`tests/fixtures/protocol/`（真实日志）。
- **取消**：必须杀整棵进程树（harness 会再 spawn jsr_backend.exe）——已有 START-pid 兜底。
- **后端产物**：`<outputDir>/<prefix>_V15_3_5_K7_<ts>/<同名>.dng` + `辅助文件/`；失败残留 `.partial` 目录（成功 glob 需排除）。
- **后端 DNG 形态**：LinearRaw(34892) 16bit×3通道、无 EXIF/预览/校正、BL=0/WL=65535、尺寸=输入 raw 填充尺寸×2（8280×5520→16560×11040）。
- **PTC**：`Ptc/Embedded/ptc_z8.csv`（自原包提取）；表内 ISO 直取 1/alpha，表外双段 **log-log 最小二乘**（<500 / ≥500 两段）——字面线性拟合违反计划自身留一检验且 ISO499 得负满阱，勿改回。

## 5. 关键决策（用户裁定，勿推翻）

1. **不传显存预算**（用户明确要求"始终不限制"）——设置界面无此项。
2. **最终 DNG 的 IFD0 无 Artist、Software="JSR for Z8"**——后端算法署名不进 IFD0（载荷不再携带 THIRD_PARTY_NOTICES.md——用户裁定；第三方许可仍在 resources/licenses 内随载荷分发）；NEF EXIF 区内摄影者自有标签照常迁移。
3. **黑场标定恒为预置自动匹配（无手动黑场文件路径）**。7 帧在 **机型/ISO/位深/压缩/画幅** 五维两两一致（机型逐文件验 Z8；位深=TIFF BitsPerSample、压缩=MakerNote NEFCompression、画幅=raw 尺寸；**陷阱**：Z8 全部 NEF 的 TIFF Compression 恒 34713，HE(13)/HE*(14) 与无损(3) 只能靠 NEFCompression 判别），任一不一致禁止执行。PTC 按机型+ISO 匹配（单像素参数，读出维度不影响归一化 shot 斜率）；预置黑场 `Dark/Embedded/dark_z8.csv`（102 行 = 34 档 ISO × 3 压缩，后端 2.3.27 z8_presets manifest 离线导出：black_dn 标称、RN=√四相位时域方差均值、映射 0→3/3→14/4→13；旧自测 93 行表弃用；Lo 档 32/40/50 为完备性纳入）按 **(压缩,ISO) 双键**精确匹配——画幅不参与（读噪与画幅无关，DX vs FX ≤1%@高ISO，任意画幅取 FX 行）。画像外（12bit）或表外 ISO → `CaptureFormatUnsupported`"拍摄格式不支持"。物理事实：HE/HE\* 读噪比无损 +3~7%（量化噪声叠加；后端 2.3.27 库 Lo 档另见 6~13% 级偏差：无损 Lo32 实测 RN 1.87 vs 64 档 2.00（低约 6%），Lo32 HE 2.12 对无损溢价至 +13%）；ISO500=DCG 切换点故不插值；Lo 扩展档=假ISO（数据≡ISO64，自动命中 64 行）；常规快门不影响标定。
4. 输出名固定 `JSR_<基准帧名>.dng`；基准帧=等曝光时文件名自然排序第 1 张、曝光包围（仅快门不同）时按曝光升序取中间帧（第 4 张）。任务组可先少后补（卡内「添加 RAW…」按钮或再拖放到卡）；芯片★星标=手动基准（仅就绪/无法执行态可设，星标帧被移除则回退自动选取）；悬浮文件名显示关键参数（机型/快门/光圈/ISO/位深/压缩/画幅/拍摄时间）；>7 或 <7 帧均阻断为「帧数不符」。非等曝光组芯片显示 ±EV 偏差胶囊（点击设基准，等曝光组保留星标；EV 恒以中间曝光为 0EV 锚点，不随基准切换）；拖入/补齐即时建卡并显示校验中 Loading，校验由卡片自持（ValidateAsync），状态条随分组状态着色于卡底。
5. 顺序队列一次一组；失败/停止不阻断后续；失败/停止立即删除本任务临时目录（不留诊断目录），启动时清扫临时根（TempBase）下 Jsr4Z8 的全部残留（应对崩溃残留；单实例互斥；清扫跳过 2 分钟内新写的目录）。
6. 布局默认 **Tiles**（`DngWriteOptions.RawLayout` 有 Strips 选项，为 ACR 排障而加）。根因是几何而非布局；Strips 路径同样可用。
7. 界面纯中文；Semi.Avalonia 默认暗色、跟随系统+切换；以 `D:\GitRepos\Save_Z8` 的视觉规范为设计权威（本轮用户需求，覆盖 `docs/design/*.html` 旧 mockup）。
8. 不签名、单 exe 分发；README 有 SmartScreen/杀软指引。

## 6. 经验教训（血泪，浓缩）

1. **DefaultCropOrigin 是 ActiveArea 相对坐标**（DNG 规范），不是绝对坐标。本项目曾因此被 ACR 全尺寸拒开：×2 几何路径 ActiveArea 原点非零 (16,24) 而暴露；Save_Z8 的 CFA（原点 0,0）与所有裁剪探针（回退几何）都侥幸合法——**绝对=相对的巧合掩盖了 bug**。回归测试已锁（`CropOrigin_MustBeActiveAreaRelative_*`）。排障方法论：单变量判别件（tools/finalize-variant 的 featureSet/crop/passthrough）+ 用户 ACR 做终审；本地裁判（rawpy/tifffile/pylibjpeg）只能证伪不能证实 ACR 行为。
2. **兼容性问题的排查顺序**：先字节级对照三方（实证可开件/失败件/同源参考），再单变量二分；警惕自建工具引入未验证变量（手搓 wrapper 曾污染一轮结论）。
3. **LibRaw fork（含 mainline）不读 LinearRaw DNG**（-2）——像素验证走 `ljd_decode_stream`（Task 9 加的原生解码导出，libjpeg 错误遏制已接 setjmp，勿动）。rawpy 对 LinearRaw 的读取行为不可靠，别当裁判。
4. **编码器/解码器对称偏误**：自家编解码往返全绿 ≠ 规范正确；必须有独立血统解码器交叉验证（pylibjpeg=经典 IJG）。
5. **测试不留垃圾**：测试临时目录统一 GUID 根+finally 清理（TestTemp helper）；曾有 34 个 `jsr4z8_*` 泄漏到用户 %TEMP%。
6. **UI 进度必须节流**：后端进度 ~20 行/秒、子任务名轮换，直通 UI 会闪烁（200ms 合并节流，终态必达）。
7. 派 subagent 审查时**禁止全局 find**——reference/(649MB)、external/、~/.nuug 会超时；限定 grep 路径。
8. 运行期错误文案要人话化：从后端日志尾提取 `SRHQ_NET_ABORT:stage|msg` / MemoryError 行，但要**跳过 ASSISTANT_* 信封行**（信封内嵌日志整行 JSON，会盖过干净的裸行）。
9. 进入 Running/Done 必须清 ErrorText（用户踩过"重试后旧失败提示不消失"）。
10. **E2E 门控用例长期不跑会腐烂**：中文路径副本拍平、抓取流写句柄两缺陷即例（自引入起一直被 `JSR4Z8_E2E=1` 门控跳过所掩盖）——发布前应跑一轮 `JSR4Z8_E2E=1` E2E 全绿再交付。

## 7. 已知遗留（低风险，勿顺手重构）

- 编码侧 `lje_encode_tile`/`lje_selftest` 仍用 libjpeg 默认 error_exit（输入自生成，风险低）。
- DngWriter 潜在 ≥2GiB 载荷的 int/uint 算术隐患 5 处（W8/W12/W13/W14/R6，见 `.superpowers/sdd-postrelease-fix6-report.md` 审计表，当前最大 1.1GB 安全）。
- build.bat Step4 的 cmake 路径硬编码（载荷段已改自动选版+哈希比对换新，2026-10-15）。
- StartAll 与用户单卡点击交错时可能跳过一张就绪卡（需重点击）；卡片 VM 订阅共享 runner 事件不退订（有界）。
- TaskCardView 与 MainWindow 各有一份 NEF 过滤小助手（可共享）。
- 星标/✕ 命令绑定经 ReflectionBinding（运行时解析，编译期不校验）——UI 冒烟（人工手测）覆盖。
- ~~分支 `feat/jsr4z8` 尚未合并回 main~~ 已合并（含 ACR 几何修复，用户人工核验 DNG 输出通过）。

## 8. 文档地图

- 设计文档（已批准）：`docs/superpowers/specs/2026-10-13-jsr4z8-design.md`
- 实施计划（含实测协议事实节）：`docs/superpowers/plans/2026-10-13-jsr4z8-implementation.md`
- 本轮 UI 重构（内联建组/曝光包围/失败即删/载荷版本清理）计划：`docs/superpowers/plans/2026-10-15-inline-groups-ui-bracketing.md`
- 本轮渐进补齐+载荷裁剪计划：`docs/superpowers/plans/2026-10-15-progressive-groups-payload-trim.md`
- GUI 定稿 mockup（旧 mockup，视觉权威自本轮起为 Save_Z8 规范——见 §5 决策 7）：`docs/design/*.html`
- 排障/修复报告（ACR 攻坚全程）：`.superpowers/sdd-postrelease-fix*.md`（fix1-10）
- 样例：`samples/`（DSC_7575=黑场，7576-7582=七帧；`samples/dark_frames/`={FX{Lossless,HE,HE Star},DX{Lossless}} 四画像实测黑场（旧 93 行预置表的来源），未入 git）；后端原包：`reference/` 下 2.3.25 与 2.3.27 两树均在（均勿改一字节）；载荷现取 2.3.27（build.bat 自动选最高版本；黑场表/argv 契约已随之重验）