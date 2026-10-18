# Bouncy music-box soundtrack for the Buck2 explainer (deterministic).
# usage: python3 music.py out.wav [duration_seconds]
import numpy as np, wave, sys
SR, DUR = 48000, float(sys.argv[2]) if len(sys.argv) > 2 else 284.0
n = int(SR * DUR)
L = np.zeros(n); R = np.zeros(n)
def hz(m): return 440 * 2 ** ((m - 69) / 12)
BPM = 100; BEAT = 60 / BPM; E8 = BEAT / 2
rng = np.random.default_rng(7)

def add(t0, sig, pan=0.5, gain=1.0):
    a = int(t0 * SR)
    if a >= n: return
    b = min(n, a + len(sig)); s = sig[:b - a] * gain
    L[a:b] += s * (1 - pan); R[a:b] += s * pan

def marimba(f, length=0.9):
    tt = np.arange(int(length * SR)) / SR
    return (np.sin(2 * np.pi * f * tt) + 0.25 * np.sin(2 * np.pi * 4 * f * tt) * np.exp(-tt * 18)) * np.exp(-tt * 5.5) * np.minimum(1, tt / 0.004)

def glock(f, length=1.6):
    tt = np.arange(int(length * SR)) / SR
    return (np.sin(2 * np.pi * f * tt) + 0.4 * np.sin(2 * np.pi * 2.76 * f * tt) * np.exp(-tt * 6)) * np.exp(-tt * 2.6) * np.minimum(1, tt / 0.002)

def bass(f, length):
    tt = np.arange(int(length * SR)) / SR
    return (np.sin(2 * np.pi * f * tt) + 0.3 * np.sin(2 * np.pi * 2 * f * tt)) * np.exp(-tt * 2.2) * np.minimum(1, tt / 0.01)

def shaker(length=0.08):
    m = int(length * SR); tt = np.arange(m) / SR
    x = rng.standard_normal(m); x = np.diff(np.concatenate([[0], x]))  # crude high-pass
    return x * np.exp(-tt * 60)

# C - Am - F - G, two bars each
PROG = [(48, [60, 64, 67, 72, 76]), (45, [57, 60, 64, 69, 72]), (41, [53, 57, 60, 65, 69]), (43, [55, 59, 62, 67, 71])]
BAR = 4 * BEAT
PENT = [72, 74, 76, 79, 81, 84, 86, 88]
t = 0.0; bar = 0; idx = 3
while t < DUR:
    root, chord = PROG[(bar // 2) % 4]
    intro = t < 6.0
    # pad: soft sustained chord
    tt = np.arange(int((BAR + 1.0) * SR)) / SR
    pad = sum(np.sin(2 * np.pi * hz(m) * tt + k) for k, m in enumerate(chord[:3])) * np.minimum(1, tt / 0.8) * np.clip((BAR + 1.0 - tt) / 1.0, 0, 1)
    add(t, pad, 0.5, 0.018)
    # bass on beats 1 and 3
    if not intro:
        add(t, bass(hz(root), BEAT * 1.8), 0.5, 0.16)
        add(t + 2 * BEAT, bass(hz(root + (7 if bar % 2 else 12)), BEAT * 1.8), 0.5, 0.12)
    # arpeggio on 8ths (music box)
    for k in range(8):
        tk = t + k * E8
        if k % 2 == 0 or rng.random() < 0.7:
            m = chord[[0, 2, 1, 3, 2, 4, 3, 2][k]]
            add(tk, marimba(hz(m)), 0.3 + 0.4 * ((k % 4) / 3), 0.055)
    # melody: pentatonic random walk, some rests
    if not intro:
        for k in range(8):
            if rng.random() < (0.55 if k % 2 == 0 else 0.25):
                idx = int(np.clip(idx + rng.choice([-2, -1, -1, 0, 1, 1, 2]), 0, len(PENT) - 1))
                add(t + k * E8, marimba(hz(PENT[idx]), 1.2), 0.62, 0.075)
        # shaker on off-beats
        for k in range(8):
            if k % 2 == 1: add(t + k * E8, shaker(), 0.7, 0.025)
    # sparkle every 4 bars
    if bar % 4 == 3:
        for j, m in enumerate([84, 88, 91, 96]):
            add(t + 3 * BEAT + j * 0.09, glock(hz(m)), 0.3 + 0.15 * j, 0.03)
    t += BAR; bar += 1
# gentle echo
d = int(E8 * 1.5 * SR)
for side in (L, R):
    e = np.zeros(n); e[d:] = side[:-d] * 0.22; side += e
tt = np.arange(n) / SR
fade = np.minimum(1, tt / 2.5) * np.clip((DUR - tt) / 4.0, 0, 1)
L *= fade; R *= fade
peak = max(np.abs(L).max(), np.abs(R).max()); L, R = L / peak * 0.45, R / peak * 0.45
out = (np.stack([L, R], 1) * 32767).astype(np.int16)
with wave.open(sys.argv[1], 'wb') as wf:
    wf.setnchannels(2); wf.setsampwidth(2); wf.setframerate(SR); wf.writeframes(out.tobytes())
