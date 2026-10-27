import React from 'react';
import { AbsoluteFill, useCurrentFrame } from 'remotion';
import { Rig, type CharacterRig } from './CharacterRig';

export function PugPreview({ rig, assets }: { rig: CharacterRig; assets: Record<string, string> }) {
    const frame = useCurrentFrame();
    const cues = [
        { frame: 0, gesture: 'idle', label: 'Сидит и моргает' },
        { frame: 75, gesture: 'look_left', label: 'Взгляд влево' },
        { frame: 165, gesture: 'look_right', label: 'Взгляд вправо' },
        { frame: 255, gesture: 'open_mouth', label: 'Открывает рот' },
        { frame: 345, gesture: 'tongue_out', label: 'Высовывает язык' },
        { frame: 435, gesture: 'move_paws', label: 'Двигает передними лапами' },
        { frame: 540, gesture: 'hide_object', label: 'Прячет предмет под себя' },
        { frame: 705, gesture: 'look_right', label: 'Предмет остаётся спрятанным' }
    ];
    const cue = cues.filter((c) => c.frame <= frame).at(-1)!;
    if (!rig)
        return (
            <AbsoluteFill style={{ background: '#F8EEDC', padding: 80 }}>
                Загрузите подготовленный риг мопса.
            </AbsoluteFill>
        );

    return (
        <AbsoluteFill style={{ background: '#F8EEDC', color: '#503426', fontFamily: 'sans-serif' }}>
            <div style={{ position: 'absolute', top: 35, width: '100%', textAlign: 'center', fontSize: 25 }}>
                Пес Алкаша · Наглый хитрец
            </div>
            <svg width="960" height="900" viewBox="-65 -20 510 600">
                <ellipse cx="190" cy="551" rx="185" ry="12" fill="#D7B992" opacity="0.35" />
                <Rig
                    id="pug-preview"
                    rig={rig}
                    assets={assets}
                    frame={frame}
                    seed={11}
                    emotion="confident"
                    mouth={0}
                    gesture={cue.gesture}
                    age={frame - cue.frame}
                    heldProps={
                        frame < 525
                            ? []
                            : [
                                  {
                                      instance_id: 'test-phone',
                                      prop_id: 'phone',
                                      version: '1.0.0',
                                      character_id: 'pug-preview',
                                      hand: 'left',
                                      stashFrame: 540
                                  }
                              ]
                    }
                />
            </svg>
            <div style={{ position: 'absolute', bottom: 25, width: '100%', textAlign: 'center', fontSize: 26 }}>
                {cue.label}
            </div>
        </AbsoluteFill>
    );
}
