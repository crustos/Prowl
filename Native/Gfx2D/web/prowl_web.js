// prowl_web.js: runs a Prowl2D player built with `python3 build.py player GAME --web` in a page.
//
//   <canvas id="screen"></canvas>  +  startProwl({ wasm: "game.wasm", canvas, log })
//
// The module is a WASI "reactor" (no main loop of its own): this loads it, gives it a small WASI (stdout goes to `log`; no file system) and the
// "gfx" imports of Native/Gfx2D/gfx2d_web.c, which draw right here, with WebGPU when the browser has it and WebGL2 otherwise (`gfx` option, or `?gfx=webgpu` /
// `?gfx=webgl2` in the page's URL; the default, "auto", prefers WebGPU), then calls its exports prowl_init() once and prowl_frame() once
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

// The UI layer's meshes (gfx_triangles): x, y, u, v, r, g, b, a per vertex, a texture, the sprite batch's view.
const MESH_VERT = `#version 300 es
precision highp float;
layout(location = 0) in vec2 a_pos;
layout(location = 1) in vec2 a_uv;
layout(location = 2) in vec4 a_color;
uniform vec4 u_view;
out vec2 v_uv;
out vec4 v_color;
void main() {
  gl_Position = vec4(2.0 * (a_pos - u_view.xy) / (u_view.zw - u_view.xy) - 1.0, 0.0, 1.0);
  v_uv = a_uv;
  v_color = a_color;
}`;
const MESH_FRAG = `#version 300 es
precision highp float;
uniform sampler2D u_tex;
in vec2 v_uv;
in vec4 v_color;
layout(location = 0) out vec4 frag;
void main() { frag = texture(u_tex, v_uv) * v_color; }`;
const MESH_BYTES = 32, MAX_MESH_VERTICES = 65536;

function makeGfxGL(canvas, memory, log) {
  let gl = null, prog, vbo, vao, atlas, uView, uRects, w = 0, h = 0, rects = null;
  let mprog, mvao, mvbo, uMView, uMTex, view = [0, 0, 1, 1];
  const tex = [];
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
        // the UI layer's mesh program
        mprog = gl.createProgram();
        gl.attachShader(mprog, shader(gl.VERTEX_SHADER, MESH_VERT));
        gl.attachShader(mprog, shader(gl.FRAGMENT_SHADER, MESH_FRAG));
        gl.linkProgram(mprog);
        if (!gl.getProgramParameter(mprog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(mprog));
        uMView = gl.getUniformLocation(mprog, "u_view");
        uMTex = gl.getUniformLocation(mprog, "u_tex");
        mvao = gl.createVertexArray(); gl.bindVertexArray(mvao);
        mvbo = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, mvbo);
        gl.bufferData(gl.ARRAY_BUFFER, MAX_MESH_VERTICES * MESH_BYTES, gl.DYNAMIC_DRAW);
        gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 2, gl.FLOAT, false, MESH_BYTES, 0);
        gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 2, gl.FLOAT, false, MESH_BYTES, 8);
        gl.enableVertexAttribArray(2); gl.vertexAttribPointer(2, 4, gl.FLOAT, false, MESH_BYTES, 16);
        gl.bindVertexArray(null);
        return 1;
      } catch (e) { log("gfx_web_init: " + e.message); return 0; }
    },
    gfx_web_draw(ptr, n, l, b, r, t, cr, cg, cb) {
      view = [l, b, r, t];
      gl.disable(gl.SCISSOR_TEST);                       // a new frame has no clip, and the clear must not be clipped
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
    // ---- the UI layer: textures, meshes over the frame, the clip (gfx2d_ui.c has the rest) ----
    gfx_web_texture_create(id, tw, th, filter, ptr) {
      const t = gl.createTexture();
      const f = filter === 1 ? gl.LINEAR : gl.NEAREST;
      gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, t);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, tw, th, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(memory().buffer, ptr, tw * th * 4));
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, f);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, f);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      tex[id] = { name: t, w: tw, h: th };
    },
    gfx_web_texture_update(id, x, y, rw, rh, ptr) {
      const t = tex[id];
      if (!t) return;
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, t.name);
      gl.pixelStorei(gl.UNPACK_ROW_LENGTH, t.w);         // `ptr` is the whole image: read the rectangle out of it in place
      gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, x);
      gl.pixelStorei(gl.UNPACK_SKIP_ROWS, y);
      gl.texSubImage2D(gl.TEXTURE_2D, 0, x, y, rw, rh, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(memory().buffer, ptr, t.w * t.h * 4));
      gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
      gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, 0);
      gl.pixelStorei(gl.UNPACK_SKIP_ROWS, 0);
    },
    gfx_web_texture_free(id) { if (tex[id]) { gl.deleteTexture(tex[id].name); tex[id] = null; } },
    gfx_web_triangles(ptr, count, texture) {
      gl.viewport(0, 0, w, h);
      gl.enable(gl.BLEND); gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ZERO, gl.ONE);
      gl.useProgram(mprog);
      gl.uniform4f(uMView, view[0], view[1], view[2], view[3]);
      gl.uniform1i(uMTex, 0);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, (tex[texture] || tex[0]).name);
      gl.bindVertexArray(mvao);
      gl.bindBuffer(gl.ARRAY_BUFFER, mvbo);
      gl.bufferSubData(gl.ARRAY_BUFFER, 0, new Uint8Array(memory().buffer, ptr, count * MESH_BYTES));
      gl.drawArrays(gl.TRIANGLES, 0, count);
      gl.bindVertexArray(null);
    },
    gfx_web_clip(x0, y0, x1, y1) {
      if (x0 === 0 && y0 === 0 && x1 === w && y1 === h) { gl.disable(gl.SCISSOR_TEST); return; }
      gl.enable(gl.SCISSOR_TEST);
      gl.scissor(x0, h - y1, x1 - x0, y1 - y0);          // GL counts rows from the bottom
    },
    read() { const px = new Uint8Array(w * h * 4); gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, px); return { w, h, rgba: px }; },
  };
}
const glBackend = (canvas, memory, log) => {
  const g = makeGfxGL(canvas, memory, log);
  // the canvas as it was just drawn: RGBA, rows bottom to top (what both backends give)
  const read = async () => g.read();
  return { kind: "webgl2", imports: g, read, ready: async () => true };
};


