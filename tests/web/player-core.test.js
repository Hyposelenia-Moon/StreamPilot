'use strict';

/*
 * StreamPilot 播放策略纯函数回归测试（node --test）。
 *
 * 运行方式：
 *   node --test tests/web
 * 覆盖：档位归一化、候选规范化与过滤、追帧参数推导、探测开关、重连退避、分模式卡顿阈值、
 *       缓冲失控保护（阈值 / 处置顺序 / 宽限期）、手动追帧夹取、错误归类、音量换算、
 *       模式/状态/空态文案、宿主状态消息（级别 / 保留时长 / 状态行优先级）、
 *       播放页静态结构（顶部提示已移除、播放按钮顺序）（正常 / 异常 / 边界三类）。
 */

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const core = require('../../Web/player-core.js');

/**
 * 从播放页 HTML 里取出某条 CSS 规则的声明块内容。
 *
 * 先剥掉注释与 `@media` 块：否则 `#controls` / `#statusLine` / `button` 会先命中
 * `@media (forced-colors: active)` 里那条同名规则（配色覆盖），取到的不是布局规则本体。
 * 选择器允许成组出现（如 `#playBtn,` 起头的组）。
 * @param {string} html 播放页 HTML 全文。
 * @param {string} selector 选择器（如 `#controls`）。
 * @returns {string} 声明块内容。
 */
function extractRule(html, selector) {
  const layoutCss = stripAtRuleBlocks(stripCssComments(html));
  const start = layoutCss.indexOf(selector);
  assert.notEqual(start, -1, '找不到 CSS 选择器 ' + selector);

  const openBrace = layoutCss.indexOf('{', start);
  assert.notEqual(openBrace, -1, 'CSS 选择器 ' + selector + ' 后面没有声明块');

  const endBrace = findBlockEnd(layoutCss, openBrace);
  const rule = layoutCss.slice(openBrace + 1, endBrace).trim();
  assert.ok(rule.length > 0, 'CSS 选择器 ' + selector + ' 的声明块是空的');
  assert.equal(rule.includes('{'), false, selector + ' 命中的是嵌套规则（如 @media），不是规则本体');

  return rule;
}

/**
 * 剥掉 CSS 注释。
 * @param {string} css 样式文本。
 * @returns {string} 不含注释的样式文本。
 */
