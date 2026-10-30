'use strict';

// StreamPilot 弹幕纯逻辑测试（node --test）。
// 与 player-core.test.js 同一套约定：直接引用 Web/danmaku-core.js，不启动浏览器。

const test = require('node:test');
const assert = require('node:assert');
const core = require('../../Web/danmaku-core.js');

test('屏蔽词：命中即拦截，且不区分大小写', () => {
  assert.equal(core.isBlocked('这是广告内容', ['广告']), true);
  assert.equal(core.isBlocked('这是广告内容', ['AD', '广告']), true);
  assert.equal(core.isBlocked('Hello World', ['hello']), true, '大小写不敏感');
  assert.equal(core.isBlocked('正常弹幕', ['广告']), false);
});

test('屏蔽词：空列表、空串、纯空白关键词都被忽略', () => {
  assert.equal(core.isBlocked('任意文本', []), false);
  assert.equal(core.isBlocked('任意文本', ['', '   ']), false, '空关键词不能把整屏弹幕屏蔽掉');
  assert.equal(core.isBlocked('', ['广告']), false, '空文本不命中');
  assert.equal(core.isBlocked(null, ['广告']), false);
});

test('合并键：同用户同文本才相同，不同用户不合并', () => {
  const a = core.createMergeKey({ user: '甲', text: '666' });
  const b = core.createMergeKey({ user: '甲', text: '666' });
  const c = core.createMergeKey({ user: '乙', text: '666' });
  assert.equal(a, b);
  assert.notEqual(a, c, '不同用户发的相同文本不能合并');
});

test('合并：窗口内命中，窗口外失效', () => {
  const entries = new Map();
  const entry = { element: {}, count: 1, firstSeenMs: 1000 };
  entries.set(core.createMergeKey({ user: '甲', text: '666' }), entry);

  const hit = core.findMergeTarget(entries, { user: '甲', text: '666' }, 2000, 3);
  assert.equal(hit, entry, '2 秒内必须命中');

  const miss = core.findMergeTarget(entries, { user: '甲', text: '666' }, 5000, 3);
  assert.equal(miss, null, '超过 3 秒窗口必须失效');
  assert.equal(entries.size, 0, '过期条目必须被移除');
});

test('轨道分配：选最早空闲的轨道', () => {
  const tracks = [5000, 1000, 3000];
  assert.equal(core.pickTrack(tracks, 2000), 1, '轨道 1 最早空闲');
  assert.equal(core.pickTrack([], 0), 0, '空数组回落 0');
});

test('轨道数：按层高与字号计算，且有上下限', () => {
  assert.equal(core.computeTrackCount(0, 24), 1, '高度为 0 时至少 1 条');
  const normal = core.computeTrackCount(600, 24);
  assert.ok(normal >= 1 && normal <= 24, '轨道数必须在 1..24 之间');
  assert.ok(core.computeTrackCount(4000, 12) <= 24, '超大层高不能超过上限');
});

test('横穿时长：夹取到 4-20 秒', () => {
  assert.equal(core.scrollDurationMs(8), 8000);
  assert.equal(core.scrollDurationMs(1), 4000, '低于下限取 4 秒');
  assert.equal(core.scrollDurationMs(99), 20000, '高于上限取 20 秒');
  assert.equal(core.scrollDurationMs(undefined), core.DEFAULT_SPEED_SECONDS * 1000);
});

test('配置规范化：非法值回落到安全默认，类型收敛', () => {
  const options = core.normalizeOptions({
    displayMode: 'unknown',
    blockedKeywords: ['广告', 123, null],
    mergeDuplicates: false,
    mergeWindowSeconds: 99,
    opacity: 5,
    fontSize: 999,
    speedSeconds: 0,
    maxOnScreen: -1,
  });

  assert.equal(options.displayMode, 'compact', '未知显示模式回落精简');
  assert.deepEqual(options.blockedKeywords, ['广告'], '非字符串关键词被过滤');
  assert.equal(options.mergeDuplicates, false);
  assert.equal(options.mergeWindowSeconds, 10, '合并窗口上限 10 秒');
  assert.equal(options.opacity, 1, '不透明度上限 1');
  assert.equal(options.fontSize, 48, '字号上限 48');
  assert.equal(options.speedSeconds, 4, '速度下限 4 秒');
  assert.equal(options.maxOnScreen, 20, '同屏上限下限 20');
});

test('精简版与完整版：完整版带粉丝牌前缀', () => {
  const message = { user: '甲', text: '你好', medal: '粉丝牌', medalLevel: 21 };
  assert.equal(core.compactText(message), '甲：你好', '精简版只有昵称与文本');
  assert.equal(core.fullText(message), '[粉丝牌21] 甲：你好', '完整版带粉丝牌');
  assert.equal(core.fullText({ user: '乙', text: 'hi' }), '乙：hi', '无粉丝牌时不加前缀');
});

test('常量：默认值与契约一致', () => {
  assert.equal(core.DEFAULT_MAX_ON_SCREEN, 60);
  assert.equal(core.MAX_INSERT_PER_FRAME, 8);
  assert.equal(core.MAX_PENDING_QUEUE, 200);
  assert.equal(core.DEFAULT_MERGE_WINDOW_SECONDS, 3);
});
