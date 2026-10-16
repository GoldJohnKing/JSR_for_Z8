# _MEIPASS2 持久复用实验：机制发现与可行性结论（2026-10-16）

> 探针对象：现行线上载荷 2.3.27 裁剪版（`%LOCALAPPDATA%\JSR4Z8\backend\8a7b40ecde4ff7f4\`，28 文件，
> `jsr_backend.exe` 201,243,142 B，提取目录零触碰——引擎全程原地直调，事后复核仍 28 文件）。
> 工作区（脚本+日志+持久目录，未提交）：`.superpowers/sdd/2026-10-16-meipass2-experiment/ws/`。
> 结论先行：**可行（高把握）。本构建 bootloader 是 PyInstaller 6.x，`_MEIPASS2` 字面量已不存在；
> 真正的父→子复活契约是 `_PYI_ARCHIVE_FILE + _PYI_APPLICATION_HOME_DIR + _PYI_PARENT_PROCESS_LEVEL` 三元组
> （三者缺一不可，缺 `_PYI_ARCHIVE_FILE` 时引导器会主动 unsetenv 另两个——这是单/双变量注入全部失败的根因）。
> 注入三元组后：引擎单实例直跑、全程零 `_MEI`、持久目录对 1,416 文件零写入、完整 7 帧无损直调契约全绿（exit 0 / `DONE total 328.4s` / result.json `COMPLETE` / DNG 358,130,979 B）。**

## 1. E0 机制发现（bootloader stub 静态枚举 + 反汇编）

方法：取 exe 头部未压缩区（PE 镜像 = 0x54E00 = 347,648 B，CArchive 覆盖层之前）做 ASCII/UTF-16LE 字符串枚举
（`ws/e0_strings.py`，产物 `ws/e0_out/`），随后 objdump 反汇编 `.text` 定位环境变量名的全部引用点并人工复原决策树
（`ws/stub.asm`）。

### 1.1 字符串事实（文件偏移 = .rdata raw 0x2E000 起）

| 偏移 | 字符串 | 语义 |
|---|---|---|
| 0x2fa18 | `_PYI_ARCHIVE_FILE` | PKG 档案路径（父为子设置；外部设置 = side-load 通道） |
| 0x2fa30 | `_PYI_APPLICATION_HOME_DIR` | 应用主目录（onefile 下 = 解压目录；子实例运行根） |
| 0x2fa50 | `_PYI_PARENT_PROCESS_LEVEL` | 进程层级（合法值 −2/0/1，非法即致命退出） |
| 0x2fa70 | `_PYI_SPLASH_IPC` | splash 通道（本载荷无 splash 条目，不参与） |
| 0x2f9f8 | `PYINSTALLER_RESET_ENVIRONMENT` | =1 时复位引导环境（我们未设置） |
| 0x2fb18 | `PYINSTALLER_STRICT_UNPACK_MODE` | 严格解包校验开关 |
| 0x2fd00/0x2fd18/0x2fd30 | `pyi-runtime-tmpdir` / `pyi-contents-directory` / `pyi-hide-console` | TOC `o` 选项名 |
| 0x308f8 | `_MEIPASS`（×2，含 0x308de） | **Python 侧 `sys._MEIPASS` 对象名，仅此而已** |
| 0x313f0(wide) | `_MEI%d` | 临时目录命名模板（观测名 `_MEI<pid>2`） |

**`_MEIPASS2` 在整个 PE 镜像（347,648 B）中 ASCII 与 UTF-16 双零命中**——上一轮全文件 grep 的「_MEI 2 ASCII + 1 wide」
实为上表 `_MEIPASS` ×2 与 wide `_MEI%d`。PyInstaller 6.x 已删除 `_MEIPASS2` 字面量（配套错误文案也只余
`_PYI_APPLICATION_HOME_DIR environment variable is not defined!` 等），经典注入通道在本构建上**结构性不存在**。

### 1.2 决策树复原（VA 引用，pyi_getenv/setenv/unsetenv = 0x140009660/0x140009770/0x1400097D0）

```
启动（~0x1400041F9 起）：
  getenv(PYINSTALLER_RESET_ENVIRONMENT) 并 unsetenv 之；若值=="1" → 走复位块
  getenv(_PYI_ARCHIVE_FILE)：
    未设置 → 0x140004269 复位块：setenv(_PYI_ARCHIVE_FILE=<exe 自身路径>) 并
             unsetenv(_PYI_APPLICATION_HOME_DIR / _PYI_PARENT_PROCESS_LEVEL / _PYI_SPLASH_IPC)  ← 关键陷阱
    已设置 → 其值拷入档案路径字段（side-load：从外部文件读 PKG）
  解析 _PYI_PARENT_PROCESS_LEVEL（strtol，endptr 必须干净）：
    未设置 → level=-2（顶层父）；"0" → level=0（子）；"1" → level=1（孙/嵌套）；其他 → 致命退出
  level=-2 且 onefile → suffix=0（父角色）；level=0 → suffix=1；level=1 → suffix=2
  （父角色随后 setenv(_PYI_PARENT_PROCESS_LEVEL="0") 供其子继承）