function stripCssComments(css) {
  return css.replace(/\/\*[\s\S]*?\*\//g, '');
}

/**
 * 剥掉 `@media` / `@supports` 这类带块的 at-rule（本页只有配色用的 forced-colors 块，不含布局规则）。
 * @param {string} css 样式文本。
 * @returns {string} 不含 at-rule 块的样式文本。
 */
function stripAtRuleBlocks(css) {
  let removed = css;
  let head = /@(media|supports)\b[^{]*\{/.exec(removed);
  while (head !== null) {
    const openBrace = head.index + head[0].length - 1;
    const blockEnd = findBlockEnd(removed, openBrace);
    removed = removed.slice(0, head.index) + ' '.repeat(blockEnd - head.index + 1) + removed.slice(blockEnd + 1);
    head = /@(media|supports)\b[^{]*\{/.exec(removed);
  }

  return removed;
}

/**
 * 找到与 `{` 配对的那个 `}` 的位置。
 * @param {string} text 文本。
 * @param {number} openBrace `{` 的下标。
 * @returns {number} 配对 `}` 的下标。
 */
function findBlockEnd(text, openBrace) {
  let depth = 0;
  for (let index = openBrace; index < text.length; index += 1) {
    if (text[index] === '{') {
      depth += 1;
    } else if (text[index] === '}') {
      depth -= 1;
      if (depth === 0) {
        return index;
      }
    }
  }

  throw new Error('CSS 里有没配对的 {');
}

/**
 * 从播放页 HTML 里取出某个函数声明的主体（`{` 到配对 `}` 之间）。
 *
 * 用于断言"某条分支是否真的走到了上报"：只做字符串包含判断看不出提前 return 把上报跳过的情况。
 * @param {string} text 播放页 HTML 或脚本全文。
 * @param {string} marker 函数声明片段，例如 `function onTargetChange()`。
 * @returns {string} 函数主体文本。
 */
function extractFunctionBody(text, marker) {
  const start = text.indexOf(marker);
  assert.notEqual(start, -1, '找不到函数 ' + marker);

  const openBrace = text.indexOf('{', start);
  assert.notEqual(openBrace, -1, '函数 ' + marker + ' 后面没有函数体');

  return text.slice(openBrace + 1, findBlockEnd(text, openBrace));
}

test('normalizeExtremeTargetSeconds 只接受 150/200/250，其他值回落 0.25s', () => {
  assert.equal(core.normalizeExtremeTargetSeconds(150), 0.15);
  assert.equal(core.normalizeExtremeTargetSeconds(200), 0.2);
  assert.equal(core.normalizeExtremeTargetSeconds(250), 0.25);
  assert.equal(core.normalizeExtremeTargetSeconds(0), 0.25);
  assert.equal(core.normalizeExtremeTargetSeconds(-1), 0.25);
  assert.equal(core.normalizeExtremeTargetSeconds('250'), 0.25);
  assert.equal(core.normalizeExtremeTargetSeconds(undefined), 0.25);
  assert.equal(core.normalizeExtremeTargetSeconds(Number.NaN), 0.25);
});

test('isHlsCandidate 识别三类 HLS 格式字符串', () => {
  assert.equal(core.isHlsCandidate({ format: 'fmp4' }), true);
  assert.equal(core.isHlsCandidate({ format: 'TS' }), true);
  assert.equal(core.isHlsCandidate({ format: 'hls' }), true);
  assert.equal(core.isHlsCandidate({ format: 'flv' }), false);
  assert.equal(core.isHlsCandidate({}), false);
  assert.equal(core.isHlsCandidate(null), false);
});

test('normalizeCandidates 丢弃非法项并保持顺序与去重', () => {
  const candidates = core.normalizeCandidates([
    { sourceIndex: 0, url: 'https://a.example/x.flv', host: 'a.example', format: 'flv' },
    { sourceIndex: 0, url: 'https://a.example/x.flv' },
    { sourceIndex: 1, url: '   ' },
    null,
    { url: 'https://b.example/y.m3u8', format: 'hls', codec: 'HEVC' },
  ]);

  assert.equal(candidates.length, 2);
  assert.equal(candidates[0].sourceIndex, 0);
  assert.equal(candidates[0].codec, 'avc');
  assert.equal(candidates[1].sourceIndex, 1);
  assert.equal(candidates[1].codec, 'hevc');
  assert.equal(candidates[1].host, 'unknown');
});

test('buildMpegtsConfig 三档极限追帧阈值符合设计', () => {
  const tier150 = core.buildMpegtsConfig(true, 0.15);
  const tier250 = core.buildMpegtsConfig(true, 0.25);
  const stable = core.buildMpegtsConfig(false, 0.2);

  assert.equal(tier150.enableStashBuffer, false);
  assert.equal(tier150.liveBufferLatencyMinRemain, 0.15);
  assert.equal(tier150.liveSyncTargetLatency, 0.15);
  assert.equal(tier150.liveSyncPlaybackRate, 1.08);
  assert.ok(Math.abs(tier150.liveBufferLatencyMaxLatency - 0.42) < 1e-9);
  assert.ok(Math.abs(tier250.liveBufferLatencyMinRemain - 0.25) < 1e-9);
  assert.ok(Math.abs(tier250.liveSyncMaxLatency - 0.39) < 1e-9);

  assert.equal(stable.enableStashBuffer, true);
  assert.equal(stable.liveSync, false);
  assert.equal(stable.liveBufferLatencyChasing, true);
  assert.equal(stable.liveBufferLatencyMaxLatency, 1.25);
  assert.equal(stable.stashInitialSize, 96 * 1024);
});

test('buildHlsConfig 不区分三档但不启用 LL-HLS', () => {
  const extreme = core.buildHlsConfig(true);
  const stable = core.buildHlsConfig(false);

  assert.equal(extreme.lowLatencyMode, false);
  assert.equal(extreme.liveSyncDurationCount, 1);
  assert.equal(extreme.liveMaxLatencyDurationCount, 4);
  assert.equal(extreme.maxBufferLength, 6);
  assert.equal(stable.liveSyncDurationCount, 2);
  assert.equal(stable.liveMaxLatencyDurationCount, 8);
  assert.equal(stable.maxBufferLength, 12);
  assert.equal(extreme.maxLiveSyncPlaybackRate, 1.5);
});

test('getReconnectDelayMs 退避序列为 250/1000，超过上限返回 -1', () => {
  assert.equal(core.getReconnectDelayMs(0), 250);
  assert.equal(core.getReconnectDelayMs(1), 1000);
  assert.equal(core.getReconnectDelayMs(2), -1);
  assert.equal(core.getReconnectDelayMs(99), -1);
});

test('getStallThresholdMs 按模式与饥饿状态取 4s/6.5s（极限）与 6s/9s（稳定）', () => {
  assert.equal(core.getStallThresholdMs(true, true), 4000);
  assert.equal(core.getStallThresholdMs(false, true), 6500);
  assert.equal(core.getStallThresholdMs(true, false), 6000);
  assert.equal(core.getStallThresholdMs(false, false), 9000);
  assert.equal(core.getStallThresholdMs(true, undefined), 6000, '缺省按稳定档处理');
});

test('isPlaybackStalled 排除用户暂停与刚恢复播放的宽限期，并区分模式阈值', () => {
  assert.equal(core.isPlaybackStalled(3500, true, 0, 100000, true, false), false, '极限档未到 4s 不重连');
  assert.equal(core.isPlaybackStalled(4000, true, 0, 100000, true, false), true, '极限档饥饿达到 4s 重连');
  assert.equal(core.isPlaybackStalled(4000, true, 0, 100000, false, false), false, '稳定档 4s 还不重连');
  assert.equal(core.isPlaybackStalled(6000, true, 0, 100000, false, false), true, '稳定档饥饿达到 6s 才重连');
  assert.equal(core.isPlaybackStalled(6500, false, 0, 100000, true, false), true, '极限档硬卡 6.5s 触发');
  assert.equal(core.isPlaybackStalled(9000, false, 0, 100000, false, false), true, '稳定档硬卡 9s 触发');
  assert.equal(core.isPlaybackStalled(60000, false, 0, 100000, true, true), false, '用户暂停不算卡顿');
  assert.equal(
    core.isPlaybackStalled(60000, true, 100000 - core.RESUME_GRACE_MS + 1, 100000, true, false),
    false,
    '刚点继续播放时的静止属于重建缓冲');
  assert.equal(
    core.isPlaybackStalled(60000, true, 100000 - core.RESUME_GRACE_MS, 100000, true, false),
    true,
    '宽限期结束后恢复正常判定');
});

test('isPlaybackStalled 只认"用户暂停"，元素被非用户原因暂停时不能吃掉恢复分支', () => {
  // 真机坏状态（B站 814）：缓冲 81.9→111.9 秒、画面 82→112 秒没前进、reconnects 恒为 0。
  // 旧实现拿 video.paused 当"用户暂停"的门闩，元素一旦被非用户原因暂停（播放器重建时先 pause、
  // 内核暂停元素），恢复分支就永久失效——画面停住时"没有触发任何动作"。
  assert.equal(core.isPlaybackStalled(82000, false, 0, 100000, true, false), true, '元素暂停不等于用户暂停');
  assert.equal(core.isPlaybackStalled(82000, false, 0, 100000, true, true), false, '用户主动暂停才是例外');
});

test('isBufferRunaway 只在缓冲远大于目标延迟且画面停滞时成立', () => {
  assert.equal(core.BUFFER_RUNAWAY_AHEAD_SECONDS, 8, '异常下限取 8 秒');
  assert.equal(core.isBufferRunaway(0.24, 0, false), false, '健康样本（真机 179–473 ms）不算失控');
  assert.equal(core.isBufferRunaway(7.9, 60000, false), false, '未到 8 秒不按失控处理');
  assert.equal(core.isBufferRunaway(8.1, core.BUFFER_RUNAWAY_SILENCE_MS - 1, false), false, '画面还在前进就不算失控');
  assert.equal(core.isBufferRunaway(8.1, core.BUFFER_RUNAWAY_SILENCE_MS, false), true);
  assert.equal(core.isBufferRunaway(111.891, 112000, false), true, '真机失控样本（111.891 秒 / 停滞 112 秒）');
  assert.equal(core.isBufferRunaway(111.891, 112000, true), false, '用户暂停时缓冲增长属于预期');
  assert.equal(core.isBufferRunaway(null, 60000, false), false, '没有缓冲区间不判失控');
  assert.equal(core.isBufferRunaway(Number.NaN, 60000, false), false);
});

test('decideBufferRunawayAction 先恢复播放与追帧，自行处置用尽后交回重连', () => {
  const never = Number.POSITIVE_INFINITY;
  assert.equal(core.decideBufferRunawayAction(0.24, 0, false, false, 0, never), core.RUNAWAY_ACTIONS.NONE, '正常播放不处置');
  assert.equal(core.decideBufferRunawayAction(111.891, 112000, true, false, 0, never), core.RUNAWAY_ACTIONS.NONE, '用户暂停不处置');
  assert.equal(
    core.decideBufferRunawayAction(82, 82000, false, true, 0, never),
    core.RUNAWAY_ACTIONS.RESUME,
    '元素被非用户原因暂停：先恢复播放（否则追帧不会被消费）');
  assert.equal(
    core.decideBufferRunawayAction(82, 82000, false, false, 0, never),
    core.RUNAWAY_ACTIONS.CHASE,
    '缓冲可直接消费：先追帧到缓冲末端');
  assert.equal(
    core.decideBufferRunawayAction(82, 82000, false, false, 0, core.BUFFER_RUNAWAY_CHASE_GRACE_MS - 1),
    core.RUNAWAY_ACTIONS.NONE,
    '刚追过帧的宽限期内不重复处置（等 seek 生效）');
  assert.equal(
    core.decideBufferRunawayAction(82, 82000, false, false, 0, core.BUFFER_RUNAWAY_CHASE_GRACE_MS),
    core.RUNAWAY_ACTIONS.CHASE,
    '宽限期结束画面仍未恢复则再追一次');
  assert.equal(
    core.decideBufferRunawayAction(82, 82000, false, false, core.BUFFER_RUNAWAY_MAX_CHASE_ATTEMPTS, never),
    core.RUNAWAY_ACTIONS.RECONNECT,
    '自行处置用尽：改走既有重连 / 切候选');
  assert.equal(
    core.decideBufferRunawayAction(82, 82000, false, true, core.BUFFER_RUNAWAY_MAX_CHASE_ATTEMPTS, never),
    core.RUNAWAY_ACTIONS.RECONNECT,
    '元素一直暂停且用尽处置次数时同样交回重连');
  assert.equal(
    core.decideBufferRunawayAction(82, 82000, false, false, Number.NaN, never),
    core.RUNAWAY_ACTIONS.RECONNECT,
    '计数异常时按"已用尽"保守处理');
});

test('shouldProbeCandidate 只在多候选时探测', () => {
  assert.equal(core.shouldProbeCandidate(0), false);
  assert.equal(core.shouldProbeCandidate(1), false, '单候选探测零收益却会多占一条上游连接');
  assert.equal(core.shouldProbeCandidate(2), true);
  assert.equal(core.shouldProbeCandidate(8), true);
  assert.equal(core.PROBE_TIMEOUT_MS, 1200, '探测超时与参考播放页一致');
});

test('shouldRecoverAfterLoadingComplete 只在真的断流时重连，且按模式取阈值', () => {
  const now = 100000;
  const live = { playbackStarted: true, lastPlaybackProgressAt: now - 1000, extreme: true };
  const stable = { playbackStarted: true, lastPlaybackProgressAt: now - 1000, extreme: false };

  assert.equal(core.shouldRecoverAfterLoadingComplete(live, now, false), false, '缓冲取满不重连');
  assert.equal(core.shouldRecoverAfterLoadingComplete(live, now, true), true, '缓冲已空必须重连');
  assert.equal(
    core.shouldRecoverAfterLoadingComplete({ playbackStarted: true, lastPlaybackProgressAt: now - 7000, extreme: true }, now, false),
    true,
    '极限档画面 7s 不动必须重连（阈值 6.5s）');
  assert.equal(
    core.shouldRecoverAfterLoadingComplete({ playbackStarted: true, lastPlaybackProgressAt: now - 7000, extreme: false }, now, false),
    false,
    '稳定档 7s 未到 9s 阈值，继续等');
  assert.equal(
    core.shouldRecoverAfterLoadingComplete({ playbackStarted: true, lastPlaybackProgressAt: now - 20000 }, now, false),
    true,
    '画面长时间不动必须重连');
  assert.equal(
    core.shouldRecoverAfterLoadingComplete({ playbackStarted: false, lastPlaybackProgressAt: now - 20000 }, now, false),
    false,
    '还没起播时不按断流处理');
  assert.equal(core.shouldRecoverAfterLoadingComplete(null, now, true), false);
});

test('computeChaseTargetSeconds 夹取保留量并保证不为负', () => {
  assert.equal(core.computeChaseTargetSeconds(10, 20, 0.08), 19.92);
  assert.equal(core.computeChaseTargetSeconds(10, 20, 0), 19.92);
  assert.equal(core.computeChaseTargetSeconds(10, 20, 5), 19.5);
  assert.equal(core.computeChaseTargetSeconds(10, 20, -3), 19.98);
  assert.equal(core.computeChaseTargetSeconds(19.99, 20, 0.5), 19.99);
});

test('hasProgressed / getLocalBufferSeconds / looksStarved 边界行为', () => {
  assert.equal(core.hasProgressed(10.02, 10), true);
  assert.equal(core.hasProgressed(10.01, 10), false);

  assert.equal(core.getLocalBufferSeconds(null, 0), null);
  assert.equal(core.getLocalBufferSeconds({ length: 0 }, 0), null);
  assert.equal(core.getLocalBufferSeconds({ length: 1, start: () => 0, end: () => 5 }, 4.5), 0.5);
  assert.equal(core.getLocalBufferSeconds({ length: 1, start: () => 0, end: () => 5 }, 9), 0);

  assert.equal(core.looksStarved({ paused: false, ended: false, readyState: 4 }, 0.1), true, '缓冲太少算饥饿');
  assert.equal(core.looksStarved({ paused: false, ended: false, readyState: 4 }, 0.5), false);
  assert.equal(core.looksStarved({ paused: false, ended: false, readyState: 2 }, 5), true, 'readyState 不足算饥饿');
  assert.equal(core.looksStarved({ paused: false, ended: false, readyState: 4 }, null), true);
  assert.equal(core.looksStarved(null, 1), true);
  assert.equal(
    core.looksStarved({ paused: true, ended: false, readyState: 4 }, 5),
    false,
    '用户暂停且缓冲充足不是饥饿（否则暂停会被误判成卡顿）');
  assert.equal(core.looksStarved({ paused: false, ended: true, readyState: 4 }, 5), false, '播放结束不是饥饿');
});

test('buildPlaybackPlan 过滤不可播放与 HEVC 候选并标记模式', () => {
  const payload = {
    sessionId: 7,
    mode: 'extreme',
    extremeTargetMs: 150,
    candidates: [
      { sourceIndex: 0, url: 'https://a/x.flv', format: 'flv', codec: 'avc' },
      { sourceIndex: 1, url: 'https://b/y.flv', format: 'flv', codec: 'hevc' },
      { sourceIndex: 2, url: 'https://c/z.m3u8', format: 'hls', codec: 'avc' },
    ],
  };

  const withoutHevc = core.buildPlaybackPlan(payload, true, true, false);
  assert.equal(withoutHevc.plan.candidates.length, 2);
  assert.equal(withoutHevc.unsupportedCount, 1);
  assert.equal(withoutHevc.plan.sessionId, 7);
  assert.equal(withoutHevc.plan.extreme, true);
  assert.equal(withoutHevc.plan.extremeTargetSeconds, 0.15);

  const withHevc = core.buildPlaybackPlan(payload, true, true, true);
  assert.equal(withHevc.plan.candidates.length, 3);
  assert.equal(withHevc.unsupportedCount, 0);

  const noHls = core.buildPlaybackPlan(payload, true, false, true);
  assert.equal(noHls.plan.candidates.length, 2);
  assert.equal(noHls.unsupportedCount, 1);

  assert.equal(core.buildPlaybackPlan(null, true, true, true).plan, null);
});

test('modeLabel 输出可读的模式标签', () => {
  assert.equal(core.modeLabel({ mode: 'extreme', extremeTargetMs: 250 }), '极限追帧 250 ms');
  assert.equal(core.modeLabel({ mode: 'stable' }), '稳定缓冲');
  assert.equal(core.modeLabel(null), '稳定缓冲');
});

test('错误归类：HTTP 状态错误、MSE 错误、HEVC 不受支持', () => {
  const mpegts = {
    ErrorTypes: { NETWORK_ERROR: 'NetworkError' },
    ErrorDetails: { NETWORK_STATUS_CODE_INVALID: 'NetworkStatusCodeInvalid', MEDIA_MSE_ERROR: 'MediaMSEError' },
  };

  assert.equal(core.isHttpStatusInvalid('NetworkError', 'NetworkStatusCodeInvalid', mpegts), true);
  assert.equal(core.isHttpStatusInvalid('NetworkError', 'HttpStatusCodeInvalid', mpegts), true);
  assert.equal(core.isHttpStatusInvalid('MediaError', 'NetworkStatusCodeInvalid', mpegts), false);
  assert.equal(core.isHttpStatusInvalid('NetworkError', 'NetworkException', mpegts), false);
  assert.equal(core.isHttpStatusInvalid('NetworkError', 'NetworkStatusCodeInvalid', null), false);

  assert.equal(core.isMseError('MediaMSEError', mpegts), true);
  assert.equal(core.isMseError('NetworkException', mpegts), false);

  assert.equal(core.isHevcUnsupportedDescription('Failed to create decoder for hvc1.1.6.L120.90: not supported'), true);
  assert.equal(core.isHevcUnsupportedDescription('该编码不受支持 hev1'), true);
  assert.equal(core.isHevcUnsupportedDescription('avc1 decoder error'), false);
  assert.equal(core.isHevcUnsupportedDescription(''), false);
});

test('getStartupTimeoutMs 按模式返回 6000/8000', () => {
  assert.equal(core.getStartupTimeoutMs({ extreme: true }), 6000);
  assert.equal(core.getStartupTimeoutMs({ extreme: false }), 8000);
  assert.equal(core.getStartupTimeoutMs(null), 8000);
});

test('normalizeQualities 生成下拉项并回落到首档', () => {
  const payload = [
    { key: '20000', label: '4K 原画', bitrateKbps: 20000, isBest: true },
    { key: '10000', label: '原画', bitrateKbps: 10000 },
    { key: '', label: '无效档位' },
  ];

  const picked = core.normalizeQualities(payload, '10000');
  assert.equal(picked.items.length, 2);
  assert.equal(picked.selectedKey, '10000');
  assert.deepEqual(picked.items.map((item) => item.selected), [false, true]);
  assert.equal(picked.items[0].label, '4K 原画');
  assert.equal(picked.items[1].label, '原画');

  const fallback = core.normalizeQualities(payload, 'not-exist');
  assert.equal(fallback.selectedKey, '20000');
  assert.deepEqual(fallback.items.map((item) => item.selected), [true, false]);

  assert.deepEqual(core.normalizeQualities(null, 'x'), { items: [], selectedKey: '' });
  assert.deepEqual(core.normalizeQualities([], 'x'), { items: [], selectedKey: '' });
});

test('OUTBOUND_MESSAGE_TYPES 包含画质消息', () => {
  assert.equal(core.OUTBOUND_MESSAGE_TYPES.QUALITY, 'quality');
});

test('OUTBOUND_MESSAGE_TYPES 含 TARGET，且每个消息类型的取值都不为 undefined', () => {
  const types = core.OUTBOUND_MESSAGE_TYPES;

  // 页面用 outbound.TARGET 上报档位：少一个键就会发出 type: undefined，宿主入口直接丢弃（档位持久化静默失效）。
  assert.notEqual(types.TARGET, undefined, 'TARGET 必须存在，否则上报的是 type: undefined');
  assert.equal(types.TARGET, 'target', '消息名与宿主 ShellViewModel.TargetType 必须逐字一致');

  for (const [name, value] of Object.entries(types)) {
    assert.equal(value === undefined, false, name + ' 的消息类型不能是 undefined');
    assert.equal(typeof value, 'string', name + ' 的消息类型必须是字符串');
    assert.ok(value.length > 0, name + ' 的消息类型不能是空串');
  }
});

test('播放页改档位必须上报宿主：空闲与播放中两个分支都发出 target 消息', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');

  const handler = extractFunctionBody(html, 'function onTargetChange()');
  assert.equal(handler.includes('reportTargetChange(targetMs, run);'), true, '改档后必须上报宿主');
  assert.equal(
    handler.indexOf('reportTargetChange(targetMs, run);') > handler.indexOf('else {'),
    true,
    '上报要在 if/else 之外，空闲分支（还没有 run）同样要走到');
  assert.equal(
    (handler.match(/return;/g) || []).length,
    1,
    '除非法档位那一个提前 return 之外，不允许再有让上报被跳过的出口');

  const reporter = extractFunctionBody(html, 'function reportTargetChange(targetMs, run)');
  assert.equal(reporter.includes('report(outbound.TARGET'), true, '上报用的消息类型必须是 outbound.TARGET');
  assert.equal(reporter.includes('extremeTargetMs: targetMs'), true, '宿主按 extremeTargetMs 字段取值');
  assert.equal(reporter.includes('run ? run.currentCandidate : null'), true, '空闲时没有候选可选，不能抛错');
});

test('画质下拉只显示平台档位名，不追加码率后缀', () => {
  assert.equal(core.normalizeQualities([{ key: '4000', label: '蓝光4M', bitrateKbps: 4000 }], '4000').items[0].label, '蓝光4M');
  assert.equal(core.normalizeQualities([{ key: '20000', label: '蓝光20M', bitrateKbps: 20000 }], '20000').items[0].label, '蓝光20M');
  assert.equal(core.normalizeQualities([{ key: 'x', label: '超清', bitrateKbps: 2500 }], 'x').items[0].label, '超清');
  assert.equal(core.normalizeQualities([{ key: 'y', label: '流畅', bitrateKbps: 500 }], 'y').items[0].label, '流畅');
  assert.equal(core.normalizeQualities([{ key: 'z', label: '原画' }], 'z').items[0].label, '原画');
  assert.equal(core.normalizeQualities([{ key: 'k', bitrateKbps: 8000 }], 'k').items[0].label, 'k', '没有档位名时用键名');
});

test('modeLabel 正确显示 250/150 档位（含缺失 extremeTargetMs 的运行对象）', () => {
  assert.equal(core.modeLabel({ mode: 'extreme', extremeTargetMs: 250 }), '极限追帧 250 ms');
  assert.equal(core.modeLabel({ mode: 'extreme', extremeTargetMs: 150 }), '极限追帧 150 ms');
  assert.equal(core.modeLabel({ mode: 'extreme', extremeTargetSeconds: 0.25 }), '极限追帧 250 ms');
  assert.equal(core.modeLabel({ mode: 'stable' }), '稳定缓冲');
  assert.equal(core.modeLabel(null), '稳定缓冲');
});

test('applyExtremeTarget 热切换档位', () => {
  const run = { mode: 'extreme', extreme: true, extremeTargetMs: 200, extremeTargetSeconds: 0.2 };
  assert.equal(core.applyExtremeTarget(run, 250), true);
  assert.equal(run.extremeTargetMs, 250);
  assert.equal(run.extremeTargetSeconds, 0.25);
  assert.equal(core.applyExtremeTarget(run, 250), false);
  run.extremeTargetMs = 200;
  assert.equal(core.applyExtremeTarget(run, 999), true);
  assert.equal(run.extremeTargetMs, 250, '非法档位回落到默认 250');
  assert.equal(core.applyExtremeTarget(null, 250), false);
});

test('初始音量为 30 且不静音', () => {
  assert.equal(core.INITIAL_VOLUME_PERCENT, 30);
  assert.equal(core.shouldMuteAtVolume(core.INITIAL_VOLUME_PERCENT), false, '初始音量非 0 就不该静音');
  assert.equal(core.volumePercentToGain(core.INITIAL_VOLUME_PERCENT), 0.3);
  assert.equal(core.volumePercentToGain(core.MAX_VOLUME_PERCENT), 1);
  assert.equal(core.volumePercentToGain(core.MIN_VOLUME_PERCENT), 0);
});

test('clampVolumePercent 夹取音量并处理非法输入', () => {
  assert.equal(core.clampVolumePercent(30), 30);
  assert.equal(core.clampVolumePercent('30'), 30, '滑块给的是字符串');
  assert.equal(core.clampVolumePercent(130), 100);
  assert.equal(core.clampVolumePercent(-10), 0);
  assert.equal(core.clampVolumePercent(30.6), 31, '滚轮累加产生的浮点噪声要收敛');
  assert.equal(core.clampVolumePercent(undefined), 0);
  assert.equal(core.clampVolumePercent('abc'), 0);
  assert.equal(core.clampVolumePercent(Number.NaN), 0);
  assert.equal(core.clampVolumePercent(Number.POSITIVE_INFINITY), 0);
});

test('shouldMuteAtVolume 只在音量为 0 时静音', () => {
  assert.equal(core.shouldMuteAtVolume(0), true);
  assert.equal(core.shouldMuteAtVolume(1), false);
  assert.equal(core.shouldMuteAtVolume(30), false);
  assert.equal(core.shouldMuteAtVolume(100), false);
});

test('isAutoplayBlocked 只认 NotAllowedError', () => {
  assert.equal(core.isAutoplayBlocked({ name: 'NotAllowedError' }), true);
  assert.equal(core.isAutoplayBlocked({ name: 'AbortError' }), false, '播放被打断属于线路问题，不是自动播放策略');
  assert.equal(core.isAutoplayBlocked({ name: 'TimeoutError' }), false);
  assert.equal(core.isAutoplayBlocked(new Error('boom')), false);
  assert.equal(core.isAutoplayBlocked(null), false);
  assert.equal(core.isAutoplayBlocked(undefined), false);
});

test('顶部模式提示（modeHint / formatModeHint）已彻底移除，只剩底部状态行', () => {
  assert.equal(core.formatModeHint, undefined, '顶部提示格式化函数必须删除，而不是留着不用');
  assert.equal(core.MODE_HINT_IDLE, undefined);
  assert.equal(core.MODE_HINT_CONNECTING, undefined);
  assert.equal(core.MODE_HINT_PLAYING, '播放中', '底部状态行仍需要"播放中"文案');
});

test('播放页不再包含顶部提示元素，且播放按钮顺序为 开始 → 暂停/继续 → 停止', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');

  assert.equal(html.includes('modeHint'), false, 'page 里不能再有 modeHint（元素 / CSS / JS 全部删除）');
  assert.equal(html.includes('updateModeHint'), false);
  assert.equal(html.includes('formatModeHint'), false);
  assert.equal(html.includes('<header>'), false, '页头整块已删除，顶部不再有状态行');

  const playIndex = html.indexOf('id="playBtn"');
  const pauseIndex = html.indexOf('id="pauseBtn"');
  const stopIndex = html.indexOf('id="stopBtn"');
  assert.ok(playIndex > 0, '开始播放按钮必须存在');
  assert.ok(pauseIndex > playIndex, '暂停/继续播放必须排在开始播放之后');
  assert.ok(stopIndex > pauseIndex, '停止播放必须排在暂停/继续播放之后');

  assert.equal(html.includes('#statusLine'), true, '画面下方的状态行保留');
  const statusRule = html.slice(html.indexOf('#statusLine {'), html.indexOf('}', html.indexOf('#statusLine {')));
  assert.equal(statusRule.includes('color: #2f6feb'), true, '状态行自身用主题强调蓝（AccentBrush）而不是灰字');
});

test('formatPlaybackStatusText 状态行不显示候选主机名', () => {
  assert.equal(
    core.formatPlaybackStatusText({ mode: 'extreme', extremeTargetMs: 150, currentCandidate: { host: 'a.example' } }),
    '播放中（极限追帧 150 ms）');
  assert.equal(
    core.formatPlaybackStatusText({ mode: 'stable', currentCandidate: { host: 'a.example' } }),
    '播放中（稳定缓冲）');
});

test('formatPlaybackStatusText 逐字给出四种形态：延迟分段紧跟播放中，括号分段永远在最后', () => {
  assert.equal(
    core.formatPlaybackStatusText({ mode: 'extreme', extremeTargetMs: 250, lastLatencyMs: 318 }),
    '播放中 · 318 ms（极限追帧 250 ms）',
    '正在追帧：延迟用遥测实测值，括号里是当前模式与目标档位');
  assert.equal(
    core.formatPlaybackStatusText({ mode: 'extreme', extremeTargetMs: 250, autoChaseEnabled: false, lastLatencyMs: 900 }),
    '播放中 · 900 ms（未开启追帧）',
    '停止追帧：括号里是固定文案，不再显示模式与目标档位');
  assert.equal(
    core.formatPlaybackStatusText({ mode: 'extreme', extremeTargetMs: 250 }),
    '播放中（极限追帧 250 ms）',
    '取不到实测延迟：不显示延迟分段，也绝不用档位值冒充实测');
  assert.equal(
    core.formatPlaybackStatusText({ mode: 'extreme', extremeTargetMs: 250, autoChaseEnabled: false }),
    '播放中（未开启追帧）');

  assert.equal(
    core.formatPlaybackStatusText({ mode: 'extreme', extremeTargetMs: 250, lastLatencyMs: 318.6 }),
    '播放中 · 319 ms（极限追帧 250 ms）',
    '实测值四舍五入到整数毫秒');
  assert.equal(
    core.formatPlaybackStatusText({ mode: 'stable', lastLatencyMs: Number.NaN }),
    '播放中（稳定缓冲）',
    '非法实测值不得显示成 NaN ms');
});

test('formatPlaybackStatusText 不再出现旧的 · 分隔形态', () => {
  const chasing = core.formatPlaybackStatusText({ mode: 'extreme', extremeTargetMs: 250, lastLatencyMs: 318 });
  const stopped = core.formatPlaybackStatusText({
    mode: 'extreme', extremeTargetMs: 250, autoChaseEnabled: false, lastLatencyMs: 900,
  });

  [chasing, stopped].forEach((text) => {
    assert.equal(text.includes('· 追帧中'), false, '「追帧中」不再是独立分段');
    assert.equal(text.includes('已停止追帧'), false, '旧的「已停止追帧」文案必须消失');
    assert.equal(text.includes('追帧中'), false, '括号分段里不再出现「追帧中」');
    assert.equal(text.includes('· 极限追帧 250 ms ·'), false, '模式与档位不再作为独立分段出现');
    assert.equal(text.endsWith('）'), true, '括号分段必须放在最后');
    assert.equal(text.includes('（'), true, '括号分段必须永远存在');
  });

  assert.equal(core.CHASE_STATUS_LABELS.STOPPED, '未开启追帧');
  assert.equal(core.CHASE_STATE_SUFFIXES, undefined, '旧的追帧状态分段常量必须删除，不留死代码');
});

test('formatLatencySegment 只给出合法延迟分段，非法值一律返回 null', () => {
  assert.equal(core.formatLatencySegment(250), '250 ms');
  assert.equal(core.formatLatencySegment(0), '0 ms', '0 ms 是合法实测值');
  assert.equal(core.formatLatencySegment(249.5), '250 ms');
  assert.equal(core.formatLatencySegment(null), null);
  assert.equal(core.formatLatencySegment(undefined), null);
  assert.equal(core.formatLatencySegment(Number.NaN), null);
  assert.equal(core.formatLatencySegment(Number.POSITIVE_INFINITY), null);
  assert.equal(core.formatLatencySegment(-1), null, '负延迟是非法测量，不能显示');
  assert.equal(core.formatLatencySegment('abc'), null);
  assert.equal(core.isValidLatencyMs(250), true);
  assert.equal(core.isValidLatencyMs(0), true);
  assert.equal(core.isValidLatencyMs(-0.5), false);
  assert.equal(core.isValidLatencyMs('318'), true, '数字字符串按数字处理，与旧的 Number() 语义一致');
  assert.equal(core.formatStatusWithLatency, undefined, '旧的整串拼接函数必须删除，只留分段函数');
});

test('shouldAutoChase 判定自动追帧开关（默认开启）', () => {
  assert.equal(core.shouldAutoChase({ autoChaseEnabled: true }), true);
  assert.equal(core.shouldAutoChase({}), true, '字段缺失视为开启，旧会话不会被误当成已停止追帧');
  assert.equal(core.shouldAutoChase({ autoChaseEnabled: undefined }), true);
  assert.equal(core.shouldAutoChase({ autoChaseEnabled: false }), false, '只有显式 false 才算已停止');
  assert.equal(core.shouldAutoChase(null), false, '没有会话时不追帧');
  assert.equal(core.shouldAutoChase(undefined), false);
  assert.equal(core.shouldAutoChase('run'), false, '非对象输入不得抛异常');
});

test('chaseButtonLabel 给出合并按钮两侧文案（追帧 / 停止追帧）', () => {
  assert.equal(core.chaseButtonLabel(true), core.CHASE_BUTTON_LABELS.STOP);
  assert.equal(core.chaseButtonLabel(false), core.CHASE_BUTTON_LABELS.START);
  assert.equal(core.CHASE_BUTTON_LABELS.START, '追帧');
  assert.equal(core.CHASE_BUTTON_LABELS.STOP, '停止追帧');
  assert.equal(core.chaseButtonLabel(undefined), '追帧', '判定输入缺失时按"未追帧"给出「追帧」');

  assert.equal(core.chaseSwitchLabel, undefined, '旧的两侧按钮文案函数必须删除，而不是留着不用');
  assert.equal(core.CHASE_SWITCH_LABELS, undefined, '旧的「取消追帧 / 恢复追帧」文案必须删除');
});

test('停止追帧关闭 mpegts.js / hls.js 的两路自动追帧，但不影响其它配置', () => {
  const chasing = core.buildMpegtsConfig(true, 0.25, true);
  const cancelled = core.buildMpegtsConfig(true, 0.25, false);

  assert.equal(chasing.liveBufferLatencyChasing, true, '默认必须保留库内硬跳');
  assert.equal(chasing.liveSync, true, '默认必须保留倍速追赶');
  assert.equal(chasing.liveBufferLatencyMaxLatency, 0.52);

  assert.equal(cancelled.liveBufferLatencyChasing, false, '停止后不得再硬跳');
  assert.equal(cancelled.liveSync, false, '停止后不得再倍速追赶');
  assert.ok(cancelled.liveBufferLatencyMaxLatency > 1e9, '阈值同时设为永不触发，兼容忽略开关的旧版本库');
  assert.ok(cancelled.liveSyncMaxLatency > 1e9);
  assert.equal(cancelled.liveSyncPlaybackRate, 1);
  assert.equal(cancelled.autoCleanupSourceBuffer, true, '逐帧清理不受停止追帧影响');
  assert.equal(cancelled.enableStashBuffer, chasing.enableStashBuffer, '缓冲策略不变，停掉的只是追帧');

  const stableCancelled = core.buildMpegtsConfig(false, 0.2, false);
  assert.equal(stableCancelled.liveBufferLatencyChasing, false, '稳定档同样要能停止追帧');
  assert.equal(stableCancelled.liveSync, false);

  const hlsChasing = core.buildHlsConfig(true, true);
  const hlsCancelled = core.buildHlsConfig(true, false);
  assert.equal(hlsChasing.maxLiveSyncPlaybackRate, 1.5, '默认保留 hls.js 倍速追赶');
  assert.equal(hlsCancelled.maxLiveSyncPlaybackRate, 1, '停止后按正常倍速播放');
  assert.ok(hlsCancelled.liveMaxLatencyDurationCount > 1e9, '停止后不因延迟跳片');
  assert.equal(hlsCancelled.maxBufferLength, hlsChasing.maxBufferLength, '缓冲上限不变');
  assert.equal(hlsCancelled.liveSyncDurationCount, hlsChasing.liveSyncDurationCount, '起播位置不变');
});

test('chaseStatusLabel 给出括号分段取值：追帧中给模式档位，停止后给固定文案', () => {
  const stopped = { mode: 'extreme', extremeTargetMs: 250, autoChaseEnabled: false, lastLatencyMs: 900 };
  assert.equal(core.chaseStatusLabel(stopped), '未开启追帧');
  assert.equal(core.chaseStatusLabel({ mode: 'extreme', extremeTargetMs: 250 }), '极限追帧 250 ms', '字段缺失视为追帧中');
  assert.equal(core.chaseStatusLabel({ mode: 'stable' }), '稳定缓冲');
  assert.equal(core.chaseStatusLabel(null), '未开启追帧', '没有会话时与"没在追帧"一致');

  assert.equal(core.CHASE_STATUS_LABELS.STOPPED, '未开启追帧');
  assert.equal(core.CHASE_CANCELLED_SUFFIX, undefined, '旧的"仅在已取消时追加"的常量必须删除');
});

test('追帧与停止追帧合并为播放页底部的单个按钮', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');

  const chaseIds = html.match(/id="[^"]*[Cc]hase[^"]*"/g) || [];
  assert.deepEqual(chaseIds, ['id="chaseBtn"'], '追帧相关的按钮必须只剩一个（旧的取消/恢复按钮删除干净）');

  assert.equal(html.includes('toggleChase()'), true, '按钮必须绑定到合并后的开关');
  assert.equal(html.includes('toggleAutoChase'), false, '旧的开关函数必须删除，不留死代码');
  assert.equal(html.includes('syncChaseToggle'), false, '旧的按钮同步函数必须删除');
  assert.equal(html.includes('core.chaseButtonLabel'), true, '按钮文案必须来自 player-core 的纯函数');
  assert.equal(
    html.includes('const enabling = !core.shouldAutoChase(run);') && html.includes('run.autoChaseEnabled = enabling;'),
    true,
    '点「追帧」开启自动追帧，点「停止追帧」关闭它');
  assert.equal(
    html.includes('if (enabling)') && html.includes('chaseToLiveEdge(core.CHASE_KEEP_DEFAULT_SECONDS);'),
    true,
    '点「追帧」必须保留原一次性追帧的效果（开启后立刻追一次）');
  assert.equal(html.includes('refreshPlaybackStatus(run)'), true, '遥测每轮都要把实测延迟刷进状态行');
  assert.equal(html.includes('lastLatencyMs'), true, '实测延迟必须来自运行对象的遥测值');
  assert.equal(
    html.includes('applyPlayerTargetConfig(run, message'),
    true,
    '停止追帧必须走统一适配层入口（是否重建由能力表决定，见 applyEngineConfig）');
  assert.equal(
    /player\.configure\(/.test(html),
    false,
    'SP-01：全文不得再出现裸的 player.configure(...) 调用（两家引擎实例上都没有这个方法）');
});

test('追帧按钮必须预留最长文案的固定宽度，文案切换不再引起重排', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');

  // 按钮文案由 player-core 给出：最长文案就是「停止追帧」，最短是「追帧」。
  const longestLabel = core.CHASE_BUTTON_LABELS.STOP;
  const shortestLabel = core.CHASE_BUTTON_LABELS.START;
  assert.equal(longestLabel, '停止追帧');
  assert.ok(longestLabel.length > shortestLabel.length, '用例前提：两个文案长度不同，宽度才会变');

  const chaseFontSizePx = 13;
  const chasePaddingXPx = 8;
  const chaseBorderPx = 1;
  const chaseHeadroomPx = 8;
  const requiredPx = longestLabel.length * chaseFontSizePx + 2 * (chasePaddingXPx + chaseBorderPx) + chaseHeadroomPx;

  const chaseRule = extractRule(html, '#chaseBtn');
  const declaredMinWidthPx = Number(/min-width:\s*(\d+(?:\.\d+)?)px/.exec(chaseRule)?.[1]);
  assert.ok(Number.isFinite(declaredMinWidthPx), '#chaseBtn 必须用 min-width 预留固定宽度，否则文案变长会把右侧控件挤到别行');
  assert.ok(
    declaredMinWidthPx >= requiredPx,
    '预留宽度 ' + declaredMinWidthPx + 'px 必须容纳最长文案「' + longestLabel + '」的 ' + requiredPx + 'px'
      + '（' + longestLabel.length + ' 字 × ' + chaseFontSizePx + 'px + 内边距/边框 + 余量）');

  // 计算依据本身也要锁住：改成更宽的内边距 / 更大的字号而没有同步加宽 min-width 就应当失败。
  assert.equal(/font-size:\s*(\d+(?:\.\d+)?)px/.exec(extractRule(html, '#playBtn'))?.[1], String(chaseFontSizePx));
  assert.equal(/padding:\s*4px\s+(\d+(?:\.\d+)?)px/.exec(extractRule(html, 'button'))?.[1], String(chasePaddingXPx));
  assert.equal(/border:\s*1px\s+solid/.test(extractRule(html, 'button')), true);
});

/** 音量条宽度上限（像素）。超过它，「全屏」就会在常见窗口宽度下被挤到下一行。 */
const MAX_VOLUME_SLIDER_WIDTH_PX = 90;

test('音量条必须足够短，「全屏」才能留在同一行', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');
  const volumeRule = extractRule(html, '#volume');
  const volumeWidthPx = Number(/width:\s*(\d+(?:\.\d+)?)px/.exec(volumeRule)?.[1]);

  // 实测依据（无头 Edge 真实渲染，node tests/web/layout-probe/run-probe.js）：
  // 音量条 140px 时，900px 窗口下控制条仍要两行、「全屏」被挤到第二行；
  // 缩到 90px 后（探针已按真实运行态填好档位 / 画质下拉，含最宽的 HDR 档位名）：
  // 1120px 起单行、1024–620px 两行、560px 起三行，14 个宽度 × 3 快照全部 0 处重叠。
  // 锁住上限，防止宽度被调宽导致复发。
  assert.ok(Number.isFinite(volumeWidthPx), '#volume 必须显式声明宽度，否则滑块会按浏览器默认宽度撑开控制条');
  assert.ok(
    volumeWidthPx <= MAX_VOLUME_SLIDER_WIDTH_PX,
    '音量条宽度 ' + volumeWidthPx + 'px 不得超过 ' + MAX_VOLUME_SLIDER_WIDTH_PX + 'px，否则「全屏」会被挤到下一行');
});

test('布局探针必须按真实运行态填充档位与画质下拉，并保留三种快照', () => {
  const probe = fs.readFileSync(path.join(__dirname, 'layout-probe', 'probe.js'), 'utf8');

  // 空下拉的自然宽度比真实运行态窄一截，"860px 起单行"正是在空下拉下测出来的偏乐观结论。
  assert.equal(probe.includes('renderSelectOptions();'), true, '探针测量前必须填下拉，否则几何结论不代表运行态');
  assert.equal(probe.includes("getElementById('targetSelect')"), true, '档位下拉必须按页面的 renderTargets 填充');
  assert.equal(probe.includes("getElementById('qualitySelect')"), true, '画质下拉必须按页面的 renderQualities 填充');
  assert.equal(probe.includes("'250 ms'"), true, '档位下拉要填页面同款文案「150/200/250 ms」');
  assert.equal(probe.includes("'1080P 高码率'"), true, '画质下拉要填平台真实档位名，宽度才与运行态一致');
  assert.equal(probe.includes("'1080P 原画（HDR 高帧率）'"), true, '画质下拉要覆盖最宽的真实档位名（HDR / 高帧率后缀）');

  // 三种快照（追帧 / 停止追帧 / 停止追帧 + 长状态行）必须保留。
  assert.equal(probe.includes("const CHASE_LABEL_SHORT = '追帧';"), true);
  assert.equal(probe.includes("const CHASE_LABEL_LONG = '停止追帧';"), true);
  assert.equal(probe.includes('STATUS_LONG_TEXT'), true, '长状态行快照必须保留');
  assert.equal(probe.includes('lines='), true, '探针输出必须直接给出行数');
  assert.equal(probe.includes('fsSameLineAsPlay='), true, '探针输出必须直接给出「全屏」是否与播放控制同行');
  assert.equal(probe.includes('collisions='), true, '探针输出必须直接给出碰撞对');
});

test('底部控制条允许换行且不得用绝对定位把控件叠在一起', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');
  const controlsRule = extractRule(html, '#controls');

  assert.equal(/display:\s*flex/.test(controlsRule), true, '控制条必须是 flex 容器');
  assert.equal(/flex-wrap:\s*wrap/.test(controlsRule), true, '控制条必须允许换行，宽度不够时换行而不是把控件压到彼此身上');
  assert.equal(/row-gap:\s*\d/.test(controlsRule), true, '换行后必须有行间距，行与行不能贴着（堆叠感）');
  assert.equal(/position:\s*(absolute|fixed)/.test(controlsRule), false, '控制条自身不能绝对定位');

  // 靠右的「全屏」用 margin-left: auto 独占行尾：换行后仍与左侧按钮分开，不参与同一行的空间争夺。
  assert.equal(/margin-left:\s*auto/.test(extractRule(html, '#fsBtn')), true, '右下角的全屏按钮必须靠 auto 外边距独占行尾');

  // 控制条里任何控件都不许绝对定位到别的控件上面。
  const children = html.slice(html.indexOf('<div id="controls">'), html.indexOf('<div id="statusLine">'));
  assert.equal(/position:\s*absolute/.test(children), false, '控制条内部不得用绝对定位摆放控件（那正是重叠的成因）');
});

