/*
 * StreamPilot 播放页纯逻辑模块。
 *
 * 这是唯一的一份播放策略实现：player.html 通过 <script src="./player-core.js"></script>
 * 直接引用本文件，node --test 也直接引用本文件，因此不存在"两边各写一份、逐渐漂移"的问题。
 *
 * 约束：
 *  - 只允许纯函数与常量，禁止访问 window/document/MSE 等浏览器 API（便于在 Node 中测试）；
 *  - 常量必须命名，禁止魔法数字散落在调用点；
 *  - 本文件不发起任何网络请求，只做参数推导与判定。
 */
'use strict';

/** 极限追帧允许的目标延迟档位（毫秒）。 */
const EXTREME_TARGETS_MS = Object.freeze([150, 200, 250]);

/** 极限追帧默认目标延迟（毫秒）。 */
const DEFAULT_EXTREME_TARGET_MS = 250;

/** 稳定模式下的目标延迟（毫秒）。 */
const STABLE_TARGET_MS = 200;

/** 每个候选最多重连次数。 */
const MAX_RECONNECTS_PER_CANDIDATE = 2;

/** CDN 候选探测超时（毫秒）。 */
const PROBE_TIMEOUT_MS = 1200;

/** 稳定模式首帧超时（毫秒）。 */
const STABLE_STARTUP_TIMEOUT_MS = 8000;

/** 极限追帧模式首帧超时（毫秒）。 */
const EXTREME_STARTUP_TIMEOUT_MS = 6000;

/** 候选存活超过该时长后重置其重连计数（毫秒）。 */
const RECONNECT_COUNTER_RESET_MS = 60000;

/** 重连退避序列（毫秒）。 */
const RECONNECT_DELAYS_MS = Object.freeze([250, 1000]);

/** 遥测与卡顿检测的轮询间隔（毫秒）。 */
const TELEMETRY_INTERVAL_MS = 500;

/** 遥测上报的最小间隔（毫秒）。 */
const TELEMETRY_REPORT_INTERVAL_MS = 10000;

/** 判定播放进度前进的最小差值（秒）。 */
const PROGRESS_EPSILON_SECONDS = 0.015;

/** 判定本地缓冲饥饿的阈值（秒）。 */
const STARVED_BUFFER_SECONDS = 0.15;

/** 卡顿判定阈值（毫秒）：极限追帧模式。 */
const STALL_THRESHOLD_EXTREME_MS = 4000;

/** 卡顿判定阈值（毫秒）：稳定模式。 */
const STALL_THRESHOLD_STABLE_MS = 6000;

/** 硬卡顿判定阈值（毫秒）：极限追帧模式。 */
const HARD_STALL_THRESHOLD_EXTREME_MS = 6500;

/** 硬卡顿判定阈值（毫秒）：稳定模式。 */
const HARD_STALL_THRESHOLD_STABLE_MS = 9000;

/** 手动追帧保留的缓冲下限（秒）。 */
const CHASE_KEEP_MIN_SECONDS = 0.02;

/** 手动追帧保留的缓冲上限（秒）。 */
const CHASE_KEEP_MAX_SECONDS = 0.5;

/** 手动追帧默认保留的缓冲（秒）。 */
const CHASE_KEEP_DEFAULT_SECONDS = 0.08;

/** 极限追帧模式下的 stash 初始大小（字节，等价于关闭 stash）。 */
const EXTREME_STASH_INITIAL_SIZE = 64;

/** 稳定模式下的 stash 初始大小（字节）。 */
const STABLE_STASH_INITIAL_SIZE = 96 * 1024;

/** 极限追帧时允许的倍速上限。 */
const LIVE_SYNC_PLAYBACK_RATE = 1.08;

/** HLS 低延迟模式关闭（直播播放列表为普通切片，不启用 LL-HLS）。 */
const HLS_LOW_LATENCY_MODE = false;

/** HLS 回退缓冲保留时长（秒）。 */
const HLS_BACK_BUFFER_LENGTH = 30;

/** HLS 极限模式最大缓冲（秒）。 */
const HLS_MAX_BUFFER_EXTREME = 6;

