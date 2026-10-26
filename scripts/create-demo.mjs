import fs from 'node:fs/promises';
const root=new URL('../',import.meta.url), dir=new URL('examples/offline-dialogue/',root);
const lines=JSON.parse((await fs.readFile(new URL('lines.json',dir),'utf8')).replace(/^\uFEFF/,''));
const cast=[{character_id:'buyer',placement:'left',pose_id:'seated',emotion_id:'concerned'},{character_id:'advisor',placement:'right',pose_id:'seated',emotion_id:'confident'}];
const anchor=id=>({kind:'utterance_start',utterance_id:id});
const gesture=(id,speaker,g)=>({event_id:id+'_'+g,at:anchor(id),action:{kind:'gesture',character_id:speaker,gesture_id:g}});
const close=(id,who)=>({event_id:id+'_close',at:anchor(id),action:{kind:'camera',camera:{shot:'close_up',subject_character_id:who},transition_ms:200}});
const dialogue=(id,bg,ls,events,overlays=[])=>({scene_id:id,kind:'dialogue',background:{asset_id:bg,version:'1.0.0'},camera:{shot:'two_shot'},cast,utterances:ls.map(l=>({utterance_id:l.id,speaker_character_id:l.speaker,text:l.text,audio:{mode:'recording',asset:{asset_id:l.id,version:'1.0.0'}},pause_after_ms:300})),lead_in_ms:200,tail_ms:300,events,overlays});
const project={schema_version:'1.0.0',kind:'draft_project',title:'Планы на вечер — автономная речевая демонстрация',locale:'ru-RU',video:{width:1080,height:1920,fps:30},target_duration_seconds:28,max_final_duration_seconds:60,seed:42026,style:{style_id:'warm_editorial_2d',version:'1.0.0'},characters:['buyer','advisor'].map(character_id=>({character_id,version:'1.0.0'})),captions:{enabled:true,mode:'auto',preset_id:'high_contrast_ru'},scenes:[
dialogue('restaurant','restaurant_evening',lines.slice(0,2),[gesture('problem','buyer','shrug'),close('problem','buyer'),close('idea','advisor'),gesture('idea','advisor','nod')],[{overlay_id:'hook',start:{kind:'scene_start'},end:{kind:'scene_end'},content:{kind:'text',text:'Вечер — для своих планов',placement:'top',preset_id:'hook'}}]),
{...dialogue('phone_insert','neutral_dark',[lines[2]],[],[{overlay_id:'fixture_screen',start:{kind:'scene_start'},end:{kind:'scene_end'},content:{kind:'image',asset:{asset_id:'demo_screen',version:'1.0.0'},placement:'centered_phone',fit:'contain',render_mode:'exact'}}]),cast:[],camera:{shot:'screen'}},
dialogue('reaction','restaurant_evening',[lines[3]],[gesture('reaction','buyer','raise_hand'),gesture('reaction','advisor','show_phone')]),
dialogue('office','home_office',lines.slice(4),[close('home','buyer'),close('answer','advisor'),gesture('answer','advisor','nod')]),
{scene_id:'cta',kind:'hold',background:{asset_id:'neutral_dark',version:'1.0.0'},camera:{shot:'wide'},cast:[],hold_ms:3000,events:[],overlays:[{overlay_id:'cta_text',start:{kind:'scene_start'},end:{kind:'scene_end'},content:{kind:'text',text:'Больше времени\nдля своих планов.\n\nTOON · OFFLINE DEMO',placement:'center',preset_id:'cta'}}]}]};
await fs.writeFile(new URL('project.json',dir),JSON.stringify(project,null,2));