// ---- WebGPU: the same sprite path (24-byte instances, four-vertex strip, SRC_ALPHA / ONE_MINUS_SRC_ALPHA) in WGSL. ----
const WGSL = `
struct U { view: vec4f, rects: array<vec4f, 8> };
@group(0) @binding(0) var<uniform> u: U;
@group(0) @binding(1) var atlas: texture_2d<f32>;
@group(0) @binding(2) var samp: sampler;
struct VIn {
  @builtin(vertex_index) vi: u32,
  @location(0) pos: vec2f,
  @location(1) hsz: vec2f,
  @location(2) sr: vec2u,       // sprite, rotation (an i16 in a u16)
  @location(3) color: vec4f,
};
struct VOut { @builtin(position) p: vec4f, @location(0) color: vec4f, @location(1) uv: vec2f };
@vertex fn vs(i: VIn) -> VOut {
  let c = vec2f(f32(i.vi & 1u), f32((i.vi >> 1u) & 1u));
  let local = (c * 2.0 - 1.0) * i.hsz;
  let rot = bitcast<i32>(i.sr.y << 16u) >> 16u;
  let ang = f32(rot) * (6.28318530718 / 65536.0);
  let cs = cos(ang); let sn = sin(ang);
  let w = i.pos + vec2f(cs * local.x - sn * local.y, sn * local.x + cs * local.y);
  var o: VOut;
  o.p = vec4f(2.0 * (w - u.view.xy) / (u.view.zw - u.view.xy) - 1.0, 0.0, 1.0);
  o.color = i.color;
  let r = u.rects[i.sr.x & 7u];
  o.uv = mix(r.xy, r.zw, c);
  return o;
}
@fragment fn fs(i: VOut) -> @location(0) vec4f {
  let t = textureSampleLevel(atlas, samp, i.uv, 0.0);
  return vec4f(t.rgb * i.color.rgb, t.a * i.color.a);
}

// the UI layer's meshes: x, y, u, v, r, g, b, a per vertex, the texture of the bind group (the sprite bind group's atlas is only one of them)
struct MIn { @location(0) pos: vec2f, @location(1) uv: vec2f, @location(2) color: vec4f };
struct MOut { @builtin(position) p: vec4f, @location(0) uv: vec2f, @location(1) color: vec4f };
@vertex fn vs_mesh(i: MIn) -> MOut {
  var o: MOut;
  o.p = vec4f(2.0 * (i.pos - u.view.xy) / (u.view.zw - u.view.xy) - 1.0, 0.0, 1.0);
  o.uv = i.uv;
  o.color = i.color;
  return o;
}
@fragment fn fs_mesh(i: MOut) -> @location(0) vec4f {
  return textureSampleLevel(atlas, samp, i.uv, 0.0) * i.color;
}`;