test('状态行过长时用省略号收敛，不得溢出盖住控件', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');
  const statusRule = extractRule(html, '#statusLine');

  assert.equal(/white-space:\s*nowrap/.test(statusRule), true, '状态行必须保持单行，否则变长会顶掉上面的控件');
  assert.equal(/overflow:\s*hidden/.test(statusRule), true, '状态行必须裁掉超出部分，不能横向溢出到控制器上');
  assert.equal(/text-overflow:\s*ellipsis/.test(statusRule), true, '被裁掉的部分必须用省略号收尾，让用户知道还有内容');
  assert.equal(/max-width:\s*100%/.test(statusRule), true, '状态行宽度必须封在自己这一行内');
  assert.equal(/position:\s*(absolute|fixed)/.test(statusRule), false, '状态行不能绝对定位到控制条上面');
});

test('退出全屏不把已在播放的提示重新显示成空态', () => {
  assert.equal(core.shouldHideHint({ everPlayed: true, playbackStarted: true }), true);
  assert.equal(
    core.shouldHideHint({ everPlayed: true, playbackStarted: false }),
    true,
    '换线路瞬间 playbackStarted 被清零，提示也不该弹回空态');
  assert.equal(core.shouldHideHint({ everPlayed: false, playbackStarted: false }), false);
  assert.equal(core.shouldHideHint({ playbackStarted: true }), false, '没有 everPlayed 字段的旧形状不应被误判');
  assert.equal(core.shouldHideHint(null), false);
});

