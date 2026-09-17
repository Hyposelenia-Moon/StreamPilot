'use strict';

/*
 * 变异校验脚本（人工排查用）：临时把 Web/player.html 改回"会重叠的老写法"，
 * 用来确认 tests/web/player-core.test.js 里新增的静态断言真的会失败（而不是空断言）。
 *
 * 用法（在仓库根目录执行）：
 *   node tests/web/layout-probe/mutate.js <变异名> [备份文件]
 * 备份文件给定时会先复制回 Web/player.html 再注入变异；校验完必须从备份恢复：
 *   Copy-Item <备份文件> Web/player.html -Force
 */

const fs = require('node:fs');
const path = require('node:path');

const MUTATIONS = {
  'chase-min-width': function removeMinWidth(html) {
    return html.replace('    #chaseBtn {\n      min-width: 78px;\n    }\n\n', '');
  },
  'chase-min-width-too-small': function shrinkMinWidth(html) {
    return html.replace('min-width: 78px;', 'min-width: 60px;');
  },
  'controls-absolute': function absolutizeControls(html) {
    return html.replace('      flex-wrap: wrap;\n      color: #1f2329;', '      flex-wrap: wrap;\n      position: absolute;\n      color: #1f2329;');
  },
  'controls-no-wrap': function dropWrap(html) {
    return html.replace('      flex-wrap: wrap;\n      color: #1f2329;', '      color: #1f2329;');
  },
  'fs-no-auto-margin': function dropAutoMargin(html) {
    return html.replace('      margin-left: auto;\n', '');
  },
  'status-no-ellipsis': function dropEllipsis(html) {
    return html.replace('      text-overflow: ellipsis;\n', '');
  },
  'status-overflow-visible': function dropOverflow(html) {
    return html.replace('      overflow: hidden;\n      text-overflow: ellipsis;\n', '');
  },
};

const mutationName = process.argv[2];
const mutate = MUTATIONS[mutationName];
if (!mutate) {
  throw new Error('未知变异：' + mutationName);
}

const playerPath = path.join(__dirname, '..', '..', '..', 'Web', 'player.html');
const backupPath = process.argv[3];
if (backupPath) {
  fs.copyFileSync(backupPath, playerPath);
}

const original = fs.readFileSync(playerPath, 'utf8');
const mutated = mutate(original);
if (mutated === original) {
  throw new Error('变异没有生效：' + mutationName);
}

fs.writeFileSync(playerPath, mutated, 'utf8');
process.stdout.write('已注入变异 ' + mutationName + '\n');
