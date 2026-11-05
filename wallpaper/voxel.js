// 小型 WebGL 方块渲染器：小 Claude、猫咪、小屋共用一套（不依赖任何库）
// 坐标单位是「一格」；每个物体由若干长方体拼成，带方向光、天地环境光、倒角暗线、玩具高光，
// 地上的影子是一个半透明椭圆。整体光照可以随昼夜调；显卡重置（上下文丢失）后能自己恢复。
;(function () {
  'use strict'

  const VS = `
    attribute vec3 aPos; attribute vec3 aNrm;
    uniform mat4 uMVP; uniform mat4 uModel;
    varying vec3 vN; varying vec3 vL;
    void main() { vL = aPos; vN = (uModel * vec4(aNrm, 0.0)).xyz; gl_Position = uMVP * vec4(aPos, 1.0); }`
  const FS = `
    precision mediump float;
    uniform vec3 uColor; uniform vec3 uSize; uniform float uGlow; uniform vec3 uLight;
    varying vec3 vN; varying vec3 vL;
    void main() {
      vec3 n = normalize(vN);
      vec3 L = normalize(vec3(-0.45, 0.85, 0.6));
      vec3 V = normalize(vec3(0.0, 0.13, 1.0));
      float diff = max(dot(n, L), 0.0);
      vec3 amb = mix(vec3(0.26, 0.2, 0.17), vec3(0.72, 0.69, 0.66), 0.5 + 0.5 * n.y);
      // 塑料玩具的一点高光 + 背后的暖色轮廓光
      float spec = pow(max(dot(n, normalize(L + V)), 0.0), 40.0) * 0.3;
      float rim = pow(1.0 - max(dot(n, V), 0.0), 3.0);
      vec3 a = abs(vL);
      vec3 d = (0.5 - a) * uSize;
      float e = (a.x > a.y && a.x > a.z) ? min(d.y, d.z) : (a.y > a.z ? min(d.x, d.z) : min(d.x, d.y));
      float bevel = mix(0.62, 1.0, smoothstep(0.0, 0.16, e));
      // 每个方块越往下越暗一点，有体积感
      float ao = mix(0.8, 1.0, clamp(vL.y + 0.5, 0.0, 1.0));
      vec3 col = uColor * (amb + diff * vec3(1.0, 0.93, 0.86) * 0.9) * bevel * ao;
      col += vec3(1.0, 0.96, 0.9) * spec * bevel + vec3(1.0, 0.72, 0.5) * rim * 0.2;
      // uLight：随昼夜变化的整体光照；发光的部分（屏幕、窗户、灯）不受影响
      gl_FragColor = vec4(mix(col * uLight, uColor * 1.15, uGlow), 1.0);
    }`
  const SVS = `attribute vec3 aPos; uniform mat4 uMVP; varying vec2 vP; void main() { vP = aPos.xz * 2.0; gl_Position = uMVP * vec4(aPos, 1.0); }`
  const SFS = `precision mediump float; uniform float uAlpha; varying vec2 vP; void main() { float r = length(vP); gl_FragColor = vec4(0.0, 0.0, 0.0, uAlpha * (1.0 - smoothstep(0.35, 1.0, r))); }`

  // ---- 4×4 矩阵（列主序） ----
  const I = () => { const m = new Float32Array(16); m[0] = m[5] = m[10] = m[15] = 1; return m }
  const mul = (a, b) => {
    const o = new Float32Array(16)
    for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) {
      let s = 0
      for (let k = 0; k < 4; k++) s += a[k * 4 + r] * b[c * 4 + k]
      o[c * 4 + r] = s
    }
    return o
  }
  const T = (x, y, z) => { const m = I(); m[12] = x; m[13] = y; m[14] = z; return m }
  const S = (x, y, z) => { const m = I(); m[0] = x; m[5] = y; m[10] = z; return m }
  const RX = a => { const m = I(), c = Math.cos(a), s = Math.sin(a); m[5] = c; m[6] = s; m[9] = -s; m[10] = c; return m }
  const RY = a => { const m = I(), c = Math.cos(a), s = Math.sin(a); m[0] = c; m[2] = -s; m[8] = s; m[10] = c; return m }
  const RZ = a => { const m = I(), c = Math.cos(a), s = Math.sin(a); m[0] = c; m[1] = s; m[4] = -s; m[5] = c; return m }
  const chain = (...ms) => ms.reduce(mul)
  function persp(fovy, asp, n, f) {
    const t = 1 / Math.tan(fovy / 2), m = new Float32Array(16)
    m[0] = t / asp; m[5] = t; m[10] = (f + n) / (n - f); m[11] = -1; m[14] = (2 * f * n) / (n - f)
    return m
  }

  // 单位立方体（每个面两块三角形，带法线）
  const CUBE = (() => {
    const verts = []
    const faces = [
      [[1, 0, 0], [[0.5, -0.5, -0.5], [0.5, 0.5, -0.5], [0.5, 0.5, 0.5], [0.5, -0.5, 0.5]]],
      [[-1, 0, 0], [[-0.5, -0.5, 0.5], [-0.5, 0.5, 0.5], [-0.5, 0.5, -0.5], [-0.5, -0.5, -0.5]]],
      [[0, 1, 0], [[-0.5, 0.5, -0.5], [-0.5, 0.5, 0.5], [0.5, 0.5, 0.5], [0.5, 0.5, -0.5]]],
      [[0, -1, 0], [[-0.5, -0.5, 0.5], [-0.5, -0.5, -0.5], [0.5, -0.5, -0.5], [0.5, -0.5, 0.5]]],
      [[0, 0, 1], [[-0.5, -0.5, 0.5], [0.5, -0.5, 0.5], [0.5, 0.5, 0.5], [-0.5, 0.5, 0.5]]],
      [[0, 0, -1], [[0.5, -0.5, -0.5], [-0.5, -0.5, -0.5], [-0.5, 0.5, -0.5], [0.5, 0.5, -0.5]]],
    ]
    for (const [n, q] of faces) for (const i of [0, 1, 2, 0, 2, 3]) verts.push(...q[i], ...n)
    return new Float32Array(verts)
  })()
  const QUAD = new Float32Array([-0.5, 0, -0.5, 0.5, 0, -0.5, 0.5, 0, 0.5, -0.5, 0, -0.5, 0.5, 0, 0.5, -0.5, 0, 0.5])

  // camera: { fov, pitch, x, y, z, tall }：相机在 (x, y, z)，往下俯 pitch 弧度，看向 -z。
  // tall > 1：画布比原来高出这么多倍时，底边和比例不变，只是往上多看一截
  function create(canvas, camera) {
    let gl = null
    try {
      gl = canvas.getContext('webgl', { alpha: true, antialias: true, premultipliedAlpha: true, powerPreference: 'low-power' })
    } catch (err) {}
    if (!gl) return null

    let prog, sprog, loc, cubeBuf, quadBuf
    let lost = false
    let restoreHandlers = []

    function program(vs, fs) {
      const p = gl.createProgram()
      for (const [type, src] of [[gl.VERTEX_SHADER, vs], [gl.FRAGMENT_SHADER, fs]]) {
        const sh = gl.createShader(type)
        gl.shaderSource(sh, src)
        gl.compileShader(sh)
        gl.attachShader(p, sh)
      }
      gl.linkProgram(p)
      return p
    }
    // 着色器、顶点缓冲：第一次创建和显卡重置后都要建一遍
    function setup() {
      prog = program(VS, FS)
      sprog = program(SVS, SFS)
      loc = {
        aPos: gl.getAttribLocation(prog, 'aPos'), aNrm: gl.getAttribLocation(prog, 'aNrm'),
        uMVP: gl.getUniformLocation(prog, 'uMVP'), uModel: gl.getUniformLocation(prog, 'uModel'),
        uColor: gl.getUniformLocation(prog, 'uColor'), uSize: gl.getUniformLocation(prog, 'uSize'), uGlow: gl.getUniformLocation(prog, 'uGlow'),
        uLight: gl.getUniformLocation(prog, 'uLight'),
        sPos: gl.getAttribLocation(sprog, 'aPos'), sMVP: gl.getUniformLocation(sprog, 'uMVP'), sAlpha: gl.getUniformLocation(sprog, 'uAlpha'),
      }
      cubeBuf = gl.createBuffer()
      gl.bindBuffer(gl.ARRAY_BUFFER, cubeBuf)
      gl.bufferData(gl.ARRAY_BUFFER, CUBE, gl.STATIC_DRAW)
      quadBuf = gl.createBuffer()
      gl.bindBuffer(gl.ARRAY_BUFFER, quadBuf)
      gl.bufferData(gl.ARRAY_BUFFER, QUAD, gl.STATIC_DRAW)
    }
    setup()

    // 显卡重置：先停画，恢复后重建资源、通知调用方重画
    canvas.addEventListener('webglcontextlost', ev => {
      ev.preventDefault()
      lost = true
    })
    canvas.addEventListener('webglcontextrestored', () => {
      setup()
      lost = false
      resize()
      for (const fn of restoreHandlers) fn()
    })

    let vp = I()
    let light = [1, 1, 1]
    function resize() {
      // 画布被隐藏时（宽高为 0）不改尺寸，免得缩成 1×1 再放大变糊
      if (!canvas.clientWidth || !canvas.clientHeight) return
      const dpr = Math.min(window.devicePixelRatio || 1, 1.5)
      const w = Math.round(canvas.clientWidth * dpr)
      const h = Math.round(canvas.clientHeight * dpr)
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w
        canvas.height = h
      }
      const k = camera.tall || 1
      const P = persp(2 * Math.atan(Math.tan(camera.fov / 2) * k), w / h, 1, 300)
      P[9] = 1 - 1 / k
      vp = mul(P, chain(RX(camera.pitch), T(-camera.x, -camera.y, -camera.z)))
    }

    // begin 返回 false 表示这一帧画不了（上下文丢了），调用方直接跳过
    function begin() {
      if (lost || gl.isContextLost()) return false
      gl.viewport(0, 0, canvas.width, canvas.height)
      gl.clearColor(0, 0, 0, 0)
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT)
      return true
    }

    // 地上的椭圆影子：model 决定位置和大小
    function shadow(model, alpha) {
      gl.useProgram(sprog)
      gl.enable(gl.BLEND)
      gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA)
      gl.disable(gl.DEPTH_TEST)
      gl.bindBuffer(gl.ARRAY_BUFFER, quadBuf)
      gl.enableVertexAttribArray(loc.sPos)
      gl.vertexAttribPointer(loc.sPos, 3, gl.FLOAT, false, 12, 0)
      gl.uniformMatrix4fv(loc.sMVP, false, mul(vp, model))
      gl.uniform1f(loc.sAlpha, alpha)
      gl.drawArrays(gl.TRIANGLES, 0, 6)
      gl.disableVertexAttribArray(loc.sPos)
    }

    // 开始画方块（切换到方块着色器）
    function boxes() {
      gl.useProgram(prog)
      gl.disable(gl.BLEND)
      gl.enable(gl.DEPTH_TEST)
      gl.bindBuffer(gl.ARRAY_BUFFER, cubeBuf)
      gl.enableVertexAttribArray(loc.aPos)
      gl.enableVertexAttribArray(loc.aNrm)
      gl.vertexAttribPointer(loc.aPos, 3, gl.FLOAT, false, 24, 0)
      gl.vertexAttribPointer(loc.aNrm, 3, gl.FLOAT, false, 24, 12)
      gl.uniform3f(loc.uLight, light[0], light[1], light[2])
    }

    // 在 parent 坐标系里，以 (cx, cy, cz) 为中心画一个 sx×sy×sz 的方块；glow 让它发光（窗户、屏幕）
    function box(parent, cx, cy, cz, sx, sy, sz, color, glow) {
      const model = chain(parent, T(cx, cy, cz), S(sx, sy, sz))
      gl.uniformMatrix4fv(loc.uMVP, false, mul(vp, model))
      gl.uniformMatrix4fv(loc.uModel, false, model)
      gl.uniform3f(loc.uColor, color[0], color[1], color[2])
      gl.uniform3f(loc.uSize, sx, sy, sz)
      gl.uniform1f(loc.uGlow, glow || 0)
      gl.drawArrays(gl.TRIANGLES, 0, 36)
    }

    // 世界坐标 → 画布上的相对位置（0~1，左上角为原点）
    function project(x, y, z) {
      const v = [0, 0, 0, 0]
      for (let r = 0; r < 4; r++) v[r] = vp[r] * x + vp[4 + r] * y + vp[8 + r] * z + vp[12 + r]
      return [(v[0] / v[3] + 1) / 2, (1 - v[1] / v[3]) / 2]
    }

    // 把刚画好的这一帧像素拷到一个 2D 画布上（直接读绘图缓冲，不依赖浏览器何时把画面交出去）
    function copyTo(ctx) {
      const w = canvas.width
      const h = canvas.height
      const px = new Uint8Array(w * h * 4)
      gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, px)
      const img = ctx.createImageData(w, h)
      const out = img.data
      const row = w * 4
      for (let y = 0; y < h; y++) {
        const src = (h - 1 - y) * row
        const dst = y * row
        for (let i = 0; i < row; i += 4) {
          const a = px[src + i + 3]
          if (!a) continue
          // 读出来的是预乘过透明度的颜色，putImageData 要的是没预乘的
          const k = a === 255 ? 1 : 255 / a
          out[dst + i] = px[src + i] * k
          out[dst + i + 1] = px[src + i + 1] * k
          out[dst + i + 2] = px[src + i + 2] * k
          out[dst + i + 3] = a
        }
      }
      ctx.putImageData(img, 0, 0)
    }

    // 整体光照颜色（r, g, b 各 0~1）
    function setLight(rgb) { light = rgb }
    function onRestore(fn) { restoreHandlers.push(fn) }

    return { resize, begin, shadow, boxes, box, project, setLight, onRestore, copyTo }
  }

  window.Voxel = { create, M: { I, mul, T, S, RX, RY, RZ, chain } }
})()
