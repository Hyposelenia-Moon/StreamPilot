'use strict';

/*
 * StreamPilot 播放策略纯函数回归测试（node --test）。
 *
 * 运行方式：
 *   node --test tests/web
 * 覆盖：档位归一化、候选规范化与过滤、追帧参数推导、重连退避、卡顿阈值、
 *       手动追帧夹取、错误归类、音量换算、模式/状态/空态文案（正常 / 异常 / 边界三类）。
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

test('getStallThresholdMs 饥饿 6s、非饥饿 9s（不按模式区分）', () => {
  assert.equal(core.getStallThresholdMs(true), 6000);
  assert.equal(core.getStallThresholdMs(false), 9000);
});

test('isPlaybackStalled 排除用户暂停与刚恢复播放的宽限期', () => {
  const playing = { paused: false };
  const paused = { paused: true };

  assert.equal(core.isPlaybackStalled(playing, 5000, true, 0, 100000), false, '未到阈值不重连');
  assert.equal(core.isPlaybackStalled(playing, 6000, true, 0, 100000), true, '饥饿达到 6s 才重连');
  assert.equal(core.isPlaybackStalled(playing, 9000, false, 0, 100000), true, '非饥饿 9s 触发');
  assert.equal(core.isPlaybackStalled(paused, 60000, false, 0, 100000), false, '用户暂停不算卡顿');
  assert.equal(
    core.isPlaybackStalled(playing, 60000, true, 100000 - core.RESUME_GRACE_MS + 1, 100000),
    false,
    '刚点继续播放时的静止属于重建缓冲');
  assert.equal(
    core.isPlaybackStalled(playing, 60000, true, 100000 - core.RESUME_GRACE_MS, 100000),
    true,
    '宽限期结束后恢复正常判定');
});

test('applyStallFallback 连续饥饿后自动回落到稳定档', () => {
  const run = { mode: 'extreme', extreme: true, extremeTargetMs: 150, extremeTargetSeconds: 0.15 };

  for (let index = 1; index < core.STALL_FALLBACK_SAMPLES; index++) {
    assert.equal(core.applyStallFallback(run, true), null, '未达样本上限前不改档位');
    assert.equal(run.extremeTargetMs, 150);
  }

  assert.equal(core.applyStallFallback(run, true), core.FALLBACK_TARGET_MS, '达到上限回落稳定档');
  assert.equal(run.extremeTargetMs, core.FALLBACK_TARGET_MS);
  assert.equal(run.extremeTargetSeconds, core.FALLBACK_TARGET_MS / 1000);

  assert.equal(core.applyStallFallback(run, true), null, '已是稳定档时不再回落');
  assert.equal(run.stalledSamples, 1, '切换后重新开始计数（每个样本只计一次）');

  const recovered = { mode: 'extreme', extreme: true, extremeTargetMs: 200, extremeTargetSeconds: 0.2 };
  core.applyStallFallback(recovered, true);
  core.applyStallFallback(recovered, true);
  assert.equal(core.applyStallFallback(recovered, false), null, '恢复后计数清零');
  assert.equal(recovered.stalledSamples, 0);
  assert.equal(core.applyStallFallback(null, true), null);
});

test('shouldRecoverAfterLoadingComplete 只在真的断流时重连', () => {
  const now = 100000;
  const live = { playbackStarted: true, lastPlaybackProgressAt: now - 1000 };

  assert.equal(core.shouldRecoverAfterLoadingComplete(live, now, false), false, '缓冲取满不重连');
  assert.equal(core.shouldRecoverAfterLoadingComplete(live, now, true), true, '缓冲已空必须重连');
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

test('formatModeHint 只显示档位与播放状态，不显示候选主机名', () => {
  const playing = {
    mode: 'extreme',
    extremeTargetMs: 250,
    playbackStarted: true,
    currentCandidate: { host: 'al-game.flv.huya.com' },
  };
  assert.equal(core.formatModeHint(playing), '极限追帧 250 ms · 播放中');
  assert.equal(core.formatModeHint(playing).includes('huya'), false);

  const connecting = {
    mode: 'stable',
    playbackStarted: false,
    currentCandidate: { host: 'al-game.flv.huya.com' },
  };
  assert.equal(core.formatModeHint(connecting), '稳定缓冲 · 连接中');
  assert.equal(core.formatModeHint(connecting).includes('huya'), false);

  assert.equal(core.formatModeHint({ mode: 'extreme', extremeTargetMs: 150, playbackStarted: true }), '等待直播源');
  assert.equal(core.formatModeHint(null), '等待直播源');
  assert.equal(core.formatModeHint(undefined), '等待直播源');
});

test('formatPlaybackStatusText 状态行不显示候选主机名', () => {
  assert.equal(
    core.formatPlaybackStatusText({ mode: 'extreme', extremeTargetMs: 150, currentCandidate: { host: 'a.example' } }),
    '播放中 · 极限追帧 150 ms');
  assert.equal(
    core.formatPlaybackStatusText({ mode: 'stable', currentCandidate: { host: 'a.example' } }),
    '播放中 · 稳定缓冲');
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

test('消息契约包含播放页侧的请求消息', () => {
  assert.equal(core.INBOUND_MESSAGE_TYPES.PAUSE, 'pause');
  assert.equal(core.INBOUND_MESSAGE_TYPES.MPV, 'mpv');
  assert.equal(core.OUTBOUND_MESSAGE_TYPES.REQUEST_PLAY, 'request-play');
  assert.equal(core.OUTBOUND_MESSAGE_TYPES.TOGGLE_PAUSE, 'toggle-pause');
});