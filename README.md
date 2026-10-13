# JSR for Z8

Nikon Z8 七帧 RAW 超分辨率桌面客户端：.NET 8 + Avalonia 前端，内嵌原版
Jiangtherapee Python 后端（stormer7 引擎，V15.3.5 Transformer），在 GPU 上完成
对齐与重建，输出 16560×11040 的 16bit 线性 RGB DNG（`JSR_*.dng`，无损 JPEG 压缩，
约为未压缩体积的 1/3）。

## 系统要求

- Windows 10/11 x64
- 支持 Vulkan 的 GPU（计算经 wgpu 走 Vulkan）。验收配置为 12 GB 显存预算；
  预算不足（如 4 GB）会在对齐阶段失败并给出明确报错，不会损坏原片
- 磁盘：程序本体约 **537 MB**（单文件 exe）；首次运行释放约 650 MB 载荷到
  `%LOCALAPPDATA%\JSR4Z8`；每个任务另需约 1–2 GB 临时空间。失败或停止的任务
  会保留约 3 GB 诊断目录于临时路径，连续失败时请确保系统盘（或自定义临时
  路径所在盘）有 5 GB 以上可用空间

## 使用说明

1. 运行 `Jsr4Z8_v1.0.0.exe`。首次启动校验并释放后端载荷（约 650 MB，仅一次；
   之后每次启动只做哈希校验，秒级完成）。
2. 新建任务：选择同一场景的 **7 张连拍 NEF**（如 `DSC_7576`–`DSC_7582`）与
   **1 张同 ISO 黑场参考 NEF**。校验行显示识别结果
   （如 `Z8 · ISO3200 · 满阱 1102.2 e⁻`）。
3. 开始后卡片实时显示后端进度（对齐 → Transformer 重建 → DNG 导出），可随时
   停止——后端进程树随即终止，临时目录保留供诊断。
4. 完成后在样张目录得到 `JSR_DSC_7576.dng`（330 MB 量级）。
5. 设置中可调整显存预算（GB）。样张路径含中文与空格完全支持。

## SmartScreen 与杀毒软件说明

本程序未购买代码签名证书，首次运行可能触发 Windows SmartScreen
（“Windows 已保护你的电脑”）——点击 **更多信息 → 仍要运行** 即可。
单文件内嵌完整的 Python 运行时与模型（PyInstaller 风格载荷），部分杀软可能对
`jsr_backend.exe` 等载荷进程误报：如被隔离，请将程序所在目录与
`%LOCALAPPDATA%\JSR4Z8` 加入信任/排除后重试。载荷具备自愈能力——载荷目录中
任何文件缺失或损坏都会在下次启动时被哈希校验发现并完整重新释放。

## 构建

一键脚本，产出 `out\Jsr4Z8_v<版本>.exe`：

```bat
build.bat
```

前置：VS 2022/2026（VC/NMake 工具链）+ .NET 8 SDK + git 子模块 + CMake。
脚本依次构建 LibRaw（JO nikon-he-production fork）与 libjpeg-turbo 原生库、
打包后端载荷（`tools\pack-payload`，取自 `reference\` 下的 Jiangtherapee
Bridge 发布），最后单文件自包含发布（原生 DLL 压缩内嵌）。
注意：发布后请删除 `src\Jsr4Z8.Core\Payload\Embedded\payload.bin`（及同名
`.manifest.json`），否则此后每次 Core 构建/测试都会重新内嵌 ~493 MB 载荷，
显著拖慢开发构建（详见 `build.bat` 头注释）。

## 致谢与许可

- 超分辨率后端与算法来自 **y-g-jiang** 的 Jiangtherapee 项目，本仓库仅做
  打包与 GUI 集成。后端及其第三方组件的完整声明随载荷分发：安装后见
  `%LOCALAPPDATA%\JSR4Z8\backend\<hash>\THIRD_PARTY_NOTICES.md` 与
  `resources\licenses\`。
- LibRaw、libjpeg-turbo 等原生组件的权利声明见上述载荷内文件。
