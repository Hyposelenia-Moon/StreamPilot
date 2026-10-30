/*
 * StreamPilot 弹幕渲染器（DOM 操作层）。
 *
 * 依赖 danmaku-core.js 提供的纯函数（屏蔽词、合并键、轨道分配），本文件只做 DOM 与动画。
 * 设计要点：
 *  - 轨道分配：每条轨道记录"下一条可进入时间"，新弹幕选最早空闲的轨道，避免追尾；
 *  - DOM 复用：动画结束即移除节点，不做无限累积；
 *  - 限流：单帧最多插入 MAX_INSERT_PER_FRAME 条，超出排队；队列溢出丢最旧；
 *  - 合并：命中合并键的重复弹幕只更新文本计数，不新增节点。
 */
(function (root) {
  'use strict';

  const core = root.StreamPilotDanmakuCore;
  if (!core) {
    // 缺依赖时静默退出：弹幕是附加能力，不能因为脚本加载顺序影响播放。
    return;
  }

  const ITEM_CLASS = 'sp-danmaku-item';
  const TEXT_CLASS = 'sp-danmaku-text';
  const MEDAL_CLASS = 'sp-danmaku-medal';

  class DanmakuRenderer {
    /**
     * @param {HTMLElement} layer 弹幕层容器（绝对定位于画面之上）。
     */
    constructor(layer) {
      this.layer = layer;
      this.options = core.normalizeOptions({});
      this.trackFreeAtMs = [];
      this.entries = new Map();
      this.pending = [];
      this.frameRequest = null;
      this.visible = false;
    }

    /**
     * 设置可见性（关闭时立即清屏并停止插入）。
     * @param {boolean} visible 是否可见。
     */
    setVisible(visible) {
      this.visible = Boolean(visible);
      if (this.layer) {
        this.layer.classList.toggle('sp-danmaku-hidden', !this.visible);
      }
      if (!this.visible) {
        this.clear();
      }
    }

    /**
     * 更新渲染配置（立即生效，不重建 DOM）。
     * @param {object} options 宿主下发的配置。
     */
    updateOptions(options) {
      this.options = core.normalizeOptions(options);
      if (this.layer) {
        this.layer.style.opacity = String(this.options.opacity);
        this.layer.style.fontSize = this.options.fontSize + 'px';
      }
    }

    /**
     * 批量推入弹幕。
     * @param {object[]} messages 消息数组。
     */
    push(messages) {
      if (!this.visible || !Array.isArray(messages) || messages.length === 0) {
        return;
      }

      for (const message of messages) {
        if (!message || !message.text) {
          continue;
        }

        if (core.isBlocked(message.text, this.options.blockedKeywords)) {
          continue;
        }

        this.pending.push(message);
      }

      if (this.pending.length > core.MAX_PENDING_QUEUE) {
        this.pending.splice(0, this.pending.length - core.MAX_PENDING_QUEUE);
      }

      this.scheduleFrame();
    }

    /** 清空屏幕与队列。 */
    clear() {
      this.pending.length = 0;
      this.entries.clear();
      this.trackFreeAtMs.length = 0;
      if (this.layer) {
        this.layer.replaceChildren();
      }
    }

    scheduleFrame() {
      if (this.frameRequest !== null || typeof root.requestAnimationFrame !== 'function') {
        return;
      }

      this.frameRequest = root.requestAnimationFrame(() => {
        this.frameRequest = null;
        this.renderBatch();
      });
    }

    renderBatch() {
      const now = typeof root.performance?.now === 'function' ? root.performance.now() : Date.now();
      let inserted = 0;
      while (this.pending.length > 0 && inserted < core.MAX_INSERT_PER_FRAME) {
        this.renderOne(this.pending.shift(), now);
        inserted += 1;
      }

      if (this.pending.length > 0) {
        this.scheduleFrame();
      }
    }

    renderOne(message, now) {
      // 1) 重复合并：命中则只更新计数，不新建节点。
      if (this.options.mergeDuplicates) {
        const target = core.findMergeTarget(this.entries, message, now, this.options.mergeWindowSeconds);
        if (target) {
          target.count += 1;
          this.applyText(target, message);
          return;
        }
      }

      // 2) 轨道分配。
      const trackCount = core.computeTrackCount(
        this.layer ? this.layer.clientHeight : 0,
        this.options.fontSize);
      if (this.trackFreeAtMs.length !== trackCount) {
        this.trackFreeAtMs = new Array(trackCount).fill(0);
      }

      const track = core.pickTrack(this.trackFreeAtMs, now);
      const durationMs = core.scrollDurationMs(this.options.speedSeconds);
      this.trackFreeAtMs[track] = now + durationMs;

      // 3) 创建节点并启动动画。
      const element = root.document.createElement('div');
      element.className = ITEM_CLASS;
      element.style.top = this.trackTop(track);
      element.style.animationDuration = durationMs + 'ms';
      if (this.options.displayMode === 'full' && message.color) {
        element.style.color = message.color;
      }

      const entry = { element, count: 1, firstSeenMs: now };
      this.applyText(entry, message);
      element.addEventListener('animationend', () => {
        element.remove();
        const key = core.createMergeKey(message);
        if (this.entries.get(key) === entry) {
          this.entries.delete(key);
        }
      }, { once: true });

      if (this.layer) {
        this.layer.appendChild(element);
      }

      if (this.options.mergeDuplicates) {
        this.entries.set(core.createMergeKey(message), entry);
      }
    }

    applyText(entry, message) {
      const text = this.options.displayMode === 'full'
        ? core.fullText(message)
        : core.compactText(message);
      const suffix = entry.count > 1 ? ' ×' + String(entry.count) : '';
      entry.element.textContent = text + suffix;
      if (this.options.displayMode === 'full' && message.medal) {
        entry.element.title = '粉丝牌：' + message.medal + String(message.medalLevel || '');
      }
    }

    trackTop(track) {
      const size = this.options.fontSize;
      const lineHeight = size * 1.35;
      return Math.round(8 + track * lineHeight) + 'px';
    }
  }

  root.DanmakuRenderer = DanmakuRenderer;
}(typeof window !== 'undefined' ? window : this));