/** HLS 稳定模式最大缓冲（秒）。 */
const HLS_MAX_BUFFER_STABLE = 12;

/** HLS 极限模式的直播同步切片数。 */
const HLS_LIVE_SYNC_DURATION_COUNT_EXTREME = 1;

/** HLS 稳定模式的直播同步切片数。 */
const HLS_LIVE_SYNC_DURATION_COUNT_STABLE = 2;

/** HLS 极限模式允许的最大延迟切片数。 */
const HLS_MAX_LATENCY_COUNT_EXTREME = 4;

/** HLS 稳定模式允许的最大延迟切片数。 */
const HLS_MAX_LATENCY_COUNT_STABLE = 8;

/** HLS 追帧最大倍速。 */
const HLS_MAX_LIVE_SYNC_PLAYBACK_RATE = 1.5;

/** 稳定模式的延迟追帧阈值（秒）。 */
const STABLE_LATENCY_MAX_SECONDS = 1.25;

/** 稳定模式的追帧后保留缓冲（秒）。 */
const STABLE_LATENCY_MIN_REMAIN_SECONDS = 0.32;

/** mpegts.js 延迟阈值的附加余量（秒）。 */
const LATENCY_MAX_MARGIN_SECONDS = 0.27;

/** mpegts.js 倍速追帧阈值的附加余量（秒）。 */
const LIVE_SYNC_MAX_MARGIN_SECONDS = 0.14;

/** HLS 候选的格式字符串集合。 */
const HLS_FORMAT_NAMES = Object.freeze(['fmp4', 'ts', 'hls']);

/** 音量百分比刻度：100% 对应 HTMLMediaElement.volume = 1.0。 */
const VOLUME_PERCENT_SCALE = 100;

/** 音量百分比上限。 */
const MAX_VOLUME_PERCENT = VOLUME_PERCENT_SCALE;

/** 音量百分比下限（0 等价于静音）。 */
const MIN_VOLUME_PERCENT = 0;

/** 初始音量（百分比）。70% 在大多数直播间偏响，改为 30% 起步。 */
const INITIAL_VOLUME_PERCENT = 30;

/** 空态提示标题。 */
const EMPTY_HINT_TITLE = '等待直播源';

/** 空态提示里的操作指引：真正要点的按钮在主界面左侧面板，不在播放页内。 */
const EMPTY_HINT_ACTION = '在左侧点「开始播放」即可观看';

/** 自动播放被浏览器策略拦截时的提示（需要用户先与页面交互一次）。 */
const AUTOPLAY_BLOCKED_HINT = '浏览器暂时拦住了自动播放，点一下画面即可开始播放。';

/** 模式提示：没有活动会话。 */
const MODE_HINT_IDLE = '等待直播源';

/** 模式提示：已选中候选但首帧还没出来。 */
const MODE_HINT_CONNECTING = '连接中';

/** 模式提示：画面已经在播。 */
const MODE_HINT_PLAYING = '播放中';

/** 页面消息类型：宿主 → 页面。 */
const INBOUND_MESSAGE_TYPES = Object.freeze({
  PLAY: 'play',
  CHASE: 'chase',
  STOP: 'stop',
  TARGET: 'target',
});

/** 页面消息类型：页面 → 宿主。 */
const OUTBOUND_MESSAGE_TYPES = Object.freeze({
  READY: 'ready',
  CHECKING: 'checking',
  PROBE_RESULT: 'probe-result',
  PROBE_REJECTED: 'probe-rejected',
  CANDIDATE_QUEUE: 'candidate-queue',
  CANDIDATE_ACTIVE: 'candidate-active',
  STATUS: 'status',
  TELEMETRY: 'telemetry',
  WARNING: 'warning',
  ERROR: 'error',
  RECONNECTING: 'reconnecting',
  REFRESH_NEEDED: 'refresh-needed',
  FULLSCREEN_ENTER: 'fullscreen-enter',
  FULLSCREEN_EXIT: 'fullscreen-exit',
  QUALITY: 'quality',
});

