import { useEffect, useRef } from 'react';

/*
 * Siri 光团（WebGL）。
 * color：1 = 彩色光团（播放中 / 启动动画）：几团彩光沿漩涡游走、叠加发光，中心白热，边缘柔和起伏，
 *        不做玻璃边、高光点这类“实体球”质感；0 = 透明白色球体（未播放）；中间值平滑过渡。
 * energy / energyRef：0..1 声音能量，越大旋转越快、越亮、边缘起伏越大、光晕越开。
 * 时间取墙钟（Date.now），这样启动动画窗口和面板窗口里的光团花纹完全同相，交接时不会跳。
 * 托盘图标用 Rust 按同一套公式在 CPU 上绘制（src-tauri/src/orb.rs），两边保持一致。
 */

const VERT = `
attribute vec2 aPos;
void main() { gl_Position = vec4(aPos, 0.0, 1.0); }
`;

const FRAG = `
precision highp float;
uniform vec2  uRes;
uniform float uTime;
uniform float uColor;
uniform float uEnergy;
uniform float uRadius;

float g(float d, float w) { return exp(-(d * d) / (w * w)); }
float g2(vec2 d, float s) { return exp(-dot(d, d) / (s * s)); }

void main() {
  vec2 p = (gl_FragCoord.xy - 0.5 * uRes) / (0.5 * min(uRes.x, uRes.y));
  float r = length(p);
  float ang = atan(p.y, p.x);
  float R = uRadius;
  float aa = 2.0 / min(uRes.x, uRes.y);
  float e = clamp(uEnergy, 0.0, 1.0);
  float t = uTime;
  float k = clamp(uColor, 0.0, 1.0);
  /* 画布边缘前淡出，避免光晕被方形裁切 */
  float edgeFade = 1.0 - smoothstep(0.86, 0.995, r);

  /* ---------- 彩色光团 ---------- */
  /* 外形：软边圆，边缘随时间和能量起伏（像在说话） */
  float wob = 0.038 * sin(3.0 * ang + t * 1.3) + 0.018 * sin(5.0 * ang - t * 2.1) + 0.024 * sin(2.0 * ang - t * 0.8 + 1.0);
  float Rc = R * (0.90 + 0.10 * e) * (1.0 + wob * (0.45 + 1.4 * e));
  float x = r / Rc;
  /* 漩涡坐标：越靠近中心转得越多 */
  float tw = (1.0 - smoothstep(0.0, 1.1, x)) * (1.3 + 1.7 * e);
  float sa = ang + tw + t * (0.22 + 0.85 * e);
  vec2 q = vec2(cos(sa), sin(sa)) * x;
  float sp = t * (0.45 + 0.9 * e);

  vec3 c = vec3(0.32, 0.18, 0.70) * 0.30;
  c += vec3(1.00, 0.18, 0.56) * g2(q - 0.50 * vec2(cos(sp), sin(sp * 1.3)), 0.50);
  c += vec3(0.10, 0.52, 1.00) * g2(q - 0.52 * vec2(cos(sp * 0.8 + 2.1), sin(sp * 1.1 + 2.1)), 0.50);
  c += vec3(0.56, 0.26, 1.00) * g2(q - 0.46 * vec2(cos(sp * 1.2 + 4.2), sin(sp * 0.9 + 4.2)), 0.48);
  c += vec3(0.16, 0.95, 0.80) * g2(q - 0.42 * vec2(cos(sp * 1.5 + 1.0), sin(sp * 1.4 + 3.0)), 0.38) * 0.80;
  c += vec3(1.00, 0.55, 0.30) * g2(q - 0.40 * vec2(cos(sp * 0.7 + 5.3), sin(sp * 1.6 + 0.6)), 0.34) * 0.55;

  float body = 1.0 - smoothstep(0.62, 1.10, x);
  float core = g(x, 0.30 + 0.08 * e);
  vec3 tint = mix(vec3(0.62, 0.42, 1.0), vec3(1.0, 0.36, 0.66), 0.5 + 0.5 * sin(t * 0.6));
  vec3 cc = c * body * (0.95 + 0.75 * e) + vec3(1.0) * core * (0.30 + 0.40 * e);
  cc += tint * exp(-max(x - 0.80, 0.0) * mix(4.2, 2.6, e)) * (1.0 - body * 0.6) * (0.28 + 0.30 * e);
  cc = (vec3(1.0) - exp(-cc * 1.15)) * edgeFade;
  /* 当作发光：不透明度取最亮通道，输出预乘色 */
  float ca = max(max(cc.r, cc.g), cc.b);

  /* ---------- 透明白色球体 ---------- */
  vec2 u = p / R;
  float ur = length(u);
  float inS = 1.0 - smoothstep(1.0 - aa / R, 1.0 + aa / R, ur);
  float z = sqrt(max(1.0 - ur * ur, 0.0));
  float fres = pow(1.0 - z, 2.2);
  float hl = exp(-((u.x + 0.32) * (u.x + 0.32) / 0.09 + (u.y - 0.45) * (u.y - 0.45) / 0.035));
  float lum = dot(c, vec3(0.299, 0.587, 0.114));
  vec3 pearl = mix(vec3(lum), c, 0.45);
  float wa = clamp(0.10 + 0.52 * fres + 0.30 * hl + 0.06 * lum, 0.0, 1.0) * inS;
  vec3 wcol = min(vec3(0.97, 0.98, 1.0) + pearl * 0.18, vec3(1.0));
  float wh = exp(-max(ur - 1.0, 0.0) * R * 11.0) * 0.14 * (1.0 - inS) * edgeFade;
  vec3 wpm = wcol * wa + vec3(1.0) * wh;
  float wA = min(wa + wh, 1.0);

  gl_FragColor = vec4(mix(wpm, cc, k), mix(wA, ca, k));
}
`;

