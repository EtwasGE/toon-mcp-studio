import React, { useEffect, useState } from 'react';
import { AbsoluteFill, Img, delayRender, continueRender, cancelRender, staticFile, useCurrentFrame } from 'remotion';
import { Rig } from './CharacterRig';
import { EnvironmentStage, builtinEnvironments, environmentCamera } from './Environment';
import type { Timeline } from './types';
import fontUrl from '@fontsource/noto-sans/files/noto-sans-cyrillic-700-normal.woff2';
import latinUrl from '@fontsource/noto-sans/files/noto-sans-latin-700-normal.woff2';

export function Film({ timeline }: { timeline: Timeline }) {
    const frame = useCurrentFrame();
    const [handle] = useState(() => delayRender('Local fonts and measured caption layout'));
    useEffect(() => {
        (async () => {
            const cyr = new FontFace('Toon', `url(${fontUrl})`, {
                weight: '700',
                unicodeRange: 'U+0400-045F,U+0490-0491,U+04B0-04B1,U+2116'
            });
            const latin = new FontFace('Toon', `url(${latinUrl})`, { weight: '700' });
            document.fonts.add(await latin.load());
            document.fonts.add(await cyr.load());
            await document.fonts.ready;
            const canvas = document.createElement('canvas'),
                ctx = canvas.getContext('2d')!;
            ctx.font = '700 52px Toon';
            for (const cap of timeline.captions) {
                let lines = 1,
                    width = 0;
                for (const word of cap.text.split(/\s+/)) {
                    const w = ctx.measureText(word + ' ').width;
                    if (w > 820) throw Error('CAPTION_OVERFLOW: word wider than safe area');

                    if (width + w > 820) {
                        lines++;
                        width = 0;
                    }
                    width += w;
                }
                if (lines > 2) throw Error('CAPTION_OVERFLOW: utterance requires more than two lines');
            }
            continueRender(handle);
        })().catch(cancelRender);
    }, [handle, timeline]);
    const scene =
        timeline.scenes.find((s) => frame >= s.start_frame && frame < s.end_frame_exclusive) ??
        timeline.scenes[timeline.scenes.length - 1];
    const environment =
        timeline.environments?.[`${scene.background.asset_id}@${scene.background.version}`] ??
        builtinEnvironments[scene.background.asset_id];
    if (!environment) throw Error('ASSET_MISSING: environment');
    let camera = scene.camera,
        transform = environmentCamera(camera, scene, environment);
    for (const e of scene.events.filter((e) => e.action.kind === 'camera' && e.frame <= frame)) {
        const to = environmentCamera(e.action.camera!, scene, environment),
            frames = Math.round((e.action.transition_ms ?? 0) * 0.03),
            t = frames ? Math.min(1, (frame - e.frame) / frames) : 1,
            eased = t * t * (3 - 2 * t);
        transform = {
            scale: transform.scale + (to.scale - transform.scale) * eased,
            x: transform.x + (to.x - transform.x) * eased,
            y: transform.y + (to.y - transform.y) * eased
        };
        camera = e.action.camera!;
    }
    const caption = timeline.captions.find((c) => frame >= c.start_frame && frame < c.end_frame_exclusive);
    return (
        <AbsoluteFill style={{ background: '#243647' }}>
            <div style={{ position: 'absolute', width: 1080, height: 1920, fontFamily: 'Toon', fontWeight: 700 }}>
                <EnvironmentStage
                    environment={environment}
                    scene={scene}
                    transform={transform}
                    assets={timeline.assets}
                    renderCharacter={(c, slot) => {
                        const changes = scene.events.filter(
                                (e) => e.frame <= frame && e.action.character_id === c.character_id
                            ),
                            emotion =
                                changes.filter((e) => e.action.kind === 'emotion').at(-1)?.action.emotion_id ??
                                c.emotion_id,
                            gesture = changes.filter((e) => e.action.kind === 'gesture').at(-1);
                        const line = timeline.utterances.find(
                            (u) =>
                                u.speaker_character_id === c.character_id &&
                                frame * 1600 >= u.start_sample &&
                                frame * 1600 < u.end_sample_exclusive
                        );
                        const amp = line?.envelope[Math.floor((frame * 1600 - line.start_sample) / 1600)] ?? 0;
                        const mouth = amp === 0 ? 0 : amp > 0.7 ? 2 : amp > 0.35 ? 3 : 1;
                        return (
                            <g
                                key={c.character_id}
                                transform={`translate(${slot.x} ${slot.floor_y - 560 * slot.scale}) scale(${slot.scale})`}
                            >
                                <Rig
                                    id={c.character_id}
                                    rig={timeline.rigs?.[c.character_id]}
                                    assets={timeline.assets}
                                    surfaceY={
                                        environment.anchors.table
                                            ? (environment.anchors.table[1] - slot.floor_y) / slot.scale + 560
                                            : undefined
                                    }
                                    frame={frame}
                                    seed={timeline.seed}
                                    emotion={emotion}
                                    mouth={mouth}
                                    gesture={gesture?.action.gesture_id ?? 'idle'}
                                    age={gesture ? frame - gesture.frame : 999}
                                    heldProps={(scene.props ?? [])
                                        .filter((p) => p.character_id === c.character_id)
                                        .map((p) => ({
                                            ...p,
                                            stashFrame:
                                                p.hand === 'left'
                                                    ? changes.find(
                                                          (e) =>
                                                              e.action.kind === 'gesture' &&
                                                              e.action.gesture_id === 'hide_object'
                                                      )?.frame
                                                    : undefined,
                                            opened: scene.events.some(
                                                (e) =>
                                                    e.action.kind === 'prop' &&
                                                    e.action.instance_id === p.instance_id &&
                                                    e.action.prop_action === 'open' &&
                                                    (e.action.contact_frame ?? Infinity) <= frame
                                            ),
                                            cue: scene.events
                                                .filter(
                                                    (e) =>
                                                        e.action.kind === 'prop' &&
                                                        e.action.instance_id === p.instance_id &&
                                                        e.frame <= frame
                                                )
                                                .at(-1)
                                        }))}
                                />
                            </g>
                        );
                    }}
                />
                {scene.overlays
                    .filter((o) => frame >= o.start_frame && frame < o.end_frame_exclusive)
                    .map((o, i) =>
                        o.content.kind === 'text' ? (
                            <div
                                key={i}
                                data-safe-text
                                style={{
                                    position: 'absolute',
                                    left: 80,
                                    top:
                                        o.content.placement === 'top'
                                            ? 200
                                            : o.content.placement === 'lower'
                                              ? 1400
                                              : 600,
                                    width: 820,
                                    color: o.content.preset_id === 'hook' ? '#293747' : '#fff2ce',
                                    fontSize: o.content.preset_id === 'hook' ? 64 : 60,
                                    lineHeight: 1.2,
                                    whiteSpace: 'pre-line',
                                    textAlign: 'center',
                                    padding: '20px 0'
                                }}
                            >
                                {o.content.text}
                            </div>
                        ) : (
                            <div
                                key={i}
                                style={{
                                    position: 'absolute',
                                    left:
                                        o.content.placement === 'full_frame'
                                            ? 0
                                            : o.content.placement === 'inset'
                                              ? 620
                                              : 190,
                                    top:
                                        o.content.placement === 'full_frame'
                                            ? 0
                                            : o.content.placement === 'inset'
                                              ? 340
                                              : 350,
                                    width:
                                        o.content.placement === 'full_frame'
                                            ? 1080
                                            : o.content.placement === 'inset'
                                              ? 260
                                              : 640,
                                    height:
                                        o.content.placement === 'full_frame'
                                            ? 1920
                                            : o.content.placement === 'inset'
                                              ? 500
                                              : 950,
                                    background: '#172735',
                                    border: o.content.placement === 'full_frame' ? 'none' : '12px solid #172735',
                                    borderRadius: o.content.placement === 'full_frame' ? 0 : 40,
                                    boxShadow: '0 20px 0 #111a2233'
                                }}
                            >
                                <Img
                                    src={staticFile(
                                        timeline.assets[o.content.asset!.asset_id + '@' + o.content.asset!.version]
                                    )}
                                    style={{ width: '100%', height: '100%', objectFit: 'contain', display: 'block' }}
                                />
                            </div>
                        )
                    )}
                {caption && (
                    <div
                        data-caption
                        style={{
                            position: 'absolute',
                            left: 80,
                            top: 1370,
                            width: 820,
                            height: 190,
                            boxSizing: 'border-box',
                            display: 'flex',
                            alignItems: 'center',
                            justifyContent: 'center',
                            textAlign: 'center',
                            fontSize: 52,
                            lineHeight: '68px',
                            color: caption.mode === 'word' ? '#ffe08a' : '#fff9eb',
                            background: '#223341ee',
                            borderRadius: 26,
                            padding: '12px 0',
                            textShadow: '0 3px 0 #15212f'
                        }}
                    >
                        {caption.text}
                    </div>
                )}
            </div>
        </AbsoluteFill>
    );
}