/**
 * 把宿主传入的目标延迟归一化为秒。
 * @param {*} value 宿主传入的 extremeTargetMs。
 * @returns {number} 150/200/250 对应 0.15/0.2/0.25；其他值回落为 0.2。
 */
function normalizeExtremeTargetSeconds(value) {
  const milliseconds = Number(value);
  return EXTREME_TARGETS_MS.includes(milliseconds) ? milliseconds / 1000 : STABLE_TARGET_MS / 1000;
}

/**
 * 判断候选是否为 HLS 家族。
 * @param {{format?: string}} candidate 候选。
 * @returns {boolean} 是 HLS 返回 true。
 */
function isHlsCandidate(candidate) {
  return !!candidate && HLS_FORMAT_NAMES.includes(String(candidate.format || '').toLowerCase());
}

/**
 * 规范化宿主下发的候选列表：去重、补默认值、保持顺序。
 * @param {*} raw 宿主下发的 candidates。
 * @returns {Array<object>} 规范化后的候选列表。
 */
function normalizeCandidates(raw) {
  const result = [];
  const seen = new Set();
  const list = Array.isArray(raw) ? raw : [];
  for (const item of list) {
    if (!item || typeof item !== 'object') {
      continue;
    }

    const url = typeof item.url === 'string' ? item.url.trim() : '';
    if (url.length === 0) {
      continue;
    }

    const sourceIndex = Number.isInteger(item.sourceIndex) ? item.sourceIndex : result.length;
    const key = sourceIndex + ':' + url;
    if (seen.has(key)) {
      continue;
    }

    seen.add(key);
    result.push({
      sourceIndex,
      url,
      host: typeof item.host === 'string' && item.host.length > 0 ? item.host : 'unknown',
      format: String(item.format || 'flv').toLowerCase(),
      codec: String(item.codec || 'avc').toLowerCase(),
      urlFingerprint: typeof item.urlFingerprint === 'string' ? item.urlFingerprint : '',
      label: typeof item.label === 'string' ? item.label : '',
      referer: typeof item.referer === 'string' ? item.referer : '',
    });
  }

  return result;
}

/**
 * 计算 mpegts.js 的播放配置。
 * @param {boolean} extreme 是否极限追帧模式。
 * @param {number} targetSeconds 目标延迟（秒）。
 * @returns {object} mpegts.js 配置对象。
 */
function buildMpegtsConfig(extreme, targetSeconds) {
  return {
    isLive: true,
    enableWorker: true,
    enableStashBuffer: !extreme,
    stashInitialSize: extreme ? EXTREME_STASH_INITIAL_SIZE : STABLE_STASH_INITIAL_SIZE,
    lazyLoad: false,
    deferLoadAfterSourceOpen: false,
    autoCleanupSourceBuffer: true,
    liveBufferLatencyChasing: true,
    liveBufferLatencyMaxLatency: extreme
      ? Math.max(0.35, targetSeconds + LATENCY_MAX_MARGIN_SECONDS)
      : STABLE_LATENCY_MAX_SECONDS,
    liveBufferLatencyMinRemain: extreme ? targetSeconds : STABLE_LATENCY_MIN_REMAIN_SECONDS,
    liveSync: extreme,
    liveSyncMaxLatency: extreme ? Math.max(0.22, targetSeconds + LIVE_SYNC_MAX_MARGIN_SECONDS) : STABLE_LATENCY_MAX_SECONDS,
    liveSyncTargetLatency: extreme ? targetSeconds : 0.8,
    liveSyncPlaybackRate: extreme ? LIVE_SYNC_PLAYBACK_RATE : 1,
    fixAudioTimestampGap: true,
  };
}

/**
 * 计算 hls.js 的播放配置。注意：HLS 路径不区分 150/200/250 三档。
 * @param {boolean} extreme 是否极限追帧模式。
 * @returns {object} hls.js 配置对象。
 */