test('空态提示指向播放页底部的「开始播放」', () => {
  assert.equal(core.EMPTY_HINT_TITLE, '等待直播源');
  assert.equal(core.EMPTY_HINT_ACTION.includes('开始播放'), true);
  assert.equal(core.EMPTY_HINT_ACTION.includes('下方'), true, '按钮在播放页底部，必须说清楚位置');
  assert.equal(core.AUTOPLAY_BLOCKED_HINT.includes('点一下画面'), true, '自动播放被拦时给出可执行的下一步');
});

test('消息契约包含播放页侧的请求消息与页面日志', () => {
  assert.equal(core.INBOUND_MESSAGE_TYPES.PAUSE, 'pause');
  assert.equal(core.INBOUND_MESSAGE_TYPES.MPV, 'mpv');
  assert.equal(core.OUTBOUND_MESSAGE_TYPES.REQUEST_PLAY, 'request-play');
  assert.equal(core.OUTBOUND_MESSAGE_TYPES.TOGGLE_PAUSE, 'toggle-pause');
  assert.equal(core.OUTBOUND_MESSAGE_TYPES.REQUEST_STOP, 'request-stop', '停止播放要销毁播放器并释放会话，必须让宿主回收中继');
  assert.equal(core.OUTBOUND_MESSAGE_TYPES.LOG, 'log', '页面日志只上报宿主，页面不再显示日志面板');
  assert.equal(core.LOG_LEVELS.INFO, 'info');
  assert.equal(core.LOG_LEVELS.WARN, 'warn');
  assert.equal(core.LOG_LEVELS.ERROR, 'error');
});

