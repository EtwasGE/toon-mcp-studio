import { bundle } from '@remotion/bundler';
import { renderStill, selectComposition } from '@remotion/renderer';
import path from 'node:path';
const serveUrl = await bundle({ entryPoint: path.resolve('renderer/src/TabletOptions.tsx') });
const composition = await selectComposition({ serveUrl, id: 'TabletOptions' });
await renderStill({
    serveUrl,
    composition,
    output: path.resolve('examples/novostroykin-episode/tablet-options.png'),
    frame: 0
});
