import { writeFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { resolve } from 'node:path';

const outDir = resolve(process.env.SP_EVIDENCE_DIR ?? '.dsh-evidence', 'raw');
mkdirSync(outDir, { recursive: true });
const logPath = resolve(process.env.SP_EVIDENCE_DIR ?? '.dsh-evidence', 'net-probe.log');

const record = (line) => {
  try {
    appendFileSync(logPath, `${line}\n`, 'utf8');
  } catch {
    /* 探针日志写入失败不影响取证 */
  }
};

const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';

const HEADERS = {
  'User-Agent': UA,
  Referer: 'https://live.bilibili.com/',
  Accept: 'application/json, text/plain, */*',
  'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8',
};

const TARGETS = [
  ['room-page', 'https://live.bilibili.com/814'],
  ['info-by-room', 'https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id=814'],
  [
    'room-base-info',
    'https://api.live.bilibili.com/xlive/web-room/v1/index/getRoomBaseInfo?room_ids=814&req_biz=web_room_componet',
  ],
  [
    'play-info',
    'https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?protocol=0,1&format=0,1,2&codec=0,1&qn=30000&platform=web&ptype=8&dolby=5&panorama=1&room_id=814',
  ],
];

for (const [name, url] of TARGETS) {
  try {
    const res = await fetch(url, { headers: HEADERS, redirect: 'follow' });
    const body = await res.text();
    writeFileSync(resolve(outDir, `${name}.txt`), body, 'utf8');
    record(`${name} status=${res.status} bytes=${body.length}`);
  } catch (error) {
    record(`${name} ERROR ${String(error)}`);
  }
}

record('done');
