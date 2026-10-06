#!/usr/bin/env node
// web_test.mjs -- runs a `build.py player GAME --web` page in headless Chromium and checks it.
//
//   node tools/web_test.mjs DIR [--frames N] [--ref frame_0000.ppm] [--shot out.png] [--tol T]
//
// Serves DIR over http, opens index.html?manual=1, steps N frames (default 240), and reports: the game's console lines, how many sprites were drawn, and,
// with --ref (a PPM the native player wrote with GFX.SaveFrame), how the canvas differs from it: the share of pixels that differ by more than T (default 8,
// of 255) in any channel. WebGL on a machine with no GPU is Chromium's SwiftShader, native here is Mesa's llvmpipe: two software rasterisers that round
// differently, so the edges of discs can differ by a few levels; the test is for the image being the same picture, not the same bits.
// Exit 0 when the page ran and (with --ref) at most 0.5% of the pixels differ by more than T.
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
let chromium;
for (const m of ["playwright", "/opt/npm-tools/node_modules/playwright", "playwright-core"]) {
  try { ({ chromium } = require(m)); break; } catch (_) { /* next */ }
}
if (!chromium) { console.error("web_test: playwright is not installed (npm i -g playwright; npx playwright install chromium)"); process.exit(2); }

const args = process.argv.slice(2);
const dir = path.resolve(args[0] || ".");
const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
const frames = parseInt(opt("--frames", "240"), 10), tol = parseInt(opt("--tol", "8"), 10);
const ref = opt("--ref", null), shot = opt("--shot", null);

const types = { ".html": "text/html", ".js": "text/javascript", ".wasm": "application/wasm" };
const server = http.createServer((req, res) => {
  const f = path.join(dir, decodeURIComponent(req.url.split("?")[0]).replace(/^\/+$/, "/index.html"));
  if (!f.startsWith(dir) || !fs.existsSync(f) || fs.statSync(f).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { "content-type": types[path.extname(f)] || "application/octet-stream" });
  fs.createReadStream(f).pipe(res);
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const url = `http://127.0.0.1:${server.address().port}/index.html?manual=1`;

const browser = await chromium.launch({ args: ["--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"] });
let code = 0;
try {
  const page = await browser.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(String(e)));
  page.on("console", (m) => { if (m.type() === "error") errors.push(m.text()); });
  await page.goto(url);
  await page.waitForFunction("window.prowlReady === true || window.prowlFailed", null, { timeout: 30000 }).catch(() => {});
  const ok = await page.evaluate("!!window.prowl && !window.prowl.error");
  if (!ok) { console.log("FAIL: the page did not start", errors.join("\n")); process.exit(1); }
  await page.evaluate((n) => window.prowl.step(n), frames);
  const out = await page.evaluate(() => {
    const c = document.getElementById("screen"), gl = c.getContext("webgl2");
    const w = c.width, h = c.height, px = new Uint8Array(w * h * 4);
    gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, px);
    return { w, h, px: Array.from(px), log: document.getElementById("log").textContent };
  });
  console.log(out.log.trimEnd());
  if (errors.length) { console.log("page errors:\n" + errors.join("\n")); code = 1; }
  const px = Uint8Array.from(out.px);
  if (shot) await page.locator("#screen").screenshot({ path: shot });
  if (ref) {
    const buf = fs.readFileSync(ref);
    let p = 0, tok = [];
    while (tok.length < 4) { while (buf[p] === 10 || buf[p] === 32) p++; let s = p; while (buf[p] !== 10 && buf[p] !== 32) p++; tok.push(buf.toString("latin1", s, p)); }
    p++;
    const [, rw, rh] = tok.map(Number);
    if (rw !== out.w || rh !== out.h) { console.log(`FAIL: size ${out.w}x${out.h} vs reference ${rw}x${rh}`); process.exit(1); }
    let bad = 0, maxd = 0, sum = 0;
    for (let y = 0; y < rh; y++) for (let x = 0; x < rw; x++) {
      const o = (y * rw + x) * 3, q = ((rh - 1 - y) * rw + x) * 4;      // the PPM runs top to bottom, GL bottom to top
      let d = 0;
      for (let k = 0; k < 3; k++) d = Math.max(d, Math.abs(buf[p + o + k] - px[q + k]));
      sum += d; if (d > maxd) maxd = d; if (d > tol) bad++;
    }
    const share = bad / (rw * rh);
    console.log(`compare with ${path.basename(ref)}: ${bad} of ${rw * rh} pixels differ by more than ${tol} (${(share * 100).toFixed(3)}%), largest difference ${maxd}, mean ${(sum / (rw * rh)).toFixed(4)}`);
    if (share > 0.005) { console.log("FAIL: the page's picture is not the native one"); code = 1; }
  }
  // and the page as a visitor sees it: no ?manual, so requestAnimationFrame drives the fixed 1/60 s steps
  const live = await browser.newPage();
  await live.goto(url.replace("?manual=1", ""));
  await live.waitForFunction("window.prowlReady === true", null, { timeout: 30000 });
  await live.waitForTimeout(1500);
  const n = await live.evaluate("window.prowl.frame");
  console.log(`live page: ${n} frames in 1.5 s of wall time`);
  if (n < 30 || n > 140) { console.log("FAIL: the animation clock is not stepping at about 60 frames a second"); code = 1; }
  console.log(code === 0 ? "ok" : "FAIL");
} finally {
  await browser.close();
  server.close();
}
process.exit(code);
