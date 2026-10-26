import React, { useId } from 'react';
import { Img, staticFile } from 'remotion';
import buyer from '../../styles/warm_editorial_2d/characters/buyer.rig.json';
import advisor from '../../styles/warm_editorial_2d/characters/advisor.rig.json';

export type RigNode = {
    type: 'group' | 'path' | 'ellipse' | 'circle' | 'rect' | 'cutout';
    props?: Record<string, string | number>;
    children?: RigNode[];
    motion?: 'head' | 'left_arm' | 'right_arm';
    pivot?: number[];
    when?: { emotion?: string[]; mouth?: number[]; blink?: boolean; phone?: boolean };
    asset?: { asset_id: string; version: string };
    source_width?: number;
    source_height?: number;
    polygon?: number[][];
    box?: number[];
    target?: number[];
};
export type CharacterRig = {
    schema_version: string;
    name: string;
    canvas: { width: number; height: number };
    poses: string[];
    emotions: string[];
    gestures: Record<string, number>;
    motion: {
        blink_phase: number;
        blink_period: number;
        head_phase: number;
        body_phase: number;
        head_angles: Record<string, number>;
        left_arm: Record<string, number>;
        right_arm: Record<string, number>;
    };
    layers: RigNode[];
};
export const builtinRigs: Record<string, CharacterRig> = {
    buyer: buyer as unknown as CharacterRig,
    advisor: advisor as unknown as CharacterRig
};

export function Rig({
    id,
    rig = builtinRigs[id],
    assets = {},
    frame,
    seed,
    emotion,
    mouth,
    gesture,
    age
}: {
    id: string;
    rig?: CharacterRig;
    assets?: Record<string, string>;
    frame: number;
    seed: number;
    emotion: string;
    mouth: number;
    gesture: string;
    age: number;
}) {
    const prefix = useId().replace(/:/g, '');
    if (!rig) throw Error('ASSET_MISSING: character rig');
    const duration = rig.gestures[gesture] ?? 30;
    const active = age >= 0 && age < duration;
    const hold = active ? Math.min(1, age / 12, (duration - age) / 12) : 0;
    const motion = rig.motion;
    const blink =
        (frame + motion.blink_phase + (seed % 83)) % motion.blink_period < 5 || (gesture === 'blink' && active);
    const nod = gesture === 'nod' && active ? Math.sin((age / duration) * Math.PI * 2) * 7 : 0;
    const shake = gesture === 'shake_head' && active ? Math.sin((age / duration) * Math.PI * 4) * 8 : 0;
    const headAngle = (motion.head_angles[emotion] ?? 0) + Math.sin((frame + motion.head_phase) / 42) * 1.4 + nod;
    const phone = gesture === 'show_phone' && hold > 0;
    function draw(node: RigNode, key: string): React.ReactNode {
        const w = node.when;
        if (
            w &&
            ((w.emotion && !w.emotion.includes(emotion)) ||
                (w.mouth && !w.mouth.includes(mouth)) ||
                (w.blink !== undefined && w.blink !== blink) ||
                (w.phone !== undefined && w.phone !== phone))
        )
            return null;
        const props = { ...node.props };
        if (node.motion) {
            const angle = node.motion === 'head' ? headAngle : (motion[node.motion][gesture] ?? 0) * hold;
            props.transform = `rotate(${angle} ${node.pivot![0]} ${node.pivot![1]})`;
        }

        if (node.type === 'cutout') {
            const asset = node.asset!;
            const source = assets[`${asset.asset_id}@${asset.version}`];
            if (!source) throw Error('ASSET_MISSING: cutout layer');
            const target = node.target!;
            const clipId = `${prefix}-${key}`;
            return (
                <svg
                    key={key}
                    x={target[0]}
                    y={target[1]}
                    width={target[2]}
                    height={target[3]}
                    viewBox={node.box!.join(' ')}
                    preserveAspectRatio="none"
                    overflow="hidden"
                >
                    <defs>
                        <clipPath id={clipId}>
                            <polygon points={node.polygon!.map((p) => p.join(',')).join(' ')} />
                        </clipPath>
                    </defs>
                    <g clipPath={`url(#${clipId})`}>
                        <foreignObject x="0" y="0" width={node.source_width} height={node.source_height}>
                            <Img src={staticFile(source)} style={{ width: '100%', height: '100%' }} />
                        </foreignObject>
                    </g>
                </svg>
            );
        }
        const children = node.children?.map((child, index) => draw(child, `${key}-${index}`));
        switch (node.type) {
            case 'group':
                return (
                    <g key={key} {...props}>
                        {children}
                    </g>
                );
            case 'path':
                return <path key={key} {...props} />;
            case 'ellipse':
                return <ellipse key={key} {...props} />;
            case 'circle':
                return <circle key={key} {...props} />;
            case 'rect':
                return <rect key={key} {...props} />;
            default:
                throw Error('UNSUPPORTED_ACTION: rig node');
        }
    }
    return (
        <g transform={`translate(${shake},${Math.sin((frame + motion.body_phase) / 39) * 2})`}>
            {rig.layers.map((node, index) => draw(node, String(index)))}
        </g>
    );
}
