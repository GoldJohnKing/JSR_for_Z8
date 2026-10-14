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
| 全量测试 | `dotnet test --nologo`（当前 **221/221**，含规范不变量/外来流真值/模糊/金丝雀等经验驱动套件） |
| 一键发布 | `build.bat` → `out\Jsr4Z8_v<版本>.exe`（~537MB 单文件；载荷已打包时跳过打包段；改了 reference/ 后删 `src/Jsr4Z8.Core/Payload/Embedded/payload.*` 强制重打包） |
| 无头自检 | `out\Jsr4Z8_v*.exe --selftest [file.nef]`（释放/校验载荷+LibRaw 冒烟） |
| E2E（真 GPU，~6min/全幅） | cmd 脚本内 `set JSR4Z8_E2E=1` + `dotnet test --filter FullyQualifiedName~E2eTests.<名>`；门控见 `tests/Jsr4Z8.Tests/E2eTests.cs` |
| 诊断工具 | `tools/finalize-variant/`（离线调用融合器：crop/passthrough/featureSet 剥离矩阵，ACR 排障利器） |

版本号在 `src/Jsr4Z8.UI/Jsr4Z8.UI.csproj` `<Version>`（当前 **0.1.0**，用户指定不用 1.x）。

## 3. 架构

```
Avalonia GUI(Jsr4Z8.UI: 卡片流/新建面板/设置)
   └─ QueueMediator(单飞+IDispatcher UI 线程编组+进度节流200ms)
        └─ TaskQueueRunner(顺序队列: 释放载荷→临时目录→请求JSON→BackendRunner→JsrDngFinalizer)
             ├─ PayloadManager: exe 内嵌载荷(zlib) → %LOCALAPPDATA%\JSR4Z8\backend\<hash>\ 释放+SHA校验+自愈
             ├─ BackendRunner: spawn 原版exe --assistant-jsr-job → stdout协议解析(ProtocolParser) → 杀进程树取消
             └─ JsrDngFinalizer: 后端裸DNG → 解码像素 → DngComposer(复刻自Save_Z8) 重写
                  （EXIF/GPS/XMP/三级预览搬迁、镜头校正Opcode、无损JPEG Compress=7、LinearRaw 16bit×3通道）
```

- `src/Jsr4Z8.Core`：RawMetadataReader(LibRaw)、NefAssetExtractor(TiffIfdExtractor 复刻)、Ptc(双段log-log拟合)、TaskGroupValidator(8文件机型/ISO一致性)、Backend(请求+协议+执行器)、Dng(写盘器)、Payload、Queue。
- `external/`：git submodule ×2（y-g-jiang/LibRaw@nikon-he-production、libjpeg-turbo）——**唯一允许的源码依赖方式**；.NET 库一律 NuGet 锁版本（Avalonia 11.2.2 / Semi.Avalonia 11.2.1.10 / CT.Mvvm 8.3.2，与 Save_Z8 对齐）。
- 复刻纪律：Save_Z8 代码（`D:\GitRepos\Save_Z8`，作者自有项目）按「复刻源文件+保留头注」方式移植，**不**作为 submodule。参考产物：`D:\GitRepos\Save_Z8\samples\DSC_7576_fixed.dng`（ACR 实证可开的 CFA 对照基准）。

## 4. 硬契约（实测钉死，勿凭直觉改）

