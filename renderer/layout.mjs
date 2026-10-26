import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {create} from 'fontkit';
const here=path.dirname(fileURLToPath(import.meta.url));
const fonts=['latin','cyrillic','latin-ext','cyrillic-ext','symbols'].map(s=>path.join(here,`node_modules/@fontsource/noto-sans/files/noto-sans-${s}-700-normal.woff2`)).filter(p=>fs.existsSync(p)).map(p=>create(fs.readFileSync(p)));
export function width(text,size){let advance=0;for(const ch of text){const code=ch.codePointAt(0);const font=fonts.find(f=>f.hasGlyphForCodePoint(code));if(!font)throw Object.assign(Error('Font does not contain U+'+code.toString(16)),{code:'UNSUPPORTED_ACTION'});advance+=font.glyphForCodePoint(code).advanceWidth/font.unitsPerEm*size;}return advance;}
export function lines(text,size,maxWidth){let count=0;for(const paragraph of text.split('\n')){let w=0;count++;for(const word of paragraph.split(/\s+/)){const n=width(word,size);if(n>maxWidth)throw Object.assign(Error('Single word exceeds text safe area'),{code:'CAPTION_OVERFLOW'});const space=w?width(' ',size):0;if(w+space+n>maxWidth){count++;w=n;}else w+=space+n;}}return count;}
if(process.argv[1]===fileURLToPath(import.meta.url)){
 const p=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));const issues=[];
 for(const [si,s] of p.scenes.entries()){
  for(const [ui,u] of (s.utterances??[]).entries())try{width(u.text,52);if(p.captions.enabled&&(p.captions.mode==='utterance'||p._caption_mode_by_utterance?.[u.utterance_id]==='utterance')&&lines(u.text,52,820)>2)throw Object.assign(Error('Whole utterance exceeds two lines at 52px; shorten or provide verified timing'),{code:'CAPTION_OVERFLOW'});}catch(e){issues.push({code:e.code??'CAPTION_OVERFLOW',message:e.message,pointer:`/scenes/${si}/utterances/${ui}/text`});}
  for(const [oi,o] of s.overlays.entries())if(o.content.kind==='text')try{const size=o.content.preset_id==='hook'?64:60;const height=lines(o.content.text,size,820)*size*1.2+40;const top=o.content.placement==='top'?200:o.content.placement==='lower'?1400:600;if(top+height>1580)throw Object.assign(Error('Overlay exceeds vertical safe area'),{code:'CAPTION_OVERFLOW'});}catch(e){issues.push({code:e.code??'CAPTION_OVERFLOW',message:e.message,pointer:`/scenes/${si}/overlays/${oi}/content/text`});}
 }
 process.stdout.write(JSON.stringify({issues}));
}