test('宿主状态消息类型与级别归一化', () => {
  assert.equal(core.INBOUND_MESSAGE_TYPES.HOST_STATUS, 'host-status', '宿主状态文案必须有独立的消息类型');
  assert.equal(core.STATUS_IDLE_TEXT, '未连接');
  assert.equal(core.normalizeStatusLevel('info'), 'info');
  assert.equal(core.normalizeStatusLevel('warn'), 'warn');
  assert.equal(core.normalizeStatusLevel('error'), 'error');
  assert.equal(core.normalizeStatusLevel('ERROR'), 'error', '级别大小写不敏感');
  assert.equal(core.normalizeStatusLevel('boom'), 'info', '未知级别回落信息级');
  assert.equal(core.normalizeStatusLevel(undefined), 'info');
  assert.equal(core.normalizeStatusLevel(null), 'info');
  assert.equal(core.normalizeStatusLevel(7), 'info');
});

test('宿主状态保留时长按级别递增，未知级别按信息级', () => {
  assert.equal(core.getHostStatusHoldMs('info'), core.HOST_STATUS_HOLD_INFO_MS);
  assert.equal(core.getHostStatusHoldMs('warn'), core.HOST_STATUS_HOLD_WARN_MS);
  assert.equal(core.getHostStatusHoldMs('error'), core.HOST_STATUS_HOLD_ERROR_MS);
  assert.equal(core.getHostStatusHoldMs('boom'), core.HOST_STATUS_HOLD_INFO_MS);
  assert.ok(core.HOST_STATUS_HOLD_INFO_MS < core.HOST_STATUS_HOLD_WARN_MS, '警告要比信息留得久');
  assert.ok(core.HOST_STATUS_HOLD_WARN_MS < core.HOST_STATUS_HOLD_ERROR_MS, '错误要留得最久，用户需要读完失败原因');
});

