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

/** 极限追帧默认目标延迟（毫秒），同时也是非法档位的回落值与稳定模式下的等价目标。 */
const DEFAULT_EXTREME_TARGET_MS = 250;

/** 每个候选最多重连次数。 */
const MAX_RECONNECTS_PER_CANDIDATE = 2;

/** CDN 候选探测超时（毫秒，与参考播放页一致）。 */
const PROBE_TIMEOUT_MS = 1200;

/** 稳定模式首帧超时（毫秒）。 */
const STABLE_STARTUP_TIMEOUT_MS = 8000;

/** 极限追帧模式首帧超时（毫秒）。 */
const EXTREME_STARTUP_TIMEOUT_MS = 6000;

/** 候选存活超过该时长后重置其重连计数（毫秒）。 */
const RECONNECT_COUNTER_RESET_MS = 60000;

/** 重连退避序列（毫秒）。 */
const RECONNECT_DELAYS_MS = Object.freeze([250, 1000]);

/**
 * 受控重建前退避所用的档位下标：复用既有重连退避序列的第一档。
 *
 * 为什么复用而不是新造一个数字：重建与重连面对的是同一个物理约束——上游还没察觉旧连接已断，
 * 立刻建新连接会撞上"同一条签名地址只允许一条并发连接"的限流（`docs/parsers/douyu.md` 记录的实测），
 * 表现为首帧偶发变慢。两者用同一档退避，语义与取值都不漂移。
 */
const REBUILD_BACKOFF_STEP = 0;

/** 受控重建前退避的硬上限（毫秒）：退避只是"等上游收掉旧连接"，不是重连等待。 */
const MAX_REBUILD_BACKOFF_MS = 1000;

/** 遥测与卡顿检测的轮询间隔（毫秒）。 */
const TELEMETRY_INTERVAL_MS = 500;

/** 遥测上报的最小间隔（毫秒）。 */
const TELEMETRY_REPORT_INTERVAL_MS = 10000;

/** 判定播放进度前进的最小差值（秒）。 */
const PROGRESS_EPSILON_SECONDS = 0.015;

/** 暂停后恢复播放时允许的位置容差（秒）：超出视为流已被上游丢弃。 */
const PLAY_RESTORE_TOLERANCE_SECONDS = 3;

/** 恢复播放后暂不判定卡顿的时长（毫秒）：给播放器重建缓冲的时间。 */
const RESUME_GRACE_MS = 4000;

/** 判定本地缓冲饥饿的阈值（秒）。 */
const STARVED_BUFFER_SECONDS = 0.15;

/** 判定画面停滞的最低就绪状态（HAVE_FUTURE_DATA 及以上才算能继续播）。 */
const READY_STATE_PLAYABLE = 3;

/*
 * 卡顿判定阈值（毫秒，与参考播放页逐项一致）。
 *
 * 极限档的目标延迟本来就贴着物理下限，buffer 偶尔见底属于预期；
 * 但一旦真的停住，恢复必须比稳定档更快，否则用户看到的是"卡住好几秒"。
 * 参考播放页因此对极限档给出更短的阈值（4s / 6.5s），稳定档给 6s / 9s。
 */

/** 稳定档的饥饿卡顿阈值（毫秒）：缓冲见底，网络供不上。 */
const STALL_THRESHOLD_STARVED_MS = 6000;

/** 稳定档的硬卡顿阈值（毫秒）：缓冲充足但画面不动。 */
const STALL_THRESHOLD_IDLE_MS = 9000;

/** 极限档的饥饿卡顿阈值（毫秒）。 */
const EXTREME_STALL_THRESHOLD_STARVED_MS = 4000;

/** 极限档的硬卡顿阈值（毫秒）。 */
const EXTREME_STALL_THRESHOLD_IDLE_MS = 6500;

/*
 * 缓冲失控保护（"数据一直到达、缓冲一直增长、画面一直不前进"）。
 *
 * 阈值依据（真机遥测，B站 814）：健康会话里追帧器把延迟压在 0.2–0.5 s
 * （bufferedAheadMs 实测 179–473），而失控会话里 bufferedAheadMs 从 81.9 s 涨到 111.9 s。
 * 取 **8 s** 作为"异常"下限的理由：
 *  - 它是极限档目标延迟（0.25 s）的 32 倍，也是稳定档追帧上限（`STABLE_LATENCY_MAX_SECONDS = 1.25 s`）的 6 倍以上，
 *    正常播放（哪怕是网络剧烈抖动）不可能停在 8 s 上不动；
 *  - 8 s 的缓冲全部丢弃的代价只是几百毫秒的重复画面，比"画面停住 80 秒"小得多；
 *  - 阈值低于它时就该走原有的分模式卡顿阈值（4 s / 6.5 s / 6 s / 9 s），不要提前打扰正常追帧。
 */

/** 缓冲失控：本地缓冲剩余超过该秒数即视为异常。 */
const BUFFER_RUNAWAY_AHEAD_SECONDS = 8;

/** 缓冲失控：画面停滞达到该毫秒数才判定（健康会话的停滞以毫秒计）。 */
const BUFFER_RUNAWAY_SILENCE_MS = 2000;

/** 缓冲失控时自行处置（恢复播放 / 追帧）后的等待时长（毫秒）：seek 生效需要时间，期间不重复处置也不升级。 */
const BUFFER_RUNAWAY_CHASE_GRACE_MS = 4000;

/** 缓冲失控时自行追帧的次数上限；用尽后交给既有重连 / 切候选。 */
const BUFFER_RUNAWAY_MAX_CHASE_ATTEMPTS = 2;

/** 缓冲失控的处置动作。 */
const RUNAWAY_ACTIONS = Object.freeze({
  /** 未失控，无需处置。 */
  NONE: 'none',
  /** 媒体元素被"非用户原因"暂停：先恢复播放。 */
  RESUME: 'resume',
  /** 缓冲可用：先追帧到缓冲末端。 */
  CHASE: 'chase',
  /** 自行处置用尽：交给既有重连 / 切候选。 */
  RECONNECT: 'reconnect',
});

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

/** 空态提示里的操作指引：真正要点的按钮在播放页底部，不在别处。 */
const EMPTY_HINT_ACTION = '点下方「开始播放」即可观看';

/** 自动播放被浏览器策略拦截时的提示（需要用户先与页面交互一次）。 */
const AUTOPLAY_BLOCKED_HINT = '浏览器暂时拦住了自动播放，点一下画面即可开始播放。';

/** 播放状态文案：画面已经在播（底部状态行与 `status` 上报共用）。 */
const MODE_HINT_PLAYING = '播放中';

/** 未连接时的状态行文案（页面初始化时使用）。 */
const STATUS_IDLE_TEXT = '未连接';

/** 状态行文案：用户点了「暂停播放」（不销毁会话，保留地址与缓冲）。 */
const STATUS_PAUSED_TEXT = '已暂停（保留当前地址）';

/** 状态行文案：用户点了「停止播放」（画面已关闭）。 */
const STATUS_STOPPED_TEXT = '已停止播放（画面已关闭）';

/** 重连状态行文案的固定前缀。 */
const STATUS_RECONNECTING_PREFIX = '重连中（第 ';

/** 重连状态行文案的固定后缀。 */
const STATUS_RECONNECTING_SUFFIX = ' 次）…';

/** 重连次数的计数起点（第 1 次重连）。 */
const FIRST_RECONNECT_ATTEMPT = 1;

/*
 * 状态行的会话阶段：由页面维护、由纯函数消费。
 *
 * 为什么要把它与"播放中遥测"分开：遥测每 `TELEMETRY_INTERVAL_MS`（500 ms）刷新一次状态行，
 * 只允许更新数值（实测延迟 / 缓冲），一旦让它改写"用户暂停 / 正在重连 / 已停止"这类操作态文案，
 * 用户点了「暂停播放」后 0.5 秒内文案就被刷回「播放中 · N ms（…）」——按钮写着「继续播放」，
 * 状态行却说在播放。阶段因此集中在一处判定（{@link resolvePlaybackStatusText}），页面只负责调用。
 */