主决策（0x14000465C）：
  非	onefile → 盘上目录路径（含 pyi-contents-directory 拼接，本构建不走）
  onefile 且 suffix==0 → 父路径：GetTempPath+_MEI<pid><lvl>2 → zlib 解包 1,416 个 b 条目 →
      setenv(_PYI_APPLICATION_HOME_DIR=_MEI) → 同 argv spawn 子 → 等待 → 优雅退出时递归删除 _MEI
  onefile 且 suffix!=0 → 子路径：getenv(_PYI_APPLICATION_HOME_DIR)（缺失/空 = 致命）→
      app_home = env 值 → 本进程内直接初始化 Python 运行（不解包、不 spawn、不清理）
```

**推论（E1 实测全部证实）**：
1. 只注入 `_PYI_APPLICATION_HOME_DIR`（历史 `_MEIPASS2` 玩法的 6.x 等价物）**无效**——`_PYI_ARCHIVE_FILE` 缺席触发复位块，把它 unset 掉了，进程照走父路径。
2. 注入 `_PYI_APPLICATION_HOME_DIR + _PYI_PARENT_PROCESS_LEVEL` 二元组**同样无效**——复位块两个一起剥。
3. **三元组 `_PYI_ARCHIVE_FILE=<exe> + _PYI_APPLICATION_HOME_DIR=<持久目录> + _PYI_PARENT_PROCESS_LEVEL=0` 有效**——进程完整伪装成正常父子契约里的子实例：零解压、单实例、不清理（它从不拥有该目录）。

## 2. CArchive 解包器与持久目录（对账）

自写解包器 `ws/extract_carchive.py`（约 80 行，纯 stdlib）按 round-5 探针事实重建：
cookie `MEI\x0c\x0b\x0a\x0b\x0e`（EOF 前 8 KB 内，大端字段 pkgLen/tocOff/tocLen/pyver=310/pylib=python310.dll）；
archive_start = cookie 尾 − pkgLen = **0x54E00**（TOC 恰结束于 cookie 起，自洽校验通过）；
TOC 条目 18 B 头（structlen/pos/csize/usize/cmprsFlag/typechar + NUL 名，**名内分隔符为 `\`，Windows 构建**）。

| 对账项 | 探针基线 | 本次实测 |
|---|---|---|
| TOC 条目总数 | 1,434 | 1,434（5m + 11s + **1,416b** + 1z + 1o）✓ |
| b 条目未压缩总字节 | 386,096,731 | 386,096,731 ✓ |
| 解包产物 | — | 1,416 文件 / 386,096,731 B（逐文件 zlib 校验长度）✓ |
| 与引导器自解压比对 | — | 与历史泄漏 `_MEI247362` **全 1,416 文件 sha256 逐一相同、布局逐一相同**（`ws/reconcile_hist.py`）✓ |

持久目录：`ws\meipass-persist\5e315a17\`（**内容寻址**：exe sha256 前 8 位；`ws/caextract-5e315a17.json` 存 cookie/TOC/全量 manifest）。
运行时布局注意：本构建 `pyi-contents-directory _internal` 选项**不参与 onefile 解包布局**——1,416 文件平铺 MEI 根（历史目录亦然），m/s/z 条目留在 exe 内由引导器内存读取。

## 3. E1 注入跳过判定（逐候选）

冒烟协议：直调 argv（argv-probe §2/§5/§7 契约；7×`--raw-frame` 无损帧 `samples\test_frames\lossless\DSC_7576-7582.NEF`，
RN=14.07 BL=1008 FW=1102.2，其余开关按观测值硬编码；新鲜输出目录；1s 轮询 %TEMP% `_MEI*` 新增 + 引擎实例数 + stdout 尾行）。
判定：新 `_MEI` 出现 = 跳过失败；stdout 推进至解码/对齐 = 运行正常。

| 跑 | 注入 | 结果 | 判定 |
|---|---|---|---|
| e1-c1 | `_PYI_APPLICATION_HOME_DIR` 单独 | t≈1s 新 `_MEI322562`，t=2s 起双实例，推进正常 | ✗（实为无效注入，见注 A） |
| e1-c2 | 二元组（home+level=1） | 同上：新 `_MEI261042`，双实例 | ✗（无效注入，注 A） |
| e1-c2b | 二元组（home+level=0） | 同上：新 `_MEI241562`，双实例 | ✗（无效注入，注 B） |
| e1-c2c | 二元组（home+level=0，cmd 链内 set，环境确达引擎） | 仍新 `_MEI333162`，双实例 | ✗（**有效注入被 1.2 复位块剥除**——静态/动态互证） |
| **e1-c3** | **三元组（archive+home+level=0）** | **60s 全程 newMei=0；单实例 [31560]；t=1s 即出首条进度（正常跑 t=3s）；60s 推进至 `SRHQ_V12_PROGRESS\|40`；树杀后 0 残留；持久目录前后 1416/386,096,731 不变** | **✓ 跳过成立** |

注 A（引号污染）：WSL→cmd→`powershell -File` 传参时 `-EnvMap` 值里嵌的 `"` 进入 PS 字符串，拆分出的变量名带引号前缀（引擎根本看不到合法名）——c1/c2 实为无注入对照组（行为与 16/16 历史观测一致：解压+双实例）。
注 B（PS 5.1 Start-Process 环境丢失）：同脚本内 `Set-Item Env:` 后带 `-RedirectStandardOutput/Error` 的 Start-Process **丢掉启动后新设的环境变量**（继承原 50 个、恰好丢新设 3 个；不带 -Redirect* 的同款 spawn 则正常传递——`ws/envtest*.ps1`/`ws/envdiff.ps1` 双向隔离实验）。修复：环境变量改在子 cmd 链内 `set X=...&& engine ... > log` 设置，引擎退出码取 cmd 退出码。

