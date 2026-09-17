/*
 * 在受限沙箱里执行 node:test 用例的替代入口。
 *
 * `node --test <file>` 会为每个测试文件新建子进程（stdio 走管道），沙箱会以访问被拒结束；
 * 这里改为在当前进程内直接 import 测试文件，node:test 在无 runner 时仍然会执行并报告结果
 * （失败会让进程以非 0 退出）。用法：node run-tests-inline.mjs <测试文件相对路径>
 */
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

const target = resolve(process.cwd(), process.argv[2] || 'tests/web/player-core.test.js');
await import(pathToFileURL(target).href);
