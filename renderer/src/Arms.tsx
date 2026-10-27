import React, { useId } from 'react';
import { PropShape } from './Props';
import { type ArmPose, type Point, type PlacedProp } from './ArmKinematics';
const skin = '#fac38f';
const outline = '#25171a';
const point = (p: Point) => `${p.x} ${p.y}`;

function Limb({ points, frontOnly = false }: { points: Point[]; frontOnly?: boolean }) {
    const clipId = useId().replace(/:/g, '') + '-forearm';
    const e = points[1],
        w = points[2];
    const length = Math.hypot(w.x - e.x, w.y - e.y);
    const u = { x: (w.x - e.x) / length, y: (w.y - e.y) / length };
    const corner = (t: number, side: number) => `${e.x + u.x * t - u.y * side},${e.y + u.y * t + u.x * side}`;
    const clip = [corner(-26, -25), corner(length + 35, -25), corner(length + 35, 25), corner(-26, 25)].join(' ');
    const d = `M${points.map(point).join(' L')}`;
    const a = points[0],
        b = points[1];
    const middle = { x: a.x * 0.55 + b.x * 0.45, y: a.y * 0.55 + b.y * 0.45 };
    const angle = (Math.atan2(b.y - a.y, b.x - a.x) * 180) / Math.PI;
    return (
        <g>
            <defs>
                <clipPath id={clipId}>
                    <polygon points={clip} />
                </clipPath>
            </defs>
            <g clipPath={frontOnly ? `url(#${clipId})` : undefined}>
                <path
                    d={d}
                    fill="none"
                    stroke={outline}
                    strokeWidth={35}
                    strokeLinejoin="round"
                    strokeLinecap="round"
                />
                <path d={d} fill="none" stroke={skin} strokeWidth={28} strokeLinejoin="round" strokeLinecap="round" />
                <g
                    transform={`translate(${middle.x} ${middle.y}) rotate(${angle})`}
                    stroke="#b98459"
                    strokeWidth={1.5}
                    strokeLinecap="round"
                    fill="none"
                >
                    <path d="M-8 -3 l7 -1 M6 4 l6 -1" />
                </g>
                {points.length === 3 &&
                    (() => {
                        const e = points[1],
                            w = points[2];
                        const a = Math.atan2(points[0].y - e.y, points[0].x - e.x);
                        const b = Math.atan2(w.y - e.y, w.x - e.x);
                        if (Math.cos(a - b) < 0.5) return null;
                        const dx = Math.cos(a) + Math.cos(b),
                            dy = Math.sin(a) + Math.sin(b);
                        const length = Math.hypot(dx, dy);
                        return (
                            <path
                                d={`M${e.x + (dx / length) * 14} ${e.y + (dy / length) * 14} l${(dx / length) * 15} ${(dy / length) * 15}`}
                                stroke="#a37350"
                                strokeWidth={1.7}
                                strokeLinecap="round"
                            />
                        );
                    })()}
            </g>
        </g>
    );
}
function Palm({ arm }: { arm: ArmPose }) {
    const angle = arm.item?.angle ?? (arm.grip === 'point' ? 0 : arm.hand === 'left' ? -12 : 12);
    return (
        <g transform={`translate(${arm.wrist.x} ${arm.wrist.y}) rotate(${angle})`}>
            <ellipse rx={13} ry={17} fill={skin} />
        </g>
    );
}
function PropFingers({ prop }: { prop: PlacedProp }) {
    const g = prop.grip;
    const type = prop.item.prop_id;
    // Shapes are authored in the same local coordinates as the attachment.
    // The palm is behind the prop; only the fingers and thumb overlap its surface.
    return (
        <g
            transform={`translate(${prop.origin.x} ${prop.origin.y}) rotate(${prop.angle}) scale(${prop.flip} 1)`}
            fill={skin}
            stroke={outline}
            strokeWidth={2}
            strokeLinecap="round"
            strokeLinejoin="round"
        >
            {type === 'cigarette' ? (
                <g>
                    <path d="M-7 10 Q-12 2 -4 -2 L12 -2 Q17 -1 15 3 L1 5 L11 6 Q16 8 12 12 Z" />
                    <path d="M1 5 L10 4" fill="none" />
                </g>
            ) : type === 'beer_mug' ? (
                <g>
                    <path d="M29 -34 Q44 -39 43 -30 L37 -29 L43 -25 Q44 -20 38 -19 L42 -15 Q39 -9 30 -15 L26 -22 Z" />
                    <path d="M33 -29 h7 M32 -22 h7" fill="none" />
                </g>
            ) : type === 'tablet' ? (
                <g transform={`translate(${g.x} ${g.y}) scale(${g.x < 0 ? -1 : 1} 1)`}>
                    <path d="M-8 10 L-10 -7 Q-10 -14 -4 -14 Q1 -14 1 -8 L3 4 Q12 0 12 8 Q10 15 -1 16 Z" />
                </g>
            ) : (
                <g transform={`translate(${g.x} ${g.y}) scale(${g.x < 0 ? -1 : 1} 1)`}>
                    <path d="M8 -13 Q-2 -22 -9 -12 L-17 -5 Q-20 0 -15 3 L-7 -1 L-15 5 Q-17 10 -11 12 L-3 8 Q1 18 9 10 Z" />
                    <path d="M-7 -1 L1 -7 M-3 8 L3 3" fill="none" />
                </g>
            )}
        </g>
    );
}
function FreeFingers({ arm }: { arm: ArmPose }) {
    const sign = arm.hand === 'left' ? 1 : -1;
    return (
        <g
            transform={`translate(${arm.wrist.x} ${arm.wrist.y}) scale(${sign} 1)`}
            fill={skin}
            stroke={outline}
            strokeWidth={2}
            strokeLinejoin="round"
        >
            {arm.grip === 'pinch' ? (
                <g>
                    <path d="M-9 10 Q-12 -5 -5 -10 L21 -16 Q25 -16 25 -12 L8 -7 L23 -5 Q28 -1 22 3 L8 4 L11 11 Q2 20 -9 10 Z" />
                    <path d="M-1 5 L5 8" fill="none" />
                </g>
            ) : arm.grip === 'point' ? (
                <g>
                    <path d="M-9 10 Q-12 -5 -5 -10 L21 -16 Q25 -16 25 -12 Q25 -8 21 -8 L7 -5 L14 -1 Q19 5 13 12 L2 16 Z" />
                    <path d="M2 2 L8 5 M0 7 L6 10" fill="none" />
                </g>
            ) : (
                <g>
                    <path d="M-11 -5 Q-12 -17 -5 -17 L5 -12 Q16 -14 16 -4 L13 13 Q9 22 -1 18 L-9 12 Z" />
                    <path d="M-4 1 V11 M2 2 V13 M8 1 V10" fill="none" />
                </g>
            )}
        </g>
    );
}
export function ArmLayer({ arm, part }: { arm: ArmPose; part: 'back' | 'front' }) {
    if (part === 'back') {
        return arm.front ? null : <Limb points={[arm.shoulder, arm.elbow, arm.wrist]} />;
    }
    return (
        <g>
            <Limb points={[arm.shoulder, arm.elbow, arm.wrist]} frontOnly={!arm.front} />
            <Palm arm={arm} />
        </g>
    );
}
export function AttachmentLayer({ arm, frame }: { arm: ArmPose; frame: number }) {
    const prop = arm.item;
    if (!prop) return null;
    return (
        <g transform={`translate(${prop.origin.x} ${prop.origin.y}) rotate(${prop.angle}) scale(${prop.flip} 1)`}>
            <PropShape
                id={prop.item.prop_id}
                frame={frame}
                active={prop.phase.contact}
                opened={prop.item.opened || prop.item.cue?.action.prop_action === 'sip'}
                view={prop.item.view}
            />
        </g>
    );
}
export function FingerLayer({ arm }: { arm: ArmPose }) {
    return arm.item ? <PropFingers prop={arm.item} /> : <FreeFingers arm={arm} />;
}