副产品观测：注入成功后**每次调用只剩一个引擎实例**（正常流程父 spawn 子双实例消失——父路径被整体跳过）；首条进度提前 ~2s（不再解压 386 MB）。

## 4. E4 硬杀残留 + 复用

- e1-c3（注入模式）60s 处 `taskkill /PID <root> /T /F`：3 秒内 `jsr_backend.exe` 归零（post-kill engineprocs=0）。
- **%TEMP% 本跑零新增 `_MEI`**（本来就没建）；持久目录完好（1416/386,096,731）。
- 复用冒烟 e4-reuse（同一持久目录、同三元组、20s 后树杀）：t=1s 首条进度、推进至全局配准、newMei=0、单实例、杀后 0 残留、目录仍完好——**硬杀后持久目录立即可复用，无需任何修复动作**。
- 对照：注入失败跑（c1/c2/c2b/c2c）被杀后各泄漏一个完整 `_MEI`（386 MB）——现行线上行为的复现；本轮 4 个泄漏目录已清理（历史 10 个非本任务产物未动）。

## 5. E5/E2 完整优雅运行（单跑双证：契约 + 清理/写入语义）

注入三元组完整直调 7 帧无损（引擎原地、不建 cfg、不跑预览、不经 Bridge），等待自然退出：

