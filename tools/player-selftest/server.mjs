// SoulLink H.264 播放器自测服务
// 用法: node server.mjs <port> <h264文件> <仓库根> <evidence输出路径>
// 提供:
//   /            → tools/player-selftest/index.html
//   /src         → docs/web-index.html 原文（自测页运行时抽取播放器函数）
//   /sample      → 完整 h264 文件（自测页做 NAL 预扫描）
//   /stream      → 分块模拟流（~700B/块、3ms 间隔、一次性）
//   POST /evidence → 自测页回传证据 JSON
import http from 'node:http';
import { readFileSync, writeFileSync } from 'node:fs';
import { extname, join, resolve } from 'node:path';

const PORT = parseInt(process.argv[2] || '8614', 10);
const H264 = resolve(process.argv[3] || '');
const ROOT = resolve(process.argv[4] || '.');
const EVIDENCE = process.argv[5] ? resolve(process.argv[5]) : null;

const page = readFileSync(join(ROOT, 'tools/player-selftest/index.html'), 'utf8');
const src = readFileSync(join(ROOT, 'docs/web-index.html'), 'utf8');
const stream = readFileSync(H264);
let streamServed = false;   // /stream 一次性：防页面重连逻辑在自测里循环

const server = http.createServer((req, res) => {
  if (req.method === 'GET' && (req.url === '/' || req.url === '/index.html')) {
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    return res.end(page);
  }
  if (req.method === 'GET' && req.url === '/src') {
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    return res.end(src);
  }
  if (req.method === 'GET' && (req.url === '/sample' || req.url.startsWith('/sample?'))) {
    res.writeHead(200, {
      'content-type': 'application/octet-stream',
      'content-length': stream.length
    });
    return res.end(stream);
  }
  if (req.method === 'GET' && (req.url === '/stream' || req.url.startsWith('/stream?'))) {
    if (streamServed) { res.writeHead(404); return res.end('stream already served'); }
    streamServed = true;
    res.writeHead(200, {
      'content-type': 'video/h264',
      'transfer-encoding': 'chunked',
      'cache-control': 'no-store'
    });
    const CHUNK = 700, DELAY = 3;
    let pos = 0, chunks = 0, bytes = 0;
    const push = () => {
      if (res.destroyed) return;
      if (pos >= stream.length) { res.end(); console.log(`[stream] done: ${chunks} chunks, ${bytes} bytes`); return; }
      const slice = stream.subarray(pos, Math.min(pos + CHUNK, stream.length));
      pos += slice.length; chunks++; bytes += slice.length;
      res.write(slice);
      setTimeout(push, DELAY);
    };
    push();
    return;
  }
  if (req.method === 'GET' && (req.url === '/hold' || req.url.startsWith('/hold?'))) {
    // 挡 load 事件的长请求：evidence 文件出现（或 30s 超时）才响应。
    // headless --screenshot/--dump-dom 在 load 完成时触发，
    // 挡住它 = 截图时测试必然已结束（真实时间等待，不被虚拟时间骗过）。
    const t0 = Date.now();
    const poll = () => {
      let done = false;
      if (EVIDENCE) { try { done = readFileSync(EVIDENCE, 'utf8').includes('"pass"'); } catch (e) {} }
      if (done || Date.now() - t0 > 30000 || res.destroyed) {
        res.writeHead(204); return res.end();
      }
      setTimeout(poll, 250);
    };
    poll();
    return;
  }
  if (req.method === 'POST' && req.url === '/evidence') {
    let body = '';
    req.on('data', d => body += d);
    req.on('end', () => {
      if (EVIDENCE) {
        try { writeFileSync(EVIDENCE, body); console.log('[evidence] saved to ' + EVIDENCE); }
        catch (e) { console.error('[evidence] write failed', e.message); }
      }
      console.log('[evidence] ' + body.slice(0, 2000));
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end('{"ok":true}');
    });
    return;
  }
  res.writeHead(404);
  res.end('not found');
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`selftest server: http://127.0.0.1:${PORT}/`);
  console.log(`  h264: ${H264} (${stream.length} bytes)`);
  console.log(`  root: ${ROOT}`);
  if (EVIDENCE) console.log(`  evidence → ${EVIDENCE}`);
});