test('状态行优先级：宿主失败消息覆盖播放状态，超时后回落', () => {
  const host = { message: '解析失败：主播未开播（状态：未开播）', level: 'error', receivedAt: 1000 };
  const hold = core.getHostStatusHoldMs('error');

  const fresh = core.resolveStatusLine(host, '播放中 · 稳定缓冲', 1000 + hold - 1);
  assert.equal(fresh.text, '解析失败：主播未开播（状态：未开播）', '保留期内显示宿主消息');
  assert.equal(fresh.level, 'error', '级别原样带给页面用于换色');
  assert.equal(fresh.source, core.STATUS_LINE_SOURCES.HOST);
  assert.equal(core.getHostStatusRemainingMs(host, 1000 + hold - 1), 1);

  const expired = core.resolveStatusLine(host, '播放中 · 稳定缓冲', 1000 + hold);
  assert.equal(expired.text, '播放中 · 稳定缓冲', '到期后回落显示页面自己的播放状态');
  assert.equal(expired.level, 'info', '回落后的播放状态用主题蓝（info）');
  assert.equal(expired.source, core.STATUS_LINE_SOURCES.PLAYBACK);
  assert.equal(core.getHostStatusRemainingMs(host, 1000 + hold), 0);

  const justArrived = core.resolveStatusLine(
    { message: '已新增预设「测试」', level: 'info', receivedAt: 5000 },
    '播放中 · 稳定缓冲',
    5000);
  assert.equal(justArrived.text, '已新增预设「测试」', '操作反馈同样覆盖播放状态');
  assert.equal(justArrived.level, 'info', '信息级宿主消息仍用蓝色');
});

test('状态行优先级：没有宿主消息或消息非法时一律显示播放状态', () => {
  assert.equal(core.resolveStatusLine(null, '未连接', 1000).text, '未连接');
  assert.equal(core.resolveStatusLine(undefined, '未连接', 1000).source, core.STATUS_LINE_SOURCES.PLAYBACK);
  assert.equal(core.resolveStatusLine({ message: '', level: 'error', receivedAt: 0 }, '未连接', 1).text, '未连接', '空文本不算宿主消息');
  assert.equal(core.resolveStatusLine({ message: '   ', level: 'error', receivedAt: 0 }, '未连接', 1).text, '未连接', '纯空白文本不算宿主消息');
  assert.equal(
    core.resolveStatusLine({ message: '就绪', receivedAt: Number.NaN }, '未连接', 1).text,
    '未连接',
    '时间戳非法时不能把消息永久钉在状态行上');
  assert.equal(core.resolveStatusLine({ message: '就绪' }, '未连接', 1).text, '未连接', '缺少时间戳同样按过期处理');
  assert.equal(core.resolveStatusLine({ message: '就绪', level: 'boom', receivedAt: 0 }, '未连接', 1).level, 'info', '未知级别回落 info');
  assert.equal(core.resolveStatusLine(null, undefined, 1000).text, '', '没有播放状态时给出空串而不是 undefined');
});

test('resolvePlaybackStatusText 逐字给出各会话阶段的文案（操作态优先）', () => {
  const phases = core.PLAYBACK_PHASES;
  assert.equal(core.STATUS_PAUSED_TEXT, '已暂停（保留当前地址）');
  assert.equal(core.STATUS_STOPPED_TEXT, '已停止播放（画面已关闭）');
  assert.equal(core.STATUS_IDLE_TEXT, '未连接');

  const run = { mode: 'extreme', extremeTargetMs: 250, lastLatencyMs: 318 };
  assert.equal(
    core.resolvePlaybackStatusText(run, { statusPhase: phases.PLAYING, pausedByUser: false }),
    '播放中 · 318 ms（极限追帧 250 ms）',
    '播放中：延迟用遥测实测值，括号里是当前模式与目标档位');
  assert.equal(
    core.resolvePlaybackStatusText(run, { statusPhase: phases.PAUSED, pausedByUser: true }),
    '已暂停（保留当前地址）');
  assert.equal(
    core.resolvePlaybackStatusText(run, { statusPhase: phases.PLAYING, pausedByUser: true }),
    '已暂停（保留当前地址）',
    '用户暂停优先于播放态：阶段没来得及切换时也必须是暂停文案');
  assert.equal(
    core.resolvePlaybackStatusText(run, { statusPhase: phases.RECONNECTING, reconnectAttempt: 2 }),
    '重连中（第 2 次）…');
  assert.equal(
    core.resolvePlaybackStatusText(run, { statusPhase: phases.STOPPED }),
    '已停止播放（画面已关闭）');
  assert.equal(
    core.resolvePlaybackStatusText({ mode: 'stable', stopped: true }, { statusPhase: phases.PLAYING }),
    '已停止播放（画面已关闭）',
    '运行对象已被标记停止时，即使阶段还是播放中也必须给停止文案');
  assert.equal(
    core.resolvePlaybackStatusText(null, { statusPhase: phases.IDLE }),
    '未连接');
  assert.equal(
    core.resolvePlaybackStatusText(run, null),
    '未连接',
    '会话态缺失时按未连接处理，不得回落到播放中');

  assert.equal(core.formatReconnectingStatusText(1), '重连中（第 1 次）…');
  assert.equal(core.formatReconnectingStatusText(3), '重连中（第 3 次）…');
  assert.equal(core.formatReconnectingStatusText(undefined), '重连中（第 1 次）…', '次数缺失时按第 1 次，不显示 NaN');
  assert.equal(core.FIRST_RECONNECT_ATTEMPT, 1);
  assert.equal(
    core.STATUS_RECONNECTING_PREFIX + 2 + core.STATUS_RECONNECTING_SUFFIX,
    '重连中（第 2 次）…',
    '重连文案的前后缀常量必须与纯函数拼接结果一致');
});

test('遥测每 500 ms 刷新只更新数值，不覆盖暂停 / 重连 / 停止文案', () => {
  const phases = core.PLAYBACK_PHASES;
  const run = { mode: 'extreme', extremeTargetMs: 250, lastLatencyMs: 318, autoChaseEnabled: true };

  const pausedSession = { statusPhase: phases.PAUSED, pausedByUser: true, reconnectAttempt: 0 };
  assert.equal(core.resolvePlaybackStatusText(run, pausedSession), '已暂停（保留当前地址）');
  run.lastLatencyMs = 5120;
  assert.equal(
    core.resolvePlaybackStatusText(run, pausedSession),
    '已暂停（保留当前地址）',
    '暂停后缓冲再涨，状态行也不得被刷回「播放中」');

  const reconnectingSession = { statusPhase: phases.RECONNECTING, pausedByUser: false, reconnectAttempt: 2 };
  assert.equal(core.resolvePlaybackStatusText(run, reconnectingSession), '重连中（第 2 次）…');
  run.lastLatencyMs = 40;
  assert.equal(
    core.resolvePlaybackStatusText(run, reconnectingSession),
    '重连中（第 2 次）…',
    '重连期间遥测刷新不得覆盖重连文案');

  const stoppedSession = { statusPhase: phases.STOPPED, pausedByUser: false, reconnectAttempt: 0 };
  run.stopped = true;
  assert.equal(core.resolvePlaybackStatusText(run, stoppedSession), '已停止播放（画面已关闭）');
  run.lastLatencyMs = 12;
  assert.equal(
    core.resolvePlaybackStatusText(run, stoppedSession),
    '已停止播放（画面已关闭）',
    '停止后遥测刷新不得覆盖停止文案');

  const playingRun = { mode: 'extreme', extremeTargetMs: 250, lastLatencyMs: 318 };
  const playingSession = { statusPhase: phases.PLAYING, pausedByUser: false, reconnectAttempt: 0 };
  assert.equal(core.resolvePlaybackStatusText(playingRun, playingSession), '播放中 · 318 ms（极限追帧 250 ms）');
  playingRun.lastLatencyMs = 420;
  assert.equal(
    core.resolvePlaybackStatusText(playingRun, playingSession),
    '播放中 · 420 ms（极限追帧 250 ms）',
    '播放中这一支才允许被遥测改写，且只改延迟数值');
});

test('继续播放后回到播放中遥测文案，宿主消息仍优先并能回落到暂停文案', () => {
  const phases = core.PLAYBACK_PHASES;
  const run = { mode: 'extreme', extremeTargetMs: 250, lastLatencyMs: 318 };
  const session = { statusPhase: phases.PAUSED, pausedByUser: true, reconnectAttempt: 0 };
  assert.equal(core.resolvePlaybackStatusText(run, session), '已暂停（保留当前地址）');

  // 点「继续播放」：页面把阶段切回 PLAYING 并复位 pausedByUser（见 Web/player.html 的 resumePlayback）。
  session.statusPhase = phases.PLAYING;
  session.pausedByUser = false;
  assert.equal(
    core.resolvePlaybackStatusText(run, session),
    '播放中 · 318 ms（极限追帧 250 ms）',
    '恢复播放后必须回到「播放中 · N ms（…）」');

  // 宿主消息优先级最高：暂停中文案被覆盖，超时后回落到的仍是暂停文案（而不是「播放中」）。
  const pausedSession = { statusPhase: phases.PAUSED, pausedByUser: true, reconnectAttempt: 0 };
  const pausedText = core.resolvePlaybackStatusText(run, pausedSession);
  const hold = core.getHostStatusHoldMs('info');
  const host = { message: '已新增预设「测试」', level: 'info', receivedAt: 1000 };

  const covered = core.resolveStatusLine(host, pausedText, 1000 + hold - 1);
  assert.equal(covered.text, '已新增预设「测试」', '宿主消息必须能覆盖暂停文案');
  assert.equal(covered.source, core.STATUS_LINE_SOURCES.HOST);

  const fallback = core.resolveStatusLine(host, pausedText, 1000 + hold);
  assert.equal(fallback.text, '已暂停（保留当前地址）', '宿主消息超时后回落到暂停文案');
  assert.equal(fallback.source, core.STATUS_LINE_SOURCES.PLAYBACK);
  assert.equal(fallback.level, core.LOG_LEVELS.INFO);
});

