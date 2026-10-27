import React from 'react';
import { Img, staticFile } from 'remotion';
import type { Overlay, Scene, Timeline } from './types';

const ease = (value: number) => {
    const t = Math.max(0, Math.min(1, value));
    return t * t * (3 - 2 * t);
};

export function ScreenDemo({
    overlay,
    scene,
    timeline,
    frame
}: {
    overlay: Overlay;
    scene: Scene;
    timeline: Timeline;
    frame: number;
}) {
    const asset = overlay.content.asset!;
    const startScreen = asset.asset_id === 'asset_05cde3af7bdc476cc71a245d';
    const menu = asset.asset_id === 'asset_7285f398759be3d3e368f5a0';
    const choices = asset.asset_id === 'asset_13c5c61fa619b2dfd27dbb5b';
    const tap = scene.events.find((event) => event.event_id === (menu ? 'tap_subscribe' : 'tap_start'));
    const tapX = menu ? 450 : 550;
    const tapY = menu ? 1180 : 1778;
    const tapAge = tap ? frame - tap.frame : -1;
    const contactAge = tap ? (tap.action.contact_frame ?? tap.frame + 18) - tap.frame : 18;
    const line = timeline.utterances.find((utterance) => utterance.utterance_id === 's3_u1');
    const elapsed = line ? (frame * 1600 - line.start_sample) / 48 : -1;
    const words = line?.alignment?.normalized_words ?? [];
    const highlights = [
        { first: 5, last: 6, x: 118, y: 1040, width: 382, height: 90 },
        { first: 7, last: 7, x: 506, y: 1040, width: 385, height: 90 },
        { first: 8, last: 11, x: 118, y: 1140, width: 773, height: 90 },
        { first: 12, last: 14, x: 118, y: 1240, width: 773, height: 90 },
        { first: 16, last: 17, x: 118, y: 1340, width: 773, height: 90 }
    ];
    const highlight = choices
        ? highlights.find(
              (item) =>
                  elapsed >= (words[item.first]?.start_ms ?? Infinity) &&
                  elapsed < (words[item.last]?.end_ms ?? -1) + 120
          )
        : undefined;
    const zoom = choices ? 0.975 + 0.025 * ease((frame - overlay.start_frame) / 45) : 1;
    return (
        <div style={{ width: '100%', height: '100%', transform: `scale(${zoom})`, transformOrigin: '50% 50%' }}>
            <Img
                src={staticFile(timeline.assets[`${asset.asset_id}@${asset.version}`])}
                style={{ width: '100%', height: '100%', objectFit: 'contain', display: 'block' }}
            />
            {choices && highlight && (
                <svg viewBox="0 0 1080 1920" style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }}>
                    <rect
                        x={highlight.x}
                        y={highlight.y}
                        width={highlight.width}
                        height={highlight.height}
                        rx={16}
                        fill="#ffd44d"
                        fillOpacity={0.13}
                        stroke="#e6b336"
                        strokeWidth={4}
                    />
                </svg>
            )}
            {(startScreen || menu) && tapAge >= 0 && (
                <svg viewBox="0 0 1080 1920" style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }}>
                    {tapAge >= contactAge && (
                        <circle
                            cx={tapX}
                            cy={tapY}
                            r={25 + (tapAge - contactAge) * 5}
                            fill="none"
                            stroke="#fff3c7"
                            strokeWidth={7}
                            opacity={Math.max(0, 1 - (tapAge - contactAge) / 10)}
                        />
                    )}
                    <g
                        transform={`translate(${tapX} ${tapY + (2120 - tapY) * (1 - ease(tapAge / contactAge))}) rotate(-12)`}
                    >
                        <path
                            d="M-17 112V23Q-17 0 0 0Q17 0 17 23V91Q34 76 49 99Q68 85 83 109Q104 100 112 127L121 184Q126 219 104 246L88 282H-1L-15 229Q-38 204-47 167L-60 129Q-65 106-47 104Q-34 103-17 134Z"
                            fill="#fac38f"
                            stroke="#25171a"
                            strokeWidth={7}
                            strokeLinejoin="round"
                        />
                        <path
                            d="M17 91V143M49 100V149M83 110V155"
                            fill="none"
                            stroke="#a37350"
                            strokeWidth={4}
                            strokeLinecap="round"
                        />
                    </g>
                </svg>
            )}
        </div>
    );
}
