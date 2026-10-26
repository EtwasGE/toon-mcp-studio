import React, { useEffect, useState } from 'react';
import { AbsoluteFill, Img, delayRender, continueRender, cancelRender, staticFile, useCurrentFrame } from 'remotion';
import { Rig } from './Rig';
import type { Timeline, Camera, Scene } from './types';
import fontUrl from '@fontsource/noto-sans/files/noto-sans-cyrillic-700-normal.woff2';
import latinUrl from '@fontsource/noto-sans/files/noto-sans-latin-700-normal.woff2';

function Background({ id }: { id: string }) {
    return (
        <svg width="1080" height="1920" viewBox="0 0 1080 1920" style={{ position: 'absolute' }}>
            <defs>
                <pattern id="paper" width="43" height="47" patternUnits="userSpaceOnUse">
                    <circle cx="5" cy="8" r="1" fill="#fff" opacity=".12" />
                    <circle cx="24" cy="35" r="1" fill="#000" opacity=".07" />
                </pattern>
                <linearGradient id="wall" x2="0" y2="1">
                    <stop stopColor={id === 'restaurant_evening' ? '#eee0bd' : '#e7dcca'} />
                    <stop offset="1" stopColor="#cfb68e" />
                </linearGradient>
            </defs>
            <rect width="1080" height="1920" fill={id === 'neutral_dark' ? '#243647' : 'url(#wall)'} />
            {id === 'restaurant_evening' ? (
                <g stroke="#37414a" strokeWidth="9">
                    <path d="M0 1080H1080V1920H0Z" fill="#4b777a" />
                    <rect x="90" y="360" width="350" height="520" rx="130" fill="#314d62" />
                    <rect x="640" y="360" width="350" height="520" rx="130" fill="#314d62" />
                    <path d="M265 360V880 M90 620H440 M815 360V880 M640 620H990" fill="none" />
                    <g stroke="#eac37d" strokeWidth="10">
                        <path d="M140 770V700 M204 770V660 M327 770V710 M710 770V660 M850 770V700 M930 770V630" />
                    </g>
                    <path d="M540 0V350" />
                    <path d="M445 425Q540 268 635 425Z" fill="#e7ad5d" />
                    <ellipse cx="540" cy="426" rx="95" ry="15" fill="#ffe9a9" />
                </g>
            ) : id === 'home_office' ? (
                <g stroke="#37414a" strokeWidth="8">
                    <rect x="70" y="340" width="410" height="510" rx="22" fill="#b4d5d0" />
                    <path d="M275 340V850 M70 595H480" />
                    <path d="M620 470H1000V820H620Z" fill="#9d725b" />
                    <path d="M620 650H1000" />
                    <path
                        d="M655 495V630 M690 495V630 M735 495V630 M815 690V795 M860 690V795 M905 690V795"
                        stroke="#ecd393"
                        strokeWidth="24"
                    />
                    <path d="M0 1120H1080V1920H0Z" fill="#7f9990" />
                </g>
            ) : null}
            <rect width="1080" height="1920" fill="url(#paper)" />
        </svg>
    );
}
function cameraTransform(cam: Camera, scene: Scene) {
    if (cam.shot !== 'close_up') return { scale: 1, x: 0, y: 0 };
    const cast = scene.cast.find((c) => c.character_id === cam.subject_character_id);
    const x = cast?.placement === 'left' ? 285 : cast?.placement === 'right' ? 790 : 540;
    return { scale: 1.72, x: 540 - x * 1.72, y: 740 - 805 * 1.72 };
}
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
    let camera = scene.camera,
        transform = cameraTransform(camera, scene);
    for (const e of scene.events.filter((e) => e.action.kind === 'camera' && e.frame <= frame)) {
        const to = cameraTransform(e.action.camera!, scene),
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
                <Background id={scene.background.asset_id} />
                <svg width="1080" height="1920" viewBox="0 0 1080 1920" style={{ position: 'absolute' }}>
                    <g transform={`translate(${transform.x} ${transform.y}) scale(${transform.scale})`}>
                        {scene.cast.map((c) => {
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
                                    transform={`translate(${c.placement === 'left' ? 55 : c.placement === 'right' ? 570 : 300} 670) scale(1.2)`}
                                >
                                    <Rig
                                        id={c.character_id}
                                        frame={frame}
                                        seed={timeline.seed}
                                        emotion={emotion}
                                        mouth={mouth}
                                        gesture={gesture?.action.gesture_id ?? 'idle'}
                                        age={gesture ? frame - gesture.frame : 999}
                                    />
                                </g>
                            );
                        })}
                        {scene.cast.length > 0 && (
                            <g stroke="#303a45" strokeWidth="8">
                                <path d="M20 1230Q540 1180 1060 1230L1010 1360H70Z" fill="#c99563" />
                                <path d="M140 1360L130 1700 M950 1360L960 1700" />
                                <ellipse cx="365" cy="1235" rx="61" ry="17" fill="#f0dcb8" />
                                <path d="M335 1190H390V1230Q360 1250 335 1230Z" fill="#e9f0e7" />
                                <path d="M392 1199Q425 1197 415 1220H394" fill="none" />
                            </g>
                        )}
                    </g>
                </svg>
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
                <div
                    style={{
                        position: 'absolute',
                        left: 80,
                        top: 1780,
                        fontSize: 22,
                        letterSpacing: 5,
                        color: '#f7eed788'
                    }}
                >
                    TOON · ОРИГИНАЛЬНАЯ СТУДИЯ
                </div>
            </div>
        </AbsoluteFill>
    );
}
