'use strict';

/*
 * StreamPilot 播放策略纯函数回归测试（node --test）。
 *
 * 运行方式：
 *   node --test tests/web
 * 覆盖：档位归一化、候选规范化与过滤、追帧参数推导、重连退避、卡顿阈值、
 *       手动追帧夹取、错误归类（正常 / 异常 / 边界三类）。
 */

const test = require('node:test');
const assert = require('node:assert/strict');
const core = require('../../Web/player-core.js');

test('normalizeExtremeTargetSeconds 只接受 150/200/250，其他值回落 0.2s', () => {
  assert.equal(core.normalizeExtremeTargetSeconds(150), 0.15);
  assert.equal(core.normalizeExtremeTargetSeconds(200), 0.2);
  assert.equal(core.normalizeExtremeTargetSeconds(250), 0.25);
  assert.equal(core.normalizeExtremeTargetSeconds(0), 0.2);
  assert.equal(core.normalizeExtremeTargetSeconds(-1), 0.2);
  assert.equal(core.normalizeExtremeTargetSeconds('250'), 0.25);
  assert.equal(core.normalizeExtremeTargetSeconds(undefined), 0.2);
  assert.equal(core.normalizeExtremeTargetSeconds(Number.NaN), 0.2);
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

test('getStallThresholdMs 按模式与饥饿状态选择阈值', () => {
  assert.equal(core.getStallThresholdMs(true, true), 4000);
  assert.equal(core.getStallThresholdMs(true, false), 6500);
  assert.equal(core.getStallThresholdMs(false, true), 6000);
  assert.equal(core.getStallThresholdMs(false, false), 9000);
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

  assert.equal(core.looksStarved({ paused: true, ended: false, readyState: 4 }, 2), true);
  assert.equal(core.looksStarved({ paused: false, ended: false, readyState: 4 }, 0.1), true);
  assert.equal(core.looksStarved({ paused: false, ended: false, readyState: 4 }, 0.5), false);
  assert.equal(core.looksStarved(null, 1), true);
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
  assert.equal(picked.items[0].label, '4K 原画 · 20000 kbps');
  assert.equal(picked.items[1].label, '原画 · 10000 kbps');

  const fallback = core.normalizeQualities(payload, 'not-exist');
  assert.equal(fallback.selectedKey, '20000');
  assert.deepEqual(fallback.items.map((item) => item.selected), [true, false]);

  assert.deepEqual(core.normalizeQualities(null, 'x'), { items: [], selectedKey: '' });
  assert.deepEqual(core.normalizeQualities([], 'x'), { items: [], selectedKey: '' });
});

test('OUTBOUND_MESSAGE_TYPES 包含画质消息', () => {
  assert.equal(core.OUTBOUND_MESSAGE_TYPES.QUALITY, 'quality');
});