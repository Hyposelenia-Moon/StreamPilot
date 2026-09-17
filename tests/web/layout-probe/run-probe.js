'use strict';

/*
 * 底部控制条几何探针运行器（人工排查用）。
 *
 * 沙箱内由 DSH 的进程启动策略决定能否成功；失败时会打印实际错误，不做静默兜底。
 * 用法：node tests/web/layout-probe/run-probe.js
 */

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

/** 无头浏览器候选路径（Edge 优先，其次 Chrome）。 */
const BROWSER_CANDIDATES = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
];

/** 无头渲染等待页面脚本跑完的虚拟时间（毫秒）。 */
const VIRTUAL_TIME_BUDGET_MS = 3000;

/** 探针默认窗口尺寸。 */
const VIEWPORT_WIDTH = 900;
const VIEWPORT_HEIGHT = 700;

// 本文件位于 tests/web/layout-probe，向上三级才是仓库根目录。
const playerHtmlPath = path.join(__dirname, '..', '..', '..', 'Web', 'player.html');
const probeScriptPath = path.join(__dirname, 'probe.js');
const workDir = path.join(os.tmpdir(), 'streampilot-layout-probe');

const browser = BROWSER_CANDIDATES.find(function exists(candidate) {
  return fs.existsSync(candidate);
});
if (!browser) {
  throw new Error('找不到可用的无头浏览器（Edge / Chrome 均不在默认安装路径）');
}

const pageHtml = fs.readFileSync(playerHtmlPath, 'utf8')
  // 探针只关心静态布局，去掉页面自身脚本（无宿主环境下它会不断报错）。
  .replace(/<script\b[\s\S]*?<\/script>/g, '')
  .replace('</body>', '<p id="probeOut"></p><script>' + fs.readFileSync(probeScriptPath, 'utf8') + '</script></body>');

fs.mkdirSync(workDir, { recursive: true });
const pagePath = path.join(workDir, 'probe.html');
const dumpPath = path.join(workDir, 'dom.txt');
fs.writeFileSync(pagePath, pageHtml, 'utf8');

const result = spawnSync(browser, [
  '--headless=new',
  '--disable-gpu',
  '--no-first-run',
  '--no-default-browser-check',
  '--user-data-dir=' + path.join(workDir, 'profile'),
  '--window-size=' + VIEWPORT_WIDTH + ',' + VIEWPORT_HEIGHT,
  '--virtual-time-budget=' + VIRTUAL_TIME_BUDGET_MS,
  '--dump-dom',
  'file:///' + pagePath.replace(/\\/g, '/'),
], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });

if (result.error) {
  throw result.error;
}
if (result.status !== 0) {
  throw new Error('无头浏览器退出码 ' + result.status + '：' + (result.stderr || ''));
}

fs.writeFileSync(dumpPath, result.stdout, 'utf8');
const matched = /<p id="probeOut">([\s\S]*?)<\/p>/.exec(result.stdout);
if (!matched) {
  throw new Error('页面里没有探针输出，说明无头渲染没有跑到探针；完整 DOM 已写入 ' + dumpPath);
}

process.stdout.write(matched[1]
  .replace(/&quot;/g, '"')
  .replace(/&lt;/g, '<')
  .replace(/&gt;/g, '>')
  .replace(/&amp;/g, '&') + '\n');
