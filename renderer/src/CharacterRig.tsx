import React, { useId } from 'react';
import { Img, staticFile } from 'remotion';
import buyer from '../../styles/warm_editorial_2d/characters/buyer.rig.json';
import advisor from '../../styles/warm_editorial_2d/characters/advisor.rig.json';
import { type HeldProp, poseArms, ALKASH_ID } from './ArmKinematics';
import { ArmLayer, AttachmentLayer, FingerLayer } from './Arms';
import { seatedPugPose } from './SeatedPugMotion';
import { PropShape } from './Props';

export type RigNode = {
    type: 'group' | 'path' | 'ellipse' | 'circle' | 'rect' | 'cutout';
    props?: Record<string, string | number>;
    children?: RigNode[];
    motion?: 'head' | 'left_arm' | 'right_arm';
    channel?: 'gaze' | 'tongue' | 'mouth_open';
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
        profile?: 'seated_pug';
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
    age,
    heldProps = [],
    surfaceY
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
    heldProps?: HeldProp[];
    surfaceY?: number;
}) {
    const prefix = useId().replace(/:/g, '');
    if (!rig) throw Error('ASSET_MISSING: character rig');
    const tapping = gesture === 'tap_phone';
    const duration = tapping ? 60 : (rig.gestures[gesture] ?? 30);
    const active = age >= 0 && age < duration;
    const ramp = active ? Math.min(1, age / 12, (duration - age) / 12) : 0;
    const hold = ramp * ramp * (3 - 2 * ramp);
    const motion = rig.motion;
    const blink =
        (frame + motion.blink_phase + (seed % 83)) % motion.blink_period < 5 || (gesture === 'blink' && active);
    const nod = gesture === 'nod' && active ? Math.sin((age / duration) * Math.PI * 2) * 7 : 0;
    const shake = gesture === 'shake_head' && active ? Math.sin((age / duration) * Math.PI * 4) * 8 : 0;
    const headAngle = (motion.head_angles[emotion] ?? 0) + Math.sin((frame + motion.head_phase) / 42) * 1.4 + nod;
    const phone = heldProps.length === 0 && (gesture === 'show_phone' || tapping) && hold > 0;
    const tap = tapping && age >= 12 && age <= 24 ? Math.sin(((age - 12) / 12) * Math.PI) : 0;
    const isPug = motion.profile === 'seated_pug';
    const useArms = !isPug && (id === ALKASH_ID || heldProps.length > 0);
    const stashedItem = heldProps.find((item) => item.hand === 'left');
    const stashAge = stashedItem?.stashFrame === undefined ? undefined : frame - stashedItem.stashFrame;
    const pug = seatedPugPose(frame, gesture, age, mouth, stashAge);
    const stashClip = `${prefix}-haunch`;
    const items: HeldProp[] = [...heldProps];
    if (useArms && items.length === 0 && phone) {
        items.push({
            instance_id: 'legacy_phone',
            prop_id: 'phone',
            version: '1.0.0',
            character_id: id,
            hand: 'left',
            cue: tapping
                ? {
                      frame: frame - age,
                      action: {
                          kind: 'prop',
                          prop_action: 'tap',
                          contact_frame: frame - age + 18,
                          end_frame_exclusive: frame - age + 60
                      }
                  }
                : undefined
        });
    }
    const bodyY = Math.sin((frame + motion.body_phase) / 39) * 2;
    const posed = poseArms(
        items,
        frame,
        headAngle,
        gesture,
        hold,
        surfaceY === undefined ? undefined : surfaceY - bodyY
    );
    const armList = [posed.arms.left, posed.arms.right];
    function draw(node: RigNode, key: string): React.ReactNode {
        if (useArms && (node.motion === 'left_arm' || node.motion === 'right_arm')) return null;

        const w = node.when;
        if (
            w &&
            ((w.emotion && !w.emotion.includes(emotion)) ||
                (w.mouth && !w.mouth.includes(mouth)) ||
                (w.blink !== undefined && w.blink !== blink))
        )
            return null;
        const props = { ...node.props };
        if (w?.phone !== undefined) {
            const phoneOpacity = phone ? hold : 0;
            const visibility = w.phone ? phoneOpacity : 1 - phoneOpacity;
            if (visibility === 0) return null;

            props.opacity = Number(props.opacity ?? 1) * visibility;
        }

        if (node.motion) {
            const angle =
                node.motion === 'head'
                    ? headAngle
                    : (motion[node.motion][tapping ? 'show_phone' : gesture] ?? 0) * hold;
            props.transform = `rotate(${angle} ${node.pivot![0]} ${node.pivot![1]})`;
            if (isPug && (node.motion === 'left_arm' || node.motion === 'right_arm')) {
                const left = node.motion === 'left_arm';
                const pivot = node.pivot!;
                const tip = left ? [150 + pug.leftX, 530 + pug.leftY] : [242, 530 + pug.rightY];
                const restX = (left ? 150 : 242) - pivot[0];
                const restY = 530 - pivot[1];
                const dx = tip[0] - pivot[0];
                const dy = tip[1] - pivot[1];
                const rotation = ((Math.atan2(dy, dx) - Math.atan2(restY, restX)) * 180) / Math.PI;
                const length = Math.hypot(dx, dy) / Math.hypot(restX, restY);
                props.transform = `translate(${pivot[0]} ${pivot[1]}) rotate(${rotation}) scale(${length}) translate(${-pivot[0]} ${-pivot[1]})`;
            }
        }

        if (isPug && node.channel) {
            if (node.channel === 'gaze') {
                props.transform = `translate(${pug.gazeX} 0)`;
            } else {
                const amount = node.channel === 'tongue' ? pug.tongue : pug.opening;
                if (amount <= 0) return null;

                const pivot = node.pivot!;
                props.transform = `translate(${pivot[0]} ${pivot[1]}) scale(1 ${amount}) translate(${-pivot[0]} ${-pivot[1]})`;
            }
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
                    {...props}
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
                        {node.when?.phone && tapping && tap > 0 && (
                            <g transform={'translate(0 ' + (1 - tap) * 22 + ')'}>
                                <path
                                    d="M29 47 L29 78 Q29 88 39 88 L47 88"
                                    fill="none"
                                    stroke="#e4a77a"
                                    strokeWidth="10"
                                    strokeLinecap="round"
                                />
                                {age >= 18 && age <= 21 && (
                                    <circle cx="29" cy="43" r="9" fill="none" stroke="#ffffff" strokeWidth="3" />
                                )}
                            </g>
                        )}
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
        <g transform={`translate(${shake},${bodyY})`}>
            {useArms && armList.map((arm) => <ArmLayer key={arm.hand} arm={arm} part="back" />)}
            {isPug && (
                <defs>
                    <clipPath id={stashClip}>
                        <path
                            d="M-120 -50 H500 V600 H-120 Z M20 600 V500 Q18 450 55 427 Q100 413 125 460 Q145 500 130 540 V600 Z"
                            clipRule="evenodd"
                            fillRule="evenodd"
                        />
                    </clipPath>
                </defs>
            )}
            {rig.layers.map((node, index) => {
                if (!isPug || node.motion !== 'left_arm') return draw(node, String(index));

                return (
                    <g
                        key={index}
                        clipPath={
                            stashAge !== undefined && stashAge >= 0 && stashAge < 135 ? `url(#${stashClip})` : undefined
                        }
                    >
                        {stashedItem && pug.stash.objectOpacity > 0 && (
                            <g
                                transform={`translate(${150 + pug.stash.objectX} ${530 + pug.stash.objectY}) scale(0.65)`}
                            >
                                <PropShape id={stashedItem.prop_id} frame={frame} view={stashedItem.view} />
                            </g>
                        )}
                        {draw(node, String(index))}
                    </g>
                );
            })}
            {useArms && (
                <>
                    {armList.map((arm) => (
                        <ArmLayer key={arm.hand} arm={arm} part="front" />
                    ))}
                    {armList.map((arm) => (
                        <AttachmentLayer key={arm.hand} arm={arm} frame={frame} />
                    ))}
                    {armList.map((arm) => (
                        <FingerLayer key={arm.hand} arm={arm} />
                    ))}
                </>
            )}
        </g>
    );
}
