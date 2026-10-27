import fs from 'node:fs';
import assert from 'node:assert/strict';
import ts from 'typescript';
import vm from 'node:vm';
const source = fs.readFileSync(new URL('../src/ArmKinematics.ts', import.meta.url), 'utf8');
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const context = { exports: {} };
vm.runInNewContext(code, context);
const { poseArms, solveArm, armProfile, propAnchors, distance, localToWorld } = context.exports;
const close = (a, b, message) => assert.ok(Math.abs(a - b) < 0.025, `${message}: ${a} vs ${b}`);
let frames = 0;
for (const hand of ['left', 'right']) {
    for (const [id, actions] of Object.entries({
        phone: ['tap', 'notification'],
        tablet: ['tap'],
        cigarette: ['puff'],
        beer_mug: ['sip', 'place'],
        beer_can: ['open', 'sip'],
        bottle: ['open', 'sip', 'place']
    })) {
        for (const action of actions) {
            let previous;
            for (let frame = 0; frame < 100; frame++) {
                const item = {
                    instance_id: 'prop',
                    prop_id: id,
                    hand,
                    cue: { frame: 0, action: { prop_action: action, contact_frame: 18, end_frame_exclusive: 90 } }
                };
                const headAngle = Math.sin(frame / 42) * 1.4;
                const pose = poseArms([item], frame, headAngle);
                for (const arm of Object.values(pose.arms)) {
                    close(distance(arm.shoulder, arm.elbow), armProfile.upper, 'upper length');
                    close(distance(arm.elbow, arm.wrist), armProfile.lower, 'lower length');
                    assert.ok(Number.isFinite(arm.elbow.x));
                    if (arm.item) close(distance(localToWorld(arm.item, arm.item.grip), arm.wrist), 0, 'grip attached');
                }
                if (frame === 18) {
                    const p = pose.arms[hand].item;
                    if (['sip', 'puff'].includes(action))
                        close(distance(p.contactPoint, pose.mouth), 0, `${id} mouth contact ${hand}`);
                    if (action === 'place') close(p.basePoint.y, armProfile.surfaceY, `${id} surface contact ${hand}`);
                    if (['tap', 'open'].includes(action))
                        close(
                            distance(pose.arms[hand === 'left' ? 'right' : 'left'].tip, p.tapPoint),
                            0,
                            `${id} assisting finger contact ${hand}`
                        );
                }
                if (previous && frame < 90) {
                    for (const side of ['left', 'right'])
                        assert.ok(
                            distance(pose.arms[side].elbow, previous.arms[side].elbow) < 32,
                            `${id} elbow jump at ${frame}`
                        );
                }
                previous = pose;
                frames++;
            }
        }
    }
}
for (const hand of ['left', 'right']) {
    for (const target of [
        { x: 10000, y: -2000 },
        { x: 95, y: 275 },
        { x: 0, y: 0 }
    ]) {
        const arm = solveArm(armProfile.shoulders[hand], target, hand);
        close(distance(arm.shoulder, arm.elbow), armProfile.upper, 'unreachable upper');
        close(distance(arm.elbow, arm.wrist), armProfile.lower, 'unreachable lower');
    }
}
const rest = poseArms([], 0);
assert.ok(rest.arms.left.wrist.y > 300 && rest.arms.right.wrist.y > 300);
console.log(
    `PASS ${frames} pose frames: fixed bone lengths, both hands, attachment/contact constraints, no elbow flips, unreachable targets`
);
