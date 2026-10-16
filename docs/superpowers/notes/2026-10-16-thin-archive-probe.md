# 薄档案（thin archive）可行性探针：零填充体实证与压缩测量（2026-10-16）

> **已实施**（本评估的 F1 形态）：打包期预解包+引擎本体薄拷贝已上线，实测 payload.bin 202,208,052 B（AGENT.md §2/§3）；下文为评估期口径。

> 探针对象：现行线上载荷释放树 `%LOCALAPPDATA%\JSR4Z8\backend\9382ed09d9739092\resources\jsr_backend_runtime\`
> （1,417 文件＝1,416 个 CArchive b 文件 + 引擎本体；树内 `jsr_backend.exe` 201,243,142 B，
> sha256 `5e315a17…`——与 meipass2 实验同体）。提取树全程只读（每跑前后清点 1,417 文件 / 587,339,873 B 不变）。
> 工作区（脚本+日志+零填充拷贝，未提交）：`.superpowers/sdd/2026-10-16-thin-archive/ws/`。
> 结论先行：**可行（F1/E2 双绿）。子模式（`_PYI_*` 三元组）下引导器从档案文件读取的活体集 = stub+TOC+cookie+`z/m/s` 条目体；
> 1,416 个 `b` 条目体（192,322,504 B，占 exe 95.6%）从未被读——整段清零后 60s 冒烟与完整 7 帧无损跑全绿
> （exit 0 / `DONE total 332.4s` / DNG 358,130,979 B / result.json `COMPLETE` / 全程零 `_MEI` / 单实例）。
> 零填充 exe 经我们同款 DEFLATE 压至 ~8.8 MB：**payload.bin 预估 −191.3 MB（393,487,154 → ~202,205,337 B，−48.6%）**。**
> 原始假设「只读 stub+TOC+cookie」被证伪（`z/m/s` 体必读）；另发现 side-load 通道的档案路径必须等于运行 exe 自身路径。

## 1. 方法

### 1.1 零填充拷贝构造（`ws/build_thin.py`，只读解析 LIVE 树引擎 exe）

TOC 解析逐逻辑对齐 `tools/pack-payload/EnginePreextractor.cs`（cookie `MEI\x0c\x0b\x0a\x0b\x0e` 末 8 KB 末次出现、
88 B 大端 cookie、`archiveStart = cookie 末 − pkgLen`、TOC 恰止于 cookie、18 B 大端条目头 + NUL 名）。对账全部命中已知事实：

| 项 | 值 |
|---|---|
| archiveStart / cookie / TOC | 347,648（0x54E00）/ 201,243,054（文件末 88 B）/ [201,160,798, +82,256) |
| 条目 | 1,434 = 5 `m` + 11 `s` + 1,416 `b` + 1 `z` + 1 `o` |
| 体区间覆盖 | 条目体恰好铺满 [archiveStart, tocStart)＝200,813,150 B，**零间隙**（无对齐填充残留） |
| `b` 体合计 | csize Σ=192,322,504 / usize Σ=386,096,731 ✓（meipass2 §2 同值） |
| `z`（PYZ.pyz）/ `m`+`s` 体 | 8,459,795 / 14,140+16,711 |

两种形态（长度均不变＝201,243,142 B，所有 TOC pos/csize 偏移保持有效；stub 头/TOC/cookie 原样；逐区间清零后自校验
「体区间外逐字节相同、体内全零」通过）：

- **thin-all**（E1 决定性形态）：全部条目体清零（200,813,150 B；`o` 类 csize=0 无体）。sha256 `54658a56…`。
- **thin-b**（回退形态 F1，最终胜出）：仅 `b` 类体清零（192,322,504 B）；保留 `z/m/s` 体 8,490,646 B。sha256 `b0ddafd0…`。

### 1.2 冒烟/完整跑协议（meipass2 §3 已验证配方）

argv 契约逐字（argv-probe §2/§5/§7）：7×`--raw-frame D:\…\lossless\DSC_7576..7582.NEF`、`--read-noise-dn 14.07
--black-level-dn 1008 --full-well-electrons 1102.2`，其余开关按观测值硬编码；新鲜输出目录；环境三元组在**子 cmd 链内**
`set`（`_PYI_ARCHIVE_FILE=<档案> && _PYI_APPLICATION_HOME_DIR=<LIVE 树> && _PYI_PARENT_PROCESS_LEVEL=0`——规避
PS 5.1 `-Redirect*` 丢环境陷阱）；PS `Start-Process -PassThru` 持 cmd 根 pid；1s 轮询（%TEMP% `_MEI*` 新增、
`jsr_backend` 实例数、stdout 行数/尾行）；60s 处 `taskkill /PID <根> /T /F` + tasklist 复核归零；每跑前后树清点。
冒烟绿判据：60s 存活、推进至 `SRHQ_V12_PROGRESS|≥39|`（解码/对齐/恢复段）、newMei=0、单实例。

## 2. E1 结果（决定性冒烟 + 机制发现）

| 跑 | 形态 | 结果 | 判定 |
|---|---|---|---|
| e1a | 真 exe 直跑 + `_PYI_ARCHIVE_FILE`=**工作区** thin-all（外来档案） | 60s 存活推进至 `\|40\|`，**但 t=1s 新 `_MEI260442`、t=3s 起双实例**＝父路径完整自解压（泄漏目录已清理） | ✗ 外来档案不生效（见机制事实 #2） |
| e1b | **thin-all 自身作为 exe**（档案=自身，home=LIVE 树） | t=1s 即死：stderr `[PYI-30784:ERROR] Failed to extract struct: decompression resulted in return code -3!`（zlib `Z_DATA_ERROR`），exit 0xC0000409；**newMei=0、无解压**＝子模式确已进入 | ✗ **证伪「只读 stub+TOC+cookie」**——引导器至少读 `m/s` 引导体（错误消息里的 `struct` 即首个 `m` 条目名） |
| f1 | **thin-b 自身作为 exe**（同上） | **60s 全绿**：newMei=0×60 轮、单实例、t=1s 即出首条进度（12 行）、60s 推进至 `\|40\|Transformer 恢复`（与控制组同构）、stderr 0 B、树 1,417/587,339,873 不变、树杀后 0 残留 | ✓ **`b` 体（192.3 MB）从未被读；最小活集 = `z/m/s` 体 + stub+TOC+cookie** |

**机制事实 #2（本轮新发现，修正 meipass2 §1.2 复原的不完备处）**：`_PYI_ARCHIVE_FILE` 指向**非运行 exe 自身**的文件时不被采纳——
引导器按「未设置」处置（复位块：archive=自身 + 剥除另两个变量）→ 父路径从**自身真实档案**完整解压。证据链：
① 环境确达（`ws/test-env.ps1` 同型 spawn 链 dump 出完整三元组）；② e1a 若真读了 thin-all（体全零）则引导必崩，实际却从真档案解压成功；
③ e1b/f1 档案=自身时子模式立即进入（newMei=0）。精确校验规则（字符串相等 vs 解析路径相等）未逆向——对部署契约无影响
（我们恒指向树内 exe＝自身）。**部署形态因此必须是「薄 exe 放树根、三元组指向它自身」**，本实验 e1b/f1/E2 即此形态。

## 3. E2 完整运行契约（thin-b 形态，单跑）

完整 7 帧无损直调（不建 cfg、不跑预览、不经 Bridge），阻塞等待自然退出。一次监视器事故（§8）不影响证据完整性：
原 cmd 根 pid 25196 的内核句柄（`SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION`）上恢复等待并取回真实退出码。

| 证据 | 值 |
|---|---|
| 退出码（cmd 转述，句柄等待后读取） | **0** |
| 墙钟 / 引擎自报 | 332.9s（17:32:54.6→17:38:27.5）/ `DONE total 332.4s` |
| stdout | 324 行（与历次契约跑同数）；`SRHQ_V12_PROGRESS\|100\|` 恰一次；尾三行 `…\|100\|导出完成` → `V12 output: <dir>` → `DONE total 332.4s` |
| stderr | 0 B |
| DNG | `e2thin_V15_3_5_K7_20261016_173254_962806.dng`，**358,130,979 B**（与 argv-probe run1 / meipass2 e5 逐次同尺寸） |
| result.json | `辅助文件\result.json` → `"status": "COMPLETE"` |
| `_MEI` | 全程 333s **零新增**（原监视器 t=0..95s + 恢复监视器 t≈95..180s 双段覆盖，maxMei=0） |
| 实例数 | 恒 1（maxEngine=1）；退出后 `jsr_backend` 归零 |
| LIVE 树 | 跑前后 1,417 文件 / 587,339,873 B 不变 |

## 4. E3 离线压缩测量（估计口径，非 .NET 实测）

载荷打包器每文件体 = 原始 DEFLATE（`DeflateStream` `CompressionLevel.Optimal`，无包装）。以 python zlib raw-deflate
（wbits=-15）level 6（≈.NET Optimal/zlib 默认级）为主口径、level 9 为上界括号：

| 对象 | level 6 | level 9 |
|---|---:|---:|
| 原 exe（201,243,142 B） | 200,095,047 B | 200,093,899 B |
| **thin-b exe** | **8,813,230 B** | 8,812,045 B |
| thin-all exe（不可用，仅参照） | 420,282 B | 419,267 B |

原 exe 几乎不可压（内嵌体本就是 zlib 流，−0.6%）与 AGENT.md 记账一致（实际打包 190.8 MiB ≈ 本估计 200,095,047 B，偏差 0.02%）。
**预估节省（thin-b）= 191,281,817 B**：

| 项 | 现行（AGENT.md 实测） | 薄档案预估 |
|---|---:|---:|
| exe 条目在 payload.bin 中 | ~200.1 MB（190.8 MiB） | **~8.8 MB** |
| payload.bin | 393,487,154 B（375.3 MiB） | **~202,205,337 B（192.8 MiB，−48.6%）** |
| 单文件发布 exe | 440,567,896 B | **~249,286,079 B（237.8 MiB，−43.4%）** |

注意：节省全在**载荷/分发面**；盘上提取树的 exe 文件长度不变（201,243,142 B，零字节不自动稀疏）。

## 5. E4 风险登记（仅分析）

1. **AV/启发式面（低-中）**：薄 exe 的 PE 代码节（stub）与现发行字节相同，被清零的是 CArchive 覆盖层数据区；
   打包面 payload.bin 本就是 DEFLATE 容器，AV 视角容器形态不变，且解压后内容熵大幅下降、无新增可执行内容。
   残余风险：个别启发式引擎校验 PE 覆盖层一致性可能报「损坏」。缓解（若立项）：发布链 AV 扫描门禁（现状同款要求）。
2. **PyInstaller 升级耦合（主风险，与 `_PYI_*` 注入同契约族）**：新版本引导器若改读 `b` 体或更名环境变量，薄档案失效。
   失败形态**响亮且即时**（e1b 实测签名：`[PYI-xxxx:ERROR] Failed to extract …` + exit 0xC0000409，spawn 即死，非静默错果）。
   缓解：载荷升级链注入冒烟（20-60s + %TEMP% 无新 `_MEI` 断言），与 meipass2 §7 建议合并为同一条门禁。
3. **manifest 完整性/披露**：变换发生在**打包期**（确定性：同 exe → 同薄字节），我们的 payload manifest 哈希覆盖的是
   **变换后**文件——完整性链不断，但树内 exe ≠ 上游发行字节。披露：打包管线代码注释 + 发布说明一行（与预解包变换同款披露）；
   `jsr_backend_manifest.json` 的 `backend_sha256` 仍指上游原体，两个哈希并存、各注其义。
4. **与注入契约的强耦合（真实约束）**：薄 exe 只在子模式（三元组生效）下成立。若任何路径以**无三元组**方式 spawn 树内 exe，
   引导器走父路径 → 对零填充 `b` 体解压 → PYI-ERROR 崩（fail-fast，无静默错果，但失去「无注入回退自解压」模式）。
   现行预解包设计三元组已是标准 spawn 形态，成立；若未来要保留无注入回退，需另行携带完整 exe（放弃节省）。
5. **参考树/线上树零触碰**：本轮全程只读（每跑清点不变）；薄拷贝只存在于工作区；实施形态在打包期变换（同预解包）。
6. **盘面无收益**：提取树磁盘占用不变；如需盘面收益可另议稀疏文件（出本实验范围，仅记录）。

## 6. 结论与推荐实施形态

**可行，把握度高（E1bisect + F1 冒烟 + E2 完整契约三层实证）。** 推荐形态（打包期，`EnginePreextractor` 管线内）：

1. 预解包（现步骤 1-4 不变）后，把「exe 本体逐字节拷贝进树根」（现步骤 5）替换为**薄拷贝**：同一 TOC 解析结果上，
   对每个 `b` 条目把 `[archiveStart+pos, +csize)` 清零写入树根 `jsr_backend.exe`（stub/TOC/cookie/`z/m/s` 体原样）；
   自校验：体区间覆盖铺满（本轮实测零间隙）+ 长度不变。
2. 运行时契约**零改动**：spawn 树根 exe + 三元组（`_PYI_ARCHIVE_FILE`=树内 exe 自身路径——机制事实 #2 要求相等）。
3. 门禁：`PayloadTests` 期望尺寸/条目更新；升级链冒烟断言（无新 `_MEI` + 20s 内出进度）；披露注释（§5.3）。
4. 预期收益：payload.bin 393,487,154 → ~202,205,337 B（−48.6%）；单文件发布 440.6 → ~249.3 MB（估计口径见 §4）。

**已实施（2026-10-16，feat/thin-archive）**：EnginePreextractor 打包期薄拷贝上线并实测——payload.bin 393,487,154 → **202,208,052 B**（−48.6%）、发布 exe 440,567,896 → **249,288,792 B**（−43.4%）、树内 exe sha256 `b0ddafd0…`（=§1.1 thin-b 同体，长度 201,243,142 不变）；完整 7 帧契约跑绿（exit 0/DNG 358,130,979 B/result.json COMPLETE/零 `_MEI`/单实例/树不变）；无三变量裸 spawn 实测 <1s 即死：`[PYI-…:ERROR] Failed to extract MSVCP140.dll: decompression resulted in return code -3!`、退出码 −1、遗留一个含 0 字节文件的 `_MEI` 目录（已清理）——§5.4 推断闭合（退出码为 −1 非 e1b 的 0xC0000409：失败点在引导期 C 级解压，早于 Python fail-fast）。

## 7. 失败模式速查（实测签名）

| 症状 | 根因 | 实测 |
|---|---|---|
| spawn 即死，stderr `[PYI-xxxx:ERROR] Failed to extract <名>: decompression resulted in return code -3!`，exit 0xC0000409 | 活体（`z/m/s`）被清零或引导器改读 `b` 体 | e1b |
| 每跑新 `_MEI` + 双实例、运行本身正常 | 三元组未生效或 `_PYI_ARCHIVE_FILE` ≠ 自身路径（复位→父路径自解压） | e1a（泄漏目录需清理，本轮即清） |
| 完整管线中途 PYI-ERROR | 引导器晚期改读某 `b` 体（未观测；仅理论） | —（60s+完整跑双绿覆盖全管线段） |

## 8. GPU/运行账目（诚实记账）

引擎共 4 次真机跑：e1a（60s，外来档案形态——被机制事实 #2 作废，但产出了该事实本身）、e1b（<1s，自灭）、
f1（60s 冒烟绿）、e2-full（333s 完整契约跑，用户授权）。合计 ≈454s，较预算（1-2 冒烟+1 全跑）多一次冒烟：
e1a 的形态缺陷源于 side-load 相等性约束是新发现（类比 meipass2 注 A/B 的基础设施学费，本轮已实证固化）。
事故记录：e2 原生监视器在 t≈95s 被 WSL 侧管道 `head` 截断 SIGPIPE 杀死（引擎不受影响——spawn 是分离的）；
恢复监视器以内核句柄等待原 cmd pid 并取回真实退出码 0，契约证据完整（§3）。所有跑树杀/自然退出后 `jsr_backend`
归零复核通过；本轮唯一泄漏 `_MEI260442`（e1a）当场清理，末态 %TEMP% `_MEI*` 计数 0。
