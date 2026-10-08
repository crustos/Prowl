// gfx_fx_web_test.mjs -- drives the effects test page (tools/gfx_fx_web_scene.c) in headless Chromium and writes the pictures it makes.
//
//   node tools/gfx_fx_web_test.mjs --dir PAGE_DIR --gfx webgl2|webgpu --cases CASES.json --out PICTURES.bin
//
// CASES.json: { sprites: [floats], mesh: [floats], late: [floats], cases: [ { e1, p1: [128 floats], e2, p2, clip: [x, y, w, h] | null, late: n } ] }.
// For each case the page is given the scene and the case, stepped one frame and read back exactly (state.readPixels, even on WebGPU). PICTURES.bin is each
// picture as RGBA, top row first, one after another. Prints one JSON line (the API that drew, what the page logged); exit 0 if all went well, 3 if
// playwright is missing, 4 if software WebGPU has no display (run it under xvfb-run).
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { createRequire } from "node:module";
import { execSync } from "node:child_process";

function arg(name, dflt) {
  const i = process.argv.indexOf("--" + name);
  return i < 0 ? dflt : process.argv[i + 1];
}
const dir = arg("dir"), gfx = arg("gfx", "webgl2"), casesFile = arg("cases"), out = arg("out");
if (!dir || !casesFile || !out) { console.error("usage: node gfx_fx_web_test.mjs --dir PAGE_DIR --gfx webgl2|webgpu --cases CASES.json --out PICTURES.bin"); process.exit(2); }

function loadPlaywright() {
  const roots = [process.env.PLAYWRIGHT_CORE_DIR];
  try { roots.push(execSync("npm root -g", { encoding: "utf8" }).trim()); } catch {}
  roots.push("/opt/npm-tools/node_modules", "/opt/node-tools/node_modules");
  for (const r of roots.filter(Boolean)) for (const pkg of ["playwright-core", "playwright"]) { try { return createRequire(path.join(r, "x.js"))(pkg); } catch {} }
  console.error("gfx_fx_web_test: playwright not found (npm i -g playwright-core, or set PLAYWRIGHT_CORE_DIR)");
  process.exit(3);
}
const { chromium } = loadPlaywright();
const MIME = { ".html": "text/html", ".js": "text/javascript", ".wasm": "application/wasm" };
const server = http.createServer((req, res) => {
  const file = path.join(dir, decodeURIComponent(new URL(req.url, "http://x").pathname));
  if (!file.startsWith(path.resolve(dir)) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.statusCode = 404; res.end("not found"); return; }
  res.setHeader("content-type", MIME[path.extname(file)] || "application/octet-stream");
  res.end(fs.readFileSync(file));
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));

// software rendering, as in tools/web_test.mjs: WebGL2 on SwiftShader; WebGPU on the system's lavapipe, which needs a headed browser (xvfb-run)
const args = ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"];
let headed = false;
if (gfx === "webgpu") {
  args.push("--enable-unsafe-webgpu", "--enable-features=Vulkan,WebGPU");
  if (!process.env.DISPLAY) { console.log(JSON.stringify({ ok: false, error: "software WebGPU needs a display: run under xvfb-run -a" })); process.exit(4); }
  headed = true;
  if (!process.env.VK_ICD_FILENAMES) {
    const icd = ["lvp_icd.json", "lvp_icd.x86_64.json"].map((f) => "/usr/share/vulkan/icd.d/" + f).find((f) => fs.existsSync(f));
    if (icd) process.env.VK_ICD_FILENAMES = icd;
  }
}
const launch = { headless: !headed, args };
if (process.env.CHROMIUM_PATH) launch.executablePath = process.env.CHROMIUM_PATH;
const browser = await chromium.launch(launch);
let result = { ok: false };
const logs = [];
try {
  const page = await browser.newPage();
  page.on("console", (m) => logs.push(m.text()));
  page.on("pageerror", (e) => logs.push("pageerror: " + e.message));
  await page.goto(`http://localhost:${server.address().port}/index.html?gfx=${gfx}&manual=1`);
  await page.waitForFunction(() => window.prowlReady === true || (window.prowl && window.prowl.error), null, { timeout: 60000 });
  const job = JSON.parse(fs.readFileSync(casesFile, "utf8"));
  const chunks = [];
  let backend = null;
  for (let i = 0; i < job.cases.length; i++) {
    const info = await page.evaluate(async ({ job, c }) => {
      const s = window.prowl;
      if (s.error) return { error: s.error };
      const x = s.exports, mem = () => x.memory.buffer;
      const put = (ptr, a) => new Float32Array(mem(), ptr, a.length).set(a);
      put(x.fxt_sprites(), job.sprites); put(x.fxt_mesh(), job.mesh); put(x.fxt_late(), job.late);
      put(x.fxt_p1(), c.p1 || []); put(x.fxt_p2(), c.p2 || []);
      const cfg = new Int32Array(mem(), x.fxt_cfg(), 16), clip = c.clip || [0, 0, -1, 0];
      cfg.set([job.sprites.length / 12, job.mesh.length / 8, c.e1 || 0, c.e2 || 0, clip[0], clip[1], clip[2], clip[3], c.late || 0]);
      s.step(1);
      const px = await s.readPixels();
      let bin = "";
      for (let k = 0; k < px.rgba.length; k += 0x8000) bin += String.fromCharCode.apply(null, px.rgba.subarray(k, k + 0x8000));
      return { backend: s.backend, w: px.w, h: px.h, b64: btoa(bin) };
    }, { job, c: job.cases[i] });
    if (info.error) throw new Error(info.error);
    backend = info.backend;
    const rgba = Buffer.from(info.b64, "base64"), top = Buffer.alloc(rgba.length);        // rows bottom to top -> top first
    for (let y = 0; y < info.h; y++) rgba.copy(top, y * info.w * 4, (info.h - 1 - y) * info.w * 4, (info.h - y) * info.w * 4);
    chunks.push(top);
  }
  fs.writeFileSync(out, Buffer.concat(chunks));
  result = { ok: true, backend, requested: gfx, logs: logs.filter((l) => !l.startsWith("effect ")) };
} catch (e) {
  result = { ok: false, error: String(e && e.message || e), logs };
} finally {
  await browser.close();
  server.close();
}
console.log(JSON.stringify(result));
process.exit(result.ok ? 0 : 1);
