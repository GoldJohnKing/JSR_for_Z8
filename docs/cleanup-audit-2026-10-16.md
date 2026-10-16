# 清理调研报告（2026-10-16，已复核版）

只读调研，本报告不含任何代码改动。本稿**取代同日旧稿**：旧稿成文于薄档案载荷落地（提交 `d2d26e3`/`f030f9e`/`078645d`）之前，其若干文档行与"未实施"判断已过时；本稿对每一项结论逐一用 grep/读文件/磁盘实测复核到当前 HEAD（`078645d`），并新补旧稿没有的三处尺寸口径漂移与一处工具文档死引用。

置信度：**H** = 已实证可执行；**M** = 需小步验证或连带改动；**L** = 仅有疑点，动手前需再查。

范围 = git 追踪文件（src / tests / tools / native / docs / README / AGENT.md / build.bat；约 Core 5,278 LOC + UI 2,935 LOC + 测试 7,365 LOC / 204 个 [Fact]/[Theory]、285 测试用例）。`build/`、`out/`、`libs/`、`external/`、`reference/`、`samples/`、`src/Jsr4Z8.Core/Payload/Embedded/` 为 gitignore 的生成物，不属于对象。

## 结论概览

| 类别 | 数量 | 估计收益 |
|---|---|---|
| 死代码（可直接删） | 9 项 | ~450 LOC（含 49+14 转换器对）+ 2 个一次性脚本 |
| 死代码（连带改动） | 3 项 | 其中 2 项建议**保留**（§1b） |
| 重复代码合并 | 8 组 | ~150–250 LOC（非删除型，按收益排序） |
| 死夹具/重复夹具 | 1 字节级重复 + 1 旧信封夹具 | ~2.2 MB 质量 + 连带 3 处断言 |
| E2E 长期门控测试瘦身 | 2 个 fact | ~250 LOC（保留其余 3 个） |
| 文档/注释修订 | 35+ 处 | 精炼为最终事实；含 3 处新发现的 "~393 MB" 过时口径 |
| 未跟踪杂物 | tmp_e2e 等 | ~1.1 GB 磁盘 |

## 1. 死代码 —— 直接删除（均为 H，已复核）

| 位置 | 符号 | 证据 | 动作 |
|---|---|---|---|
| src/Jsr4Z8.Core/Dng/NefToDngConverter.cs（49 行全文，类文档 :6-11 含 "Tasks 2/3" 编号叙事） | NefToDngConverter | 全库仅 DngWriterTests.cs:20-26 等测试消费；产品链为 后端 DNG→JsrDngFinalizer | 删除，处置见 §4（A 移入测试 / B 连测试删） |
| src/Jsr4Z8.Core/Dng/Models/NefToDngResult.cs（14 行） | NefToDngResult | 仅被上者消费 | 随上者删除 |
| src/Jsr4Z8.Core/Dng/DngComposer.cs:42-85 | Compose 5 参数重载 | 仅测试调用方（NefToDngConverter:37、UncompressedPathTests:42）；finalizer 走 :97 options 重载 | 删除；UncompressedPathTests 改用 options 重载 |
| src/Jsr4Z8.Core/Payload/PayloadPacker.cs:100-101,173-181 | ExclusionKind.PrefixSuffix 及匹配分支 | 排除清单 24 条全为 ExactPath/DirectoryPrefix；无 `*` 模式，分支不可达 | 删枚举成员 + case 分支 |
| src/Jsr4Z8.Core/Backend/BackendRequest.cs:69 | Deghost(bool) 内部包装 | 唯一调用点 :51 传常量 false；ProtocolTests:118-119 断言映射本身 | 内联 `"off"`；测试改保 `--deghost off` argv 断言 |
| src/Jsr4Z8.Core/Payload/PayloadPacker.cs:25 | PayloadPackResult.PayloadPath | `.PayloadPath` 全库零读取（构造点 pack-payload/Program.cs:48、PayloadTests.cs:276 均不读） | 删字段（两个构造点同步） |
| src/Jsr4Z8.UI/ViewModels/TaskGroupViewModel.cs:836 | OnPropertyChanged(nameof(IsRunning)) | 无 XAML 绑定（全 axaml 零命中）、无测试订阅；IsRunning 属性（:258）被 BarValue(:263) 使用，**属性保留** | 仅删此行；同组 BarValue:837 通知已覆盖其依赖 |
| tools/probe_dng_variants.py | 全文 | build/tests/文档零引用；ACR 裁剪攻坚已收口（fix 8 回归锁 + AGENT.md §6.1 方法论）；:11 SRC 指向迁移前路径 `samples/JSR_DSC_7576.dng` 已失效；被 finalize-variant 取代 | 删除（留意：cleanup-wave1 曾决定保留并补头注——本稿评估该理由已随攻坚收口失效，交用户裁决） |
| tools/extract-ptc.py | 全文 | 一次性迁移脚本，产物 ptc_z8.csv 已入库；零引用 | 删除（或留并在 AGENT.md 记一行用途） |

