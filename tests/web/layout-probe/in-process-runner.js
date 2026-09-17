'use strict';

/*
 * 沙箱内跑 Web 回归用例的回执脚本（人工排查用）。
 *
 * 本仓库沙箱禁止 `node --test <目录>`（用例运行器要 spawn 子进程，报 EPERM），
 * 所以改成在当前进程里 require 用例文件；为了不刷屏，只把"失败用例名 + 失败原因 + 最终统计"打到 stdout。
 * 用法（在仓库根目录执行）：node tests/web/layout-probe/in-process-runner.js [用例文件]
 */

require('node:test');

const originalWrite = process.stdout.write.bind(process.stdout);
const chunks = [];
process.stdout.write = function capture(chunk) {
  chunks.push(String(chunk));
  return true;
};

const target = process.argv[2] || 'tests/web/player-core.test.js';
require(require('node:path').resolve(target));

process.on('exit', function flush() {
  process.stdout.write = originalWrite;
  const report = chunks.join('').split('\n').filter(function interesting(line) {
    return /^(✖|ℹ (tests|pass|fail|suites))/u.test(line.trim()) || line.includes('AssertionError');
  });
  originalWrite(report.join('\n') + '\n');
});
