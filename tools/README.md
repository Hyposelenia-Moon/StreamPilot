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

默认使用低延迟参数（见 `docs/architecture/播放策略.md` 与 `src/StreamPilot.Bridge/MpvLauncher.cs`）：

```
--cache=no --cache-pause=no --demuxer-readahead-secs=0 --demuxer-max-bytes=512K
--demuxer-max-back-bytes=0 --speed=1.08 --audio-pitch-correction=yes
--force-window=yes --keep-open=no --title=StreamPilot - {主播/标题}
```

需要 Referer 的平台（B站 / 抖音）会自动追加 `--http-header-fields=Referer: ...`。

在设置面板的“mpv 附加参数”中可追加参数（按空格分隔），例如：

```
--profile=low-latency --vo=gpu-next --hwdec=auto-safe
```

## ffmpeg

**不需要**。StreamPilot 的录制路径完全自研（FLV/TS 字节级写入、不转码），不调用 ffmpeg。
