# 第三方依赖清单（THIRD-PARTY-NOTICES）

本文件登记 StreamPilot 使用的全部第三方依赖、随包资产与外部工具。
**约束**：所有第三方依赖必须在此登记版本、许可证与来源；官方二进制（`*.dll`/`*.exe`）不得提交到 Git 仓库。

## 1. NuGet 依赖

| 包名 | 版本 | 许可证 | 用途 | 来源 |
|------|------|--------|------|------|
| `Microsoft.Web.WebView2` | `1.0.2903.40` | BSD-3-Clause（Microsoft） | WPF 内嵌 Edge 内核，承载播放页 | https://www.nuget.org/packages/Microsoft.Web.WebView2 |

说明：
- 这是**唯一**的运行时第三方包（见 [ADR 0001](../docs/adr/0001-technology-stack.md)）。
- 该包内含原生库 `WebView2Loader.dll`，随发布产物一并分发；它**不提交到仓库**（`.gitignore` 已排除 `*.dll`）。
- 测试工程不引入任何测试框架包，使用自研极简运行器。

## 2. 随包分发的前端资产

| 文件 | 版本 | 许可证 | SHA256 | 来源与校验 |
|------|------|--------|--------|------------|
| `Web/mpegts.js` | **1.8.2** | Apache-2.0 | `BDA31748736A69CB610C2EDF4623E633F1F4F47B5BDA83668C8D287E51B0C3A8` | 上游 mpegts.js（bilibili 官方开源）；库内自报版本 `1.8.2`；文件头带 `sourceMappingURL=mpegts.js.map`（未随包分发，缺失无害）；许可文本随包提供 `Web/MPEGTS-LICENSE.txt` |
| `Web/hls.js` | **1.6.16** | Apache-2.0 | `442F599C34F103C3355B375A23BDFF560592D7117D09A8C847242EA3DE2D40E0` | 上游 hls.js（Dailymotion / video-dev）；库内自报 `version = "1.6.16"`；为 `hls.min.js` 重命名；许可文本随包提供 `Web/HLS-LICENSE.txt` |
| `Web/HLS-LICENSE.txt` | — | Apache-2.0 文本 | `CA8773CF798C7ED997D4DD7C8E23C348699F8D5B7462636694CC14DE6CDA12DB` | hls.js 许可文本（Dailymotion 2017 + videojs-contrib-hls 说明） |
| `Web/MPEGTS-LICENSE.txt` | — | Apache-2.0 文本 | `58D1E17FFE5109A7AE296CAAFCADFDBE6A7D176F0BC4AB01E12A689B0499D8BD` | mpegts.js 许可文本 |
| `Web/player.html`、`Web/player-core.js` | 自研 | MIT | 见 `build/verify-tree.ps1 -VerifyHashes` | 本项目源码；播放策略与宿主消息契约见 `docs/architecture/player-message-contract.md` |
| `Web/decode-selftest.mp4` | **未随包** | — | — | 参考项目中的 H.264+AAC 自检片段，本项目**不使用**（参考项目的 MSE 自检逻辑未移植），因此未复制该二进制资产 |

> 校验方式：`powershell -NoProfile -ExecutionPolicy Bypass -File build/verify-tree.ps1 -VerifyHashes`
> 上表 SHA256 为**本仓库当前文件**的实测值（复制自参考项目 `MultiLive-Windows-v10.4.16-diag\Web\`，
> 与该项目 `THIRD-PARTY-NOTICES.md` 登记的哈希一致）。若未来升级前端库，必须同时更新版本与哈希。

## 3. 外部工具（用户自备，不随包分发、不提交仓库）

| 工具 | 版本要求 | 许可证 | 用途 | 获取方式 |
|------|----------|--------|------|----------|
| `mpv` | 0.37+ 建议 | GPL-2.0-or-later / LGPL-2.1-or-later（依构建选项） | 外挂播放：超分、HDR、高画质 | https://mpv.io/installation/ ；放置于 `tools\mpv\mpv.exe` 或在设置中指定路径 |

说明：
- `tools/` 目录被 `.gitignore` 排除，仅保留 `tools/README.md`。
- 本程序**不调用 ffmpeg**（录制路径完全自研、不转码）。

## 4. 参考项目（仅参考设计，未复用其代码或二进制）

| 项目 | 用途 | 复用情况 |
|------|------|----------|
| `MultiLive-Windows-v10.4.16-diag` | 低延迟播放参数、稳定性边界、WebView2 消息契约、桥接经验值 | 复用其**随包的 Apache-2.0 前端库**（见第 2 节）；C# 层为闭源编译产物，**未复用**，已用 C# 重写 |
| `lsar-0.3.19`（Rust/Tauri） | 六个平台解析思路 | 仅参考思路，**全部用 C# 重写**（见 [ADR 0003](../docs/adr/0003-parser-contract.md)） |
| `BililiveRecorder-WPF-Portable` | 原始流录制行为（分片、重连、元数据） | 仅参考行为，**独立实现**（见 [ADR 0004](../docs/adr/0004-raw-recording.md)） |

## 5. 发布前待办

1. 核对 `Web/mpegts.js`、`Web/hls.js`、`Web/HLS-LICENSE.txt`、`Web/MPEGTS-LICENSE.txt` 的 SHA256
   与第 2 节一致：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File build\verify-tree.ps1 -VerifyHashes
   ```

2. 核对 `Microsoft.Web.WebView2` 包内 `WebView2Loader.dll` 的版本与来源（由 NuGet 包签名保证）。
3. 确认 `tools/` 中未混入任何二进制（`verify-tree.ps1` 的第 5 项会检查）。
