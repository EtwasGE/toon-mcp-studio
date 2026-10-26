import React from 'react';
import { registerRoot, Composition, Audio, staticFile, AbsoluteFill } from 'remotion';
import { Film } from './Film';
import type { Timeline } from './types';
function StudioFilm({ timeline, studio_audio }: { timeline: Timeline; studio_audio?: string }) {
    if (timeline.scenes.length === 0) {
        return (
            <AbsoluteFill style={{ background: '#243647', color: 'white', padding: 80, fontSize: 48 }}>
                Откройте подготовленный выпуск из Codex, чтобы увидеть сцены и прослушать озвучку.
            </AbsoluteFill>
        );
    }
    return (
        <>
            <Film timeline={timeline} />
            {studio_audio ? <Audio src={staticFile(studio_audio)} /> : null}
        </>
    );
}
registerRoot(() => (
    <Composition
        id="Toon"
        component={StudioFilm}
        width={1080}
        height={1920}
        fps={30}
        durationInFrames={30}
        defaultProps={{ timeline: { total_frames: 30, seed: 1, scenes: [], utterances: [], captions: [], assets: {} } }}
        calculateMetadata={({ props }) => ({ durationInFrames: props.timeline.total_frames })}
    />
));