test('播放页只调用纯函数决定状态行，不在页面里重写操作态文案', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');

  assert.equal(html.includes('core.resolvePlaybackStatusText(run, state)'), true, '状态行文案必须来自纯函数');
  assert.equal(html.includes('applyPlaybackPhase(core.PLAYBACK_PHASES.PAUSED, run)'), true, '暂停要切到暂停阶段');
  assert.equal(html.includes('applyPlaybackPhase(core.PLAYBACK_PHASES.RECONNECTING, run)'), true, '重连要切到重连阶段');
  assert.equal(html.includes('applyPlaybackPhase(core.PLAYBACK_PHASES.PLAYING, run)'), true, '首帧 / 继续播放要切回播放阶段');
  assert.equal(html.includes('applyPlaybackPhase(core.PLAYBACK_PHASES.STOPPED, null)'), true, '停止要切到停止阶段');
  assert.equal(html.includes('state.reconnectAttempt = reconnectCount + 1;'), true, '重连次数要记进会话态');

  // 操作态文案只有一处来源：页面里不得再各写一份。
  assert.equal(html.includes("setStatus('已暂停（保留当前地址）')"), false, '暂停文案必须来自纯函数常量');
  assert.equal(html.includes("setStatus('已停止播放（画面已关闭）')"), false, '停止文案必须来自纯函数常量');
  assert.equal(html.includes("setStatus('重连中（第 '"), false, '重连文案必须来自纯函数');
  assert.equal(/setStatus\(core\.formatPlaybackStatusText/.test(html), false, '页面不得绕开阶段判定直接写「播放中」文案');

  const refreshBody = extractFunctionBody(html, 'function refreshPlaybackStatus(run)');
  assert.equal(refreshBody.includes('core.resolvePlaybackStatusText(run, state)'), true, '遥测刷新必须走阶段判定');
  assert.equal(refreshBody.includes('formatPlaybackStatusText'), false, '遥测刷新不得直接把文案写成「播放中」');

  const pauseBody = extractFunctionBody(html, 'function togglePausePlayback(paused)');
  assert.equal(pauseBody.includes('applyPlaybackPhase(core.PLAYBACK_PHASES.PAUSED, run)'), true);

  const stopBody = extractFunctionBody(html, 'function handleStop()');
  assert.equal(stopBody.includes('applyPlaybackPhase(core.PLAYBACK_PHASES.STOPPED, null)'), true);
});

test('宿主状态消息不得占用顶部状态行，页面也不再有日志面板', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');

  assert.equal(html.includes('INBOUND_MESSAGE_TYPES.HOST_STATUS'), true, '页面必须处理 host-status 消息');
  assert.equal(html.includes('applyHostStatus'), true);
  assert.equal(html.includes('data-level="warn"') && html.includes('data-level="error"'), true, '状态行按级别换色（警示 / 错误）');
  assert.equal(html.includes('#statusLine[data-level="warn"]'), true);
  assert.equal(html.includes('#statusLine[data-level="error"]'), true);

  assert.equal(html.includes('显示日志<'), false, '「显示日志」按钮文案必须仍然不存在');
  assert.equal(/<[^>]*id="[^"]*[Ll]og[^"]*"/.test(html), false, '日志面板 / 日志开关元素必须仍然不存在');
  assert.equal(html.includes('modeHint'), false, '顶部状态行必须仍然不存在');
  assert.equal(html.includes('id="statusLine"'), true, '画面下方的状态行仍是唯一状态显示位');
});

/*
 * SP-01 回归：追帧开关与档位切换的"引擎适配层"。
 *
 * 这些用例注入**引擎替身**（带能力标志与方法调用记录），因此结构上能测到"配置到底有没有生效"：
 * mpegts.js 没有覆盖两个方向的运行时配置入口 → 必须走受控重建；
 * hls.js 的 `hls.config` 是共享对象 → 走热改并读回校验。
 */

/**
 * 造一个能力替身：带方法调用记录与"写入是否被接受"的行为开关。
 *
 * 替身只在 `hotConfig: true` 时提供可写 `config`（普通对象包一层 Proxy 以便记录写入）；
 * `verify: 'reject'` 用于模拟"引擎接受了调用但值没生效"，`writeThrows: true` 模拟写入直接抛异常。
 * @param {{hotConfig?:boolean,config?:object,verify?:string,writeThrows?:boolean}} options 替身行为。
 * @returns {{player:object,calls:Array<string>}} 替身与调用记录。
 */
function createEngineDouble(options) {
  const settings = options || {};
  const calls = [];
  const player = {};
  if (settings.hotConfig) {
    const target = Object.assign({}, settings.config);
    player.config = new Proxy(target, {
      set(config, key, value) {
        if (settings.writeThrows) {
          throw new Error('写入配置被拒绝');
        }

        calls.push('set:' + String(key));
        if (settings.verify === 'reject') {
          // 记录调用但不落值：读回校验必须发现"值没生效"。
          return true;
        }

        config[key] = value;
        return true;
      },
    });
  }

  return { player: player, calls: calls };
}

/**
 * 造一个重建回调替身：记录调用次数并返回指定实例。
 * @param {object|null} rebuilt 重建后返回的实例。
 * @returns {{rebuild:Function,calls:Array<string>}} 回调与调用记录。
 */
function createRebuildDouble(rebuilt) {
  const calls = [];
  return {
    calls: calls,
    rebuild: function rebuild() {
      calls.push('rebuild');
      return rebuilt;
    },
  };
}

test('发动机能力表：mpegts 必须重建，hls 可以热改（SP-01 的判定依据）', () => {
  assert.equal(core.ENGINE_KINDS.MPEGTS, 'mpegts');
  assert.equal(core.ENGINE_KINDS.HLS, 'hls');
  assert.equal(core.canHotApplyEngineConfig(core.ENGINE_KINDS.MPEGTS), false, 'mpegts.js 没有覆盖两个方向的运行时配置入口');
  assert.equal(core.canHotApplyEngineConfig(core.ENGINE_KINDS.HLS), true, 'hls.js 的 config 是共享对象，可直接改并读回');
  assert.equal(core.canHotApplyEngineConfig('unknown'), false, '未知引擎按"不支持热改"处理');
  assert.equal(core.canHotApplyEngineConfig(undefined), false);

  // 候选格式 → 引擎：HLS 家族走 hls.js，其余走 mpegts.js（与页面创建播放器的分支一致）。
  assert.equal(core.engineKindForCandidate({ format: 'flv' }), core.ENGINE_KINDS.MPEGTS);
  assert.equal(core.engineKindForCandidate({ format: 'ts' }), core.ENGINE_KINDS.HLS);
  assert.equal(core.engineKindForCandidate({ format: 'fmp4' }), core.ENGINE_KINDS.HLS);
  assert.equal(core.engineKindForCandidate(null), core.ENGINE_KINDS.MPEGTS);
});

test('buildEngineConfig 按候选格式给出同一份推导（页面不再自己挑函数）', () => {
  const run = { extreme: true, extremeTargetSeconds: 0.25, autoChaseEnabled: true };
  const mpegtsConfig = core.buildEngineConfig(run, { format: 'flv' });
  assert.deepEqual(mpegtsConfig, core.buildMpegtsConfig(true, 0.25, true));

  const hlsConfig = core.buildEngineConfig(run, { format: 'fmp4' });
  assert.deepEqual(hlsConfig, core.buildHlsConfig(true, true));

  // 停止追帧后两份配置都要反映"不自动追帧"。
  const stopped = core.buildEngineConfig({ extreme: true, extremeTargetSeconds: 0.25, autoChaseEnabled: false }, { format: 'flv' });
  assert.equal(stopped.liveBufferLatencyChasing, false);
  assert.equal(stopped.liveSync, false);
  const stoppedHls = core.buildEngineConfig({ extreme: true, autoChaseEnabled: false }, { format: 'fmp4' });
  assert.equal(stoppedHls.maxLiveSyncPlaybackRate, 1);
});

test('适配层：hls.js 走热改，写进 hls.config 并读回校验', () => {
  const config = { maxLiveSyncPlaybackRate: 1, liveMaxLatencyDurationCount: Number.MAX_SAFE_INTEGER };
  const engine = createEngineDouble({ hotConfig: true });
  const rebuild = createRebuildDouble({});

  const outcome = core.applyEngineConfig({
    engineKind: core.ENGINE_KINDS.HLS,
    player: engine.player,
    config: config,
    rebuild: rebuild.rebuild,
  });

  assert.equal(outcome.ok, true, '能读回校验的引擎必须走热改');
  assert.equal(outcome.action, core.ENGINE_APPLY_ACTIONS.HOT_APPLY);
  assert.equal(outcome.reason, core.ENGINE_APPLY_REASONS.HOT_APPLIED);
  assert.equal(outcome.report, true, '确认生效后才允许上报成功');
  assert.deepEqual(engine.calls, ['set:maxLiveSyncPlaybackRate', 'set:liveMaxLatencyDurationCount'], '必须真的写了配置');
  assert.deepEqual(rebuild.calls, [], '热改路径不得重建播放器');
  assert.equal(engine.player.config.maxLiveSyncPlaybackRate, 1, '读回校验的对象就是引擎自己的 config');
});

test('适配层：热改读回不一致时判为失败（不得假装成功）', () => {
  const target = { maxLiveSyncPlaybackRate: 1 };
  const engine = createEngineDouble({ hotConfig: true, config: target, verify: 'reject' });
  const outcome = core.applyEngineConfig({
    engineKind: core.ENGINE_KINDS.HLS,
    player: engine.player,
    config: { maxLiveSyncPlaybackRate: 1.5 },
    rebuild: null,
  });

  assert.equal(outcome.ok, false, '写入被接受但值没生效时不得声称成功');
  assert.equal(outcome.reason, core.ENGINE_APPLY_REASONS.VERIFY_FAILED);
  assert.equal(outcome.report, false, '未生效时不得上报成功');
  assert.equal(target.maxLiveSyncPlaybackRate, 1, '替身确实没有落值（前提校验）');
});

test('适配层：热改写入抛异常时判为失败，有重建回调则降级为重建', () => {
  const config = { maxLiveSyncPlaybackRate: 1.5 };
  const throwing = createEngineDouble({ hotConfig: true, config: config, writeThrows: true });

  // 没有重建回调：失败即失败。
  const failed = core.applyEngineConfig({
    engineKind: core.ENGINE_KINDS.HLS,
    player: throwing.player,
    config: config,
    rebuild: null,
  });
  assert.equal(failed.ok, false);
  assert.equal(failed.reason, core.ENGINE_APPLY_REASONS.HOT_APPLY_THREW);
  assert.equal(failed.report, false);

  // 有重建回调：从"写入抛异常"降级为重建，并如实报告动作是重建。
  const rebuilt = createEngineDouble({ hotConfig: true });
  const rebuild = createRebuildDouble(rebuilt.player);
  const recovered = core.applyEngineConfig({
    engineKind: core.ENGINE_KINDS.HLS,
    player: throwing.player,
    config: config,
    rebuild: rebuild.rebuild,
  });
  assert.equal(recovered.ok, true);
  assert.equal(recovered.action, core.ENGINE_APPLY_ACTIONS.REBUILD);
  assert.equal(recovered.reason, core.ENGINE_APPLY_REASONS.REBUILT);
  assert.deepEqual(rebuild.calls, ['rebuild']);
  assert.equal(rebuilt.player.config.maxLiveSyncPlaybackRate, undefined, '重建实例的配置由创建方负责（替身未写）');
});