| 证据 | 值 |
|---|---|
| 墙钟 / 引擎自报 | 329.4s / `DONE total 328.4s`（argv-probe §7 run1 对照：342.3s / 339.2s） |
| 退出码 | **0**（cmd 转述引擎退出码） |
| stdout | 324 行（=run1 行数）；首行 `PTC {"selection": "manual_fullwell", …}`；尾三行 `SRHQ_V12_PROGRESS\|100\|导出完成` → `V12 output: <dir>` → `DONE total 328.4s` |
| stderr | 0 字节 |
| DNG | `e5full_V15_3_5_K7_20261016_114844_543552.dng`，**358,130,979 B（与 run1 逐字节同尺寸）**，TIFF 头合法（`II*\0`） |
| result.json | `辅助文件\result.json` → `"status": "COMPLETE"`（V15.3.5，outputs.off.dng schema `JSR_V1242_LINEAR_DNG16_V3`） |
| `_MEI` | 全程 329s **零新增**（meiTotal 恒 14 = 历史 10 + 本轮已清 4 前） |
| 优雅退出清理语义 | 无 `_MEI` 可清、持久目录**不会**被引导器删除（它从不拥有该目录；对照：正常父路径退出时递归删自建 `_MEI`） |

**E2 写入语义（持久目录前后全量对比，`ws/persist-{before,after}-full.json`，1,416 文件 size+mtime_ns 清单）**：

| 项 | 跑前 | 跑后 |
|---|---|---|
| 文件数 / 总字节 | 1,416 / 386,096,731 | 1,416 / 386,096,731 |
| 新增 / 删除 / size 或 mtime 变化 | — | **0 / 0 / 0** |

引擎在完整无损管线下对 MEIPASS **严格只读**（含 `__pycache__` 类写入也没有）——并发共享同一持久目录的风险面实测为零（仍建议产品侧以下节的内容寻址+引用计数兜底）。

## 6. 结论

**可行，把握度高（静态反汇编与动态行为互证 + 全量契约绿）**：
- 机制：PyInstaller 6.x `_PYI_*` 三元组注入（见 §1.2），`_MEIPASS2` 通道在本构建不存在。
- 用户三判据逐条满足：① 版本不变零重释放（持久目录存在即用，实测硬杀后直接复用）② 异常退出零遗留（根本没有 `_MEI` 可遗留；树杀验证通过）③ 零重复占用（持久目录一份 386 MB；不再每次 386 MB 解压-删除循环，顺带消除每跑 ~772 MB 临时盘写放大与 ~2.5s 解压延迟）。
- 附带收益：每次调用少一个引擎实例（省一次 spawn）；首条输出提前 ~2s。

## 7. 推荐实施形态（若立项）与风险清单

> 已按「打包期预解包（EnginePreextractor）入载荷 resources\jsr_backend_runtime\，释放于 %LOCALAPPDATA%\JSR4Z8\backend\<hash16>，_PYI_* 注入直跑」形态实施（AGENT.md §3/§4）；本节为当时的候选草案，保留作背景。

