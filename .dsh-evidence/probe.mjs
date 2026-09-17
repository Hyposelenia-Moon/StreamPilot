import { writeFileSync, appendFileSync } from 'node:fs';

const LOG = '.dsh-evidence/net-probe.log';
writeFileSync(LOG, 'start\n', 'utf8');

const record = (line) => {
  try {
    appendFileSync(LOG, `${line}\n`, 'utf8');
  } catch {
    /* 探针日志失败可忽略 */
  }
};

record(`fetch_type=${typeof fetch}`);

try {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 20000);
  const res = await fetch('https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id=814', {
    headers: {
      'User-Agent':
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
      Referer: 'https://live.bilibili.com/',
    },
    signal: controller.signal,
  });
  clearTimeout(timer);
  const body = await res.text();
  record(`status=${res.status} bytes=${body.length}`);
  writeFileSync('.dsh-evidence/raw/info-by-room.txt', body, 'utf8');
} catch (error) {
  record(`error=${error && error.name} ${error && error.message} cause=${error && error.cause && error.cause.message}`);
}

record('done');
