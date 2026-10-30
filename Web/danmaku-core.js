/*
 * StreamPilot 弹幕纯逻辑模块。
 *
 * 与 player-core.js 同一套约定：player.html 通过 <script> 直接引用，node --test 也直接引用，
 * 两边共用同一份实现。约束：
 *  - 只允许纯函数与常量，禁止访问 window/document/DOM；
 *  - 常量必须命名；
 *  - 不发起任何网络请求。
 *
 * 分层职责（与宿主侧 DanmakuOptions 对应）：
 *  - 类型过滤（Filter）在宿主侧完成，页面只负责展示；
 *  - 屏蔽词（BlockedKeywords）、重复合并（MergeDuplicates）、显示模式（DisplayMode）
 *    在页面侧完成——它们只影响"怎么显示"，改配置后立即生效，不需要重连弹幕。
 */
'use strict';

/** 默认同屏弹幕上限。 */
const DEFAULT_MAX_ON_SCREEN = 60;

/** 单帧最多插入条数（超出排队）。 */
const MAX_INSERT_PER_FRAME = 8;

/** 待渲染队列上限（超出丢弃最旧）。 */
const MAX_PENDING_QUEUE = 200;

/** 默认合并窗口（秒）。 */
const DEFAULT_MERGE_WINDOW_SECONDS = 3;

/** 默认横穿屏幕耗时（秒）。 */
const DEFAULT_SPEED_SECONDS = 8;

/** 默认字号（像素）。 */
const DEFAULT_FONT_SIZE = 24;

/** 轨道数上限（防止字号过小时铺满整屏）。 */
const MAX_TRACKS = 24;

/** 弹幕层上下留白（像素），避免贴边。 */
const TRACK_PADDING_PX = 8;

/** 行高系数（字号 × 该值 = 单条轨道高度）。 */
const LINE_HEIGHT_RATIO = 1.35;

/** 精简模式下是否省略粉丝牌。 */
const COMPACT_HIDES_MEDAL = true;

/**
 * 判断文本是否命中屏蔽词。
 * @param {string} text 弹幕文本。
 * @param {string[]} blockedKeywords 屏蔽词列表（可能为空）。
 * @returns {boolean} 命中返回 true。
 * @remarks 不区分大小写；空串与纯空白关键词被忽略。
 */
function isBlocked(text, blockedKeywords) {
  if (!text || !Array.isArray(blockedKeywords) || blockedKeywords.length === 0) {
    return false;
  }

  const lower = String(text).toLowerCase();
  for (const raw of blockedKeywords) {
    if (typeof raw !== 'string') {
      continue;
    }

    const keyword = raw.trim().toLowerCase();
    if (keyword.length > 0 && lower.includes(keyword)) {
      return true;
    }
  }

  return false;
}

/**
 * 生成重复合并键：同一用户发送的相同文本才合并。
 * @param {object} message 弹幕消息。
 * @returns {string} 合并键。
 * @remarks 带上用户是为了避免"不同人发的 666"被合并成一条，那样会让弹幕看起来像同一个人刷屏。
 */
function createMergeKey(message) {
  if (!message) {
    return '';
  }

  return String(message.user || '') + '\u0000' + String(message.text || '');
}

/**
 * 在已有条目中查找可合并的项。
 * @param {Map<string, object>} entries 当前在屏条目（key → entry）。
 * @param {object} message 新消息。
 * @param {number} nowMs 当前时间（毫秒）。
 * @param {number} windowSeconds 合并窗口（秒）。
 * @returns {object|null} 命中的条目；未命中返回 null。
 */
function findMergeTarget(entries, message, nowMs, windowSeconds) {
  if (!entries || typeof entries.get !== 'function') {
    return null;
  }

  const key = createMergeKey(message);
  const entry = entries.get(key);
  if (!entry) {
    return null;
  }

  const windowMs = Math.max(1, Number(windowSeconds) || DEFAULT_MERGE_WINDOW_SECONDS) * 1000;
  if (nowMs - entry.firstSeenMs > windowMs) {
    entries.delete(key);
    return null;
  }

  return entry;
}

/**
 * 选择最早空闲的轨道。
 * @param {number[]} trackFreeAtMs 每条轨道恢复空闲的时间戳（毫秒）。
 * @param {number} nowMs 当前时间（毫秒）。
 * @returns {number} 轨道索引；全部繁忙时返回最早空闲的那条。
 */
function pickTrack(trackFreeAtMs, nowMs) {
  if (!Array.isArray(trackFreeAtMs) || trackFreeAtMs.length === 0) {
    return 0;
  }

  let bestIndex = 0;
  let bestFreeAt = trackFreeAtMs[0];
  for (let index = 1; index < trackFreeAtMs.length; index += 1) {
    if (trackFreeAtMs[index] < bestFreeAt) {
      bestFreeAt = trackFreeAtMs[index];
      bestIndex = index;
    }
  }

  return bestFreeAt <= nowMs ? bestIndex : bestIndex;
}

