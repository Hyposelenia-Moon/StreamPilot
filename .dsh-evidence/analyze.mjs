import { readFileSync, appendFileSync, writeFileSync } from 'node:fs';

const dir = '.dsh-evidence/raw/';
const log = '.dsh-evidence/summary.txt';
writeFileSync(log, '', 'utf8');
const out = (line) => appendFileSync(log, `${line}\n`, 'utf8');

const readJson = (name) => JSON.parse(readFileSync(dir + name, 'utf8'));

const info = readJson('info-by-room.txt');
out(`[getInfoByRoom] code=${info.code} message=${info.message} hasData=${Object.hasOwn(info, 'data')}`);

const base = readJson('room-base-info.txt');
out(`[getRoomBaseInfo] code=${base.code} message=${base.message}`);
out(`  data keys = ${Object.keys(base.data).join(', ')}`);
out(`  by_room_ids keys = ${JSON.stringify(Object.keys(base.data.by_room_ids))}`);
for (const [key, room] of Object.entries(base.data.by_room_ids)) {
  out(
    `  key=${key} room_id=${room.room_id} short_id=${room.short_id} uid=${room.uid} live_status=${room.live_status}` +
      ` uname=${room.uname} title=${room.title} area_name=${room.area_name} live_time=${room.live_time}`
  );
}
out(`  by_room_ids 是对象(Object)? ${base.data.by_room_ids !== null && typeof base.data.by_room_ids === 'object' && !Array.isArray(base.data.by_room_ids)}`);

const play = readJson('play-info.txt');
out(`[getRoomPlayInfo] code=${play.code} message=${play.message} hasPlayurlInfo=${play.data.playurl_info !== null}`);
out(`  data.room_id=${play.data.room_id} data.short_id=${play.data.short_id} data.live_status=${play.data.live_status} data.uid=${play.data.uid}`);
const p = play.data.playurl_info.playurl;
out(`  playurl.local keys = ${Object.keys(p).join(', ')}（无顶层 current_qn / accept_qn）`);
out(`  g_qn_desc 条数 = ${p.g_qn_desc.length}`);
for (const item of p.g_qn_desc) {
  out(`    qn=${item.qn} desc=${item.desc} hdr_desc="${item.hdr_desc}" attr_desc=${item.attr_desc} hdr_type=${item.hdr_type} media_base_desc=${JSON.stringify(item.media_base_desc)}`);
}

let candidateCount = 0;
for (const stream of p.stream) {
  for (const format of stream.format) {
    for (const codec of format.codec) {
      out(
        `  stream=${stream.protocol_name} format=${format.format_name} codec=${codec.codec_name}` +
          ` current_qn=${codec.current_qn} accept_qn=${JSON.stringify(codec.accept_qn)}` +
          ` base_url=${codec.base_url} url_info=${codec.url_info.length}`
      );
      candidateCount += codec.url_info.length;
    }
  }
}
out(`  代码 collectCandidates 口径下的候选总数 = ${candidateCount}`);

const page = readFileSync(dir + 'room-page.txt', 'utf8');
for (const marker of ['"defaultRoomId":"', '"room_id":', '"roomid":', '"roomId":']) {
  const index = page.indexOf(marker);
  out(`[room-page] marker ${marker} index=${index}` + (index >= 0 ? ` 紧随内容=${JSON.stringify(page.slice(index + marker.length, index + marker.length + 20))}` : ''));
}
out(`[room-page] 含 g_qn_desc? ${page.includes('g_qn_desc')}  含 playurl_info? ${page.includes('playurl_info')}  字节数=${Buffer.byteLength(page)}`);
