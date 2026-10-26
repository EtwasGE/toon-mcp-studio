import React from 'react';
import { registerRoot, Composition } from 'remotion';
import { Film } from './Film';
import type { Timeline } from './types';
const empty: Timeline = { total_frames: 30, seed: 1, scenes: [], utterances: [], captions: [], assets: {} };
registerRoot(() => (
    <Composition
        id="Toon"
        component={Film}
        width={1080}
        height={1920}
        fps={30}
        durationInFrames={30}
        defaultProps={{ timeline: empty }}
        calculateMetadata={({ props }) => ({ durationInFrames: props.timeline.total_frames })}
    />
));
