import type { Cue, PropInstance } from './types';

export type Point = { x: number; y: number };
export type HeldProp = PropInstance & { cue?: Cue; opened?: boolean; stashFrame?: number };
export type Hand = 'left' | 'right';
export type Grip = 'relaxed' | 'pinch' | 'handle' | 'wrap' | 'support' | 'point';
export const ALKASH_ID = 'content_ed869c2c29264425b7f870a8340935fb';
export const armProfile = {
    upper: 100,
    lower: 140,
    shoulders: { left: { x: 105, y: 265 }, right: { x: 265, y: 265 } },
    rest: { left: { x: 125, y: 450 }, right: { x: 250, y: 450 } },
    mouth: { x: 209, y: 199 },
    headPivot: { x: 190, y: 220 },
    surfaceY: 490
};
export const propAnchors: Record<
    PropInstance['prop_id'],
    { grip: Point; contact: Point; tap: Point; base: Point; type: Grip }
> = {
    phone: {
        grip: { x: 19, y: 0 },
        contact: { x: 0, y: -30 },
        tap: { x: 0, y: -30 },
        base: { x: 0, y: 10 },
        type: 'wrap'
    },
    tablet: {
        grip: { x: 49, y: 0 },
        contact: { x: 18, y: -32 },
        tap: { x: 18, y: -32 },
        base: { x: 0, y: 12 },
        type: 'support'
    },
    cigarette: {
        grip: { x: 4, y: 3 },
        contact: { x: -12, y: 0 },
        tap: { x: -12, y: 0 },
        base: { x: 0, y: 4 },
        type: 'pinch'
    },
    beer_mug: {
        grip: { x: 35, y: -25 },
        contact: { x: -16, y: -58 },
        tap: { x: -16, y: -58 },
        base: { x: 0, y: 11 },
        type: 'handle'
    },
    beer_can: {
        grip: { x: 19, y: -23 },
        contact: { x: -14, y: -61 },
        tap: { x: 0, y: -61 },
        base: { x: 0, y: 11 },
        type: 'wrap'
    },
    bottle: {
        grip: { x: 19, y: -20 },
        contact: { x: 0, y: -86 },
        tap: { x: 0, y: -89 },
        base: { x: 0, y: 9 },
        type: 'wrap'
    }
};
export const add = (a: Point, b: Point): Point => ({ x: a.x + b.x, y: a.y + b.y });
export const sub = (a: Point, b: Point): Point => ({ x: a.x - b.x, y: a.y - b.y });
export const distance = (a: Point, b: Point) => Math.hypot(a.x - b.x, a.y - b.y);
export function rotate(p: Point, degrees: number): Point {
    const a = (degrees * Math.PI) / 180;
    return { x: p.x * Math.cos(a) - p.y * Math.sin(a), y: p.x * Math.sin(a) + p.y * Math.cos(a) };
}
const mix = (a: Point, b: Point, t: number): Point => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
const ease = (t: number) => t * t * (3 - 2 * t);