// `target` onto the canvas: a pass that loads each pixel (a texture-to-texture copy into the canvas's own texture is not something every implementation takes)
const BLIT = `
@group(0) @binding(0) var src: texture_2d<f32>;
@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4f {
  let p = vec2f(f32((i << 1u) & 2u), f32(i & 2u));
  return vec4f(p * 2.0 - 1.0, 0.0, 1.0);
}
@fragment fn fs(@builtin(position) p: vec4f) -> @location(0) vec4f { return textureLoad(src, vec2i(p.xy), 0); }`;

// Everything that can fail or must be awaited (adapter, device, pipeline, the canvas's context) happens here, before gfx_web_init, which is synchronous;
// null when this browser has no usable WebGPU (nothing has touched the canvas then, so WebGL2 can still take it).
async function makeGfxGPU(canvas, memory, log) {
  if (!globalThis.navigator || !navigator.gpu) return null;
  let adapter, device, ctx, pipeline, meshPipe, blitPipe, blitLayout, layout, sampler, samplers, spriteBuf, meshBuf, uniBuf;
  const FORMAT = "rgba8unorm";
  try {
    adapter = await navigator.gpu.requestAdapter();     // kept in this closure: if it is collected, Chrome tears the whole instance down
    if (!adapter) return null;
    device = await adapter.requestDevice();
    device.lost.then((i) => log("webgpu: device lost: " + i.message));
    device.addEventListener("uncapturederror", (e) => log("webgpu: " + e.error.message));
    const module = device.createShaderModule({ code: WGSL });
    const info = await module.getCompilationInfo();
    for (const m of info.messages) if (m.type === "error") throw new Error("WGSL " + m.lineNum + ": " + m.message);
    layout = device.createBindGroupLayout({ entries: [
      { binding: 0, visibility: GPUShaderStage.VERTEX, buffer: { type: "uniform" } },
      { binding: 1, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: "float" } },
      { binding: 2, visibility: GPUShaderStage.FRAGMENT, sampler: { type: "filtering" } },
    ] });
    pipeline = await device.createRenderPipelineAsync({
      layout: device.createPipelineLayout({ bindGroupLayouts: [layout] }),
      vertex: { module, entryPoint: "vs", buffers: [{
        arrayStride: 24, stepMode: "instance",
        // EngineGpuSprite, 24 bytes: float x,y | half hw,hh | u16 sprite | i16 rot | u8 r,g,b,a | u8 layer,flags,effect,arg
        attributes: [
          { shaderLocation: 0, offset: 0, format: "float32x2" },
          { shaderLocation: 1, offset: 8, format: "float16x2" },
          { shaderLocation: 2, offset: 12, format: "uint16x2" },
          { shaderLocation: 3, offset: 16, format: "unorm8x4" },
        ] }] },
      fragment: { module, entryPoint: "fs", targets: [{ format: FORMAT, blend: {
        color: { srcFactor: "src-alpha", dstFactor: "one-minus-src-alpha", operation: "add" },
        alpha: { srcFactor: "src-alpha", dstFactor: "one-minus-src-alpha", operation: "add" } } }] },
      primitive: { topology: "triangle-strip" },
    });
    meshPipe = await device.createRenderPipelineAsync({
      layout: device.createPipelineLayout({ bindGroupLayouts: [layout] }),
      vertex: { module, entryPoint: "vs_mesh", buffers: [{
        arrayStride: MESH_BYTES, stepMode: "vertex",
        attributes: [
          { shaderLocation: 0, offset: 0, format: "float32x2" },
          { shaderLocation: 1, offset: 8, format: "float32x2" },
          { shaderLocation: 2, offset: 16, format: "float32x4" },
        ] }] },
      fragment: { module, entryPoint: "fs_mesh", targets: [{ format: FORMAT, blend: {
        color: { srcFactor: "src-alpha", dstFactor: "one-minus-src-alpha", operation: "add" },
        alpha: { srcFactor: "zero", dstFactor: "one", operation: "add" } } }] },   // the picture's own alpha stays as the background made it, as on the desktop
      primitive: { topology: "triangle-list" },
    });
    const bmod = device.createShaderModule({ code: BLIT });
    blitLayout = device.createBindGroupLayout({ entries: [{ binding: 0, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: "float" } }] });
    blitPipe = await device.createRenderPipelineAsync({
      layout: device.createPipelineLayout({ bindGroupLayouts: [blitLayout] }),
      vertex: { module: bmod, entryPoint: "vs" }, fragment: { module: bmod, entryPoint: "fs", targets: [{ format: FORMAT }] },
      primitive: { topology: "triangle-list" } });
    sampler = device.createSampler({ magFilter: "nearest", minFilter: "nearest", addressModeU: "clamp-to-edge", addressModeV: "clamp-to-edge" });
    samplers = [0, 1].map((f) => device.createSampler({ magFilter: f ? "linear" : "nearest", minFilter: f ? "linear" : "nearest",
                                                         addressModeU: "clamp-to-edge", addressModeV: "clamp-to-edge" }));
    meshBuf = device.createBuffer({ size: MAX_MESH_VERTICES * MESH_BYTES, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
    spriteBuf = device.createBuffer({ size: 8192 * 24, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
    uniBuf = device.createBuffer({ size: 144, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    ctx = canvas.getContext("webgpu");
    if (!ctx) return null;
  } catch (e) { log("webgpu unavailable: " + e.message); return null; }

  let w = 0, h = 0, rects, bind, blitBind, target, bpr = 0, stage, cache = null, reading = false, wantRead = false;
  const tex = [];
  let clipRect = null, hidden = false;     // the UI layer's clip (pixels from the top left; an empty one hides what follows)
  const mem8 = () => new Uint8Array(memory().buffer);

  // `target` rows copied into `buf` (mapped by the caller), top to bottom -> an RGBA array, rows bottom to top
  const unpack = (mapped) => {
    const out = new Uint8Array(w * h * 4);
    for (let y = 0; y < h; y++) out.set(mapped.subarray(y * bpr, y * bpr + w * 4), (h - 1 - y) * w * 4);
    return out;
  };
  const copyOut = (enc, buf) => enc.copyTextureToBuffer({ texture: target }, { buffer: buf, bytesPerRow: bpr }, [w, h]);
  // `target` onto the canvas, a read-back of it if the game has asked for pixels, and the commands to the GPU
  const finish = (enc) => {
    const out = enc.beginRenderPass({ colorAttachments: [{ view: ctx.getCurrentTexture().createView(), loadOp: "clear", clearValue: { r: 0, g: 0, b: 0, a: 1 }, storeOp: "store" }] });
    out.setPipeline(blitPipe); out.setBindGroup(0, blitBind); out.draw(3); out.end();
    const grab = wantRead && !reading;
    if (grab) copyOut(enc, stage);
    device.queue.submit([enc.finish()]);
    if (grab) {
      reading = true;
      stage.mapAsync(GPUMapMode.READ).then(() => { cache = unpack(new Uint8Array(stage.getMappedRange())); stage.unmap(); reading = false; }, () => { reading = false; });
    }
  };

  const imports = {
    gfx_web_init(width, height, atlasPtr, side, rectsPtr, sprites) {
      try {
        w = width; h = height; canvas.width = w; canvas.height = h;
        ctx.configure({ device, format: FORMAT, usage: GPUTextureUsage.RENDER_ATTACHMENT, alphaMode: "opaque" });
        const atlas = device.createTexture({ size: [side, side], format: FORMAT, usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
        device.queue.writeTexture({ texture: atlas }, mem8().slice(atlasPtr, atlasPtr + side * side * 4), { bytesPerRow: side * 4 }, [side, side]);
        const r16 = new Uint16Array(memory().buffer.slice(rectsPtr, rectsPtr + sprites * 8));
        rects = new Float32Array(36);                      // view (4) + 8 rects of u0 v0 u1 v1
        for (let i = 0; i < sprites * 4; i++) rects[4 + i] = r16[i] / 65535;
        bind = device.createBindGroup({ layout, entries: [
          { binding: 0, resource: { buffer: uniBuf } }, { binding: 1, resource: atlas.createView() }, { binding: 2, resource: sampler } ] });
        // the picture is drawn into `target`, then blitted to the canvas: so it can be read back at any time (the canvas's own texture expires with the frame)
        target = device.createTexture({ size: [w, h], format: FORMAT, usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_SRC });
        blitBind = device.createBindGroup({ layout: blitLayout, entries: [{ binding: 0, resource: target.createView() }] });
        bpr = Math.ceil(w * 4 / 256) * 256;
        stage = device.createBuffer({ size: bpr * h, usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST });
        return 1;
      } catch (e) { log("gfx_web_init: " + e.message); return 0; }
    },
    gfx_web_draw(ptr, n, l, b, r, t, cr, cg, cb) {
      rects[0] = l; rects[1] = b; rects[2] = r; rects[3] = t;
      device.queue.writeBuffer(uniBuf, 0, rects);
      if (n > 0) device.queue.writeBuffer(spriteBuf, 0, memory().buffer, ptr, n * 24);
      const enc = device.createCommandEncoder();
      const pass = enc.beginRenderPass({ colorAttachments: [{ view: target.createView(), clearValue: { r: cr, g: cg, b: cb, a: 1 }, loadOp: "clear", storeOp: "store" }] });
      if (n > 0) { pass.setPipeline(pipeline); pass.setBindGroup(0, bind); pass.setVertexBuffer(0, spriteBuf); pass.draw(4, n); }
      pass.end();
      clipRect = null; hidden = false;                  // a new frame has no clip
      finish(enc);
    },
    gfx_web_texture_create(id, tw, th, filter, ptr) {
      const texture = device.createTexture({ size: [tw, th], format: FORMAT, usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
      device.queue.writeTexture({ texture }, new Uint8Array(memory().buffer, ptr, tw * th * 4), { bytesPerRow: tw * 4 }, [tw, th]);
      const tbind = device.createBindGroup({ layout, entries: [
        { binding: 0, resource: { buffer: uniBuf } }, { binding: 1, resource: texture.createView() }, { binding: 2, resource: samplers[filter === 1 ? 1 : 0] } ] });
      tex[id] = { texture, w: tw, h: th, bind: tbind };
    },
    gfx_web_texture_update(id, x, y, rw, rh, ptr) {
      const t = tex[id];
      if (!t) return;
      // `ptr` is the whole image: the layout's offset and row pitch pick the rectangle out of it
      device.queue.writeTexture({ texture: t.texture, origin: [x, y] }, new Uint8Array(memory().buffer, ptr, t.w * t.h * 4),
                                { offset: (y * t.w + x) * 4, bytesPerRow: t.w * 4 }, [rw, rh]);
    },
    gfx_web_texture_free(id) { if (tex[id]) { tex[id].texture.destroy(); tex[id] = null; } },
    // meshes go over the picture so far, one pass each (`loadOp: "load"`), and the canvas gets the result at once
    gfx_web_triangles(ptr, count, texture) {
      if (hidden) return;
      device.queue.writeBuffer(meshBuf, 0, memory().buffer, ptr, count * MESH_BYTES);
      const enc = device.createCommandEncoder();
      const pass = enc.beginRenderPass({ colorAttachments: [{ view: target.createView(), loadOp: "load", storeOp: "store" }] });
      if (clipRect) pass.setScissorRect(clipRect[0], clipRect[1], clipRect[2], clipRect[3]);
      pass.setPipeline(meshPipe); pass.setBindGroup(0, (tex[texture] || tex[0]).bind); pass.setVertexBuffer(0, meshBuf); pass.draw(count);
      pass.end();
      finish(enc);
    },
    gfx_web_clip(x0, y0, x1, y1) {
      hidden = x1 <= x0 || y1 <= y0;                     // WebGPU has no zero-size scissor rectangle: nothing is drawn instead
      clipRect = (x0 === 0 && y0 === 0 && x1 === w && y1 === h) || hidden ? null : [x0, y0, x1 - x0, y1 - y0];   // framebuffer rows count from the top, as the clip does
    },
    // A synchronous read cannot wait for the GPU. Once a game has asked for pixels (gfx_pixel, gfx_frame_hash) the backend reads each frame back in the
    // background, and this hands over the newest picture that has arrived: a frame or more behind, black until the first one comes. The exact picture
    // is `state.readPixels()`, which is asynchronous.
    gfx_web_read(ptr) {
      wantRead = true;
      const dst = new Uint8Array(memory().buffer, ptr, w * h * 4);
      if (cache) dst.set(cache); else dst.fill(0);
    },
    gfx_web_shutdown() { wantRead = false; cache = null; },
  };
  async function read() {
    const buf = device.createBuffer({ size: bpr * h, usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST });
    const enc = device.createCommandEncoder();
    copyOut(enc, buf);
    device.queue.submit([enc.finish()]);
    await buf.mapAsync(GPUMapMode.READ);
    const rgba = unpack(new Uint8Array(buf.getMappedRange()));
    buf.unmap(); buf.destroy();
    return { w, h, rgba };
  }
  return { kind: "webgpu", imports, read, adapter };
}

function makeWasi(memory, log, exit, files) {
  const dec = new TextDecoder();
  let line = "";
  const ENOSYS = 52, EBADF = 8, ENOENT = 44, EINVAL = 28;
  // A read-only file system of the files the page fetched (a hybrid player's managed assembly and corlib.dll): fd 3 is the preopened directory ".",
  // paths are looked up by name ("./corlib.dll" is "corlib.dll"), and an opened file is a position in its bytes.
  const open = new Map();
  let nextFd = 4;
  const norm = (p) => p.replace(/^(\.\/)+/, "").replace(/^\/+/, "");
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
    fd_read(fd, iovs, n, nread) {
      const of = open.get(fd);
      if (!of) return EBADF;
      let total = 0;
      for (let i = 0; i < n; i++) {
        const p = dv().getUint32(iovs + i * 8, true), len = dv().getUint32(iovs + i * 8 + 4, true);
        const k = Math.min(len, of.data.length - of.pos);
        new Uint8Array(memory().buffer, p, k).set(of.data.subarray(of.pos, of.pos + k));
        of.pos += k; total += k;
        if (k < len) break;
      }
      dv().setUint32(nread, total, true);
      return 0;
    },
    fd_close(fd) { open.delete(fd); return 0; },
    fd_seek(fd, off, whence, out) {
      const of = open.get(fd);
      if (!of) return 70;                                  // ESPIPE: the terminals cannot seek
      const o = Number(off), base = whence === 0 ? 0 : whence === 1 ? of.pos : of.data.length;
      if (base + o < 0) return EINVAL;
      of.pos = base + o;
      dv().setBigUint64(out, BigInt(of.pos), true);
      return 0;
    },
    fd_tell(fd, out) { const of = open.get(fd); if (!of) return EBADF; dv().setBigUint64(out, BigInt(of.pos), true); return 0; },
    fd_filestat_get(fd, buf) {
      const of = open.get(fd);
      if (!of) return EBADF;
      const d = dv();
      for (let i = 0; i < 64; i += 8) d.setBigUint64(buf + i, 0n, true);
      d.setUint8(buf + 16, 4);                             // a regular file
      d.setBigUint64(buf + 32, BigInt(of.data.length), true);
      return 0;
    },
    fd_prestat_get(fd, buf) {
      if (fd !== 3) return EBADF;
      dv().setUint8(buf, 0); dv().setUint32(buf + 4, 1, true);   // a directory, its name one byte long
      return 0;
    },
    fd_prestat_dir_name(fd, p, len) { if (fd !== 3) return EBADF; new Uint8Array(memory().buffer, p, 1)[0] = 46; return 0; },   // "."
    path_open(dirfd, dirflags, p, plen, oflags, rb, ri, fdflags, out) {
      if (dirfd !== 3) return EBADF;
      const name = norm(new TextDecoder().decode(new Uint8Array(memory().buffer, p, plen)));
      if (globalThis.PROWL_TRACE) console.log("path_open " + dirfd + " " + JSON.stringify(name));
      const data = files[name];
      if (!data || (oflags & 0xf) !== 0) return ENOENT;    // read-only: nothing is created or truncated
      const fd = nextFd++;
      open.set(fd, { data, pos: 0 });
      dv().setUint32(out, fd, true);
      return 0;
    },
    // stdout/stderr say they are terminals (a character device that cannot seek), so libc line-buffers them and each line reaches the log when it is printed
    fd_fdstat_get(fd, buf) {
      const d = dv();
      if (fd === 3 || open.has(fd)) {                       // the preopened directory, or a file of it: everything is allowed (it is read-only anyway)
        d.setUint8(buf, fd === 3 ? 3 : 4); d.setUint8(buf + 1, 0); d.setUint16(buf + 2, 0, true);
        d.setBigUint64(buf + 8, 0x1fffffffn, true); d.setBigUint64(buf + 16, 0x1fffffffn, true);
        return 0;
      }
      if (fd > 2) return EBADF;
      d.setUint8(buf, 2); d.setUint8(buf + 1, 0); d.setUint16(buf + 2, 0, true);
      d.setBigUint64(buf + 8, 1n << 6n, true); d.setBigUint64(buf + 16, 0n, true);
      return 0;
    },
    environ_sizes_get(c, s) { dv().setUint32(c, 0, true); dv().setUint32(s, 0, true); return 0; }, environ_get: () => 0,
    args_sizes_get(c, s) { dv().setUint32(c, 0, true); dv().setUint32(s, 0, true); return 0; }, args_get: () => 0,
    clock_time_get(id, prec, out) { dv().setBigUint64(out, BigInt(Math.round(performance.now() * 1e6)), true); return 0; },
    random_get(p, n) { crypto.getRandomValues(new Uint8Array(memory().buffer, p, n)); return 0; },
    proc_exit(code) { exit(code); },
  };
  return new Proxy(f, { get: (t, k) => {
    const fn = k in t ? t[k] : () => ENOSYS;
    if (!globalThis.PROWL_TRACE) return fn;
    return (...a) => { const r = fn(...a); console.log("wasi " + String(k) + "(" + a.map(String).join(",") + ") = " + r); return r; };
  } });
}

// ---- input -------------------------------------------------------------------------------------------------------------------------------------------
// The page's events as the five ints gfx2d.h describes ([type, a, b, c, d]; positions in canvas pixels, y from the top), queued here until the module asks
// for them with gfx_web_poll. Keys are named by what they are (event.code), not by the text they make, as in gfx2d.h; the text is its own event.
const KEYS = {
  Escape: 256, Enter: 257, NumpadEnter: 257, Tab: 258, Backspace: 259, Insert: 260, Delete: 261, ArrowRight: 262, ArrowLeft: 263, ArrowDown: 264, ArrowUp: 265,
  PageUp: 266, PageDown: 267, Home: 268, End: 269, ShiftLeft: 340, ControlLeft: 341, AltLeft: 342, MetaLeft: 343, ShiftRight: 344, ControlRight: 345,
  AltRight: 346, MetaRight: 347, Space: 32, Minus: 45, Equal: 61, Comma: 44, Period: 46, Slash: 47, Semicolon: 59, Quote: 39, Backquote: 96,
  BracketLeft: 91, BracketRight: 93, Backslash: 92,
};
function keyOf(e) {
  const c = e.code;
  if (KEYS[c] !== undefined) return KEYS[c];
  let m;
  if ((m = /^Key([A-Z])$/.exec(c))) return m[1].charCodeAt(0);
  if ((m = /^Digit([0-9])$/.exec(c))) return m[1].charCodeAt(0);
  if ((m = /^F([0-9]{1,2})$/.exec(c)) && +m[1] >= 1 && +m[1] <= 12) return 290 + (+m[1]) - 1;
  return 0;
}
function makeInput(canvas, memory) {
  const q = [];
  const mods = (e) => (e.shiftKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.altKey ? 4 : 0) | (e.metaKey ? 8 : 0);
  const pos = (e) => {
    const r = canvas.getBoundingClientRect();
    return [Math.floor((e.clientX - r.left) * canvas.width / r.width), Math.floor((e.clientY - r.top) * canvas.height / r.height)];
  };
  canvas.tabIndex = 0;                                       // so it can have the keyboard
  canvas.style.outline = "none";
  canvas.addEventListener("contextmenu", (e) => e.preventDefault());
  canvas.addEventListener("pointermove", (e) => { const [x, y] = pos(e); q.push([1, x, y, 0, mods(e)]); });
  canvas.addEventListener("pointerdown", (e) => {
    canvas.focus();
    try { canvas.setPointerCapture(e.pointerId); } catch {}
    const [x, y] = pos(e); q.push([2, x, y, e.button === 1 ? 1 : e.button === 2 ? 2 : 0, mods(e)]);
  });
  canvas.addEventListener("pointerup", (e) => { const [x, y] = pos(e); q.push([3, x, y, e.button === 1 ? 1 : e.button === 2 ? 2 : 0, mods(e)]); });
  canvas.addEventListener("wheel", (e) => {
    e.preventDefault();
    const k = e.deltaMode === 0 ? 1 / 100 : e.deltaMode === 1 ? 1 / 3 : 1;     // pixels, lines or pages -> notches (a notch is about 100 pixels, 3 lines)
    const [x, y] = pos(e);
    q.push([4, x, y, Math.round(e.deltaX * k * 120), Math.round(-e.deltaY * k * 120)]);
  }, { passive: false });
  canvas.addEventListener("keydown", (e) => {
    const k = keyOf(e);
    if (k) { q.push([5, k, e.repeat ? 1 : 0, 0, mods(e)]); e.preventDefault(); }
    const shortcut = e.metaKey || (e.ctrlKey && !e.altKey);
    if (!shortcut && !e.isComposing && e.key && [...e.key].length === 1) q.push([7, e.key.codePointAt(0), 0, 0, 0]);
  });
  canvas.addEventListener("keyup", (e) => { const k = keyOf(e); if (k) { q.push([6, k, 0, 0, mods(e)]); e.preventDefault(); } });
  canvas.addEventListener("focus", () => q.push([8, 1, 0, 0, 0]));
  canvas.addEventListener("blur", () => q.push([8, 0, 0, 0, 0]));
  return {
    queue: q,
    poll(ptr) {
      const e = q.shift();
      if (!e) return 0;
      new Int32Array(memory().buffer, ptr, 5).set(e);
      return 1;
    },
  };
}

export async function startProwl({ wasm, canvas, log = console.log, manual = false, files = [], gfx = "auto" }) {
  let mem = null;
  const fs = {};
  for (const name of files) {
    const r = await fetch(name);
    if (!r.ok) throw new Error("cannot fetch " + name);
    fs[name] = new Uint8Array(await r.arrayBuffer());
  }
  const memory = () => mem;
  let backend = null;
  if (gfx === "auto" || gfx === "webgpu") backend = await makeGfxGPU(canvas, memory, log);
  if (!backend && gfx === "webgpu") log("webgpu requested but not available here: using WebGL2");
  if (!backend) backend = glBackend(canvas, memory, log);
  const input = makeInput(canvas, memory);
  let inst = null;
  const jit = { compiled: 0, refused: 0 };
  const imports = {
    // DotNetAnywhere's compiler from CIL to wasm (a hybrid player): it hands over a small module for a hot method, which goes into the module's own function
    // table. A page may only compile modules this way on the main thread when Chrome calls them small (4 KB), so a larger one is refused (-1) and that method
    // stays interpreted: slower, never wrong.
    dna: {
      emit_wasm(ptr, len) {
        try {
          const bytes = new Uint8Array(inst.exports.memory.buffer, ptr, len).slice();
          const m = new WebAssembly.Instance(new WebAssembly.Module(bytes), { env: { memory: inst.exports.memory, table: inst.exports.__indirect_function_table } });
          const table = inst.exports.__indirect_function_table, index = table.length;
          table.grow(1);
          table.set(index, m.exports.f);
          jit.compiled++;
          return index;
        } catch (e) { jit.refused++; return -1; }
      },
    },
    wasi_snapshot_preview1: makeWasi(memory, log, (c) => { throw new Error("exit " + c); }, fs),
    gfx: { ...backend.imports, gfx_web_poll: (ptr) => input.poll(ptr) },
  };
  const { instance } = await WebAssembly.instantiateStreaming(fetch(wasm), imports);
  inst = instance;
  const x = instance.exports;
  mem = x.memory;
  if (x._initialize) x._initialize();
  const state = { frame: 0, exports: x, error: null, jit, backend: backend.kind, readPixels: backend.read, gfx: backend };   // gfx: keeps the WebGPU adapter reachable
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