### 1b. 连带改动才能删 —— 复核后建议保留（防误删）

| 位置 | 说明 |
|---|---|
| DarkTable.cs:18 DarkMatch.Iso | 测试以它断言查表往返精度（ValidationTests:207、DarkTableTests:100、E2eTests:178）。删除需连带改断言，收益 0 —— 保留 |
| TaskGroupValidator.cs:89 Validate(g) 单参重载 | E2E/单测的"真实读取"接缝（默认 RawMetadataReader.Read），生产走 :93 带缓存读取。保留 |
| ValidationResult 各字段 | 均有消费方，勿动 |

## 2. 重复/冗余代码 —— 合并候选（非删除型）

| 组 | 位置（已核到行） | 建议 | 置信 |
|---|---|---|---|
| LE 字节读取 ×5 份 | NefAssetExtractor:282-289（ReadUInt16/32/32BE）、RawMetadataReader:119-122、DngComposer:247-250（U16/U32）、BackendDngReader:294（U32）、NikonLensCorrection:238-241 | 抽共享 internal LE 助手，~15 LOC + 口径一致 | H |
| 内嵌 CSV 表加载 ×2 份 | DarkTable.cs:36,108 ↔ PtcTable.cs:26,125（ExactTolerance/ReadEmbeddedCsv/DefaultInstance 逐字镜像） | 抽公共加载基类，~30 LOC | H |
| 校验 mock 工厂 ×3 份 | ValidationTests:26-62、TaskGroupCreatorTests:26-64、TaskGroupVmTests:25-50（Meta/Reader 三胞胎，默认值互有出入） | 测试内共享工厂，~60 LOC | M |
| .nef 过滤 ×3 份 | MainViewModel:116、TaskGroupViewModel:457、TaskGroupCreator:43 | **复用既有** NefFiles.IsNef/FilterNefPaths（Views 命名空间，同程序集）——上移到中性命名空间即可，勿造新助手 | M |
| 图标/样式重复 | MainWindow.axaml:19-20,28-32,57 ↔ TaskCardView.axaml:22-23,37,48（Icon.Trash/FolderPlus 两处、Path.icon / Ellipse.dot 样式各异值） | 上移 App.axaml 资源块，~10 LOC | M |
| TIFF IFD 解析 ×3 份 | NefAssetExtractor:172-236、DngComposer:194-245、BackendDngReader:337-378 | 大重构，单独排期 | M→L |
| DngWriter 编码脚手架 ×2 | :187-223（strip）↔ :227-274（tile） | 抽并行编码公共段，谨慎（性能敏感路径，并行调度契约需随迁） | M |
| 摄测字面量散布 | photometric 34892/32803 于 DngComposer:71、BackendDngReader:187、JsrDngFinalizer:81、DngWriteOptions:34、DngWriter:317 | 单一常量来源 | L |
| 芯片/参考帧构造重复 | TaskGroupViewModel:678-729 ↔ TaskGroupCreator:84-118；:286-304 三连 reset | 与上游创建流合并时一并处理；独立收益低 | L |

