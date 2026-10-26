import {bundle} from '@remotion/bundler';
import {renderMedia,renderStill,selectComposition,openBrowser} from '@remotion/renderer';
import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url));
const [timelinePath,outputDir,quality='preview',mode='video']=process.argv.slice(2);
const timeline=JSON.parse(await fs.readFile(timelinePath,'utf8'));
const serveUrl=await bundle({entryPoint:path.join(here,'src/index.tsx'),publicDir:path.join(outputDir,'public'),outDir:path.join(outputDir,'bundle'),webpackOverride:config=>({...config,module:{...config.module,rules:[...(config.module?.rules??[]),{test:/\.woff2$/,type:'asset/resource'}]}})});
const browser=await openBrowser('chrome');
try {
 const props={timeline}; const composition=await selectComposition({serveUrl,id:'Toon',inputProps:props,puppeteerInstance:browser});
 if(mode==='video') await renderMedia({composition,serveUrl,codec:'h264',pixelFormat:'yuv420p',outputLocation:path.join(outputDir,'silent.partial.mp4'),inputProps:props,scale:quality==='preview'?.5:1,concurrency:2,puppeteerInstance:browser,crf:quality==='preview'?25:20,onProgress:({progress})=>{if(Math.round(progress*100)%10===0)process.stderr.write(`render ${Math.round(progress*100)}%\n`);}});
 const frames=new Set([0,timeline.total_frames-1,...timeline.scenes.map(s=>s.start_frame),...timeline.scenes.flatMap(s=>s.events.flatMap(e=>[Math.max(0,e.frame-1),e.frame,Math.min(s.end_frame_exclusive-1,e.frame+12),Math.min(s.end_frame_exclusive-1,e.frame+54)]))]);
 for(const frame of frames)await renderStill({composition,serveUrl,output:path.join(outputDir,`frame-${String(frame).padStart(4,'0')}.png`),frame,inputProps:props,puppeteerInstance:browser});
 await fs.copyFile(path.join(outputDir,'frame-0000.png'),path.join(outputDir,'cover.png'));
}finally{await browser.close({silent:true});}
