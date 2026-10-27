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
    const isAlkash = id === ALKASH_ID;
    const blinkFrame = (frame + motion.blink_phase + (seed % 83)) % motion.blink_period;
    const eyelidClose =
        gesture === 'blink' && active ? hold : blinkFrame < 10 ? Math.sin((blinkFrame / 10) * Math.PI) : 0;
    const armSway =
        5 * Math.sin(frame / 31) * Math.max(0, Math.sin(frame / 57)) +
        (emotion === 'annoyed' ? 10 : 0) -
        (gesture === 'show_phone' ? 16 * hold : 0);
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
        surfaceY === undefined ? undefined : surfaceY - bodyY,
        isAlkash ? armSway : 0
    );
    const armList = [posed.arms.left, posed.arms.right];
    function draw(node: RigNode, key: string): React.ReactNode {
        if (useArms && (node.motion === 'left_arm' || node.motion === 'right_arm')) return null;

        // Replace legacy mouth and eyelid shapes in the Alkash head with anchored facial features.
        if (isAlkash && (node.when?.mouth || node.when?.blink !== undefined)) return null;

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
                return (
                    <g key={key}>
                        <ellipse cx={159} cy={145} rx={19} ry={14} fill="#fff4dc" />
                        <ellipse cx={253} cy={128} rx={17} ry={14} fill="#fff4dc" />
                        {[
                            { x: 169, y: 145 },
                            { x: 243, y: 128 }
                        ].map((eye, i) => (
                            <g key={i} transform={`translate(${pug.gazeX * 0.5} 0)`}>
                                <ellipse
                                    cx={eye.x}
                                    cy={eye.y}
                                    rx={8}
                                    ry={12}
                                    fill="#794322"
                                    stroke="#35231b"
                                    strokeWidth={2}
                                />
                                <ellipse cx={eye.x} cy={eye.y} rx={3.5} ry={9} fill="#201a17" />
                                <circle cx={eye.x - 2} cy={eye.y - 5} r={2} fill="#fff6e4" />
                            </g>
                        ))}
                    </g>
                );
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
                        {isAlkash && node.motion === 'head' && (
                            <g>
                                {[
                                    { x: 165, y: 116 },
                                    { x: 233, y: 112 }
                                ].map((eye, i) => (
                                    <g key={i}>
                                        <ellipse
                                            cx={eye.x}
                                            cy={eye.y}
                                            rx={23}
                                            ry={17}
                                            fill="#fff5e3"
                                            stroke="#282125"
                                            strokeWidth={2.5}
                                        />
                                        <ellipse
                                            cx={eye.x + Math.sin(frame / 65) * 5}
                                            cy={eye.y + 2}
                                            rx={7}
                                            ry={11}
                                            fill="#282125"
                                        />
                                        <path
                                            d={`M${eye.x - 24} ${eye.y - 22} Q${eye.x - 24} ${eye.y - 28} ${eye.x - 18} ${eye.y - 28} Q${eye.x} ${eye.y - 31} ${eye.x + 18} ${eye.y - 28} Q${eye.x + 24} ${eye.y - 28} ${eye.x + 24} ${eye.y - 22} L${eye.x + 23} ${eye.y - 2 + eyelidClose * 19} Q${eye.x} ${eye.y + 6 + eyelidClose * 16} ${eye.x - 23} ${eye.y - 2 + eyelidClose * 19} Z`}
                                            fill="#e6b38b"
                                            stroke="#282125"
                                            strokeWidth={2.5}
                                        />
                                    </g>
                                ))}
                                {mouth > 0 && (
                                    <g>
                                        <ellipse
                                            cx={209}
                                            cy={199}
                                            rx={21}
                                            ry={mouth === 2 ? 13 : mouth === 3 ? 10 : 7}
                                            fill="#48171b"
                                            stroke="#261f22"
                                            strokeWidth={2}
                                        />
                                        <g
                                            transform={`translate(0 ${mouth === 2 ? 187 : mouth === 3 ? 190 : 193}) scale(1 0.68) translate(0 ${-(mouth === 2 ? 187 : mouth === 3 ? 190 : 193)})`}
                                        >
                                            <path
                                                d={`M205 ${mouth === 2 ? 187 : mouth === 3 ? 190 : 193} H216 V200 Q211 204 205 200 Z`}
                                                fill="#e9b93e"
                                                stroke="#8a5d16"
                                                strokeWidth={1}
                                            />
                                            <path
                                                d={`M207 ${mouth === 2 ? 189 : mouth === 3 ? 192 : 195} V199`}
                                                stroke="#fff1a1"
                                                strokeWidth={2}
                                            />
                                        </g>
                                    </g>
                                )}
                            </g>
                        )}
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
            {isAlkash && (
                <defs>
                    <clipPath id={`${prefix}-shirt`}>
                        <path d="M0 0 H380 V438 Q190 450 0 438 Z" />
                    </clipPath>
                </defs>
            )}
            {isPug && (
                <defs>
                    <clipPath id={`${prefix}-floor-phone`}>
                        <rect x={0} y={542} width={380} height={100} />
                    </clipPath>
                </defs>
            )}
            {isAlkash && (
                <g fill="#4c4540" stroke="#282125" strokeWidth={4}>
                    <path d="M83 425 Q190 412 302 425 L310 560 H195 L188 477 L177 560 H72 Z" />
                    <path d="M188 447 V477" fill="none" />
                </g>
            )}
            {rig.layers.map((node, index) => {
                if (isAlkash && node.type === 'cutout' && node.target?.[1] === 159.3) {
                    return (
                        <g key={index} clipPath={`url(#${prefix}-shirt)`}>
                            {draw(node, String(index))}
                            <path
                                d="M86 429 Q123 418 146 433 Q190 421 225 433 Q260 419 296 429 L296 441 Q190 453 86 441 Z"
                                fill="#9a8964"
                                opacity={0.24}
                            />
                            <path
                                d="M91 426 Q123 419 150 429 M164 431 Q194 423 221 430 M239 428 Q265 420 290 426"
                                fill="none"
                                stroke="#fff7dd"
                                opacity={0.6}
                                strokeWidth={3}
                                strokeLinecap="round"
                            />
                            <path
                                d="M101 408 Q117 424 141 432 M279 407 Q264 426 244 433 M155 418 Q171 433 188 436"
                                fill="none"
                                stroke="#b2a17b"
                                opacity={0.7}
                                strokeWidth={1.8}
                                strokeLinecap="round"
                            />
                            <path d="M86 437 Q190 450 296 437" fill="none" stroke="#6c6250" strokeWidth={3} />
                        </g>
                    );
                }

                if (!isPug || node.motion !== 'left_arm') return draw(node, String(index));

                return (
                    <g key={index}>
                        {stashedItem && pug.stash.objectOpacity > 0 && (
                            <g clipPath={`url(#${prefix}-floor-phone)`}>
                                <g
                                    transform={`translate(${150 + pug.stash.objectX} ${530 + pug.stash.objectY}) ${
                                        stashedItem.prop_id === 'phone'
                                            ? 'scale(0.9 0.65) rotate(90) translate(28 0)'
                                            : 'scale(0.65)'
                                    }`}
                                >
                                    <PropShape id={stashedItem.prop_id} frame={frame} view={stashedItem.view} />
                                </g>
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