## 3. 测试与夹具清理

| # | 项 | 置信 | 复核结论 |
|---|---|---|---|
| T1 | fixtures/protocol/success.log | **H** | 与 stdout3.log **逐字节相同**（同 SHA256 `58e941f4…`，FixtureIntegrityTests:23-24 已把二者锁成同一哈希；各 1,035,779 B）。仅 3 个代码引用点（Integrity:24、Fuzz:111,152）可删其一 |
| T2 | fixtures/protocol/stdout2.log（60 KB） | **M→保留** | 旧 Bridge-2.3.25 ASSISTANT_* 信封时代捕获。信封降级为 Unrecognized 的行为**已由合成语料锁死**（ProtocolTests:186-197），但 integrity+Fuzz 各 2 处引用；作为"外来旧流不崩溃"探针成本仅 60 KB——本稿改判**保留**（可与 reserved 以外的删除打包为同一提交） |
| T3 | E2eTests.cs 5 个 fact（562 行） | **H（事实）/M（处置）** | 默认 `JSR4Z8_E2E≠1` 时全体早退、断言为零。**保留为 opt-in 验收栓**（StopMidRun 与中文路径无廉价等价物），建议①执行轮统计口径注明"5 个恒过不做功"；②可瘦身 ~250 LOC：SuccessStdoutCapture（:458，捕获流势能已由 direct_success.log 夹具承载）与 FullRun_HeFrames（:190，最重 ~5.5min 且与 lossless 全跑同构重复） |
| T4 | QueueRunnerTests:118-122 EngineRelativePath 常量 pin | **改判：保留** | 旧稿以"常量断言常量"列为可选删。薄档案落地后它升级为**注入契约防退化锁**之一（路径拼写错 → 引导器剥环境变量 → 静默回退 per-run 自解压），与 BackendRunnerTests:142-160 三元组字符锁、Fact A 锁（:164-）构成新机制的测试三角 |
| T5 | ValidationTests ↔ TaskGroupCreatorTests 同型重叠（~40 LOC） | **L（保留）** | 分层是有意设计（validator 单元 vs creator 管道 vs VM 再校验）。随 mock 工厂合并顺带观察，不单独动 |
| T6 | 夹具依赖环境（**属工具问题，勿删测试**） | — | samples/、libs/ 均 gitignore ⇒ 约 30 个 fact 在新克隆硬失败而非跳过；ReferenceDriftCanary 2 facts 在 CI 真空通过（刻意设计，保留）。修法：CI 备料步骤或 [Trait] 纪律 |

已核实**非死**、勿动：DngSpecInvariants.cs（DngSpecInvariantsTests:43 单调用点）、TestTemp/TestTiff/SampleFiles（多文件引用）、**7 个** stub_*.bat（旧稿写 8，现 7——stub_env 于 2026-10-16 加入、另有 1 个随预解包改造移除；全部在 FixtureIntegrityTests:32-41 锁定）、roi_sample.dng（4 个测试地真值）、backend-dng 与 protocol/direct_*.log。Payload 区无 _MEI 遗留死测试；Fact A 锁（BackendRunnerTests:164）为薄档案新增防回归锁，保留。

## 4. NefToDngConverter 删除的测试连锁（§1 执行注记）

删除转换器会失去 DngSpecInvariantsTests 的第 4 件产品物（"dngwriter-cfa"，:22）。两案：
- **A（推荐）**：NefToDngConverter + NefToDngResult 移入 tests/Jsr4Z8.Tests（internal 化，类文档改一句定性）——产品代码瘦身且 CFA 不变式保真；
- **B**：直接删除，DngSpecInvariantsTests 产品物 4→3（ACR-c、finalizer、writer 直产）——损失一层回归位。

## 5. 文档与注释 —— 修订清单

### README.md

