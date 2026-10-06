// prowl_web.js: runs a Prowl2D player built with `python3 build.py player GAME --web` in a page.
//
//   <canvas id="screen"></canvas>  +  startProwl({ wasm: "game.wasm", canvas, log })
//
// The module is a WASI "reactor" (no main loop of its own): this loads it, gives it a small WASI (stdout goes to `log`; no file system) and the
// "gfx" imports of Native/Gfx2D/gfx2d_web.c, which draw with WebGL2 right here, then calls its exports prowl_init() once and prowl_frame() once
// per 1/60 s of game time (a fixed step, as the native player's frames are, whatever the display's refresh rate is).
// `?manual=1` in the page's URL stops the clock: window.prowl.step(n) then advances n frames (what the tests use).

const VERT = `#version 300 es
precision highp float;
precision highp int;
layout(location = 0) in vec2 a_pos;
layout(location = 1) in vec2 a_half;
layout(location = 2) in uint a_sprite;
layout(location = 3) in int a_rot;
layout(location = 4) in vec4 a_color;
uniform vec4 u_view;            // left, bottom, right, top
uniform vec4 u_rects[8];        // u0 v0 u1 v1 of each sprite, 0..1
out vec4 v_color;
out vec2 v_uv;
void main() {
    vec2 c = vec2(float(gl_VertexID & 1), float((gl_VertexID >> 1) & 1));
    vec2 local = (c * 2.0 - 1.0) * a_half;
    float ang = float(a_rot) * (6.28318530718 / 65536.0);
    float cs = cos(ang), sn = sin(ang);
    vec2 w = a_pos + vec2(cs * local.x - sn * local.y, sn * local.x + cs * local.y);
    gl_Position = vec4(2.0 * (w - u_view.xy) / (u_view.zw - u_view.xy) - 1.0, 0.0, 1.0);
    v_color = a_color;
    vec4 r = u_rects[a_sprite & 7u];
    v_uv = mix(r.xy, r.zw, c);
}`;
const FRAG = `#version 300 es
precision mediump float;
in vec4 v_color;
in vec2 v_uv;
uniform sampler2D u_atlas;
layout(location = 0) out vec4 frag;
void main() {
    vec4 t = texture(u_atlas, v_uv);
    frag = vec4(t.rgb * v_color.rgb, t.a * v_color.a);
}`;

function makeGfx(canvas, memory, log) {
  let gl = null, prog, vbo, vao, atlas, uView, uRects, w = 0, h = 0, rects = null;
  const mem8 = () => new Uint8Array(memory().buffer);
  function shader(type, src) {
    const s = gl.createShader(type);
    gl.shaderSource(s, src); gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
    return s;
  }
  return {
    gfx_web_init(width, height, atlasPtr, side, rectsPtr, sprites) {
      try {
        w = width; h = height; canvas.width = w; canvas.height = h;
        gl = canvas.getContext("webgl2", { alpha: false, antialias: false, preserveDrawingBuffer: true });
        if (!gl) return 0;
        prog = gl.createProgram();
        gl.attachShader(prog, shader(gl.VERTEX_SHADER, VERT));
        gl.attachShader(prog, shader(gl.FRAGMENT_SHADER, FRAG));
        gl.linkProgram(prog);
        if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
        uView = gl.getUniformLocation(prog, "u_view");
        uRects = gl.getUniformLocation(prog, "u_rects");
        atlas = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, atlas);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, side, side, 0, gl.RGBA, gl.UNSIGNED_BYTE, mem8().slice(atlasPtr, atlasPtr + side * side * 4));
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        const r16 = new Uint16Array(memory().buffer.slice(rectsPtr, rectsPtr + sprites * 8));
        rects = new Float32Array(32);
        for (let i = 0; i < sprites * 4; i++) rects[i] = r16[i] / 65535;
        vao = gl.createVertexArray(); gl.bindVertexArray(vao);
        vbo = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
        gl.bufferData(gl.ARRAY_BUFFER, 8192 * 24, gl.DYNAMIC_DRAW);
        // EngineGpuSprite, 24 bytes: float x,y | half hw,hh | u16 sprite | i16 rot | u8 r,g,b,a | u8 layer,flags,effect,arg
        gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 24, 0);
        gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 2, gl.HALF_FLOAT, false, 24, 8);
        gl.enableVertexAttribArray(2); gl.vertexAttribIPointer(2, 1, gl.UNSIGNED_SHORT, 24, 12);
        gl.enableVertexAttribArray(3); gl.vertexAttribIPointer(3, 1, gl.SHORT, 24, 14);
        gl.enableVertexAttribArray(4); gl.vertexAttribPointer(4, 4, gl.UNSIGNED_BYTE, true, 24, 16);
        for (let i = 0; i < 5; i++) gl.vertexAttribDivisor(i, 1);
        return 1;
      } catch (e) { log("gfx_web_init: " + e.message); return 0; }
    },
    gfx_web_draw(ptr, n, l, b, r, t, cr, cg, cb) {
      gl.viewport(0, 0, w, h);
      gl.clearColor(cr, cg, cb, 1); gl.clear(gl.COLOR_BUFFER_BIT);
      if (n <= 0) return;
      gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      gl.useProgram(prog);
      gl.uniform4f(uView, l, b, r, t);
      gl.uniform4fv(uRects, rects);
      gl.bindTexture(gl.TEXTURE_2D, atlas);
      gl.bindVertexArray(vao);
      gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
      gl.bufferSubData(gl.ARRAY_BUFFER, 0, new Uint8Array(memory().buffer, ptr, n * 24));
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, n);
    },
    gfx_web_read(ptr) {
      gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(memory().buffer, ptr, w * h * 4));
    },
    gfx_web_shutdown() { gl = null; },
  };
}

