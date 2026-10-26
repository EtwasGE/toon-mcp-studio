import React from 'react';
import type { Cue, PropInstance } from './types';
import { Tablet } from './Tablet';

export type HeldProp = PropInstance & { cue?: Cue; opened?: boolean };

export function PropShape({
    id,
    frame,
    active = false,
    opened = false,
    view = 'screen'
}: {
    id: PropInstance['prop_id'];
    frame: number;
    active?: boolean;
    opened?: boolean;
    view?: 'screen' | 'back';
}) {
    const outline = { stroke: '#2c2628', strokeWidth: 3, strokeLinejoin: 'round' as const };
    if (id === 'tablet') {
        return <Tablet view={view} pulse={active} />;
    }
    if (id === 'phone') {
        return (
            <g {...outline}>
                <rect x={-22} y={-65} width={44} height={75} rx={7} fill="#353b43" />
                <rect x={-15} y={-51} width={30} height={35} fill={active ? '#e7f8ab' : '#aacb9a'} />
                <circle cx={0} cy={-5} r={5} fill="#697279" />
            </g>
        );
    }
    if (id === 'cigarette') {
        return (
            <g>
                <g transform="rotate(-25)" {...outline}>
                    <path d="M-8 -6 H40 V4 H-8 Z" fill="#f3e5c9" />
                    <path d="M-8 -6 H5 V4 H-8 Z" fill="#ba855b" />
                    <path d="M40 -6 H47 V4 H40 Z" fill={active ? '#f7a04d' : '#b96043'} />
                </g>
                {[0, 1, 2].map((i) => {
                    const t = ((frame + i * 23) % 90) / 90;
                    return (
                        <path
                            key={i}
                            d={`M40 ${-20 - t * 60} q${Math.sin(t * 6 + i) * 12} -12 0 -24 t4 -20`}
                            fill="none"
                            stroke="#d0d0cb"
                            strokeWidth={2 + t * 3}
                            opacity={(1 - t) * 0.65}
                        />
                    );
                })}
            </g>
        );
    }
    if (id === 'beer_mug') {
        return (
            <g {...outline}>
                <path d="M20 -43 H35 Q47 -43 47 -27 Q47 -11 35 -11 H20" fill="none" strokeWidth={7} />
                <rect x={-23} y={-57} width={47} height={68} rx={5} fill="#e5b444" />
                <path d="M-13 -38 V0 M0 -38 V0 M12 -38 V0" stroke="#f4d88a" />
                <path d="M-24 -51 Q-29 -66 -15 -65 Q-9 -77 2 -66 Q14 -74 21 -62 Q31 -60 24 -49 Z" fill="#fff4d8" />
            </g>
        );
    }
    if (id === 'beer_can') {
        return (
            <g {...outline}>
                <rect x={-22} y={-63} width={44} height={74} rx={7} fill="#80a06c" />
                <path d="M-21 -44 H21 V-13 H-21 Z" fill="#f0dfab" />
                <ellipse cy={-61} rx={21} ry={5} fill="#c9ccbe" />
                <path d="M-4 -63 L6 -59 L2 -55 L-6 -59 Z" fill={opened ? '#333333' : '#999f95'} />
                <path d="M-11 -33 H11 M-11 -25 H8" />
            </g>
        );
    }
    return (
        <g {...outline}>
            <path d="M-9 -85 H9 V-52 Q24 -43 24 -30 V9 H-24 V-30 Q-24 -43 -9 -52 Z" fill="#47784d" />
            {!opened && <rect x={-10} y={-89} width={20} height={9} rx={2} fill="#c8ad65" />}
            <path d="M-22 -26 H22 V-2 H-22 Z" fill="#d9d2a9" />
            <path d="M-12 -43 V-30" stroke="#9db480" />
        </g>
    );
}

export function HeldPropLayer({
    item,
    frame,
    shoulder,
    part
}: {
    item: HeldProp;
    frame: number;
    shoulder: number[];
    part: 'arm' | 'prop';
}) {
    const cue = item.cue;
    const active = cue !== undefined && frame >= cue.frame && frame < (cue.action.end_frame_exclusive ?? cue.frame);
    const contact = cue?.action.contact_frame ?? 0;
    const start = cue?.frame ?? 0;
    const end = cue?.action.end_frame_exclusive ?? 0;
    const ramp = !active
        ? 0
        : frame <= contact
          ? Math.min(1, (frame - start) / Math.max(1, contact - start))
          : Math.min(1, (end - frame) / 18);
    const amount = ramp * ramp * (3 - 2 * ramp);
    const action = cue?.action.prop_action;
    const drinking = action === 'sip' || action === 'puff';
    const left = item.hand === 'left';
    const tablet = item.prop_id === 'tablet';
    const restX = tablet ? (left ? 125 : 255) : left ? 65 : 315;
    const targetX = action === 'puff' ? 170 : 220;
    const lift = action === 'puff' ? 130 : item.prop_id === 'bottle' ? 65 : 85;
    const x = restX + (drinking ? (targetX - restX) * amount : 0);
    const y = (tablet ? 400 : 345) - (drinking ? lift : action === 'place' ? -195 : 12) * amount;
    const elbowX = left ? 35 : 345;
    const arm = `M${shoulder[0]} ${shoulder[1]} Q${elbowX} 320 ${elbowX} 375`;
    if (part === 'arm') {
        return (
            <g>
                <path
                    d={arm}
                    fill="none"
                    stroke="#25171a"
                    strokeWidth={38}
                    strokeLinecap="round"
                    strokeLinejoin="round"
                />
                <path
                    d={arm}
                    fill="none"
                    stroke="#fac28e"
                    strokeWidth={30}
                    strokeLinecap="round"
                    strokeLinejoin="round"
                />
            </g>
        );
    }
    return (
        <g>
            <path d={`M${elbowX} 375 L${x} ${y}`} fill="none" stroke="#25171a" strokeWidth={38} strokeLinecap="round" />
            <path d={`M${elbowX} 375 L${x} ${y}`} fill="none" stroke="#fac28e" strokeWidth={30} strokeLinecap="round" />
            <g transform={`translate(${x} ${y}) rotate(${action === 'sip' ? -8 * amount : 0})`}>
                <PropShape
                    id={item.prop_id}
                    frame={frame}
                    active={active && frame >= contact && (!tablet || frame < contact + 6)}
                    opened={item.opened}
                    view={item.view}
                />
                <path d="M-17 -2 Q-3 -9 6 1 L4 12 Q-10 17 -17 8 Z" fill="#fac28e" stroke="#2c2628" strokeWidth={2.5} />
                {action === 'tap' && active && (
                    <path
                        d={
                            tablet
                                ? `M4 8 Q38 -2 ${35 - 17 * amount} ${-12 - 20 * amount}`
                                : `M32 8 L${amount > 0.95 ? 10 : 25} -7`
                        }
                        stroke="#fac28e"
                        strokeWidth={8}
                        strokeLinecap="round"
                        fill="none"
                    />
                )}
            </g>
        </g>
    );
}
