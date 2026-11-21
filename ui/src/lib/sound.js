// Sons do RAGE! sintetizados com WebAudio (sem arquivos): ronco do motor, turbo e estouros do escape.
const KEY = "rage.sound";
export function isMuted() { try { return localStorage.getItem(KEY) === "off"; } catch { return false; } }
export function setMuted(m) { try { localStorage.setItem(KEY, m ? "off" : "on"); } catch { /* sem storage */ } }

let ctx = null;
function audio() {
  if (!ctx) { const AC = window.AudioContext || window.webkitAudioContext; if (!AC) return null; ctx = new AC(); }
  if (ctx.state === "suspended") ctx.resume();
  return ctx;
}

function noiseBuffer(c, seconds) {
  const len = Math.floor(c.sampleRate * seconds);
  const buf = c.createBuffer(1, len, c.sampleRate);
  const d = buf.getChannelData(0);
  for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;
  return buf;
}

function distortion(c, amount = 18) {
  const ws = c.createWaveShaper();
  const n = 1024, curve = new Float32Array(n);
  for (let i = 0; i < n; i++) { const x = (i / n) * 2 - 1; curve[i] = ((1 + amount) * x) / (1 + amount * Math.abs(x)); }
  ws.curve = curve;
  ws.oversample = "4x";
  return ws;
}

/** Motor: rpm (Hz da combustão) ao longo do tempo, com pulso de cilindros, distorção e filtro que abre com a rotação. */
function engine(c, out, t0, points, dur) {
  const fund = c.createOscillator(); fund.type = "sawtooth";
  const sub = c.createOscillator(); sub.type = "square";
  const pulse = c.createOscillator(); pulse.type = "square";
  const pulseGain = c.createGain(); pulseGain.gain.value = 0.55;
  const am = c.createGain(); am.gain.value = 0.5;
  const shaper = distortion(c, 22);
  const lp = c.createBiquadFilter(); lp.type = "lowpass"; lp.Q.value = 3.5;
  const g = c.createGain(); g.gain.value = 0;

  for (const [t, hz] of points) {
    const at = t0 + t;
    if (t === 0) { fund.frequency.setValueAtTime(hz, at); sub.frequency.setValueAtTime(hz / 2, at); pulse.frequency.setValueAtTime(hz / 2, at); lp.frequency.setValueAtTime(hz * 5, at); }
    else {
      fund.frequency.exponentialRampToValueAtTime(hz, at); sub.frequency.exponentialRampToValueAtTime(hz / 2, at);
      pulse.frequency.exponentialRampToValueAtTime(hz / 2, at); lp.frequency.exponentialRampToValueAtTime(Math.min(9000, hz * 7), at);
    }
  }
  pulse.connect(pulseGain); pulseGain.connect(am.gain);
  fund.connect(am); sub.connect(am); am.connect(shaper); shaper.connect(lp); lp.connect(g); g.connect(out);
  g.gain.setValueAtTime(0.0001, t0);
  g.gain.exponentialRampToValueAtTime(0.34, t0 + 0.06);
  g.gain.setValueAtTime(0.34, t0 + dur - 0.25);
  g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
  for (const o of [fund, sub, pulse]) { o.start(t0); o.stop(t0 + dur + 0.05); }
}

function whoosh(c, out, at, dur, from, to, level, q = 1.2) {
  const src = c.createBufferSource(); src.buffer = noiseBuffer(c, dur);
  const bp = c.createBiquadFilter(); bp.type = "bandpass"; bp.Q.value = q;
  bp.frequency.setValueAtTime(from, at); bp.frequency.exponentialRampToValueAtTime(to, at + dur);
  const g = c.createGain(); g.gain.setValueAtTime(0.0001, at);
  g.gain.exponentialRampToValueAtTime(level, at + 0.03); g.gain.exponentialRampToValueAtTime(0.0001, at + dur);
  src.connect(bp); bp.connect(g); g.connect(out); src.start(at); src.stop(at + dur + 0.02);
}

function pop(c, out, at, level = 0.9) {
  const src = c.createBufferSource(); src.buffer = noiseBuffer(c, 0.09);
  const lp = c.createBiquadFilter(); lp.type = "lowpass"; lp.frequency.value = 900;
  const g = c.createGain(); g.gain.setValueAtTime(level, at); g.gain.exponentialRampToValueAtTime(0.0001, at + 0.08);
  src.connect(lp); lp.connect(g); g.connect(out); src.start(at); src.stop(at + 0.1);
}

function whistle(c, out, at, dur, from, to, level) {
  const o = c.createOscillator(); o.type = "sine";
  o.frequency.setValueAtTime(from, at); o.frequency.exponentialRampToValueAtTime(to, at + dur);
  const g = c.createGain(); g.gain.setValueAtTime(0.0001, at); g.gain.exponentialRampToValueAtTime(level, at + dur * 0.7); g.gain.exponentialRampToValueAtTime(0.0001, at + dur);
  o.connect(g); g.connect(out); o.start(at); o.stop(at + dur + 0.02);
}

function master(c) {
  const comp = c.createDynamicsCompressor();
  comp.threshold.value = -14; comp.ratio.value = 4;
  const g = c.createGain(); g.gain.value = 0.9;
  comp.connect(g); g.connect(c.destination);
  return comp;
}

/** Envenenar: marcha lenta → acelerada até o corte → estouro da válvula de alívio → pipocos. ~2,2 s */
export function rageRev() {
  if (isMuted()) return;
  const c = audio(); if (!c) return;
  const out = master(c); const t = c.currentTime + 0.02;
  engine(c, out, t, [[0, 38], [0.22, 44], [0.32, 60], [0.42, 46], [1.15, 205], [1.3, 214], [1.45, 120], [2.1, 52]], 2.2);
  whistle(c, out, t + 0.45, 0.95, 1400, 5200, 0.05);
  whoosh(c, out, t + 1.34, 0.55, 4200, 700, 0.45, 0.9);  // psssh da válvula
  pop(c, out, t + 1.42, 0.8); pop(c, out, t + 1.55, 0.6); pop(c, out, t + 1.63, 0.9); pop(c, out, t + 1.8, 0.5);
}

/** Desligar: motor caindo para a marcha lenta. ~1,2 s */
export function coolDown() {
  if (isMuted()) return;
  const c = audio(); if (!c) return;
  const out = master(c); const t = c.currentTime + 0.02;
  engine(c, out, t, [[0, 120], [0.5, 60], [1.2, 34]], 1.25);
  pop(c, out, t + 0.18, 0.5);
}

/** Clique curto de troca de marcha ao escolher um estágio. */
export function shiftClick() {
  if (isMuted()) return;
  const c = audio(); if (!c) return;
  const out = master(c); const t = c.currentTime + 0.01;
  pop(c, out, t, 0.25);
  whoosh(c, out, t, 0.12, 2600, 1200, 0.12, 2);
}