function buildHlsConfig(extreme) {
  return {
    enableWorker: true,
    lowLatencyMode: HLS_LOW_LATENCY_MODE,
    backBufferLength: HLS_BACK_BUFFER_LENGTH,
    maxBufferLength: extreme ? HLS_MAX_BUFFER_EXTREME : HLS_MAX_BUFFER_STABLE,
    liveSyncDurationCount: extreme ? HLS_LIVE_SYNC_DURATION_COUNT_EXTREME : HLS_LIVE_SYNC_DURATION_COUNT_STABLE,
    liveMaxLatencyDurationCount: extreme ? HLS_MAX_LATENCY_COUNT_EXTREME : HLS_MAX_LATENCY_COUNT_STABLE,
    maxLiveSyncPlaybackRate: HLS_MAX_LIVE_SYNC_PLAYBACK_RATE,
  };
}

/**
 * 计算重连退避时间。
 * @param {number} reconnectCount 已完成的重连次数（从 0 开始）。
 * @returns {number} 退避毫秒数；超过上限返回 -1 表示应切换候选。
 */
function getReconnectDelayMs(reconnectCount) {
  if (reconnectCount < 0) {
    return RECONNECT_DELAYS_MS[0];
  }

  if (reconnectCount >= MAX_RECONNECTS_PER_CANDIDATE) {
    return -1;
  }

  return RECONNECT_DELAYS_MS[Math.min(reconnectCount, RECONNECT_DELAYS_MS.length - 1)];
}

/**
 * 计算卡顿判定阈值。
 * @param {boolean} extreme 是否极限追帧模式。
 * @param {boolean} looksStarved 是否处于饥饿状态（暂停/无缓冲/readyState 偏低）。
 * @returns {number} 判定阈值毫秒数。
 */
function getStallThresholdMs(extreme, looksStarved) {
  if (extreme) {
    return looksStarved ? STALL_THRESHOLD_EXTREME_MS : HARD_STALL_THRESHOLD_EXTREME_MS;
  }

  return looksStarved ? STALL_THRESHOLD_STABLE_MS : HARD_STALL_THRESHOLD_STABLE_MS;
}

/**
 * 计算手动追帧的目标播放位置。
 * @param {number} bufferStart 缓冲起点（秒）。
 * @param {number} bufferEnd 缓冲终点（秒）。
 * @param {*} keepSeconds 保留的缓冲秒数。
 * @returns {number} 目标 currentTime。
 */
function computeChaseTargetSeconds(bufferStart, bufferEnd, keepSeconds) {
  const keep = Math.max(CHASE_KEEP_MIN_SECONDS, Math.min(CHASE_KEEP_MAX_SECONDS, Number(keepSeconds) || CHASE_KEEP_DEFAULT_SECONDS));
  return Math.max(bufferStart, bufferEnd - keep);
}

/**
 * 判断播放进度是否前进。
 * @param {number} currentTime 当前播放位置（秒）。
 * @param {number} lastPosition 上次记录的位置（秒）。
 * @returns {boolean} 前进返回 true。
 */
function hasProgressed(currentTime, lastPosition) {
  return Number(currentTime) > Number(lastPosition) + PROGRESS_EPSILON_SECONDS;
}

/**
 * 计算本地缓冲剩余量。
 * @param {TimeRanges|{length:number,start:Function,end:Function}} buffered 缓冲区间。
 * @param {number} currentTime 当前播放位置（秒）。
 * @returns {number|null} 剩余秒数；无缓冲返回 null。
 */
function getLocalBufferSeconds(buffered, currentTime) {
  if (!buffered || buffered.length === 0) {
    return null;
  }

  const last = buffered.length - 1;
  return Math.max(0, buffered.end(last) - Number(currentTime));
}

/**
 * 判断是否处于缓冲饥饿状态。
 * @param {{paused:boolean,ended:boolean,readyState:number}} video 视频元素状态快照。
 * @param {number|null} localBufferSeconds 本地缓冲剩余秒数。
 * @returns {boolean} 饥饿返回 true。
 */
function looksStarved(video, localBufferSeconds) {
  if (!video) {
    return true;
  }

  return !!video.paused
    || !!video.ended
    || video.readyState < 3
    || localBufferSeconds === null
    || localBufferSeconds < STARVED_BUFFER_SECONDS;
}

