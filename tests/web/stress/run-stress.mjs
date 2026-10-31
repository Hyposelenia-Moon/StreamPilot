/**
 * 弹幕压测运行器：无头 Edge 加载 danmaku-stress.html，跑满 60 秒后读回统计结果。
 *
 * 用法：node tests/web/stress/run-stress.mjs
 *
 * 为什么不用 --screenshot / --dump-dom：压测需要真实时间跑 60 秒，
 * 而那两个开关会在页面加载完立刻取快照；这里改用 CDP 轮询页面里的结果对象。
 */
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const EDGE_CANDIDATES = [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
];

const edgePath = EDGE_CANDIDATES.find((candidate) => existsSync(candidate));
if (!edgePath) {
  console.error('找不到 Edge，无法运行压测。');
  process.exit(1);
}

const port = 9223;
const rateArg = process.argv.find((arg) => arg.startsWith('--rate='));
const rate = rateArg ? Number(rateArg.slice('--rate='.length)) : 1000;
const pageUrl = new URL('./danmaku-stress.html', import.meta.url).href
  + (Number.isFinite(rate) && rate > 0 ? `?rate=${rate}` : '');
const profileDir = mkdtempSync(join(tmpdir(), 'sp-stress-'));

const child = spawn(edgePath, [
  '--headless=new',
  '--disable-gpu',
  '--no-first-run',
  '--no-default-browser-check',
  '--disable-extensions',
  `--remote-debugging-port=${port}`,
  `--user-data-dir=${profileDir}`,
  '--enable-precise-memory-info',
  '--disable-background-timer-throttling',
  '--disable-renderer-backgrounding',
  '--disable-backgrounding-occluded-windows',
  '--window-size=1280,720',
  '--hide-scrollbars',
  pageUrl,
], { stdio: 'ignore' });

let exited = false;
child.on('exit', () => {
  exited = true;
});

async function findTarget() {
  for (let attempt = 0; attempt < 40; attempt++) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/list`);
      const list = await response.json();
      const page = list.find((target) => target.type === 'page' && target.url.includes('danmaku-stress'));
      if (page?.webSocketDebuggerUrl) {
        return page;
      }
    } catch {
      // 浏览器还没起来，继续等。
    }

    await delay(500);
  }

  throw new Error('CDP target 未出现（Edge 可能启动失败）');
}

function createMethodCaller(socket) {
  let nextId = 0;
  return (method, params) => new Promise((resolve, reject) => {
    const id = ++nextId;
    const onMessage = (event) => {
      const payload = JSON.parse(event.data);
      if (payload.id !== id) {
        return;
      }

      socket.removeEventListener('message', onMessage);
      if (payload.error) {
        reject(new Error(JSON.stringify(payload.error)));
        return;
      }

      resolve(payload.result);
    };

    socket.addEventListener('message', onMessage);
    socket.send(JSON.stringify({ id, method, params }));
  });
}

/**
 * 读取渲染进程的关键指标。
 *
 * 页面里的 performance.memory 在 file:// 下不可靠（不是安全上下文，Chrome 会给个极小的假值），
 * 所以内存与节点数一律走 CDP 的 Performance.getMetrics（JSHeapUsedSize / Nodes）。
 */
async function readMetrics(call) {
  const response = await call('Performance.getMetrics', {});
  const metrics = new Map((response?.metrics ?? []).map((metric) => [metric.name, metric.value]));
  const heap = metrics.get('JSHeapUsedSize');
  return {
    jsHeapMb: typeof heap === 'number' ? Math.round((heap / 1048576) * 10) / 10 : null,
    nodes: metrics.get('Nodes') ?? null,
  };
}

async function main() {
  const target = await findTarget();
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', () => reject(new Error('CDP WebSocket 连接失败')), { once: true });
  });

  const call = createMethodCaller(socket);
  const evaluate = async (expression) => {
    const response = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: false });
    return response?.result?.value;
  };

  const probe = await evaluate(
    "location.href + ' | ready=' + document.readyState + ' | core=' + typeof window.StreamPilotDanmakuCore + ' | renderer=' + typeof window.DanmakuRenderer + ' | title=' + document.title");
  console.log('[debug] ' + probe);

  await call('Performance.enable', {});
  const startMetrics = await readMetrics(call);
  let result = null;
  for (let attempt = 0; attempt < 45; attempt++) {
    const raw = await evaluate('JSON.stringify(window.__stressResult || null)');
    // JSON.stringify(null) 会得到字符串 "null"，它是 truthy，不能直接当结果用。
    if (raw && raw !== 'null') {
      result = JSON.parse(raw);
      break;
    }

    if (exited) {
      throw new Error('浏览器提前退出');
    }

    await delay(2000);
  }

  const endMetrics = await readMetrics(call);
  socket.close();

  if (result) {
    result.heapStartMb = startMetrics.jsHeapMb;
    result.heapEndMb = endMetrics.jsHeapMb;
    result.heapDeltaMb = startMetrics.jsHeapMb !== null && endMetrics.jsHeapMb !== null
      ? Math.round((endMetrics.jsHeapMb - startMetrics.jsHeapMb) * 10) / 10
      : null;
    result.nodesStart = startMetrics.nodes;
    result.nodesEnd = endMetrics.nodes;
  }

  return result;
}

let exitCode = 0;
try {
  const result = await main();
  if (!result) {
    console.error('60 秒内没有拿到压测结果。');
    exitCode = 1;
  } else if (result.error) {
    console.error('压测页面报错：' + result.error);
    exitCode = 1;
  } else {
    console.log(JSON.stringify(result, null, 2));
    const fpsOk = result.avgFps >= 55;
    const memoryOk = result.heapDeltaMb !== null && result.heapDeltaMb < 50;
    console.log('');
    console.log(`平均帧率 ${result.avgFps} fps（门槛 >= 55）：${fpsOk ? 'PASS' : 'FAIL'}`);
    console.log(`堆增长 ${result.heapDeltaMb} MB（门槛 < 50）：${memoryOk ? 'PASS' : 'FAIL'}`);
    console.log(`最差 1 秒窗口 ${result.worstSecondFps} fps；DOM 峰值 ${result.maxDomNodes} 个；长帧 ${result.longFrames} 次`);
    if (!fpsOk || !memoryOk) {
      exitCode = 1;
    }
  }
} catch (error) {
  console.error('压测失败：' + error.message);
  exitCode = 1;
} finally {
  try {
    child.kill();
  } catch {
    // 忽略：进程可能已经退出。
  }

  await delay(1000);
  try {
    rmSync(profileDir, { recursive: true, force: true });
  } catch {
    // 临时配置目录删不掉不影响结果。
  }
}

process.exit(exitCode);
