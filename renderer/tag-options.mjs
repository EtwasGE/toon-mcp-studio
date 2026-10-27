import { bundle } from '@remotion/bundler';
import { renderStill, selectComposition } from '@remotion/renderer';
import path from 'node:path';
const serveUrl = await bundle({ entryPoint: path.resolve('renderer/src/TagOptions.tsx') });
const composition = await selectComposition({ serveUrl, id: 'TagOptions' });
await renderStill({
    serveUrl,
    composition,
    output: path.resolve('examples/novostroykin-episode/tag-options.png'),
    frame: 0
});