| 处 | 问题 | 处置 |
|---|---|---|
| :40 | "330 MB 量级"过时——实验/探针文档三处实测 358,130,979 B | 改"约 358 MB" |
| :21-22 | 与 :14-15 重复叙述载荷释放（"约 588 MB/仅一次/实测秒级"两遍） | 两处留一 |
| :60-76 | 构建节内嵌裁剪清单叙述 + 探针日期（"实证（探针 2026-10-16）"）等过程性措辞 | 压成 ~4 行：前置 + build.bat 一键 + 自动选版换入；裁剪细节指向排除清单语境（AGENT.md 或代码） |
| **:75（新发现）** | "~393 MB 载荷"过时——薄档案后 payload.bin 实测 **202,208,052 B**（本机磁盘核实，manifest hash `5c78910676d79955` 与 AGENT.md §2 一致） | 改 "~202 MB" |

### AGENT.md

| 处 | 问题 | 处置 |
|---|---|---|
| :59（§5 决策 3） | "HE/HE\* 读噪比无损 +3~7%"与表实算不符——DarkTable.cs:11 为 "HE +5..13%（Lo 档最大）；HE\* −1.5..+3%"。现行行虽已附 Lo 档 6~13% 括注，主口径仍错 | 以 DarkTable.cs 数值口径为准（数值事实唯一来源放代码注释） |
| :74（§6.7） | "reference/(649MB)" 过时——du 实测 **854 MB**；"`~/.nuug`" 笔误 | 改 854 MB、`~/.nuget` |
| :82（§7）,:92（§8） | 以 gitignore 的 `.superpowers/sdd-postrelease-fix*.md` 作权威出处（fix6 审计表、ACR 攻坚全程）——新克隆不存在 | 改自足一句话结论，标注作者本地未入库 |
| :19（§2 表格行） | 单格 340+ 字符塞 4 组尺寸数 + 机制叙述（内容**数值全对**，密度过高） | 拆 2-3 行；尺寸数只保实测值，MiB 混算叙述删去 |
| :21 vs :53 | E2E 门禁规则写两遍（§2 工具行 vs §4 硬契约） | 保留 §4，§2 处改一行指引 |
| :86（§7 末行） | "分支 feat/jsr4z8 已合并回 main"——演变过程性叙述，无操作价值 | 删 |

### docs/superpowers —— 探针/计划文档（本稿修正旧稿三处）

| 文件 | 现状（已核实） | 处置 |
|---|---|---|
| notes/…meipass2-experiment.md §7 | 推荐形态（运行时解包至 `engine-extract\<sha8>`，~15 行）与**已实施形态**（打包期预解包至 `backend\<hash16>`）不一致；§8 为运行账目过程叙事 | §7 顶部加一行"已按 backend\<hash16> 预解包形态实施（AGENT.md §3），本节为当时的候选草案" |
| notes/…thin-archive-probe.md | 头部"结论先行"仍属预估口径（"预计 −191.3 MB → ~202,205,337 B"）；**已实施**（F1 形态） | 顶部加一行"已实施（评估 F1 形态），实测 payload.bin 202,208,052 B"；旧稿"未实施"判断作废 |
| notes/…argv-probe.md :17,:155 | "事实#1""run0"编号指向未跟踪账本 | 改自足表述 |
| plans/2026-10-16-preextract-payload.md、…jobobject-payload-trim.md、**…thin-archive-impl.md（旧稿此处尚无，新文档）** | 三份计划全部执行完毕（对应提交 8868365/2063e55/238f203/bca355c、d2d26e3/078645d、JobObject 落地），checkbox 仍 0/10、0/7、0/12 全 `[ ]`，且 preextract 计划内含过时的 ~200MB 预估 | **删除三份已执行计划**（结局已在 AGENT.md/notes 留痕），或统一加 "[DONE] 2026-10-16 全部完成" 并把预估改实测数 |

### 代码注释（过时机制 / 过程叙事 / 死引用）

