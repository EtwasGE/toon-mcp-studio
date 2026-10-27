import fs from 'node:fs';
import assert from 'node:assert/strict';
import ts from 'typescript';
import vm from 'node:vm';

const source = fs.readFileSync(new URL('../src/SeatedPugMotion.ts', import.meta.url), 'utf8');
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const context = { exports: {} };
vm.runInNewContext(code, context);
const { stashPose, seatedPugPose } = context.exports;
assert.equal(stashPose(-1).hidden, false);
assert.equal(stashPose(30).attached, true);
assert.equal(stashPose(80).hidden, true);
assert.equal(stashPose(300).objectOpacity, 0);
assert.equal(stashPose(150).x, 0);
assert.equal(stashPose(150).y, 0);
for (let frame = 31; frame < 80; frame++) {
    const pose = stashPose(frame);
    assert.equal(pose.objectX, pose.x, 'object must follow the paw after contact');
    assert.equal(pose.objectY, pose.y, 'object must follow the paw after contact');
    const previous = stashPose(frame - 1);
    assert.ok(Math.hypot(pose.x - previous.x, pose.y - previous.y) < 7, 'no discontinuous paw jumps');
}
assert.equal(
    seatedPugPose(800, 'look_right', 20, 0, 260).stash.objectOpacity,
    0,
    'next gesture cannot reveal hidden item'
);
assert.ok(seatedPugPose(100, 'look_left', 30, 0).gazeX < 0);
assert.ok(seatedPugPose(200, 'look_right', 30, 0).gazeX > 0);
assert.equal(seatedPugPose(200, 'look_right', 100, 0).gazeX, 0);
assert.ok(seatedPugPose(400, 'tongue_out', 30, 0).opening > 0);
assert.equal(seatedPugPose(450, 'tongue_out', 90, 0).tongue, 0);
assert.equal(seatedPugPose(500, 'idle', 999, 0).opening, 0);
assert.ok(seatedPugPose(470, 'move_paws', 20, 0).leftY < 0);
console.log('Seated pug: contact, hiding persistence, return pose, gaze, tongue and paw motion passed.');
