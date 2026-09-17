import { writeFileSync, mkdirSync } from 'node:fs';

const OUT = new URL('./raw/', import.meta.url);
mkdirSync(OUT, { recursive: true });

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
  ['room-base-info', 'https://api.live.bilibili.com/xlive/web-room/v1/index/getRoomBaseInfo?room_ids=814&req_biz=web_room_componet'],
  ['play-info', 'https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?protocol=0,1&format=0,1,2&codec=0,1&qn=30000&platform=web&ptype=8&dolby=5&panorama=1&room_id=814'],
];

const summary = {};

for (const [name, url] of TARGETS) {
  try {
    const res = await fetch(url, { headers: HEADERS, redirect: 'follow' });
    const body = await res.text();
    writeFileSync(new URL(`./raw/${name}.txt`, OUT), body, 'utf8');
    let parsed = null;
    try {
      parsed = JSON.parse(body);
    } catch {
      parsed = null;
    }
    const entry = { status: res.status, bytes: body.length, url };
    if (parsed) {
      entry.code = parsed.code;
      entry.message = parsed.message;
      const data = parsed.data ?? parsed;
      entry.dataKeys = data && typeof data === 'object' ? Object.keys(data) : null;
      if (data && typeof data === 'object') {
        entry.live_status = data.live_status ?? data?.room_info?.live_status ?? null;
        entry.room_id = data.room_id ?? data?.room_info?.room_id ?? null;
        entry.title = data.title ?? data?.room_info?.title ?? null;
        entry.anchor = data.uname ?? data?.anchor_info?.base_info?.uname ?? null;
        if (data.playurl_info !== undefined) {
          entry.has_playurl_info = data.playurl_info !== null;
          const playurl = data.playurl_info?.playurl ?? null;
          entry.playurlKeys = playurl ? Object.keys(playurl) : null;
          entry.g_qn_desc = playurl?.g_qn_desc ?? null;
          entry.current_qn = playurl?.current_qn ?? null;
          entry.accept_qn = playurl?.accept_qn ?? null;
          const streams = playurl?.stream ?? null;
          if (Array.isArray(streams)) {
            entry.streamCount = streams.length;
            entry.streamSummary = streams.map((s) => ({
              protocol_name: s.protocol_name,
              formats: (s.format ?? []).map((f) => ({
                format_name: f.format_name,
                codecs: (f.codec ?? []).map((c) => ({
                  codec_name: c.codec_name,
                  current_qn: c.current_qn,
                  accept_qn: c.accept_qn,
                  base_url_len: (c.base_url ?? '').length,
                })),
              })),
            }));
          }
        }
      }
    } else {
      entry.bodyHead = body.slice(0, 400);
    }
    summary[name] = entry;
    console.log(`=== ${name} ===`);
    console.log(JSON.stringify(entry, null, 2));
  } catch (error) {
    summary[name] = { error: String(error) };
    console.log(`=== ${name} === ERROR ${String(error)}`);
  }
}

writeFileSync(new URL('./raw/_summary.json', OUT), JSON.stringify(summary, null, 2), 'utf8');
