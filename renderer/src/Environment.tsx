import React, { useId } from 'react';
import { Img, staticFile } from 'remotion';
import restaurant from '../../styles/warm_editorial_2d/environments/restaurant_evening.environment.json';
import home from '../../styles/warm_editorial_2d/environments/home_office.environment.json';
import neutral from '../../styles/warm_editorial_2d/environments/neutral_dark.environment.json';
import type { RigNode } from './CharacterRig';
import type { Camera, Cast, Scene } from './types';

type Node = Omit<RigNode, 'type' | 'children' | 'motion' | 'pivot' | 'when'> & {
    type: 'group' | 'path' | 'ellipse' | 'circle' | 'rect' | 'cutout' | 'defs' | 'pattern' | 'linearGradient' | 'stop';
    children?: Node[];
};
type Transform = { scale: number; x: number; y: number };
export type Slot = { x: number; floor_y: number; scale: number; depth: number; focus: number[] };
export type EnvironmentDefinition = {
    schema_version: string;
    name: string;
    canvas: { width: number; height: number };
    slots: Record<string, Slot>;
    anchors: Record<string, number[]>;
    cameras: {
        wide: Transform;
        two_shot: Transform;
        screen: Transform;
        close_up: { scale: number; screen_anchor: number[]; clamp: boolean };
    };
    layers: { id: string; depth: number; space: 'world' | 'screen'; requires_cast: boolean; nodes: Node[] }[];
};
export const builtinEnvironments: Record<string, EnvironmentDefinition> = {
    restaurant_evening: restaurant as unknown as EnvironmentDefinition,
    home_office: home as unknown as EnvironmentDefinition,
    neutral_dark: neutral as unknown as EnvironmentDefinition
};
export function environmentCamera(camera: Camera, scene: Scene, environment: EnvironmentDefinition): Transform {
    if (camera.shot !== 'close_up') return environment.cameras[camera.shot];
    const cast = scene.cast.find((c) => c.character_id === camera.subject_character_id);
    const slot = environment.slots[cast?.placement ?? 'center'];
    const preset = environment.cameras.close_up;
    let x = preset.screen_anchor[0] - slot.focus[0] * preset.scale;
    let y = preset.screen_anchor[1] - slot.focus[1] * preset.scale;
    if (preset.clamp) {
        x = Math.max(1080 * (1 - preset.scale), Math.min(0, x));
        y = Math.max(1920 * (1 - preset.scale), Math.min(0, y));
    }
    return { scale: preset.scale, x, y };
}

export function EnvironmentStage({
    environment,
    scene,
    transform,
    assets,
    renderCharacter
}: {
    environment: EnvironmentDefinition;
    scene: Scene;
    transform: Transform;
    assets: Record<string, string>;
    renderCharacter: (cast: Cast, slot: Slot) => React.ReactNode;
}) {
    const prefix = useId().replace(/:/g, '');
    function draw(n: Node, key: string, scope: string): React.ReactNode {
        const props = { ...n.props };
        if (props.id) {
            props.id = `${scope}-${props.id}`;
        }

        if (typeof props.fill === 'string' && props.fill.startsWith('url(#')) {
            props.fill = props.fill.replace('url(#', `url(#${scope}-`);
        }

        if (n.type === 'cutout') {
            const ref = n.asset!;
            const source = assets[`${ref.asset_id}@${ref.version}`];
            if (!source) throw Error('ASSET_MISSING: environment layer');
            const target = n.target!;
            const clip = `${scope}-cutout-${key}`;
            return (
                <svg
                    key={key}
                    x={target[0]}
                    y={target[1]}
                    width={target[2]}
                    height={target[3]}
                    viewBox={n.box!.join(' ')}
                    preserveAspectRatio="none"
                    overflow="hidden"
                >
                    <defs>
                        <clipPath id={clip}>
                            <polygon points={n.polygon!.map((p) => p.join(',')).join(' ')} />
                        </clipPath>
                    </defs>
                    <g clipPath={`url(#${clip})`}>
                        <foreignObject x="0" y="0" width={n.source_width} height={n.source_height}>
                            <Img src={staticFile(source)} style={{ width: '100%', height: '100%' }} />
                        </foreignObject>
                    </g>
                </svg>
            );
        }
        const tags = {
            group: 'g',
            path: 'path',
            ellipse: 'ellipse',
            circle: 'circle',
            rect: 'rect',
            defs: 'defs',
            pattern: 'pattern',
            linearGradient: 'linearGradient',
            stop: 'stop'
        };
        const tag = tags[n.type];
        if (!tag) throw Error('UNSUPPORTED_ACTION: environment node');
        return React.createElement(
            tag,
            { ...props, key },
            n.children?.map((child, i) => draw(child, `${key}-${i}`, scope))
        );
    }
    const entries = [
        ...environment.layers
            .filter((l) => !l.requires_cast || scene.cast.length > 0)
            .map((l) => ({
                depth: l.depth,
                key: l.id,
                space: l.space,
                content: () => l.nodes.map((n, i) => draw(n, String(i), `${prefix}-${l.id}`))
            })),
        ...scene.cast.map((c) => ({
            depth: environment.slots[c.placement].depth,
            key: `actor-${c.character_id}`,
            space: 'world',
            content: () => renderCharacter(c, environment.slots[c.placement])
        }))
    ].sort((a, b) => a.depth - b.depth);
    return (
        <svg width="1080" height="1920" viewBox="0 0 1080 1920" style={{ position: 'absolute' }}>
            {entries.map((entry) => (
                <g
                    key={entry.key}
                    transform={
                        entry.space === 'world'
                            ? `translate(${transform.x} ${transform.y}) scale(${transform.scale})`
                            : undefined
                    }
                >
                    {entry.content()}
                </g>
            ))}
        </svg>
    );
}