test('适配层：mpegts.js 必须走受控重建（不得去改私有 _config）', () => {
  const engine = createEngineDouble({ hotConfig: false });
  const rebuilt = createEngineDouble({ hotConfig: false });
  const rebuild = createRebuildDouble(rebuilt.player);
  const snapshot = { sessionId: 7, pausedByUser: true, statusPhase: 'paused', targetMs: 200 };

  const outcome = core.applyEngineConfig({
    engineKind: core.ENGINE_KINDS.MPEGTS,
    player: engine.player,
    config: core.buildMpegtsConfig(true, 0.2, false),
    rebuild: rebuild.rebuild,
    snapshot: snapshot,
  });

  assert.equal(outcome.ok, true);
  assert.equal(outcome.action, core.ENGINE_APPLY_ACTIONS.REBUILD);
  assert.equal(outcome.reason, core.ENGINE_APPLY_REASONS.REBUILT);
  assert.equal(outcome.player, rebuilt.player, '必须返回重建后的新实例');
  assert.equal(outcome.snapshot, snapshot, '重建结果必须带上状态快照供调用方恢复');
  assert.deepEqual(rebuild.calls, ['rebuild'], '只重建一次（不重复建实例、不重复上报）');
  assert.equal(engine.calls.length, 0, '不支持热改的引擎不得被写任何配置');
});

test('适配层：没有重建回调时判为失败，且失败路径不产生上报', () => {
  const engine = createEngineDouble({ hotConfig: false });
  const outcome = core.applyEngineConfig({
    engineKind: core.ENGINE_KINDS.MPEGTS,
    player: engine.player,
    config: core.buildMpegtsConfig(true, 0.25, true),
    rebuild: null,
  });

  assert.equal(outcome.ok, false);
  assert.equal(outcome.action, core.ENGINE_APPLY_ACTIONS.REBUILD);
  assert.equal(outcome.reason, core.ENGINE_APPLY_REASONS.REBUILD_UNAVAILABLE);
  assert.equal(outcome.report, false);
});

test('适配层：重建回调返回 null 时判为失败（保持原状态，不上报成功）', () => {
  const engine = createEngineDouble({ hotConfig: false });
  const rebuild = createRebuildDouble(null);
  const outcome = core.applyEngineConfig({
    engineKind: core.ENGINE_KINDS.MPEGTS,
    player: engine.player,
    config: core.buildMpegtsConfig(true, 0.25, true),
    rebuild: rebuild.rebuild,
  });

  assert.equal(outcome.ok, false);
  assert.equal(outcome.reason, core.ENGINE_APPLY_REASONS.REBUILD_FAILED);
  assert.equal(outcome.player, null);
  assert.equal(outcome.report, false);
  assert.deepEqual(rebuild.calls, ['rebuild']);
});

test('适配层：性能相关的热改判定只看能力表，无 player 时不误报成功', () => {
  const config = core.buildHlsConfig(true, true);
  const outcome = core.applyEngineConfig({
    engineKind: core.ENGINE_KINDS.HLS,
    player: null,
    config: config,
    rebuild: null,
  });

  assert.equal(outcome.ok, false, '没有运行中的实例就不能声称配置已生效');
  assert.equal(outcome.action, core.ENGINE_APPLY_ACTIONS.REBUILD);
  assert.equal(outcome.reason, core.ENGINE_APPLY_REASONS.HOOK_MISSING);
  assert.equal(outcome.report, false);
});

test('快照 / 恢复：重建保留候选、队列、会话号、暂停态、阶段、音量与档位', () => {
  const run = {
    currentCandidate: { url: 'http://127.0.0.1:5566/relay/token', sourceIndex: 3, format: 'flv' },
    queue: [{ sourceIndex: 4 }, { sourceIndex: 5 }],
    sessionId: 42,
    extremeTargetMs: 200,
    extremeTargetSeconds: 0.2,
    mode: 'extreme',
    extreme: true,
    playbackStarted: true,
    everPlayed: true,
  };
  const session = { pausedByUser: true, statusPhase: core.PLAYBACK_PHASES.PAUSED };
  const snapshot = core.snapshotRuntimeState(run, session, { volumePercent: '65', positionSeconds: 12.5 });

  assert.deepEqual(core.RUNTIME_STATE_KEYS.slice().sort(), [
    'currentCandidate', 'extreme', 'mode', 'pausedByUser', 'positionSeconds', 'queue', 'sessionId', 'statusPhase', 'targetMs', 'volumePercent',
  ]);
  assert.equal(snapshot.sessionId, 42);
  assert.equal(snapshot.pausedByUser, true);
  assert.equal(snapshot.statusPhase, core.PLAYBACK_PHASES.PAUSED);
  assert.equal(snapshot.volumePercent, 65, '音量百分比取夹取后的整数');
  assert.equal(snapshot.positionSeconds, 12.5);
  assert.equal(snapshot.targetMs, 200);
  assert.equal(snapshot.queue.length, 2);
  assert.notEqual(snapshot.queue, run.queue, '队列必须是副本，重建期间的改动不能污染快照');

  // 模拟重建期间运行对象被改写：恢复后必须回到快照内容。
  run.currentCandidate = null;
  run.queue = [];
  run.sessionId = 0;
  run.extremeTargetMs = core.DEFAULT_EXTREME_TARGET_MS;
  run.extremeTargetSeconds = 0.25;
  run.mode = 'stable';
  run.extreme = false;
  session.pausedByUser = false;
  session.statusPhase = core.PLAYBACK_PHASES.PLAYING;

  core.restoreRuntimeState(run, session, snapshot);
  assert.deepEqual(run.currentCandidate, snapshot.currentCandidate);
  assert.deepEqual(run.queue, [{ sourceIndex: 4 }, { sourceIndex: 5 }]);
  assert.equal(run.sessionId, 42, '重建不是新会话，会话号必须保持');
  assert.equal(run.extremeTargetMs, 200);
  assert.equal(run.extremeTargetSeconds, 0.2);
  assert.equal(run.mode, 'extreme');
  assert.equal(run.extreme, true);
  assert.equal(session.pausedByUser, true, '重建不得把用户暂停悄悄取消');
  assert.equal(session.statusPhase, core.PLAYBACK_PHASES.PAUSED, '重建期间状态行不得闪回播放中');
  assert.equal(run.playbackStarted, true, '已出过画的事实不能被重建清掉');
  assert.equal(run.everPlayed, true);
});

test('快照：会话态缺失时给出安全缺省（不产生 undefined 字段）', () => {
  const snapshot = core.snapshotRuntimeState(null, null, null);
  assert.equal(snapshot.currentCandidate, null);
  assert.deepEqual(snapshot.queue, []);
  assert.equal(snapshot.sessionId, 0);
  assert.equal(snapshot.pausedByUser, false);
  assert.equal(snapshot.statusPhase, core.PLAYBACK_PHASES.IDLE);
  assert.equal(snapshot.volumePercent, 0);
  assert.equal(snapshot.targetMs, core.DEFAULT_EXTREME_TARGET_MS);
  assert.equal(snapshot.mode, 'stable');
  assert.equal(snapshot.extreme, false);
  assert.equal(snapshot.positionSeconds, 0);

  // 恢复时快照非法也不能抛异常。
  const run = { currentCandidate: null, queue: [] };
  const session = {};
  core.restoreRuntimeState(run, session, null);
  assert.equal(run.currentCandidate, null);
});

test('页面薄调用层：只用适配层入口，成功后 setStatus / 上报，失败时上报失败并回滚', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');

  assert.equal(
    /player\.configure\(/.test(html),
    false,
    'SP-01：不得再出现"无能力检查就调用 configure"的裸调用');
  assert.equal(html.includes('core.applyEngineConfig('), true, '配置应用必须走统一适配层');
  assert.equal(html.includes('core.buildEngineConfig('), true, '配置推导必须走统一入口');
  assert.equal(html.includes('core.engineKindForCandidate('), true, '引擎种类必须由候选格式推导');
  assert.equal(html.includes('core.snapshotRuntimeState('), true, '重建前必须取状态快照');
  assert.equal(html.includes('recreatePlayerWithConfig('), true, '不支持热改的引擎必须走受控重建');

  const applyBody = extractFunctionBody(html, 'function applyPlayerTargetConfig(run, message, extras)');
  assert.equal(applyBody.includes('!outcome.ok'), true, '必须按适配层结论分支');
  assert.equal(applyBody.includes('outbound.WARNING'), true, '失败必须上报失败（warning），不得静默');
  assert.equal(applyBody.includes('return false;'), true, '失败必须让调用方知道没生效，以便回滚');
  assert.equal(
    applyBody.indexOf('if (!outcome.ok)') < applyBody.indexOf('outbound.STATUS'),
    true,
    '成功上报必须只出现在"确认生效"之后');

  // 停止追帧与换档位都必须先判定生效再改 UI / 上报。
  const chaseBody = extractFunctionBody(html, 'function toggleChase()');
  assert.equal(chaseBody.includes('if (!applyPlayerTargetConfig('), true, '停止追帧必须按生效结论分支');
  assert.equal(chaseBody.includes('run.autoChaseEnabled = !enabling;'), true, '未生效时开关必须回滚');

  const targetBody = extractFunctionBody(html, 'function handleTargetChange(payload)');
  assert.equal(targetBody.includes('if (!applyPlayerTargetConfig('), true, '宿主换档位必须按生效结论分支');
  assert.equal(targetBody.includes('core.applyExtremeTarget(run, previousTargetMs);'), true, '未生效时档位必须回滚');
});

test('换档位是"改配置"而不是"换地址"：候选地址与队列在换档位路径上不被改写', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'player.html'), 'utf8');
  const targetBody = extractFunctionBody(html, 'function handleTargetChange(payload)');
  const changeBody = extractFunctionBody(html, 'elements.targetSelect.addEventListener');

  for (const [name, body] of [['handleTargetChange', targetBody], ['onTargetChange', changeBody]]) {
    assert.equal(body.includes('startCandidate'), false, name + ' 不得重新起播候选（换档位不改地址）');
    assert.equal(body.includes('switchToNextCandidate'), false, name + ' 不得切换候选');
    assert.equal(body.includes('requestFreshSources'), false, name + ' 不得请求重新解析');
    assert.equal(body.includes('candidate-active'), false, name + ' 不得重发候选激活上报');
  }

  // 三个档位都只是配置值：换档位后仍应保持同一个候选对象（用纯函数证明档位不进 candidates）。
  const run = { extreme: true, extremeTargetMs: 250, extremeTargetSeconds: 0.25 };
  const candidate = { format: 'flv', url: 'http://127.0.0.1:5566/relay/token' };
  assert.equal(core.applyExtremeTarget(run, 150), true);
  assert.equal(run.extremeTargetMs, 150);
  assert.equal(core.normalizeExtremeTargetSeconds(run.extremeTargetMs), 0.15);
  // 0.15 + 0.27 是浮点相加，用容差比较（值本身由既有用例逐档锁住）。
  assert.ok(
    Math.abs(core.buildEngineConfig(run, candidate).liveBufferLatencyMaxLatency - 0.42) < 1e-9,
    '150 ms 档的延迟阈值必须由新档位推导');
  assert.equal(run.currentCandidate, undefined, '档位推导不得往运行对象里塞候选');
});