// Analytic two-bone IK. Unreachable targets are clamped, never stretched.
// A fixed bend sign per arm avoids frame-history-dependent elbow flips.
export function solveArm(
    shoulder: Point,
    target: Point,
    hand: Hand,
    upper = armProfile.upper,
    lower = armProfile.lower
) {
    const delta = sub(target, shoulder);
    const raw = Math.hypot(delta.x, delta.y);
    const d = Math.max(Math.max(16, Math.abs(upper - lower) + 0.01), Math.min(upper + lower - 0.01, raw));
    const unit = raw > 0.00001 ? { x: delta.x / raw, y: delta.y / raw } : { x: 0, y: 1 };
    const along = (upper * upper - lower * lower + d * d) / (2 * d);
    const height = Math.sqrt(Math.max(0, upper * upper - along * along));
    const bend = hand === 'left' ? 1 : -1;
    const elbow = {
        x: shoulder.x + unit.x * along - unit.y * height * bend,
        y: shoulder.y + unit.y * along + unit.x * height * bend
    };
    const wrist = { x: shoulder.x + unit.x * d, y: shoulder.y + unit.y * d };
    return { shoulder, elbow, wrist, limited: Math.abs(raw - d) > 0.02 };
}
export function actionPhase(item: HeldProp, frame: number) {
    const cue = item.cue;
    if (!cue) return { amount: 0, contact: false, active: false };
    const contact = cue.action.contact_frame ?? cue.frame + 18;
    const end = cue.action.end_frame_exclusive ?? cue.frame + 90;
    const active = frame >= cue.frame && frame < end;
    const ramp = !active
        ? 0
        : frame < contact
          ? (frame - cue.frame) / Math.max(1, contact - cue.frame)
          : Math.min(1, (end - frame) / 18);
    return {
        amount: ease(Math.max(0, Math.min(1, ramp))),
        active,
        contact: active && frame >= contact && frame < contact + 6
    };
}
export type PlacedProp = {
    item: HeldProp;
    origin: Point;
    angle: number;
    flip: number;
    grip: Point;
    contactPoint: Point;
    tapPoint: Point;
    basePoint: Point;
    phase: ReturnType<typeof actionPhase>;
};
export type ArmPose = ReturnType<typeof solveArm> & {
    hand: Hand;
    grip: Grip;
    angle: number;
    front: boolean;
    item?: PlacedProp;
    tip?: Point;
};
export function localToWorld(prop: Pick<PlacedProp, 'origin' | 'angle' | 'flip'>, p: Point): Point {
    return add(prop.origin, rotate({ x: p.x * prop.flip, y: p.y }, prop.angle));
}
export function poseArms(
    items: HeldProp[],
    frame: number,
    headAngle = 0,
    gesture = 'idle',
    gestureAmount = 0,
    surfaceY = armProfile.surfaceY,
    handSway = 0
) {
    const mouth = add(armProfile.headPivot, rotate(sub(armProfile.mouth, armProfile.headPivot), headAngle));
    const arms = {} as Record<Hand, ArmPose>;
    for (const hand of ['left', 'right'] as const) {
        const item = items.find((p) => p.hand === hand);
        let target = add(armProfile.rest[hand], { x: 0, y: handSway });
        let prop: PlacedProp | undefined;
        let grip: Grip = 'relaxed';
        let angle = 0;
        if (item) {
            const anchors = propAnchors[item.prop_id];
            const phase = actionPhase(item, frame);
            const action = phase.active ? item.cue?.action.prop_action : undefined;
            const flip = hand === 'left' && !['phone', 'tablet'].includes(item.prop_id) ? -1 : 1;
            const localGrip = {
                ...anchors.grip,
                x: ['phone', 'tablet'].includes(item.prop_id) && hand === 'left' ? -anchors.grip.x : anchors.grip.x
            };
            angle =
                action === 'sip'
                    ? (hand === 'right' ? 1 : -1) * (item.prop_id === 'beer_mug' ? 45 : 20) * phase.amount
                    : 0;
            const rest =
                item.prop_id === 'tablet'
                    ? { x: hand === 'left' ? 125 : 255, y: 410 + handSway }
                    : { x: hand === 'left' ? 125 : 255, y: 410 + handSway };
            const gripOffset = rotate({ x: localGrip.x * flip, y: localGrip.y }, angle);
            let origin = sub(rest, gripOffset);
            if (action === 'sip' || action === 'puff') {
                const contactOffset = rotate({ x: anchors.contact.x * flip, y: anchors.contact.y }, angle);
                origin = mix(origin, sub(mouth, contactOffset), phase.amount);
                origin.x += (hand === 'right' ? -60 : 60) * 4 * phase.amount * (1 - phase.amount);
            }
            if (action === 'place') {
                // Approach the surface from above; the prop base lands exactly at contact.
                origin = mix(origin, { x: hand === 'left' ? 110 : 255, y: surfaceY - anchors.base.y }, phase.amount);
            }
            target = add(origin, gripOffset);
            const solved = solveArm(armProfile.shoulders[hand], target, hand);
            // Move the whole attachment with the solved wrist if a target is out of reach.
            origin = add(origin, sub(solved.wrist, target));
            const transform = { origin, angle, flip };
            prop = {
                item,
                ...transform,
                grip: localGrip,
                phase,
                contactPoint: localToWorld(transform, anchors.contact),
                tapPoint: localToWorld(transform, anchors.tap),
                basePoint: localToWorld(transform, anchors.base)
            };
            grip = anchors.type;
        } else if (gesture === 'raise_hand' || gesture === 'shrug') {
            target = mix(
                target,
                { x: hand === 'left' ? 60 : 310, y: gesture === 'raise_hand' ? 225 : 325 },
                gestureAmount
            );
        }
        arms[hand] = {
            ...solveArm(armProfile.shoulders[hand], target, hand),
            hand,
            grip,
            angle,
            front: !!item || gestureAmount > 0,
            item: prop
        };
    }
    for (const hand of ['left', 'right'] as const) {
        const owner = arms[hand];
        const prop = owner.item;
        if (!prop) {
            continue;
        }
        const other = hand === 'left' ? 'right' : 'left';
        if (arms[other].item) {
            continue;
        }
        const action = prop.phase.active ? prop.item.cue?.action.prop_action : undefined;
        const tablet = prop.item.prop_id === 'tablet';
        if (tablet || action === 'tap' || action === 'open') {
            const support = localToWorld(prop, { x: hand === 'right' ? -49 : 49, y: 0 });
            const rest = tablet ? support : armProfile.rest[other];
            const tipOffset = { x: other === 'left' ? 25 : -25, y: -12 };
            const target =
                action === 'tap' || action === 'open'
                    ? mix(rest, sub(prop.tapPoint, tipOffset), prop.phase.amount)
                    : rest;
            if (action === 'open') {
                const contact = prop.item.cue!.action.contact_frame ?? prop.item.cue!.frame + 18;
                const pull = ease(Math.max(0, Math.min(1, (frame - contact) / 12))) * prop.phase.amount;
                target.x += (other === 'left' ? -18 : 18) * pull;
                target.y -= 20 * pull;
            }

            const solved = solveArm(armProfile.shoulders[other], target, other);
            arms[other] = {
                ...solved,
                hand: other,
                grip: action === 'open' ? 'pinch' : action === 'tap' ? 'point' : 'support',
                angle: 0,
                front: true,
                tip: add(solved.wrist, tipOffset)
            };
        }
    }
    return { arms, mouth };
}
