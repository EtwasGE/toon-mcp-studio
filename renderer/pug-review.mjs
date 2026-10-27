import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { bundle } from '@remotion/bundler';
import { openBrowser, selectComposition, renderStill } from '@remotion/renderer';

const here = path.dirname(fileURLToPath(import.meta.url));
const sourceDirectory = path.resolve(process.argv[2] ?? 'examples/pes-alkasha');
const directory = path.resolve(process.argv[3] ?? '.data/pug-review');
const rig = JSON.parse(await fs.readFile(path.join(sourceDirectory, 'preview-rig.json'), 'utf8'));
const props = { rig, assets: { 'pug_parts@1.0.0': 'parts-v1.png' } };
await fs.mkdir(path.join(directory, 'public'), { recursive: true });
await fs.copyFile(path.join(sourceDirectory, 'parts-v1.png'), path.join(directory, 'public/parts-v1.png'));
await fs.writeFile(path.join(directory, 'preview-props.json'), JSON.stringify(props, null, 4));
const serveUrl = await bundle({
    entryPoint: path.join(here, 'src/index.tsx'),
    publicDir: path.join(directory, 'public'),
    webpackOverride: (config) => ({
        ...config,
        module: {
            ...config.module,
            rules: [...(config.module?.rules ?? []), { test: /\.woff2$/, type: 'asset/resource' }]
        }
    })
});
const browser = await openBrowser('chrome');
try {
    const composition = await selectComposition({
        serveUrl,
        id: 'PugMotionReview',
        inputProps: props,
        puppeteerInstance: browser
    });
    for (const frame of [0, 66, 110, 200, 290, 380, 465, 530, 564, 590, 620, 710]) {
        await renderStill({
            composition,
            serveUrl,
            inputProps: props,
            frame,
            puppeteerInstance: browser,
            output: path.join(directory, `review-${frame}.png`)
        });
    }
} finally {
    await browser.close({ silent: true });
}
console.log('12 review frames saved.');