/**
 * 计算轨道数量。
 * @param {number} layerHeight 弹幕层高度（像素）。
 * @param {number} fontSize 字号（像素）。
 * @returns {number} 轨道数（至少 1）。
 */
function computeTrackCount(layerHeight, fontSize) {
  const size = Math.max(12, Number(fontSize) || DEFAULT_FONT_SIZE);
  const lineHeight = size * LINE_HEIGHT_RATIO;
  const usable = Math.max(0, (Number(layerHeight) || 0) - TRACK_PADDING_PX * 2);
  if (usable <= 0) {
    return 1;
  }

  return Math.max(1, Math.min(MAX_TRACKS, Math.floor(usable / lineHeight)));
}

/**
 * 计算单条弹幕的横穿时长（毫秒）。
 * @param {number} speedSeconds 横穿耗时（秒）。
 * @returns {number} 毫秒。
 */
function scrollDurationMs(speedSeconds) {
  const seconds = Math.max(4, Math.min(20, Number(speedSeconds) || DEFAULT_SPEED_SECONDS));
  return seconds * 1000;
}

/**
 * 数值夹取：非有限数回落默认值；<c>0</c> 是有效值（会被夹到下限，而不是被当成"未设置"）。
 * @param {*} value 原始值。
 * @param {number} min 下限。
 * @param {number} max 上限。
 * @param {number} fallback 非数字时的回落值。
 * @returns {number} 夹取后的值。
 */
function clampNumber(value, min, max, fallback) {
  const number = Number(value);
  if (!Number.isFinite(number)) {
    return fallback;
  }

  return Math.max(min, Math.min(max, number));
}

/**
 * 规范化渲染配置（把宿主下发的字段收敛成安全值）。
 * @param {object} options 宿主下发的配置。
 * @returns {object} 规范化后的配置。
 */
function normalizeOptions(options) {
  const source = options && typeof options === 'object' ? options : {};
  return {
    displayMode: source.displayMode === 'full' ? 'full' : 'compact',
    blockedKeywords: Array.isArray(source.blockedKeywords)
      ? source.blockedKeywords.filter((item) => typeof item === 'string')
      : [],
    mergeDuplicates: source.mergeDuplicates !== false,
    mergeWindowSeconds: clampNumber(source.mergeWindowSeconds, 1, 10, DEFAULT_MERGE_WINDOW_SECONDS),
    opacity: clampNumber(source.opacity, 0.1, 1, 0.9),
    fontSize: clampNumber(source.fontSize, 12, 48, DEFAULT_FONT_SIZE),
    speedSeconds: clampNumber(source.speedSeconds, 4, 20, DEFAULT_SPEED_SECONDS),
    maxOnScreen: clampNumber(source.maxOnScreen, 20, 200, DEFAULT_MAX_ON_SCREEN),
  };
}

/**
 * 精简模式下的展示文本。
 * @param {object} message 弹幕消息。
 * @returns {string} 展示文本。
 * @remarks 精简版只保留「昵称：文本」，完整版额外拼粉丝牌前缀（由渲染层处理样式）。
 */
function compactText(message) {
  const user = String(message?.user || '').trim();
  const text = String(message?.text || '').trim();
  return user.length > 0 ? user + '：' + text : text;
}

/**
 * 完整模式下的展示文本（含粉丝牌）。
 * @param {object} message 弹幕消息。
 * @returns {string} 展示文本。
 */
function fullText(message) {
  const medal = String(message?.medal || '').trim();
  const level = Number(message?.medalLevel) || 0;
  const prefix = medal.length > 0 ? '[' + medal + (level > 0 ? String(level) : '') + '] ' : '';
  return prefix + compactText(message);
}

const StreamPilotDanmakuCore = {
  DEFAULT_MAX_ON_SCREEN,
  MAX_INSERT_PER_FRAME,
  MAX_PENDING_QUEUE,
  DEFAULT_MERGE_WINDOW_SECONDS,
  DEFAULT_SPEED_SECONDS,
  DEFAULT_FONT_SIZE,
  COMPACT_HIDES_MEDAL,
  isBlocked,
  createMergeKey,
  findMergeTarget,
  pickTrack,
  computeTrackCount,
  scrollDurationMs,
  normalizeOptions,
  compactText,
  fullText,
};

if (typeof module !== 'undefined' && module.exports) {
  module.exports = StreamPilotDanmakuCore;
}

if (typeof window !== 'undefined') {
  window.StreamPilotDanmakuCore = StreamPilotDanmakuCore;
}