function makeWasi(memory, log, exit) {
  const dec = new TextDecoder();
  let line = "";
  const ENOSYS = 52, EBADF = 8;
  const dv = () => new DataView(memory().buffer);
  const f = {
    fd_write(fd, iovs, n, nwritten) {
      let total = 0;
      for (let i = 0; i < n; i++) {
        const p = dv().getUint32(iovs + i * 8, true), len = dv().getUint32(iovs + i * 8 + 4, true);
        if (fd === 1 || fd === 2) {
          line += dec.decode(new Uint8Array(memory().buffer, p, len), { stream: true });
          let k;
          while ((k = line.indexOf("\n")) >= 0) { log(line.slice(0, k)); line = line.slice(k + 1); }
        }
        total += len;
      }
      dv().setUint32(nwritten, total, true);
      return 0;
    },
    fd_read: () => EBADF, fd_close: () => 0, fd_seek: () => 70,
    // stdout/stderr say they are terminals (a character device that cannot seek), so libc line-buffers them and each line reaches the log when it is printed
    fd_fdstat_get(fd, buf) {
      if (fd > 2) return EBADF;
      const d = dv();
      d.setUint8(buf, 2); d.setUint8(buf + 1, 0); d.setUint16(buf + 2, 0, true);
      d.setBigUint64(buf + 8, 1n << 6n, true); d.setBigUint64(buf + 16, 0n, true);
      return 0;
    },
    fd_prestat_get: () => EBADF, fd_prestat_dir_name: () => EBADF, path_open: () => 44,
    environ_sizes_get(c, s) { dv().setUint32(c, 0, true); dv().setUint32(s, 0, true); return 0; }, environ_get: () => 0,
    args_sizes_get(c, s) { dv().setUint32(c, 0, true); dv().setUint32(s, 0, true); return 0; }, args_get: () => 0,
    clock_time_get(id, prec, out) { dv().setBigUint64(out, BigInt(Math.round(performance.now() * 1e6)), true); return 0; },
    random_get(p, n) { crypto.getRandomValues(new Uint8Array(memory().buffer, p, n)); return 0; },
    proc_exit(code) { exit(code); },
  };
  return new Proxy(f, { get: (t, k) => (k in t ? t[k] : () => ENOSYS) });
}

export async function startProwl({ wasm, canvas, log = console.log, manual = false }) {
  let mem = null;
  const memory = () => mem;
  const imports = {
    wasi_snapshot_preview1: makeWasi(memory, log, (c) => { throw new Error("exit " + c); }),
    gfx: makeGfx(canvas, memory, log),
  };
  const { instance } = await WebAssembly.instantiateStreaming(fetch(wasm), imports);
  const x = instance.exports;
  mem = x.memory;
  if (x._initialize) x._initialize();
  const state = { frame: 0, exports: x, error: null };
  const rc = x.prowl_init();
  if (rc !== 0) { state.error = "prowl_init returned " + rc; log(state.error); return state; }
  state.step = (n = 1) => { for (let i = 0; i < n; i++) { x.prowl_frame(); state.frame++; } return state.frame; };
  if (!manual) {
    let last = performance.now(), acc = 0;
    const STEP = 1000 / 60;
    const tick = (now) => {
      acc += Math.min(now - last, 250); last = now;
      let k = 0;
      while (acc >= STEP && k < 5) { state.step(1); acc -= STEP; k++; }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }
  return state;
}