const PLAYBACK_PHASES = Object.freeze({
  /** 没有会话：页面初始化，或停止后回到空态。 */
  IDLE: 'idle',
  /** 有会话且在播放：状态行交给遥测数值刷新。 */
  PLAYING: 'playing',
  /** 用户点了「暂停播放」：文案固定为 {@link STATUS_PAUSED_TEXT}。 */
  PAUSED: 'paused',
  /** 正在等待重连：文案固定为「重连中（第 N 次）…」。 */
  RECONNECTING: 'reconnecting',
  /** 用户点了「停止播放」：文案固定为 {@link STATUS_STOPPED_TEXT}。 */
  STOPPED: 'stopped',
});

/** 状态行分段之间的间隔符（"已连接"、"250 ms"这类分段共用一个写法）。 */
const STATUS_SEGMENT_SEPARATOR = ' · ';

/** 延迟分段的单位后缀（毫秒）。 */
const LATENCY_UNIT_SUFFIX = ' ms';

/** 状态行括号分段（追帧状态）的左括号。 */
const STATUS_PARENTHESIS_OPEN = '（';

/** 状态行括号分段（追帧状态）的右括号。 */
const STATUS_PARENTHESIS_CLOSE = '）';

/** 取不到真实延迟时状态行不再追加该分段，绝不显示占位数字。 */
const LATENCY_PLACEHOLDER = null;

/*
 * 合并后的「追帧 / 停止追帧」按钮只有一处入口，判定入口统一是 `shouldAutoChase`：
 *  - 按钮文案描述"点下去会发生什么"（`chaseButtonLabel`）：正在自动追帧时是「停止追帧」，否则是「追帧」；
 *  - 状态行末尾的括号分段描述"当前处在哪个状态"（`CHASE_STATUS_LABELS`）：追帧中直接给当前模式与
 *    目标档位（`modeLabel`），未追帧给固定的 `未开启追帧`，两种状态都有标记，
 *    用户才能一眼看出追帧到底开没开（只在"已停止"时加分段会让"正在追帧"无从确认）。
 */

/** 追帧按钮文案的两侧取值。 */
const CHASE_BUTTON_LABELS = Object.freeze({
  /** 当前未自动追帧：点击后开启自动追帧并立刻追一次帧。 */
  START: '追帧',
  /** 当前正在自动追帧：点击后停止自动追帧（不暂停播放）。 */
  STOP: '停止追帧',
});

/** 状态行末尾括号分段里的追帧状态取值。 */
const CHASE_STATUS_LABELS = Object.freeze({
  /** 已停止自动追帧（固定文案，此时括号里不再显示模式与目标档位）。 */
  STOPPED: '未开启追帧',
});

/** 停止追帧时写入 mpegts.js 的"永不触发"延迟阈值（秒）。 */
const CHASE_DISABLED_LATENCY_SECONDS = Number.MAX_SAFE_INTEGER;

/** 停止追帧时写入 hls.js 的最大延迟切片数（等价于不因延迟跳片）。 */
const CHASE_DISABLED_MAX_LATENCY_COUNT = Number.MAX_SAFE_INTEGER;

/** 停止追帧时的倍速追帧上限（1 表示不加速追赶）。 */
const CHASE_DISABLED_PLAYBACK_RATE = 1;

/*
 * 追帧配置的"引擎适配层"。
 *
 * 为什么需要它（SP-01 的根因）：播放页过去把「停止追帧 / 换档位」实现成裸调用 `player.configure(config)`，
 * 而**两家引擎的实例上都没有这个方法**（依据见下面的 ENGINE_CAPABILITIES 注释）：
 * 于是 autoChaseEnabled 与 extremeTargetMs 确实被改掉了、状态行与宿主上报也说"成功"，
 * 但 mpegts.js / hls.js 的运行时配置一个字都没变——"显示成功、实际没生效"。
 *
 * 现在的规则：
 *  1. 先看引擎能力：只有声明了 `hotConfig` 的引擎才允许走热改（改后必须读回校验）；
 *  2. 不支持热改（或不支持可靠读回）时走**受控重建**：保留候选、队列、会话号、暂停态、阶段、
 *     音量与追帧档位（见 {@link snapshotRuntimeState} / {@link restoreRuntimeState}），
 *     销毁旧实例后按新配置重建；
 *  3. 只有确认生效（`{ok: true}`）才允许调用方 `setStatus` 并上报宿主；
 *     失败一律上报失败并保持原状态（`{ok: false}`）。
 */

/** 播放引擎种类。 */
const ENGINE_KINDS = Object.freeze({
  /** mpegts.js（HTTP-FLV）。 */
  MPEGTS: 'mpegts',
  /** hls.js（HLS）。 */
  HLS: 'hls',
});

/*
 * 逐引擎的配置入口能力（**依据来自两份库内代码，不是猜测**）：
 *
 * mpegts.js（Web/mpegts.js，1.8.2，全文 0 次 `configure`）：
 *   - MSEPlayer 构造时创建 `_live_latency_chaser`（内部类，构造参数是 `this._config`），
 *     其 `_chaseLiveLatency()` **在运行期**读 `_config.liveBufferLatencyChasing` /
 *     `liveBufferLatencyMaxLatency` / `liveBufferLatencyMinRemain`；
 *     LiveSync 的 `_onMediaTimeUpdate` 同样在运行期读 `_config.liveSyncMaxLatency` /
 *     `liveSyncPlaybackRate`——所以"关闭"这一半改配置是有效的。
 *   - 但 `_live_latency_chaser` / `_live_sync_controller` **只在构造时按 `_config` 决定是否创建**，
 *     所以"从关闭改回开启"改配置无效；`enableStashBuffer` / `stashInitialSize` 等 MSE 侧选项同理。
 *   - 结论：mpegts.js 没有覆盖两个方向的运行时配置入口 → 走受控重建。
 *
 * hls.js（Web/hls.js，1.6.16，全文 1 个 `configure`，且只属于 Transmuxer；
 *   Hls 实例原型上**没有** configure）：
 *   - `Hls` 构造里 `this.config = mergeConfig(Hls.DefaultConfig, userConfig)`，
 *     各控制器（含 LatencyController）构造时保存 `this.config = hls.config` 这个**同一对象引用**，
 *     运行期逐次读取 `config.maxLiveSyncPlaybackRate` / `liveMaxLatencyDurationCount` /
 *     `maxBufferLength` 等。
 *   - 结论：改 `hls.config.*` 会真实生效，且可以立刻读回校验 → 走热改。
 *
 * 两家都不提供"官方热改 API"，所以热改路径必须**读回校验**（见 {@link verifyConfigApplied}）：
 * 读不回或值不符时降级为重建，绝不假装成功。
 */
const ENGINE_CAPABILITIES = Object.freeze({
  /** mpegts.js：没有覆盖两个方向的运行时配置入口，必须重建。 */
  [ENGINE_KINDS.MPEGTS]: Object.freeze({ hotConfig: false, rebuild: true }),
  /** hls.js：`hls.config.*` 在运行期被共享读取，可直接热改并读回校验。 */
  [ENGINE_KINDS.HLS]: Object.freeze({ hotConfig: true, rebuild: true }),
});

/** 适配层动作。 */
const ENGINE_APPLY_ACTIONS = Object.freeze({
  /** 直接改运行中实例的配置（改后读回校验）。 */
  HOT_APPLY: 'hot-apply',
  /** 销毁旧实例、按新配置重建并重新挂载。 */
  REBUILD: 'rebuild',
});

/** 适配层结论原因（供调用方决定文案与上报）。 */
const ENGINE_APPLY_REASONS = Object.freeze({
  /** 热改成功且读回校验通过。 */
  HOT_APPLIED: 'hot-applied',
  /** 只能热改、但运行中的实例缺可写配置对象。 */
  HOOK_MISSING: 'hook-missing',
  /** 重建成功。 */
  REBUILT: 'rebuilt',
  /** 引擎不支持热改且没有可用的重建回调。 */
  REBUILD_UNAVAILABLE: 'rebuild-unavailable',
  /** 重建回调未返回实例（创建失败，回调内已记日志）。 */
  REBUILD_FAILED: 'rebuild-failed',
  /** 热改调用抛出。 */
  HOT_APPLY_THREW: 'hot-apply-threw',
  /** 热改读回校验不通过（值未生效或读不回来）。 */
  VERIFY_FAILED: 'verify-failed',
});

