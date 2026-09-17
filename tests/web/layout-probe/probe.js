'use strict';

/*
 * 底部控制条布局探针（人工排查用，**不在沙箱内自动运行**）。
 *
 * 用途：把播放页副本交给无头浏览器，在多个窄窗口宽度下切换「追帧 / 停止追帧」与长状态行，
 *      打印每个控件的行号与相互重叠情况，用来复现/复查"右下角堆叠"。
 *
 * 用法（必须在能启动浏览器的环境里跑，本仓库沙箱禁止启动外部进程）：
 *   1. 复制 Web/player.html 到一个临时文件，删掉页面自身的 <script>（探针只关心静态布局）；
 *   2. 在 </body> 前插入 <p id="probeOut"></p> 与本文件内容；
 *   3. msedge --headless=new --window-size=900,700 --virtual-time-budget=3000 --dump-dom file:///临时文件
 *   4. 从 --dump-dom 的 HTML 源码里取 <p id="probeOut"> 的文本。
 */

(function probeMain() {
  /** 追帧按钮的两种文案（与 player-core 的 chaseButtonLabel 逐字一致）。 */
  const CHASE_LABEL_SHORT = '追帧';
  const CHASE_LABEL_LONG = '停止追帧';

  /** 宿主失败消息形态的长状态行，用来验证状态行只收敛、不盖住控件。 */
  const STATUS_LONG_TEXT = '解析失败：主播未开播（状态：未开播），请稍后重试或更换线路';

  /** 需要测量的窗口宽度：覆盖 1280 到 480 的常见 WebView2 宽度。 */
  const VIEWPORT_WIDTHS = [1280, 1120, 1024, 960, 900, 860, 820, 780, 740, 700, 660, 620, 560, 480];

  /** 判定"同一行"的纵向容差（像素）：误差超过它就说明控件被挤到别的行。 */
  const SAME_ROW_TOLERANCE_PX = 2;

  /**
   * 取元素相对 #controls 内容盒的几何盒子（top 用偏移量换算成行号）。
   * @param {Element} element 目标元素。
   * @param {DOMRect} anchor #controls 的矩形。
   * @returns {{id: string, left: number, right: number, top: number, bottom: number, width: number}} 几何盒子。
   */
  function box(element, anchor) {
    const rect = element.getBoundingClientRect();
    return {
      id: element.id || element.className,
      left: Math.round(rect.left),
      right: Math.round(rect.right),
      top: Math.round(rect.top - anchor.top),
      bottom: Math.round(rect.bottom - anchor.top),
      width: Math.round(rect.width),
    };
  }

  /**
   * 判断两个盒子是否有交叠面积。
   * @param {{left: number, right: number, top: number, bottom: number}} a 盒子 A。
   * @param {{left: number, right: number, top: number, bottom: number}} b 盒子 B。
   * @returns {boolean} 有交叠则为 true。
   */
  function isOverlapping(a, b) {
    return Math.min(a.right, b.right) > Math.max(a.left, b.left)
      && Math.min(a.bottom, b.bottom) > Math.max(a.top, b.top);
  }

  /**
   * 把控件按纵向位置归行：同一行的控件 top 差值在容差内。
   * @param {Array<object>} boxes 控件盒子。
   * @returns {Array<Array<object>>} 行数组。
   */
  function groupRows(boxes) {
    return boxes.reduce(function pushIntoRow(rows, item) {
      const last = rows[rows.length - 1];
      if (last && Math.abs(last[0].top - item.top) <= SAME_ROW_TOLERANCE_PX) {
        last.push(item);
        return rows;
      }
      rows.push([item]);
      return rows;
    }, []);
  }

  /**
   * 采集一次布局快照：控件行数、每行控件、重叠对、状态行是否溢出。
   * @returns {object} 快照。
   */
  function snapshot() {
    const controls = document.getElementById('controls');
    const statusLine = document.getElementById('statusLine');
    const anchor = controls.getBoundingClientRect();
    const boxes = Array.prototype.map.call(controls.children, function toBox(child) {
      return box(child, anchor);
    });

    const collisions = [];
    boxes.forEach(function compareWithLater(item, index) {
      boxes.slice(index + 1).forEach(function compare(other) {
        if (isOverlapping(item, other)) {
          collisions.push(item.id + '×' + other.id);
        }
      });
    });
    if (isOverlapping(box(statusLine, anchor), box(controls, anchor))) {
      collisions.push('statusLine×controls');
    }

    const rows = groupRows(boxes);
    return {
      controlsHeight: Math.round(anchor.height),
      rows: rows.map(function rowIds(row) {
        return row.map(function id(item) {
          return item.id + '(' + item.width + ')';
        }).join('|');
      }),
      collisions: collisions,
      statusOverflow: Math.max(0, statusLine.scrollWidth - Math.round(statusLine.getBoundingClientRect().width)),
    };
  }

  /** 采集结果并按"宽度 文案 状态行长度"逐行输出到 #probeOut。 */
  function run() {
    const output = document.getElementById('probeOut');
    const chaseBtn = document.getElementById('chaseBtn');
    const statusLine = document.getElementById('statusLine');
    const lines = [];

    function record(tag) {
      lines.push('W' + document.getElementById('root').getBoundingClientRect().width
        + ' ' + tag + ' ' + JSON.stringify(snapshot()));
    }

    VIEWPORT_WIDTHS.forEach(function measureWidth(width) {
      document.getElementById('root').style.width = width + 'px';

      chaseBtn.textContent = CHASE_LABEL_SHORT;
      statusLine.textContent = '未连接';
      record('SHORT');

      chaseBtn.textContent = CHASE_LABEL_LONG;
      record('LONG');

      statusLine.textContent = STATUS_LONG_TEXT;
      record('LONG+LONGSTATUS');
    });

    output.textContent = lines.join('\n');
  }

  if (document.readyState === 'complete') {
    run();
  } else {
    window.addEventListener('load', run);
  }
}());
