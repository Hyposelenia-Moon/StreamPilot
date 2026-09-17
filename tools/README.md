# tools 目录说明

本目录用于放置**用户自备**的外部工具。仓库中**不包含**任何 `*.exe` / `*.dll`（见 `CLAUDE.md` 红线 2 与 `.gitignore`）。

## mpv（必需，用于「mpv 播放」外挂功能）

推荐目录结构：

```
tools\
  mpv\
    mpv.exe
    (mpv 其余文件)
```

程序按以下顺序查找 mpv：

1. 设置面板中显式指定的路径；
2. `<程序目录>\tools\mpv\mpv.exe`；
3. `<程序目录>\tools\mpv.exe`；
4. 系统 `PATH` 中的 `mpv.exe`；
5. 常见安装位置：`%ProgramFiles%\mpv\mpv.exe`、`%LOCALAPPDATA%\Programs\mpv\mpv.exe`、`%USERPROFILE%\scoop\shims\mpv.exe`、`C:\mpv\mpv.exe`、`D:\mpv\mpv.exe`。

未找到时「mpv 播放」会给出明确提示，Web 播放不受影响。

## mpv 启动参数

StreamPilot 只传**不传就播不了**的参数：

```
--title=StreamPilot - {主播/标题}
--http-header-fields=Referer: {平台 Referer}   # 仅需要防盗链的平台（B站 / 抖音 / YY / Bigo）
```

缓存、画质、倍速、渲染器等一律由**用户自己的 mpv 配置**决定（`mpv.conf`、`input.conf` 或
内置的 `--profile`）。这样做的原因是：外挂播放的画质与延迟调优属于用户偏好，
程序预设参数会覆盖掉用户在 mpv 里的调整（见 `src/StreamPilot.Bridge/MpvLauncher.cs`）。

## ffmpeg

**不需要**。StreamPilot 的录制路径完全自研（FLV/TS 字节级写入、不转码），不调用 ffmpeg。