/** 重建时必须跨越"销毁 / 创建"保留的字段（见 {@link snapshotRuntimeState}）。 */
const RUNTIME_STATE_KEYS = Object.freeze([
  /** 当前候选：重建后仍挂同一条地址。 */
  'currentCandidate',
  /** 候选队列：不能因为重建丢掉后面的线路。 */
  'queue',
  /** 会话号：重建不是新会话，宿主侧不能看到第二个会话。 */
  'sessionId',
  /** 用户是否主动暂停：重建不得把暂停变成播放。 */
  'pausedByUser',
  /** 状态行阶段：重建期间不得闪回"播放中"。 */
  'statusPhase',
  /** 音量百分比：由调用方写进快照（销毁后 `video.volume` 会被重置）。 */
  'volumePercent',
  /** 追帧档位（毫秒）。 */
  'targetMs',
  /** 模式（extreme / stable）：重建后状态行文案不得变样。 */
  'mode',
  /** 是否极限档（`mode` 的布尔镜像）。 */
  'extreme',
  /** 重建前的播放位置（秒）：回到同一位置，避免"跳回起播点"。 */
  'positionSeconds',
]);

/**
 * 判断某个引擎是否支持在运行期直接改配置（能力表判定入口）。
 * @param {string} engineKind 引擎种类（{@link ENGINE_KINDS}）。
 * @returns {boolean} 支持热改返回 true。
 */
function canHotApplyEngineConfig(engineKind) {
  const capability = ENGINE_CAPABILITIES[engineKind];
  return Boolean(capability && capability.hotConfig === true);
}

/*
 * 宿主状态消息（`host-status`）在画面下方状态行上的保留时长。
 *
 * 为什么需要"保留"：状态行同时要显示页面自己的播放状态（"播放中（稳定缓冲）"），
 * 而宿主的失败原因（"解析失败：主播未开播"）更需要用户读到。规则是
 * "宿主消息覆盖显示，超时后回落播放状态"；级别越高保留越久——错误原因需要阅读时间，
 * 而操作反馈（"已新增预设"）看到即可。
 */

/** 宿主信息类状态（操作反馈）的保留时长（毫秒）。 */
const HOST_STATUS_HOLD_INFO_MS = 6000;

/** 宿主警告类状态的保留时长（毫秒）。 */
const HOST_STATUS_HOLD_WARN_MS = 12000;

/** 宿主错误类状态的保留时长（毫秒）。 */
const HOST_STATUS_HOLD_ERROR_MS = 20000;

/** 级别 → 状态行保留时长（毫秒）；未列出的级别按信息类处理。 */
const HOST_STATUS_HOLD_MS = Object.freeze({
  info: HOST_STATUS_HOLD_INFO_MS,
  warn: HOST_STATUS_HOLD_WARN_MS,
  error: HOST_STATUS_HOLD_ERROR_MS,
});

/** 状态行内容的来源：宿主状态消息或页面自己的播放状态。 */
const STATUS_LINE_SOURCES = Object.freeze({
  HOST: 'host',
  PLAYBACK: 'playback',
});

/** 页面消息类型：宿主 → 页面。 */
const INBOUND_MESSAGE_TYPES = Object.freeze({
  PLAY: 'play',
  CHASE: 'chase',
  TARGET: 'target',
  PAUSE: 'pause',
  MPV: 'mpv',
  /** 宿主状态文案：左栏「当前直播」卡片删除后，宿主的状态文字只在画面下方状态行显示。 */
  HOST_STATUS: 'host-status',
  /** 兼容保留：旧宿主仍可能下发 stop（语义等同"结束会话"）。 */
  STOP: 'stop',
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
  /**
   * 页面在底部档位控件里改了追帧档位：宿主据此更新并持久化，下次播放沿用。
   * 与宿主侧的 `TargetType` 同名，宿主按 `extremeTargetMs` 字段取值。
   */
  TARGET: 'target',
  REQUEST_PLAY: 'request-play',
  TOGGLE_PAUSE: 'toggle-pause',
  /** 页面请求停止播放：销毁播放器、释放会话与地址，宿主据此回收中继。 */
  REQUEST_STOP: 'request-stop',
  /** 页面日志：页面本身不再显示日志面板，所有诊断文本只进宿主日志。 */
  LOG: 'log',
});

/** 页面日志级别（与宿主日志级别同名，宿主据此选择落盘级别）。 */
const LOG_LEVELS = Object.freeze({
  INFO: 'info',
  WARN: 'warn',
  ERROR: 'error',
});

/**
 * 把宿主传入的目标延迟归一化为秒。
 * @param {*} value 宿主传入的 extremeTargetMs。
 * @returns {number} 150/200/250 对应 0.15/0.2/0.25；其他值回落为 0.25。
 */