| 位置 | 问题 | 处置 |
|---|---|---|
| Backend/BackendRunner.cs:19-20,102-104,152-153,161-164 | 注入直跑=单实例已是现实（AGENT.md §4.47 实测"单 pid 贯穿"），但类文档与行注仍以现在时叙述"每次 invocation 自生同参子进程 ~2s + 无名孙进程、等待父子转述退出码"——与同文档 :72 "single instance" 自相矛盾 | 重写为：注入直跑=单实例；taskkill /T 为**防御性保留**（无注入回归态才出现双实例） |
| Payload/PayloadPacker.cs:27-61（+新发现 **:32**） | 35 行类文档混排裁剪清单/裁定日期/探针编号；**:32 "the real ~393 MB payload" 过时**（现 ~202 MB）；:107 "Ruling per entry:" 冒号悬空 | ::54-61 载荷布局契约保留；其余压 ~8 行（"为何预解包/排除什么"两段一句概括，细则指向 ExcludedPayloadEntries 与探针文档日期行） |
| Payload/PayloadManager.cs:5-30 | 26 行行为长文与 EnsureExtracted/VerifyExisting/PruneStaleVersions 行内注释三重复述 | 压 ~10 行（保留不变式：自愈、原子性、重试、prune 尽力而为） |
| DngWriteOptions.cs:52-53 | "(probe fact #9 …)" 死引用编号 | 改自足："finalizer 写 Software=\"JSR for Z8\"（IFD0 无后端署名）" |
| BackendRequest.cs:22、DngWriter.cs:569、JsrDngFinalizer.cs:15 等约 18 处 | "probe fact #N / probe §N" 编号（账本在探针文档） | 保留指向 docs/superpowers 的**具名文档引用**（它们已入库），删不可解的裸编号；或统一改为"见 <文档名> §N" |
| tests/*.cs 约 40 处 | "Task N / post-release fix N / finding 8 / 2026-10-XX 裁定" 编号叙事 | 删序号与日期，留不变式本体。代表位：BackendRunnerTests:8,11、DngWriterTests:9,:178、FinalizerTests:10,56,74,89,217,253、DngSpecInvariants.cs:21,204、E2eTests:277,347,407,481、FinalizerCrossCheckWarnTests:78、PayloadTests 等（grep "Task [0-9]｜fix [0-9]｜probe" 共 75 处命中，逐条裁） |
| E2eTests.cs:67 | `out\Jsr4Z8_v1.0.0.exe` 版本错（实际 0.1.0，AGENT.md §2 明示"用户指定不用 1.x"） | 改 `v<版本>` 占位 |
| E2eTests.cs:407,481-482 | "Task 3" 编排叙事 | 一句话事实化 |
| NativeSmokeTests.cs:7 | "Facts measured 2026-10-13 on samples/DSC_7576.NEF"——旧路径（现 samples/test_frames/lossless/） | 改现路径 |
| Dng/NefToDngConverter.cs:6-11 | "metadata + assets from Tasks 2/3" 编号叙事 | 随 §4 A 案移测试时一并一句定性 |
| native/dng_ljpeg_wrapper.cpp:126-127 | "Task 9 uses this to read backend DNG strips…" 编号叙事 | 改"finalizer 条带解码使用" |
| tools/pack-payload/EnginePreextractor.cs:10-11（新发现） | 引证 gitignore 的 `.superpowers/sdd/…/ws/extract_carchive.py` 作归属——新克隆不存在 | 删工作区引用，保留已入库证据 docs/superpowers/notes/2026-10-16-meipass2-experiment.md §2 |

### build.bat
- **:15、:142（新发现）**："~393 MB backend payload" ×2 过时 → 202 MB。
- B1（旧稿行，仍成立）：头注释与 README/AGENT 三处复述 Step4 行为——以 build.bat 为操作性唯一所有权，README 保持一行指引。
- :123 与 :130 连续两个 `cd /d %ROOT%`（之间无换目录动作）可去一个（H）。:139 需保留（dotnet run 会改变 cwd）。
- 全库 TODO|FIXME|HACK|XXX = 0；>3 行注释掉的代码块 = 0（已逐一核查）。

### 事实唯一所有权建议（消除三重复述）
载荷架构与尺寸 → AGENT.md §2/§3/§4；UI 规则 → AGENT.md §5.4；裁剪清单 → `PayloadPacker.ExcludedPayloadEntries` 注释；HE/HE\* 噪声口径 → DarkTable.cs 类文档（只此一处数值）；_PYI_ 契约 → AGENT.md §4 + BackendRunner 两条短注释 + 三把测试锁；载荷/输出尺寸数字在每个面上只出现一次。

## 6. 未跟踪杂物（仅磁盘，无代码影响；已实测）

| 目录 | 大小 | 说明 | 处置 |
|---|---|---|---|
| tmp_e2e/ | **1,067 MB** | E2E 证据/log（E2eTests 写入，tmp* 已 gitignore） | 可删；E2E 会再生成 |
| tmp_task3/ | <1 MB | 历史构建/运行日志 | 可删 |
| .probe/ | 空目录 | — | 可删 |
| .superpowers/ | ~4.7k 文件 | superpowers 会话工件，gitignore | **harness 会话保留品**：AGENT.md §7/§8 曾引用。建议用户裁决：验收归档后删，或仅留 docs 侧结论 |
| out/Jsr4Z8_v0.1.0.exe | 238 MB | 旧版本发布物（gitignore） | 可删；build.bat 可再生 |

## 7. 复核后否决的候选（防误删）

| 候选 | 否决理由 |
|---|---|
| JsrStart.Stage/Engine/Mode 删字段 | BackendRunnerTests:58-62 按字面断言这三个线规字段 |
| ProtocolParser Log/Unrecognized `.Text` 删 | 产品在 TaskQueueRunner:184 映射消费 |
| DarkMatch.Iso 删 | 测试往返精度断言依赖（§1b） |
| TaskGroupCreator.CreateAsync/Create、MainViewModel.AddValidated 删 | 代码自注释其为"种子/测试创建路径"（TaskGroupCreator.cs），生产走拖放流+卡片自校验——刻意接缝，非遗留 |
| DngSpecInvariants.cs / TestTemp / TestTiff 当"脚手架死文件" | 各有活跃调用点 |
| BackendRunnerTests Fact A 锁、"stub dual" 套件当"过时双实例测试" | 防退化需要：无注入回归态正以双实例形态出现，dual stub 是唯一的现成回归载体 |
| ReferenceDriftCanaryTests 删 | 刻意真空通过设计（本地咬合、CI 静默） |
| native 导出、UI XAML/命令/属性 | 全量核对：17 libraw 导出 + 3 lje 导出全有 P/Invoke 消费；[RelayCommand]/[ObservableProperty] 与绑定解析一致；静态资源键皆在用 |

## 8. 建议执行顺序（待批准后实施）

1. **波次 A（零风险，1 提交）**：§1 全部直接删除项 + T1 重复夹具(删 success.log 与 3 处断言行) + build.bat/:15/:142 尺寸一行改 + 重复 `cd` 行。验证门：`build.bat` 全绿 + `dotnet test` 全绿（E2E/Canary 环境件除外，套数需与基线一致——执行时以当日全量为准，AGENT.md 现口径 285/285）。
2. **波次 B（纯文档/注释，1 提交）**：§5 全部修订（README/AGENT/notes+plans/代码注释/build.bat），计划文档三选一（删或 [DONE]）。
3. **波次 C（合并重构，单独提交）**：LE 助手、CSV 表基类、mock 工厂三件小合并；IGFD 三合一与 DngWriter 脚手架分立排期或明确放弃。
4. **波次 D（须先定夺）**：NefToDngConverter A/B 案（§4）、probe_dng_variants.py 删除（与 wave1 决定相逆）与 extract-ptc.py、.superpowers/ 去留、tmp_e2e 清理。