/**
 * 读取候选队列所需的超时毫秒数。
 * @param {{extreme?:boolean}} plan 播放计划。
 * @returns {number} 首帧超时毫秒数。
 */
function getStartupTimeoutMs(plan) {
  return plan && plan.extreme ? EXTREME_STARTUP_TIMEOUT_MS : STABLE_STARTUP_TIMEOUT_MS;
}

/**
 * 模式标签（用于界面提示）。
 * @param {{mode?:string,extremeTargetMs?:number,extremeTargetSeconds?:number}} plan 播放计划或运行对象。
 * @returns {string} 中文标签。
 */
function modeLabel(plan) {
  if (!plan || plan.mode !== 'extreme') {
    return '稳定缓冲';
  }

  // 运行对象过去只带 extremeTargetSeconds：两种形状都认，
  // 否则会在 missing 字段时静默回落成默认档位（表现为"选了 250 却显示 200"）。
  const milliseconds = Number.isFinite(plan.extremeTargetMs)
    ? plan.extremeTargetMs
    : Number.isFinite(plan.extremeTargetSeconds) ? plan.extremeTargetSeconds * 1000 : plan.extremeTargetMs;
  return '极限追帧 ' + normalizeExtremeTargetSeconds(milliseconds) * 1000 + ' ms';
}

/**
 * 把任意输入夹取为合法的音量百分比。
 * @param {*} value 原始值（滑块字符串、宿主 volume 消息、滚轮累加结果）。
 * @returns {number} 0–100 的整数；非法输入按 0 处理（静音比"猜一个音量"更可预期）。
 */
function clampVolumePercent(value) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) {
    return MIN_VOLUME_PERCENT;
  }

  return Math.max(MIN_VOLUME_PERCENT, Math.min(MAX_VOLUME_PERCENT, Math.round(numeric)));
}

/**
 * 把音量百分比换算成 HTMLMediaElement.volume 需要的增益。
 * @param {*} percent 音量百分比。
 * @returns {number} 0–1 的增益。
 */
function volumePercentToGain(percent) {
  return clampVolumePercent(percent) / VOLUME_PERCENT_SCALE;
}

/**
 * 判断该音量下是否必须静音。
 * @param {*} percent 音量百分比。
 * @returns {boolean} 0% 返回 true。
 */
function shouldMuteAtVolume(percent) {
  return clampVolumePercent(percent) === MIN_VOLUME_PERCENT;
}

/**
 * 判断播放失败是否由自动播放策略引起（需要用户手势才能出声）。
 * @param {*} error play() 抛出的错误。
 * @returns {boolean} 属于自动播放拦截返回 true。
 */
function isAutoplayBlocked(error) {
  return Boolean(error) && error.name === 'NotAllowedError';
}

/**
 * 顶部模式提示文案：只包含档位与播放状态。
 *
 * 候选主机名（CDN 节点）不在这里出现：它对观看没有意义，
 * 用户会把它读成"页面在报一个地址"。
 * @param {{mode?:string,extremeTargetMs?:number,playbackStarted?:boolean,currentCandidate?:object}} run 运行对象。
 * @returns {string} 中文提示。
 */
function formatModeHint(run) {
  if (!run || !run.currentCandidate) {
    return MODE_HINT_IDLE;
  }

  return modeLabel(run) + ' · ' + (run.playbackStarted ? MODE_HINT_PLAYING : MODE_HINT_CONNECTING);
}

/**
 * 底部状态行在播放开始后的文案（同样不含候选主机名）。
 * @param {{mode?:string,extremeTargetMs?:number}} run 运行对象。
 * @returns {string} 中文状态。
 */
function formatPlaybackStatusText(run) {
  return MODE_HINT_PLAYING + ' · ' + modeLabel(run);
}

/**
 * 判断居中提示是否应当保持隐藏。
 *
 * 只看"这次会话是否出过画面"，与全屏状态无关：退出全屏曾经无条件取消隐藏，
 * 把藏起来的空态文案（等待直播源）又盖回画面，看起来像直播源丢了。
 * 用"出过画面"而不是"正在播放"，是因为换线路的瞬间 playbackStarted 会被清零，
 * 那时更不该弹出空态。
 * @param {{everPlayed?:boolean}} run 运行对象。
 * @returns {boolean} 应当隐藏返回 true。
 */
