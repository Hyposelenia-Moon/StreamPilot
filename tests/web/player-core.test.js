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
  assert.equal(html.includes('applyPlayerTargetConfig(run)'), true, '停止追帧必须走配置热改，而不是重建播放器');
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
