# Ambient pad soundtrack for the MSBuild explainer (deterministic).
import numpy as np, wave, sys
SR, DUR = 48000, float(sys.argv[2]) if len(sys.argv) > 2 else 170.0
n = int(SR * DUR); t = np.arange(n) / SR
L = np.zeros(n); R = np.zeros(n)
def hz(m): return 440 * 2 ** ((m - 69) / 12)
# Fmaj9 - Am7 - Dm9 - Bbmaj7(#11) progression, slow
chords = [[41, 53, 57, 60, 64, 67], [45, 52, 57, 60, 64, 67], [38, 50, 53, 57, 60, 64], [46, 53, 57, 62, 64, 69]]
seg = 8.5
i = 0; start = 0.0
while start < DUR:
    ch = chords[i % 4]
    a, b = int(start * SR), min(n, int((start + seg + 3.0) * SR))
    tt = t[a:b] - start
    env = np.minimum(1, tt / 2.5) * np.clip((seg + 3.0 - tt) / 3.0, 0, 1)
    for k, m in enumerate(ch):
        f = hz(m); amp = (0.10 if k == 0 else 0.045)
        for det, side in ((-0.7, L), (0.7, R)):
            w = np.sin(2 * np.pi * (f + det) * tt + k) + 0.18 * np.sin(2 * np.pi * 2 * (f + det) * tt)
            lfo = 0.8 + 0.2 * np.sin(2 * np.pi * 0.07 * tt + k)
            side[a:b] += amp * env * lfo * w
    i += 1; start += seg
# soft plucks
rng = np.random.default_rng(3)
scale = [65, 67, 69, 72, 74, 76, 79, 81]
tp = 7.0
while tp < DUR - 4:
    m = scale[rng.integers(len(scale))]; f = hz(m)
    a = int(tp * SR); b = min(n, a + int(3.0 * SR)); tt = t[a:b] - tp
    w = np.sin(2 * np.pi * f * tt) * np.exp(-tt * 2.2) * 0.035
    pan = rng.uniform(0.25, 0.75)
    L[a:b] += w * (1 - pan); R[a:b] += w * pan
    tp += rng.choice([1.0625, 2.125, 2.125, 3.1875])
# simple echo
d = int(0.375 * SR)
for side in (L, R):
    e = np.zeros(n); e[d:] = side[:-d] * 0.3; side += e
fade = np.minimum(1, t / 3.0) * np.clip((DUR - t) / 4.0, 0, 1)
L *= fade; R *= fade
peak = max(np.abs(L).max(), np.abs(R).max()); L, R = L / peak * 0.42, R / peak * 0.42
out = (np.stack([L, R], 1) * 32767).astype(np.int16)
with wave.open(sys.argv[1], 'wb') as wf:
    wf.setnchannels(2); wf.setsampwidth(2); wf.setframerate(SR); wf.writeframes(out.tobytes())
