import { writeFileSync, mkdirSync } from 'node:fs';

const OUT = '.dsh-evidence/raw/';
mkdirSync(OUT, { recursive: true });

const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';

const HEADERS = {
  'User-Agent': UA,
  Referer: 'https://live.bilibili.com/',
  Accept: 'application/json, text/plain, */*',
};

const TARGETS = [
  ['room-page', 'https://live.bilibili.com/814'],
  ['info-by-room', 'https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id=814'],
  ['room-base-info', 'https://api.live.bilibili.com/xlive/web-room/v1/index/getRoomBaseInfo?room_ids=814&req_biz=web_room_componet'],
  ['play-info', 'https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?protocol=0,1&format=0,1,2&codec=0,1&qn=30000&platform=web&ptype=8&dolby=5&panorama=1&room_id=814'],
];

for (const [name, url] of TARGETS) {
  try {
    const res = await fetch(url, { headers: HEADERS, redirect: 'follow' });
    const body = await res.text();
    writeFileSync(`${OUT}${name}.txt`, body, 'utf8');
    console.log(`${name} status=${res.status} bytes=${body.length}`);
  } catch (error) {
    console.log(`${name} ERROR ${String(error)}`);
  }
}