function shouldHideHint(run) {
  return Boolean(run && run.everPlayed);
}

/**
 * 根据宿主下发的 play 消息构造规范化的播放计划。
 * @param {object} payload 宿主消息。
 * @param {boolean} canPlayFlv mpegts.js 是否可用。
 * @param {boolean} canPlayHls hls.js 是否可用。
 * @param {boolean} hevcSupported 当前环境是否支持 HEVC MSE。
 * @returns {{plan:object|null,unsupportedCount:number}} 计划与因编码不支持被丢弃的数量。
 */
function buildPlaybackPlan(payload, canPlayFlv, canPlayHls, hevcSupported) {
  if (!payload || typeof payload !== 'object') {
    return { plan: null, unsupportedCount: 0 };
  }

  const extreme = payload.mode === 'extreme';
  const candidates = normalizeCandidates(payload.candidates);
  const accepted = [];
  let unsupportedCount = 0;

  for (const candidate of candidates) {
    const requiresHls = isHlsCandidate(candidate);
    const playable = requiresHls ? canPlayHls : canPlayFlv;
    if (!playable) {
      unsupportedCount++;
      continue;
    }

    if (candidate.codec === 'hevc' && !hevcSupported) {
      unsupportedCount++;
      continue;
    }

    accepted.push(candidate);
  }

  return {
    plan: {
      sessionId: Number.isSafeInteger(payload.sessionId) ? payload.sessionId : 0,
      mode: extreme ? 'extreme' : 'stable',
      extreme,
      extremeTargetMs: extreme ? Number(payload.extremeTargetMs) : DEFAULT_EXTREME_TARGET_MS,
      extremeTargetSeconds: extreme ? normalizeExtremeTargetSeconds(payload.extremeTargetMs) : STABLE_TARGET_MS / 1000,
      candidates: accepted,
    },
    unsupportedCount,
  };
}

/**
 * 在不重连的前提下把追帧档位应用到一个正在播放的运行对象上。
 * @param {object} run 运行对象（含 extreme / extremeTargetMs）。
 * @param {*} extremeTargetMs 宿主下发的目标延迟（150/200/250）。
 * @returns {boolean} 档位发生变化返回 true。
 */
function applyExtremeTarget(run, extremeTargetMs) {
  if (!run || typeof run !== 'object') {
    return false;
  }

  const milliseconds = Number(extremeTargetMs);
  const normalized = EXTREME_TARGETS_MS.includes(milliseconds) ? milliseconds : DEFAULT_EXTREME_TARGET_MS;
  const changed = run.extremeTargetMs !== normalized;
  run.extremeTargetMs = normalized;
  run.extremeTargetSeconds = normalized / 1000;
  run.extreme = true;
  run.mode = 'extreme';
  return changed;
}

/**
 * 规范化宿主下发的画质档位列表，供页面渲染下拉框。
 * @param {*} qualities 宿主 play 消息里的 qualities 字段。
 * @param {*} selectedKey 当前生效的档位键。
 * @returns {{items:Array<{key:string,label:string,selected:boolean}>,selectedKey:string}} 下拉项与选中键。
 */
function normalizeQualities(qualities, selectedKey) {
  const items = [];
  if (!Array.isArray(qualities)) {
    return { items, selectedKey: '' };
  }

  const wanted = typeof selectedKey === 'string' ? selectedKey : '';
  for (const entry of qualities) {
    if (!entry || typeof entry !== 'object') {
      continue;
    }

    const key = typeof entry.key === 'string' ? entry.key.trim() : '';
    if (key.length === 0) {
      continue;
    }

    const rawLabel = typeof entry.label === 'string' ? entry.label.trim() : '';
    const base = rawLabel.length > 0 ? rawLabel : key;
    items.push({
      key,
      // 只显示平台自己的档位名，不追加码率后缀：
      // 档位名里本来就带码率（"蓝光4M"），再补一次会出现"蓝光4M · 4 Mbps"这种不一致。
      label: base,
      selected: key === wanted,
    });
  }

  const effective = items.some((item) => item.key === wanted) ? wanted : (items.length > 0 ? items[0].key : '');
  for (const item of items) {
    item.selected = item.key === effective;
  }

  return { items, selectedKey: effective };
}