function normalizeExtremeTargetSeconds(value) {
  const milliseconds = Number(value);
  return EXTREME_TARGETS_MS.includes(milliseconds) ? milliseconds / 1000 : DEFAULT_EXTREME_TARGET_MS / 1000;
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
 *
 * 逐项与参考播放页（`startCandidate` 里的 config 字面量）保持一致：
 * `enableStashBuffer`/`stashInitialSize` 越档位而变（极限档关闭 stash 才能贴住延迟下限），
 * `liveBufferLatencyMaxLatency`/`liveBufferLatencyMinRemain` 决定硬跳的时机与落点，
 * `liveSyncPlaybackRate` 决定倍速追帧的上限——三者只要有一个偏离参考值，
 * 画面就会在"硬跳太频繁（一顿一顿）"与"追不上（越来越滞后）"之间摆动。
 * @param {boolean} extreme 是否极限追帧模式。
 * @param {number} targetSeconds 目标延迟（秒）。
 * @param {boolean} [autoChase] 是否保留库内的自动追帧（硬跳 + 倍速）；false 时两路都关掉。
 * @returns {object} mpegts.js 配置对象。
 */
function buildMpegtsConfig(extreme, targetSeconds, autoChase) {
  const chasing = autoChase === undefined ? true : autoChase === true;

  /*
    取消自动追帧的意义不是"把阈值调大一点"，而是把两路自动行为都关掉：
    `liveBufferLatencyChasing` 控制 updateend 时的硬跳，`liveSync` 控制 timeupdate 时的倍速。
    阈值同时写入永不触发的值，避免旧版本库忽略这两个开关时仍然跳帧。
    逐帧清理（autoCleanupSourceBuffer）不受影响，缓冲不会无限占内存。
  */
  const latencyMax = extreme
    ? Math.max(0.35, targetSeconds + LATENCY_MAX_MARGIN_SECONDS)
    : STABLE_LATENCY_MAX_SECONDS;
  const syncMax = extreme
    ? Math.max(0.22, targetSeconds + LIVE_SYNC_MAX_MARGIN_SECONDS)
    : STABLE_LATENCY_MAX_SECONDS;

  return {
    isLive: true,
    enableWorker: true,
    enableStashBuffer: !extreme,
    stashInitialSize: extreme ? EXTREME_STASH_INITIAL_SIZE : STABLE_STASH_INITIAL_SIZE,
    lazyLoad: false,
    deferLoadAfterSourceOpen: false,
    autoCleanupSourceBuffer: true,
    liveBufferLatencyChasing: chasing,
    liveBufferLatencyMaxLatency: chasing ? latencyMax : CHASE_DISABLED_LATENCY_SECONDS,
    liveBufferLatencyMinRemain: extreme ? targetSeconds : STABLE_LATENCY_MIN_REMAIN_SECONDS,
    liveSync: chasing && extreme,
    liveSyncMaxLatency: chasing ? syncMax : CHASE_DISABLED_LATENCY_SECONDS,
    liveSyncTargetLatency: extreme ? targetSeconds : 0.8,
    liveSyncPlaybackRate: extreme && chasing ? LIVE_SYNC_PLAYBACK_RATE : CHASE_DISABLED_PLAYBACK_RATE,
    fixAudioTimestampGap: true,
  };
}

/**
 * 计算 hls.js 的播放配置。注意：HLS 路径不区分 150/200/250 三档。
 * @param {boolean} extreme 是否极限追帧模式。
 * @param {boolean} [autoChase] 是否保留 hls.js 的延迟同步（倍速追赶）；false 时按正常倍速播放。
 * @returns {object} hls.js 配置对象。
 */
function buildHlsConfig(extreme, autoChase) {
  const chasing = autoChase === undefined ? true : autoChase === true;
  const maxLatencyCount = extreme ? HLS_MAX_LATENCY_COUNT_EXTREME : HLS_MAX_LATENCY_COUNT_STABLE;

  return {
    enableWorker: true,
    lowLatencyMode: HLS_LOW_LATENCY_MODE,
    backBufferLength: HLS_BACK_BUFFER_LENGTH,
    maxBufferLength: extreme ? HLS_MAX_BUFFER_EXTREME : HLS_MAX_BUFFER_STABLE,
    liveSyncDurationCount: extreme ? HLS_LIVE_SYNC_DURATION_COUNT_EXTREME : HLS_LIVE_SYNC_DURATION_COUNT_STABLE,
    /*
      hls.js 没有"关闭延迟同步"的开关，能表达"不自动追帧"的只有两处：
      `maxLiveSyncPlaybackRate = 1`（不加速追赶）与把最大延迟切片数放到不可能达到的值
      （不因延迟而跳片）。两者同时给出，停止追帧后画面就以正常倍速连续播放。
    */
    liveMaxLatencyDurationCount: chasing ? maxLatencyCount : CHASE_DISABLED_MAX_LATENCY_COUNT,
    maxLiveSyncPlaybackRate: chasing ? HLS_MAX_LIVE_SYNC_PLAYBACK_RATE : CHASE_DISABLED_PLAYBACK_RATE,
  };
}

/**
 * 判定当前是否应当自动追帧（把延迟压回目标值）。
 *
 * 语义（与「暂停播放」严格区分）：
 *  - **自动追帧**：库内硬跳（mpegts.js 延迟追帧器）与倍速追赶（延迟同步器）按目标延迟把
 *    画面拉回直播边缘。默认开启。
 *  - **停止追帧**：关掉上面两路自动行为，画面以正常倍速实时推进；**不暂停播放、不销毁播放器、
 *    不释放地址**，因此重新开启时不需要重新解析。它只是为了"宁可延迟大一点也不要跳帧"。
 *  - **暂停播放**：`video.pause()`，画面完全停止；恢复时可能因缓冲过期而追帧。
 * 页面上合并后的「追帧 / 停止追帧」按钮是唯一开关：点「停止追帧」关闭它，点「追帧」开启并立刻追一次帧。
 * 缺省（字段未设置）视为开启，这样旧的调用点与旧会话不会因为字段缺失而被当成"已停止"。
 *
 * 注意：停止追帧只停"自动追帧策略"，不停"缓冲失控自救"（{@link isBufferRunaway}）——
 * 后者是兜底保护，只在画面真的停住且缓冲涨到 8 秒以上时才介入。
 * @param {{autoChaseEnabled?:boolean}|null} run 运行对象。
 * @returns {boolean} 应当自动追帧返回 true。
 */
function shouldAutoChase(run) {
  if (!run || typeof run !== 'object') {
    return false;
  }

  return run.autoChaseEnabled !== false;
}

/**
 * 判断延迟是否是**可用**的实测值。
 *
 * 只认有限且非负的数字：`null` / `undefined` / `NaN` / `Infinity` / 负数 / 非数字字符串
 * 都不是有效测量，调用方据此不显示延迟分段。
 * @param {*} latencyMs 待校验的延迟毫秒数。
 * @returns {boolean} 可用返回 true。
 */
function isValidLatencyMs(latencyMs) {
  if (latencyMs === null || latencyMs === undefined) {
    return false;
  }

  const latency = Number(latencyMs);
  return Number.isFinite(latency) && latency >= 0;
}

/**
 * 生成状态行的延迟分段（`318 ms`）。
 *
 * 取不到有效实测值时返回 `null`（而不是空串），调用方据此**整段省略**，绝不显示占位数字。
 * @param {*} latencyMs 实际延迟毫秒数；非法值返回 null。
 * @returns {string|null} 延迟分段；无有效测量值返回 null。
 */
function formatLatencySegment(latencyMs) {
  return isValidLatencyMs(latencyMs) ? Math.round(Number(latencyMs)) + LATENCY_UNIT_SUFFIX : null;
}

/**
 * 用统一分隔符拼接状态行的各个分段，跳过取不到的分段。
 * @param {Array<string|null|undefined>} segments 分段列表（顺序即显示顺序）。
 * @returns {string} 拼接后的文本。
 */
function joinStatusSegments(segments) {
  return segments.filter(function (segment) {
    return Boolean(segment);
  }).join(STATUS_SEGMENT_SEPARATOR);
}

/**
 * 读取运行对象上最近一次遥测得到的真实延迟（毫秒）。
 *
 * 只认页面写进去的实测值：没有任何遥测时返回 {@link LATENCY_PLACEHOLDER}，
 * 调用方据此不显示延迟分段。刻意**不**从追帧档位推导——档位是目标值，不是实际延迟。
 * @param {{lastLatencyMs?:number}|null} run 运行对象。
 * @returns {number|null} 实际延迟毫秒数；没有有效测量值时返回 null。
 */
function readActualLatencyMs(run) {
  if (!run || typeof run !== 'object') {
    return LATENCY_PLACEHOLDER;
  }

  return isValidLatencyMs(run.lastLatencyMs) ? Number(run.lastLatencyMs) : LATENCY_PLACEHOLDER;
}

/**
 * 合并后的追帧按钮文案（「追帧」/「停止追帧」）。
 * @param {*} state 当前是否自动追帧（{@link shouldAutoChase} 的结果）。
 * @returns {string} 按钮文案：正在自动追帧时为「停止追帧」，否则为「追帧」。
 */
function chaseButtonLabel(state) {
  return state ? CHASE_BUTTON_LABELS.STOP : CHASE_BUTTON_LABELS.START;
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
 *
 * 与参考播放页一致：极限档用更短的阈值（饥饿 4s / 硬卡 6.5s），稳定档用 6s / 9s。
 * 极限档的目标延迟本来就贴近物理下限，buffer 见底是预期；但真的停住时要更快恢复。
 * @param {boolean} looksStarved 是否处于饥饿状态（无缓冲或 readyState 偏低）。
 * @param {boolean} extreme 是否极限追帧模式。
 * @returns {number} 判定阈值毫秒数。
 */
function getStallThresholdMs(looksStarved, extreme) {
  if (extreme) {
    return looksStarved ? EXTREME_STALL_THRESHOLD_STARVED_MS : EXTREME_STALL_THRESHOLD_IDLE_MS;
  }

  return looksStarved ? STALL_THRESHOLD_STARVED_MS : STALL_THRESHOLD_IDLE_MS;
}

/**
 * 判断当前是否应当按"画面停滞"触发重连。
 *
 * **不要**用媒体元素自身的 `paused` 当"用户暂停"的门闩：元素被非用户原因暂停
 * （播放器重建时 `destroyPlayer` 会先 `pause()`、内核也可能暂停元素）时，
 * 用它做门闩会让恢复分支永久失效——真机实测正是"缓冲涨到 100 秒、`reconnects` 恒为 0"。
 * 只有页面自己记录的"用户点了暂停"才是权威的用户意图。
 * @param {number} silenceMs 画面进度停止前进的时长（毫秒）。
 * @param {boolean} starved 是否处于缓冲饥饿。
 * @param {number} resumedAt 最近一次手动恢复播放的时间戳（毫秒，0 表示没有）。
 * @param {number} now 当前时间戳（毫秒）。
 * @param {boolean} extreme 是否极限追帧模式。
 * @param {boolean} pausedByUser 用户是否主动暂停。
 * @returns {boolean} 应当重连返回 true。
 */
function isPlaybackStalled(silenceMs, starved, resumedAt, now, extreme, pausedByUser) {
  // 用户手动暂停时画面本来就不前进，这不是卡顿。
  if (pausedByUser) {
    return false;
  }

  // 刚点过"继续播放"时播放器正在重建缓冲，这段时间的静止属于预期。
  if (resumedAt > 0 && Number(now) - Number(resumedAt) < RESUME_GRACE_MS) {
    return false;
  }

  return Number(silenceMs) >= getStallThresholdMs(starved, extreme);
}

/**
 * 判断本地缓冲是否"失控"：缓冲远大于目标延迟，而画面已经不再前进。
 * @param {number|null} bufferedAheadSeconds 本地缓冲剩余秒数（见 {@link getLocalBufferSeconds}）。
 * @param {number} silenceMs 画面停滞时长（毫秒）。
 * @param {boolean} pausedByUser 用户是否主动暂停（暂停时缓冲增长属于预期，不处置）。
 * @returns {boolean} 失控返回 true。
 */
function isBufferRunaway(bufferedAheadSeconds, silenceMs, pausedByUser) {
  if (pausedByUser) {
    return false;
  }

  const ahead = Number(bufferedAheadSeconds);
  if (bufferedAheadSeconds === null || !Number.isFinite(ahead)) {
    return false;
  }

  return ahead > BUFFER_RUNAWAY_AHEAD_SECONDS && Number(silenceMs) >= BUFFER_RUNAWAY_SILENCE_MS;
}

/**
 * 决定缓冲失控时先做哪一步。
 *
 * 顺序：元素被"非用户原因"暂停 → 先恢复播放（否则任何追帧都不会被消费）；
 * 否则 → 追帧到缓冲末端；自行处置用尽或刚处置完还在生效期内 → 交给既有重连 / 切候选。
 * @param {number|null} bufferedAheadSeconds 本地缓冲剩余秒数。
 * @param {number} silenceMs 画面停滞时长（毫秒）。
 * @param {boolean} pausedByUser 用户是否主动暂停。
 * @param {boolean} elementPaused 媒体元素自身的 `paused`（只用于判断"是否需要恢复播放"，不当作暂停意图）。
 * @param {number} chaseAttempts 本次失控已经自行处置的次数。
 * @param {number} sinceHandledMs 距上一次自行处置的毫秒数（未处置过传 `Number.POSITIVE_INFINITY`）。
 * @returns {string} {@link RUNAWAY_ACTIONS} 之一。
 */
function decideBufferRunawayAction(bufferedAheadSeconds, silenceMs, pausedByUser, elementPaused, chaseAttempts, sinceHandledMs) {
  if (!isBufferRunaway(bufferedAheadSeconds, silenceMs, pausedByUser)) {
    return RUNAWAY_ACTIONS.NONE;
  }

  const attempts = Number(chaseAttempts);
  const waited = Number(sinceHandledMs);

  // 刚追过帧：等它生效（seek 在大缓冲上需要数百毫秒到数秒），期间不重复处置也不升级。
  if (Number.isFinite(waited) && waited < BUFFER_RUNAWAY_CHASE_GRACE_MS) {
    return RUNAWAY_ACTIONS.NONE;
  }

  if (!Number.isFinite(attempts) || attempts >= BUFFER_RUNAWAY_MAX_CHASE_ATTEMPTS) {
    return RUNAWAY_ACTIONS.RECONNECT;
  }

  return elementPaused ? RUNAWAY_ACTIONS.RESUME : RUNAWAY_ACTIONS.CHASE;
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
 * @param {{readyState:number}} video 视频元素状态快照。
 * @param {number|null} localBufferSeconds 本地缓冲剩余秒数。
 * @returns {boolean} 饥饿返回 true。
 */
function looksStarved(video, localBufferSeconds) {
  if (!video) {
    return true;
  }

  // 只按"还能不能继续解码"判断：paused / ended 是播放意图，不是饥饿，
  // 把它们算作饥饿会让用户暂停时被误判成卡顿并触发重连。
  return video.readyState < READY_STATE_PLAYABLE
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
 * 状态行末尾括号分段里的追帧状态。
 * @param {*} run 运行对象（带 `autoChaseEnabled`）。
 * @returns {string} 正在追帧时为当前模式与目标档位（{@link modeLabel}），停止追帧时为 `未开启追帧`。
 */
function chaseStatusLabel(run) {
  return shouldAutoChase(run) ? modeLabel(run) : CHASE_STATUS_LABELS.STOPPED;
}

/**
 * 底部状态行在播放开始后的文案（不显示候选主机名与 CDN 节点）。
 *
 * 结构固定为「播放中 + 可选 ` · N ms` + `（追帧状态）`」：
 *  - 延迟分段紧跟"播放中"，取自遥测**实测值**（{@link readActualLatencyMs}），
 *    取不到就不显示该段，绝不写死档位数字冒充实测；
 *  - 括号分段永远存在且放在最后（{@link chaseStatusLabel}），追帧中给出当前模式与目标档位，
 *    停止追帧给出 `未开启追帧`。
 *
 * 本函数只负责"播放中"这一支；用户暂停 / 重连 / 停止 / 未连接这些操作态由
 * {@link resolvePlaybackStatusText} 判定，页面不得绕开它直接把本函数的结果写进状态行。
 * @param {{mode?:string,extremeTargetMs?:number,extremeTargetSeconds?:number,autoChaseEnabled?:boolean,lastLatencyMs?:number}} run 运行对象。
 * @returns {string} 中文状态。
 */
function formatPlaybackStatusText(run) {
  const chaseSegment = STATUS_PARENTHESIS_OPEN + chaseStatusLabel(run) + STATUS_PARENTHESIS_CLOSE;
  return joinStatusSegments([MODE_HINT_PLAYING, formatLatencySegment(readActualLatencyMs(run))]) + chaseSegment;
}

/**
 * 重连状态行文案。
 * @param {*} attempt 第几次重连（从 1 开始）；缺失或非法时按第 1 次。
 * @returns {string} 形如 `重连中（第 2 次）…` 的文案。
 */
function formatReconnectingStatusText(attempt) {
  const numeric = Math.round(Number(attempt));
  const normalized = Number.isFinite(numeric) && numeric >= FIRST_RECONNECT_ATTEMPT
    ? numeric
    : FIRST_RECONNECT_ATTEMPT;
  return STATUS_RECONNECTING_PREFIX + normalized + STATUS_RECONNECTING_SUFFIX;
}

/**
 * 计算状态行里"页面自己的那一份"文案（**操作态优先，播放中遥测垫底**）。
 *
 * 这是页面播放态文案的唯一判定入口，遥测每轮（{@link TELEMETRY_INTERVAL_MS}）都调它，
 * 因此它必须保证"数值刷新不会覆盖操作态"：
 *  1. 用户暂停（`pausedByUser`）→ {@link STATUS_PAUSED_TEXT}；
 *  2. 停止（阶段为 STOPPED 或运行对象已标记 `stopped`）→ {@link STATUS_STOPPED_TEXT}；
 *  3. 重连中 → `重连中（第 N 次）…`（{@link formatReconnectingStatusText}）；
 *  4. 未连接（阶段为 IDLE 或没有运行对象）→ {@link STATUS_IDLE_TEXT}；
 *  5. 播放中 → {@link formatPlaybackStatusText}（实测延迟 + 追帧括号分段），只有这一支会被遥测改写。
 * 宿主消息（`host-status`）比以上全部更高，由 {@link resolveStatusLine} 负责覆盖与超时回落，
 * 完整顺序是：宿主消息 > 用户暂停 > 重连 / 停止 / 未连接 > 播放中遥测。
 * @param {{mode?:string,extremeTargetMs?:number,extremeTargetSeconds?:number,autoChaseEnabled?:boolean,lastLatencyMs?:number,stopped?:boolean}|null} run 运行对象。
 * @param {{statusPhase?:string,pausedByUser?:boolean,reconnectAttempt?:number}|null} session 页面会话态（{@link PLAYBACK_PHASES}）。
 * @returns {string} 状态行文案。
 */
function resolvePlaybackStatusText(run, session) {
  const page = session && typeof session === 'object' ? session : {};
  const phase = typeof page.statusPhase === 'string' ? page.statusPhase : PLAYBACK_PHASES.IDLE;

  if (page.pausedByUser === true || phase === PLAYBACK_PHASES.PAUSED) {
    return STATUS_PAUSED_TEXT;
  }

  if (phase === PLAYBACK_PHASES.STOPPED || Boolean(run && run.stopped)) {
    return STATUS_STOPPED_TEXT;
  }

  if (phase === PLAYBACK_PHASES.RECONNECTING) {
    return formatReconnectingStatusText(page.reconnectAttempt);
  }

  if (phase !== PLAYBACK_PHASES.PLAYING || !run) {
    return STATUS_IDLE_TEXT;
  }

  return formatPlaybackStatusText(run);
}

/**
 * 归一化宿主状态消息的级别。
 * @param {*} level 宿主下发的 level 字段。
 * @returns {string} `info` / `warn` / `error`；未知或缺失一律回落 `info`。
 */
function normalizeStatusLevel(level) {
  const text = String(level || '').toLowerCase();
  return Object.prototype.hasOwnProperty.call(HOST_STATUS_HOLD_MS, text) ? text : LOG_LEVELS.INFO;
}

/**
 * 读取宿主状态消息按级别对应的状态行保留时长。
 * @param {*} level 状态级别。
 * @returns {number} 保留毫秒数。
 */
function getHostStatusHoldMs(level) {
  return HOST_STATUS_HOLD_MS[normalizeStatusLevel(level)];
}

/**
 * 计算宿主状态消息在状态行上还剩多少显示时间。
 * @param {{message?:string,level?:string,receivedAt?:number}|null} hostStatus 最近的宿主状态消息。
 * @param {number} now 当前时间戳（毫秒）。
 * @returns {number} 剩余毫秒数；没有有效消息或已过期返回 0。
 */
function getHostStatusRemainingMs(hostStatus, now) {
  if (!hostStatus || typeof hostStatus.message !== 'string' || hostStatus.message.trim().length === 0) {
    return 0;
  }

  const receivedAt = Number(hostStatus.receivedAt);
  if (!Number.isFinite(receivedAt)) {
    return 0;
  }

  return Math.max(0, receivedAt + getHostStatusHoldMs(hostStatus.level) - Number(now));
}

/**
 * 计算状态行当前应显示的内容。
 *
 * 优先级规则（状态行唯一的显示判定入口，便于测试）：
 *  1. 宿主消息仍在保留期内 → 显示宿主消息，级别用宿主给的（warn/error 换成警示色）；
 *  2. 宿主消息已过期或从未收到 → 显示页面自己的播放状态，级别固定为 `info`（蓝色）。
 * 这样"解析失败：主播未开播"不会被随后的"播放中（稳定缓冲）"盖掉（失败原因更重要），
 * 也不会永久占住状态行（用户回到画面时仍能看到直播延迟档位）。
 * @param {{message?:string,level?:string,receivedAt?:number}|null} hostStatus 最近的宿主状态消息。
 * @param {string} playbackText 页面自己的播放状态文案。
 * @param {number} now 当前时间戳（毫秒）。
 * @returns {{text:string,level:string,source:string}} 显示文本、级别与来源（{@link STATUS_LINE_SOURCES}）。
 */
function resolveStatusLine(hostStatus, playbackText, now) {
  if (getHostStatusRemainingMs(hostStatus, now) > 0) {
    return {
      text: hostStatus.message,
      level: normalizeStatusLevel(hostStatus.level),
      source: STATUS_LINE_SOURCES.HOST,
    };
  }

  return {
    text: String(playbackText || ''),
    level: LOG_LEVELS.INFO,
    source: STATUS_LINE_SOURCES.PLAYBACK,
  };
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
 * 判断 mpegts.js 触发 LOADING_COMPLETE 后是否应当重连。
 *
 * 直播流没有"下载结束"这回事：取满缓冲后 mpegts.js 也会报告一次 LOADING_COMPLETE，
 * 此时重连会白白新建连接并让画面重新起播，看起来就是固定间隔的卡顿。
 * 只有画面确实不再前进（超过本模式的停滞阈值）或缓冲已空时才按断流处理。
 *
 * 阈值按模式取（极限档 4s / 6.5s，稳定档 6s / 9s），与参考播放页的分模式阈值一致：
 * 极限档一旦真的停住，等 9 秒才恢复就是"卡住好几秒"。
 * @param {object} run 运行对象（含 playbackStarted / lastPlaybackProgressAt / extreme）。
 * @param {number} now 当前时间戳（毫秒）。
 * @param {boolean} starved 是否处于缓冲饥饿。
 * @returns {boolean} 应当重连返回 true。
 */
function shouldRecoverAfterLoadingComplete(run, now, starved) {
  if (!run || !run.playbackStarted) {
    return false;
  }

  const silenceMs = Number(now) - Number(run.lastPlaybackProgressAt || 0);
  return starved || silenceMs >= getStallThresholdMs(false, Boolean(run.extreme));
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
      extremeTargetSeconds: extreme ? normalizeExtremeTargetSeconds(payload.extremeTargetMs) : DEFAULT_EXTREME_TARGET_MS / 1000,
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
 * 计算某个候选对应的播放引擎。
 * @param {{format?:string}} candidate 候选。
 * @returns {string} {@link ENGINE_KINDS} 之一。
 */
function engineKindForCandidate(candidate) {
  return isHlsCandidate(candidate) ? ENGINE_KINDS.HLS : ENGINE_KINDS.MPEGTS;
}

/**
 * 按候选与追帧档位推导该引擎的运行时配置（热改与重建共用同一份推导）。
 *
 * 这是"页面只做一次推导"的落点：hls.js 配置从 `buildHlsConfig` 来，mpegts.js 配置从
 * `buildMpegtsConfig` 来，页面的薄调用层不再自己挑函数（过去挑错了就会"改了但没生效"）。
 * @param {object} run 运行对象（含 extreme / extremeTargetSeconds / autoChaseEnabled）。
 * @param {{format?:string}} candidate 当前候选。
 * @returns {object} 引擎配置对象。
 */
function buildEngineConfig(run, candidate) {
  const autoChase = shouldAutoChase(run);
  if (engineKindForCandidate(candidate) === ENGINE_KINDS.HLS) {
    return buildHlsConfig(Boolean(run && run.extreme), autoChase);
  }

  return buildMpegtsConfig(Boolean(run && run.extreme), run ? run.extremeTargetSeconds : undefined, autoChase);
}

/**
 * 取出重建时必须保留的运行时状态快照。
 *
 * 只拷贝 {@link RUNTIME_STATE_KEYS} 里的字段：重建不是新会话，候选 / 队列 / 会话号 /
 * 暂停态 / 状态阶段 / 音量 / 追帧档位都不能因为"销毁再创建"而丢失或复位
 * （丢失队列会让"换档位"顺带把后面的线路全扔掉，复位暂停态会让用户按下的暂停被悄悄取消）。
 * @param {object} run 运行对象。
 * @param {{pausedByUser?:boolean,statusPhase?:string}|null} session 页面会话态。
 * @param {{volumePercent?:number,positionSeconds?:number}} [media] 媒体侧补充值（音量 / 播放位置）。
 * @returns {object} 快照对象。
 */
function snapshotRuntimeState(run, session, media) {
  const extra = media && typeof media === 'object' ? media : {};
  return {
    currentCandidate: run ? run.currentCandidate : null,
    queue: run && Array.isArray(run.queue) ? run.queue.slice() : [],
    sessionId: run ? run.sessionId : 0,
    pausedByUser: Boolean(session && session.pausedByUser === true),
    statusPhase: session && typeof session.statusPhase === 'string' ? session.statusPhase : PLAYBACK_PHASES.IDLE,
    volumePercent: clampVolumePercent(extra.volumePercent),
    targetMs: run && Number.isFinite(run.extremeTargetMs) ? run.extremeTargetMs : DEFAULT_EXTREME_TARGET_MS,
    mode: run && run.mode === 'extreme' ? 'extreme' : 'stable',
    extreme: Boolean(run && run.extreme === true),
    positionSeconds: Number.isFinite(Number(extra.positionSeconds)) ? Number(extra.positionSeconds) : 0,
  };
}

/**
 * 把快照写回运行对象与页面会话态（重建后的"恢复原状"入口）。
 *
 * 注意：**不**改 `run.playbackStarted` / `run.everPlayed`——重建前已经出过画，
 * 清掉它们会让首帧超时判定与空态提示一起走错分支（表现为"重建后提示又弹回等待直播源"）。
 * @param {object} run 运行对象。
 * @param {object} session 页面会话态。
 * @param {object} snapshot {@link snapshotRuntimeState} 的结果。
 * @returns {void}
 */
function restoreRuntimeState(run, session, snapshot) {
  if (!snapshot || typeof snapshot !== 'object') {
    return;
  }

  if (run) {
    run.currentCandidate = snapshot.currentCandidate;
    run.queue = Array.isArray(snapshot.queue) ? snapshot.queue.slice() : [];
    run.sessionId = snapshot.sessionId;
    run.extremeTargetMs = snapshot.targetMs;
    run.extremeTargetSeconds = snapshot.targetMs / 1000;
    run.mode = snapshot.mode;
    run.extreme = snapshot.extreme;
  }

  if (session) {
    session.pausedByUser = snapshot.pausedByUser;
    session.statusPhase = snapshot.statusPhase;
  }
}

/**
 * 读取引擎当前生效的配置对象（热改与读回校验的唯一入口）。
 * @param {object} player 引擎实例。
 * @returns {object|null} 可写配置对象；该引擎没有运行期配置入口时返回 null。
 */
function readEngineConfig(player) {
  if (!player || typeof player !== 'object') {
    return null;
  }

  // hls.js：构造里把 `this.config` 指向合并后的配置对象，各控制器共享同一引用。
  if (player.config && typeof player.config === 'object') {
    return player.config;
  }

  // mpegts.js：库内一律读 `_config`（私有名，只用于读回校验，见能力表注释）。
  if (player._config && typeof player._config === 'object') {
    return player._config;
  }

  return null;
}

/**
 * 把配置写进引擎当前生效的配置对象。
 * @param {object} target 配置对象（{@link readEngineConfig} 的结果）。
 * @param {object} config 目标配置。
 * @returns {void}
 */
function writeEngineConfig(target, config) {
  for (const [key, value] of Object.entries(config)) {
    target[key] = value;
  }
}

/**
 * 读回校验：逐键比较引擎当前配置与目标配置。
 * @param {object} player 引擎实例。
 * @param {object} config 目标配置。
 * @returns {boolean} 每个键都读得回且相等返回 true；读不回或有不一致返回 false。
 */
function verifyConfigApplied(player, config) {
  const current = readEngineConfig(player);
  if (!current) {
    return false;
  }

  for (const [key, value] of Object.entries(config)) {
    if (current[key] !== value) {
      return false;
    }
  }

  return true;
}

/**
 * 按引擎能力应用一份追帧配置：能热改就热改并读回校验，否则走受控重建。
 *
 * 返回值的 `ok` 是**唯一**允许触发"成功"文案与上报的依据：只有它才代表配置真的生效了
 * （热改读回一致，或重建拿到了新实例）。`report` 字段预留一个布尔位，方便调用方与替身断言
 * "成功/失败各自只上报一次"。
 * @param {object} options 适配参数。
 * @param {string} options.engineKind 引擎种类（{@link ENGINE_KINDS}）。
 * @param {object|null} options.player 运行中的引擎实例（无会话时为 null）。
 * @param {object} options.config 目标配置（{@link buildEngineConfig} 的结果）。
 * @param {Function|null} [options.rebuild] 受控重建回调：`() => player|null`。
 * @param {object} [options.snapshot] 重建前必须保留的状态快照。
 * @returns {{ok:boolean,action:string,reason:string,report:boolean,player:object|null,config:object,snapshot:object|null}} 结论。
 */
function applyEngineConfig(options) {
  const settings = options && typeof options === 'object' ? options : {};
  const config = settings.config && typeof settings.config === 'object' ? settings.config : {};
  const snapshot = settings.snapshot && typeof settings.snapshot === 'object' ? settings.snapshot : null;
  const capability = ENGINE_CAPABILITIES[settings.engineKind];

  // 热改是否可用：只有声明了 hotConfig、且运行中的实例有可写配置对象时才是 true。
  // 任一条件不满足就落到下面的受控重建：过去这里直接 return 失败，等于"能重建也没试"。
  const hotConfigPath = capability
    && capability.hotConfig === true
    && readEngineConfig(settings.player) !== null;

  if (hotConfigPath) {
    const hook = readEngineConfig(settings.player);
    try {
      writeEngineConfig(hook, config);
    } catch (error) {
      // 写入抛出：降级为重建（下面统一处理），并把原因留给调用方日志。
      return rebuildEngineConfig(settings, config, snapshot, ENGINE_APPLY_REASONS.HOT_APPLY_THREW);
    }

    if (verifyConfigApplied(settings.player, config)) {
      return {
        ok: true,
        action: ENGINE_APPLY_ACTIONS.HOT_APPLY,
        reason: ENGINE_APPLY_REASONS.HOT_APPLIED,
        report: true,
        player: settings.player,
        config: config,
        snapshot: snapshot,
      };
    }

    // 写入没抛异常但值没生效：同样降级为重建，绝不假装成功。
    return rebuildEngineConfig(settings, config, snapshot, ENGINE_APPLY_REASONS.VERIFY_FAILED);
  }

  if (capability && capability.hotConfig === true) {
    // 只支持热改的引擎却缺运行中配置对象：没有可重建的余地。
    return rebuildEngineConfig(settings, config, snapshot, ENGINE_APPLY_REASONS.HOOK_MISSING);
  }

  return rebuildEngineConfig(settings, config, snapshot, ENGINE_APPLY_REASONS.REBUILD_UNAVAILABLE);
}

/**
 * 走**受控重建**路径：没有重建回调时如实返回失败原因。
 *
 * 失败的"原因"按降级来源区分（热改抛异常 / 读回校验不通过 / 没有重建回调），
 * 但如果引擎支持重建且回调可用，就一律重建——这条"能重建就重建"的规则是 SP-01 的关键：
 * 两家引擎都没有官方热改 API，重建是唯一保证配置真的生效的手段。
 * @param {object} settings {@link applyEngineConfig} 的入参。
 * @param {object} config 目标配置。
 * @param {object|null} snapshot 状态快照。
 * @param {string} fallbackReason 无法重建时报告的原因。
 * @returns {object} 适配层结论。
 */
function rebuildEngineConfig(settings, config, snapshot, fallbackReason) {
  const capability = ENGINE_CAPABILITIES[settings.engineKind];
  const canRebuild = typeof settings.rebuild === 'function' && Boolean(capability && capability.rebuild);
  if (!canRebuild) {
    return {
      ok: false,
      action: ENGINE_APPLY_ACTIONS.REBUILD,
      reason: fallbackReason,
      report: false,
      player: null,
      config: config,
      snapshot: snapshot,
    };
  }

  const rebuilt = settings.rebuild();
  if (!rebuilt) {
    return {
      ok: false,
      action: ENGINE_APPLY_ACTIONS.REBUILD,
      reason: ENGINE_APPLY_REASONS.REBUILD_FAILED,
      report: false,
      player: null,
      config: config,
      snapshot: snapshot,
    };
  }

  return {
    ok: true,
    action: ENGINE_APPLY_ACTIONS.REBUILD,
    reason: ENGINE_APPLY_REASONS.REBUILT,
    report: true,
    player: rebuilt,
    config: config,
    snapshot: snapshot,
  };
}

/**
 * 判断一次配置应用是否会走到**受控重建**（判定与 {@link applyEngineConfig} 的前置条件同源）。
 * @param {string} engineKind 引擎种类（{@link ENGINE_KINDS}）。
 * @param {object|null} player 运行中的引擎实例。
 * @returns {boolean} 需要重建返回 true。
 * @remarks
 * 与 {@link applyEngineConfig} 的差别只有一处：热改路径在"写入抛异常 / 读回校验不通过"时也会
 * 降级为重建，那种情况发生在写入之后，事前无法判定；这里只回答"按能力表判定是否需要重建"。
 */
function requiresControlledRebuild(engineKind, player) {
  return !canHotApplyEngineConfig(engineKind) || readEngineConfig(player) === null;
}

/**
 * 计算受控重建前的退避毫秒数（复用重连退避序列的第一档，并受硬上限约束）。
 * @param {string} engineKind 引擎种类。
 * @param {object|null} player 运行中的引擎实例。
 * @param {Function|null} [rebuild] 受控重建回调：没有它就没有"重建"可等。
 * @returns {number} 需要重建时返回退避毫秒数；走热改或无法重建时返回 0（不等待）。
 */
function getEngineRebuildBackoffMs(engineKind, player, rebuild) {
  if (typeof rebuild !== 'function' || !requiresControlledRebuild(engineKind, player)) {
    return 0;
  }

  const delay = getReconnectDelayMs(REBUILD_BACKOFF_STEP);
  return delay > 0 ? Math.min(delay, MAX_REBUILD_BACKOFF_MS) : 0;
}

/**
 * 默认的退避等待实现（浏览器与 Node 都有 `setTimeout`）。
 * @param {number} delayMs 等待毫秒数。
 * @returns {Promise<void>} 等待完成的 Promise。
 */
function waitForEngineRebuild(delayMs) {
  return new Promise(function resolveAfterDelay(resolve) {
    setTimeout(resolve, delayMs);
  });
}

/**
 * 应用追帧配置（页面唯一入口）：需要受控重建时，先断开旧连接、退避等待，再交给适配层重建。
 *
 * 为什么必须先断开再等：mpegts.js 没有运行时配置入口，只能"销毁旧实例 + 按新配置建新实例"，
 * 中间会出现"旧连接刚断、新连接刚建"。上游对同一条签名地址的并发连接是有限流的，新连接可能
 * 撞上还没回收的旧连接，表现为首帧偶发变慢。退避放在**断开之后、创建之前**，给上游留出察觉窗口。
 *
 * 三条硬约束（都由用例锁定）：
 *  - 退避只在**确实需要重建**时执行一次（热改路径、没有重建回调时都不等待）；
 *  - 退避有界（复用重连退避序列并受 {@link MAX_REBUILD_BACKOFF_MS} 限制），不是无限等待；
 *  - 结论完全来自 {@link applyEngineConfig}：只有它说 `{ok:true}` 才允许上报成功，
 *    重建失败时如实返回失败（调用方据此回滚并上报失败，不重复上报成功）。
 * @param {object} options 适配参数（同 {@link applyEngineConfig}）。
 * @param {Function|null} [options.prepareRebuild] 重建前置动作（断开旧连接），可为空。
 * @param {Function} [options.wait] 退避等待实现：`(ms) => Promise`；缺省用 {@link waitForEngineRebuild}。
 * @returns {Promise<object>} 适配层结论（{@link applyEngineConfig} 的返回值）。
 */
async function applyEngineConfigWithBackoff(options) {
  const settings = options && typeof options === 'object' ? options : {};
  const backoffMs = getEngineRebuildBackoffMs(settings.engineKind, settings.player, settings.rebuild);
  if (backoffMs <= 0) {
    return applyEngineConfig(settings);
  }

  if (typeof settings.prepareRebuild === 'function') {
    settings.prepareRebuild();
  }

  const wait = typeof settings.wait === 'function' ? settings.wait : waitForEngineRebuild;
  await wait(backoffMs);
  return applyEngineConfig(settings);
}

/**
 * 判断是否应当对候选发起探测。
 *
 * 探测本身与参考播放页逐条一致（探测请求只取到响应头就 abort，不读响应体），
 * 这里只加一条参考播放页自己后来补上的收敛条件：**只有一个候选时不探测**。
 * 依据（都是有出处的，不是感觉）：
 *  - `docs/parsers/douyu.md` 记录的实测结论是"上游对同一条签名直播长连接只允许一条并发连接，
 *    第 2 条会被在 0.2–0.4 秒内切断"，该文档给出的修复就是"候选只有 1 条时直接跳过探测：
 *    探测只用于排序，对单候选零收益却毁掉唯一连接"；
 *  - 本项目的播放地址在桥接开启时是本地中继地址（`BridgeHost.StreamRelayAsync`），
 *    中继对每个客户端请求都会各建一条上游连接，因此"单候选 + 探测"同样会让同一个签名地址多开一条上游连接。
 * 多候选时探测的收益是明确的：能提前把返回 4xx/5xx 的线路排除在队列之外，
 * 避免把时间浪费在必然失败的首帧超时上。
 * @param {number} candidateCount 候选数量。
 * @returns {boolean} 应当探测返回 true。
 */
function shouldProbeCandidate(candidateCount) {
  return Number(candidateCount) > 1;
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
  RECONNECT_DELAYS_MS,
  REBUILD_BACKOFF_STEP,
  MAX_REBUILD_BACKOFF_MS,
  LOG_LEVELS,
  CHASE_KEEP_DEFAULT_SECONDS,
  PLAY_RESTORE_TOLERANCE_SECONDS,
  RESUME_GRACE_MS,
  BUFFER_RUNAWAY_AHEAD_SECONDS,
  BUFFER_RUNAWAY_SILENCE_MS,
  BUFFER_RUNAWAY_CHASE_GRACE_MS,
  BUFFER_RUNAWAY_MAX_CHASE_ATTEMPTS,
  RUNAWAY_ACTIONS,
  VOLUME_PERCENT_SCALE,
  MIN_VOLUME_PERCENT,
  MAX_VOLUME_PERCENT,
  INITIAL_VOLUME_PERCENT,
  EMPTY_HINT_TITLE,
  EMPTY_HINT_ACTION,
  AUTOPLAY_BLOCKED_HINT,
  MODE_HINT_PLAYING,
  STATUS_IDLE_TEXT,
  STATUS_PAUSED_TEXT,
  STATUS_STOPPED_TEXT,
  STATUS_RECONNECTING_PREFIX,
  STATUS_RECONNECTING_SUFFIX,
  FIRST_RECONNECT_ATTEMPT,
  PLAYBACK_PHASES,
  STATUS_SEGMENT_SEPARATOR,
  LATENCY_UNIT_SUFFIX,
  STATUS_PARENTHESIS_OPEN,
  STATUS_PARENTHESIS_CLOSE,
  CHASE_BUTTON_LABELS,
  CHASE_STATUS_LABELS,
  ENGINE_KINDS,
  ENGINE_CAPABILITIES,
  ENGINE_APPLY_ACTIONS,
  ENGINE_APPLY_REASONS,
  RUNTIME_STATE_KEYS,
  HOST_STATUS_HOLD_INFO_MS,
  HOST_STATUS_HOLD_WARN_MS,
  HOST_STATUS_HOLD_ERROR_MS,
  HOST_STATUS_HOLD_MS,
  STATUS_LINE_SOURCES,
  INBOUND_MESSAGE_TYPES,
  OUTBOUND_MESSAGE_TYPES,
  normalizeExtremeTargetSeconds,
  isHlsCandidate,
  normalizeCandidates,
  buildMpegtsConfig,
  buildHlsConfig,
  getReconnectDelayMs,
  getStallThresholdMs,
  isPlaybackStalled,
  isBufferRunaway,
  decideBufferRunawayAction,
  computeChaseTargetSeconds,
  hasProgressed,
  getLocalBufferSeconds,
  looksStarved,
  shouldProbeCandidate,
  getStartupTimeoutMs,
  modeLabel,
  clampVolumePercent,
  volumePercentToGain,
  shouldMuteAtVolume,
  isAutoplayBlocked,
  formatPlaybackStatusText,
  formatReconnectingStatusText,
  resolvePlaybackStatusText,
  isValidLatencyMs,
  formatLatencySegment,
  readActualLatencyMs,
  shouldAutoChase,
  chaseButtonLabel,
  chaseStatusLabel,
  normalizeStatusLevel,
  getHostStatusHoldMs,
  getHostStatusRemainingMs,
  resolveStatusLine,
  shouldHideHint,
  buildPlaybackPlan,
  normalizeQualities,
  applyExtremeTarget,
  canHotApplyEngineConfig,
  engineKindForCandidate,
  buildEngineConfig,
  snapshotRuntimeState,
  restoreRuntimeState,
  readEngineConfig,
  verifyConfigApplied,
  applyEngineConfig,
  requiresControlledRebuild,
  getEngineRebuildBackoffMs,
  waitForEngineRebuild,
  applyEngineConfigWithBackoff,
  isHttpStatusInvalid,
  isMseError,
  isHevcUnsupportedDescription,
  shouldRecoverAfterLoadingComplete,
};

if (typeof module !== 'undefined' && module.exports) {
  module.exports = StreamPilotPlayerCore;
}

if (typeof window !== 'undefined') {
  window.StreamPilotPlayerCore = StreamPilotPlayerCore;
}
