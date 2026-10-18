// Renders video.html frame-by-frame with headless Chromium and encodes segments with ffmpeg.
// usage: node render.js <outdir> [workers]
const { chromium } = require('/opt/node22/lib/node_modules/playwright');
const { spawn } = require('child_process');
const path = require('path');
const out = process.argv[2], WORKERS = +(process.argv[3] || 4);
(async () => {
  const b = await chromium.launch();
  const probe = await b.newPage();
  await probe.goto('file://' + path.join(__dirname, 'video.html'));
  const DUR = await probe.evaluate(() => window.DURATION), FPS = await probe.evaluate(() => window.FPS);
  await probe.close();
  const total = Math.round(DUR * FPS), per = Math.ceil(total / WORKERS);
  const t0 = Date.now(); let done = 0;
  await Promise.all(Array.from({ length: WORKERS }, async (_, w) => {
    const pg = await b.newPage({ viewport: { width: 1920, height: 1080 } });
    await pg.goto('file://' + path.join(__dirname, 'video.html'));
    await pg.evaluate(() => window.ready);
    const ff = spawn('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String(FPS), '-c:v', 'png', '-i', '-',
      '-c:v', 'libx264', '-preset', 'slow', '-crf', '17', '-pix_fmt', 'yuv420p', '-threads', '1', path.join(out, `seg${w}.mp4`)], { stdio: ['pipe', 'inherit', 'inherit'] });
    const closed = new Promise(r => ff.on('close', r));
    for (let f = w * per; f < Math.min(total, (w + 1) * per); f++) {
      await pg.evaluate(t => window.renderFrame(t), f / FPS);
      const buf = await pg.screenshot({ type: 'png' });
      if (!ff.stdin.write(buf)) await new Promise(r => ff.stdin.once('drain', r));
      if (++done % 300 === 0) console.log(`${done}/${total} frames, ${((Date.now() - t0) / 1000).toFixed(0)}s`);
    }
    ff.stdin.end(); await closed;
  }));
  await b.close();
  console.log('frames done', total);
})();