/**
 * 判断 mpegts 错误是否代表 HTTP 状态异常（需要立刻切换候选）。
 * @param {*} errorType 错误类型枚举值。
 * @param {*} errorDetail 错误详情枚举值。
 * @param {object} mpegts mpegts 全局对象。
 * @returns {boolean} 属于 HTTP 状态错误返回 true。
 */
function isHttpStatusInvalid(errorType, errorDetail, mpegts) {
  if (!mpegts || !mpegts.ErrorTypes || !mpegts.ErrorDetails) {
    return false;
  }

  if (errorType !== mpegts.ErrorTypes.NETWORK_ERROR) {
    return false;
  }

  return errorDetail === mpegts.ErrorDetails.NETWORK_STATUS_CODE_INVALID || errorDetail === 'HttpStatusCodeInvalid';
}

/**
 * 判断 mpegts 错误是否代表 MSE 解码失败。
 * @param {*} errorDetail 错误详情枚举值。
 * @param {object} mpegts mpegts 全局对象。
 * @returns {boolean} 属于 MSE 错误返回 true。
 */
function isMseError(errorDetail, mpegts) {
  if (!mpegts || !mpegts.ErrorDetails) {
    return false;
  }

  return errorDetail === mpegts.ErrorDetails.MEDIA_MSE_ERROR || errorDetail === 'MediaMSEError';
}

/**
 * 判断错误描述是否表明 HEVC 解码器被系统拒绝。
 * @param {string} description 错误描述。
 * @returns {boolean} 是 HEVC 不受支持返回 true。
 */
function isHevcUnsupportedDescription(description) {
  const text = String(description || '');
  return /hvc1|hev1/i.test(text) && /unsupported|not supported|不受支持/i.test(text);
}

const StreamPilotPlayerCore = {
  EXTREME_TARGETS_MS,
  DEFAULT_EXTREME_TARGET_MS,
  MAX_RECONNECTS_PER_CANDIDATE,
  PROBE_TIMEOUT_MS,
  TELEMETRY_INTERVAL_MS,
  TELEMETRY_REPORT_INTERVAL_MS,
  RECONNECT_COUNTER_RESET_MS,
  VOLUME_PERCENT_SCALE,
  MIN_VOLUME_PERCENT,
  MAX_VOLUME_PERCENT,
  INITIAL_VOLUME_PERCENT,
  EMPTY_HINT_TITLE,
  EMPTY_HINT_ACTION,
  AUTOPLAY_BLOCKED_HINT,
  MODE_HINT_IDLE,
  MODE_HINT_CONNECTING,
  MODE_HINT_PLAYING,
  INBOUND_MESSAGE_TYPES,
  OUTBOUND_MESSAGE_TYPES,
  normalizeExtremeTargetSeconds,
  isHlsCandidate,
  normalizeCandidates,
  buildMpegtsConfig,
  buildHlsConfig,
  getReconnectDelayMs,
  getStallThresholdMs,
  computeChaseTargetSeconds,
  hasProgressed,
  getLocalBufferSeconds,
  looksStarved,
  getStartupTimeoutMs,
  modeLabel,
  clampVolumePercent,
  volumePercentToGain,
  shouldMuteAtVolume,
  isAutoplayBlocked,
  formatModeHint,
  formatPlaybackStatusText,
  shouldHideHint,
  buildPlaybackPlan,
  normalizeQualities,
  applyExtremeTarget,
  isHttpStatusInvalid,
  isMseError,
  isHevcUnsupportedDescription,
};

if (typeof module !== 'undefined' && module.exports) {
  module.exports = StreamPilotPlayerCore;
}

if (typeof window !== 'undefined') {
  window.StreamPilotPlayerCore = StreamPilotPlayerCore;
}