function compile(gl, type, src) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src);
  gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
    const log = gl.getShaderInfoLog(s);
    gl.deleteShader(s);
    throw new Error(log || 'shader compile failed');
  }
  return s;
}

/** 墙钟秒数（一小时取模，避免 float 精度下降） */
export function orbClock() {
  return (Date.now() / 1000) % 3600;
}

export default function Orb({
  size, color = 1, energy = 0, energyRef = null, colorRef = null, radius = 0.74, still = false, stillTime = 1.7,
  className = '', style = undefined,
}) {
  const canvasRef = useRef(null);
  const live = useRef({ color, energy, targetColor: color, targetEnergy: energy });

  useEffect(() => { live.current.targetColor = color; }, [color]);
  useEffect(() => { live.current.targetEnergy = energy; }, [energy]);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return undefined;
    const dpr = Math.min((window.devicePixelRatio || 1) * (window.__HPS_ZOOM__ || 1), 4);
    canvas.width = Math.round(size * dpr);
    canvas.height = Math.round(size * dpr);
    const gl = canvas.getContext('webgl', { alpha: true, premultipliedAlpha: true, antialias: true });
    if (!gl) return undefined;

    let prog;
    try {
      prog = gl.createProgram();
      gl.attachShader(prog, compile(gl, gl.VERTEX_SHADER, VERT));
      gl.attachShader(prog, compile(gl, gl.FRAGMENT_SHADER, FRAG));
      gl.linkProgram(prog);
      if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog) || 'link failed');
    } catch (err) {
      console.error('[orb]', err);
      return undefined;
    }
    gl.useProgram(prog);
    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(prog, 'aPos');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    const U = (n) => gl.getUniformLocation(prog, n);
    const uRes = U('uRes'), uTime = U('uTime'), uColor = U('uColor'), uEnergy = U('uEnergy'), uRadius = U('uRadius');
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.clearColor(0, 0, 0, 0);

    let raf = 0;
    const draw = () => {
      const s = live.current;
      if (energyRef) s.targetEnergy = Math.max(0, Math.min(1, energyRef.current || 0));
      // colorRef 由调用方逐帧精确控制（启动动画用），否则按目标值平滑过渡
      if (colorRef) s.color = s.targetColor = Math.max(0, Math.min(1, colorRef.current));
      else s.color += (s.targetColor - s.color) * (still ? 1 : 0.055);
      s.energy += (s.targetEnergy - s.energy) * (still ? 1 : 0.18);
      gl.clear(gl.COLOR_BUFFER_BIT);
      gl.uniform2f(uRes, canvas.width, canvas.height);
      gl.uniform1f(uTime, still ? stillTime : orbClock());
      gl.uniform1f(uColor, s.color);
      gl.uniform1f(uEnergy, s.energy);
      gl.uniform1f(uRadius, radius);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      if (!still) raf = requestAnimationFrame(draw);
    };
    draw();
    return () => {
      cancelAnimationFrame(raf);
      gl.deleteBuffer(buf);
      gl.deleteProgram(prog);
    };
  }, [size, radius, still, stillTime, energyRef, colorRef]);

  return <canvas ref={canvasRef} className={className} style={{ width: size, height: size, ...style }} />;
}
