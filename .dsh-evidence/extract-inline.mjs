import { readFileSync, writeFileSync } from 'node:fs';

const html = readFileSync('Web/player.html', 'utf8');
const match = html.match(/<script>\s*'use strict';([\s\S]*?)<\/script>/);
if (!match) {
  throw new Error('未找到播放页内联脚本');
}

writeFileSync('.dsh-evidence/player-inline.js', match[1], 'utf8');
console.log(`内联脚本已提取，长度=${match[1].length}`);