- **后端调用**：`"Jiangtherapee Bridge.exe" --assistant-jsr-job <request.json> <resources目录> <config目录>`；config 目录即排他锁 → 每任务独立目录。**`memory` 字段彻底不传 = 不限额**（实测全流程通过）。
- **请求 schema**：`tests/…/ProtocolTests.cs` 逐字段锁定（13 个 parameters 键，无 memory）。
- **stdout 协议**：`ASSISTANT_JSR_START|{json}` → N×`ASSISTANT_JSR_LOG|<行>` → 恰一条 `ASSISTANT_JSR_RESULT|{json: exit_code/state/log}`；**SRHQ 进度行大多是裸行**（无 LOG 前缀）；两种进度格式 `SRHQ_V12_PROGRESS|int|阶段` 与导出期 `SRHQ_ETA_PROGRESS|dng|done|total`。夹具：`tests/fixtures/protocol/`（真实日志）。
- **取消**：必须杀整棵进程树（harness 会再 spawn jsr_backend.exe）——已有 START-pid 兜底。
- **后端产物**：`<outputDir>/<prefix>_V15_3_5_K7_<ts>/<同名>.dng` + `辅助文件/`；失败残留 `.partial` 目录（成功 glob 需排除）。
- **后端 DNG 形态**：LinearRaw(34892) 16bit×3通道、无 EXIF/预览/校正、BL=0/WL=65535、尺寸=输入 raw 填充尺寸×2（8280×5520→16560×11040）。
- **PTC**：`Ptc/Embedded/ptc_z8.csv`（自原包提取）；表内 ISO 直取 1/alpha，表外双段 **log-log 最小二乘**（<500 / ≥500 两段）——字面线性拟合违反计划自身留一检验且 ISO499 得负满阱，勿改回。

## 5. 关键决策（用户裁定，勿推翻）

1. **不传显存预算**（用户明确要求"始终不限制"）——设置界面无此项。
2. **最终 DNG 的 IFD0 无 Artist、Software="JSR for Z8"**——后端算法署名不进 IFD0（法律署名随载荷内 THIRD_PARTY_NOTICES.md 分发）；NEF EXIF 区内摄影者自有标签照常迁移。
3. 黑场必须文件；PTC 机型+ISO 自动匹配（仅 Z8），匹配不到禁止执行。
4. 输出名固定 `JSR_<基准帧名>.dng`；基准帧=文件名自然排序第 1 张（面板内可点芯片换）。
5. 顺序队列一次一组；失败/停止不阻断后续；**失败保留 ~3GB 诊断目录**（%TEMP%\Jsr4Z8\<guid>，启动时清扫 >7 天旧目录）——README 已提示磁盘需求。
6. 布局默认 **Tiles**（`DngWriteOptions.RawLayout` 有 Strips 选项，为 ACR 排障而加）。根因是几何而非布局；Strips 路径同样可用。
7. 界面纯中文；Semi.Avalonia 默认暗色、跟随系统+切换；UI 文案/makeup 以 `docs/design/*.html` 为设计权威。
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

## 7. 已知遗留（低风险，勿顺手重构）

- 编码侧 `lje_encode_tile`/`lje_selftest` 仍用 libjpeg 默认 error_exit（输入自生成，风险低）。
- DngWriter 潜在 ≥2GiB 载荷的 int/uint 算术隐患 5 处（W8/W12/W13/W14/R6，见 `.superpowers/sdd-postrelease-fix6-report.md` 审计表，当前最大 1.1GB 安全）。
- build.bat Step4 载荷跳过仅按存在性（不比对 reference/ 新旧）；cmake 路径硬编码。
- StartAll 与用户单卡点击交错时可能跳过一张就绪卡（需重点击）；卡片 VM 订阅共享 runner 事件不退订（有界）。
- ~~分支 `feat/jsr4z8` 尚未合并回 main~~ 已合并（含 ACR 几何修复，用户人工核验 DNG 输出通过）。

## 8. 文档地图

- 设计文档（已批准）：`docs/superpowers/specs/2026-10-13-jsr4z8-design.md`
- 实施计划（含实测协议事实节）：`docs/superpowers/plans/2026-10-13-jsr4z8-implementation.md`
- GUI 定稿 mockup（设计权威）：`docs/design/*.html`
- 排障/修复报告（ACR 攻坚全程）：`.superpowers/sdd-postrelease-fix*.md`（fix1-10）
- 样例：`samples/`（DSC_7575=黑场，7576-7582=七帧）；后端原包：`reference/Jiangtherapee-Bridge-2.3.25-Windows-x64/`（载荷来源，勿改一字节）