**形态**（对应 §2 已验证的解包器与 §3 已验证的注入）：
1. **内容寻址持久目录**：`%LOCALAPPDATA%\JSR4Z8\engine-extract\<sha256(exe)[:8]>\`（本实验即此命名，`5e315a17`）。首跑或版本变更时由 C# 侧解包器（cookie→TOC→b 条目 zlib，.NET `ZLibStream`；TOC 名内 `\` 分隔符直接采纳）写入新目录后原子换引用；旧目录等引用归零再删（PayloadManager 式：作业队列串行 + 引用计数即可，无需文件锁）。完整性护栏：解包后对 manifest（文件数/总字节，可选逐文件哈希）断言，失败即回退无注入模式。
2. **环境注入点**：引擎 spawn 的 ProcessStartInfo 上设三元组：`_PYI_ARCHIVE_FILE=<jsr_backend.exe 全路径>`、`_PYI_APPLICATION_HOME_DIR=<持久目录>`、`_PYI_PARENT_PROCESS_LEVEL=0`。**必须整组设置**——缺 `_PYI_ARCHIVE_FILE` 时引导器会剥掉另两个（§1.2），静默退化为现行解压行为（无害但失去收益；可在 E2E 断言「注入跑后 %TEMP% 无新 `_MEI`」防退化）。
3. **与 Job Object / 树杀 / 清扫的交互**：单实例化不改变取消语义（Job Object + `taskkill /T` 照旧，保持 `/T` 防御）；现有 `%TEMP%\_MEI` 清扫路径自然退化为 no-op（保留作安全网）；历史泄漏目录属用户侧清理。优雅退出不会删持久目录（子路径无清理逻辑，静态+动态双证）。
4. **并发注记**：多作业并发 = 多进程共享同一只读持久目录（实测零写入）；唯一竞态是「换版本时删旧目录 vs 旧版本引擎还在跑」——内容寻址天然规避（新旧目录不同名），删除动作挂到引用归零后即可。杀毒扫描面反而下降（消除了每跑 386 MB 新解压文件）。

**风险清单**：
- **PyInstaller 版本耦合（主风险）**：`_PYI_*` 契约是 6.x bootloader 未文档化行为（`_MEIPASS2`→`_PYI_*` 的更迭本身就发生过一次）。后端升级 = 新 exe = 新 sha256 = 新持久目录，注入三元组可能对新引导器失效（静默回退现行行为，无正确性风险，仅失去收益）。缓解：载荷升级链路（PayloadTests）加一条「注入跑 20s 冒烟 + %TEMP% 无新 `_MEI`」断言；必要时以本工作区 `ws/e0_strings.py`/`ws/stub.asm` 流程重证机制。
- **引擎写 MEIPASS 的风险**：本载荷本管线实测零写入（全量 mtime 对比）；残余面在其他代码路径（selftest/预览/未来版本）。内容寻址使写入不致命（不污染其他版本目录）；如需强护栏可在作业完成后抽查 manifest。
- **首跑成本**：新版本首跑需解包 ~386 MB（一次性 ~5-8s 磁盘写）；冷启动（重启后首次）持久目录可能进冷缓存——量级与现行首跑解压相当或更优。
- **`pyi-contents-directory _internal` 选项**：本构建 onefile 解包平铺 MEI 根（历史 16 目录 + 本轮实测一致）；若未来构建真启用该布局，解包器 TOC 名直接含子路径前缀，无需改逻辑，但需以解包对账断言（1,416/386,096,731 同构校验）兜底换版本时的布局漂移。

## 8. GPU/运行账目（诚实记账）

引擎共 7 次真机跑：c1/c2/c2b（60s×3，无效注入对照组——两个基础设施陷阱所致，见 §3 注 A/B）、c2c（60s，有效二元组判定）、c3（60s，三元组成功 + E4 杀）、e4-reuse（20s，E4 复用）、e5-full（329s，E5/E2 完整优雅跑，用户授权的裁决性证据）。合计 ≈649s GPU 时间；超出的 3×60s 全部花在定位注入基础设施缺陷上（结论：这两类坑已写入 §3 注记，后续实施轮不会再付此学费）。
