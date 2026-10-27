// Coordinates are in the 380 x 560 seated-pug rig canvas. All timing is at Toon's 30 fps.
export const pugGestures = ['look_left', 'look_right', 'open_mouth', 'tongue_out', 'move_paws', 'hide_object'];

const smooth = (value: number) => {
    const t = Math.max(0, Math.min(1, value));
    return t * t * (3 - 2 * t);
};

function track(age: number, keys: number[][]) {
    if (age <= keys[0][0]) return keys[0][1];

    for (let i = 1; i < keys.length; i++) {
        if (age <= keys[i][0]) {
            const [a, x] = keys[i - 1];
            const [b, y] = keys[i];
            return x + (y - x) * smooth((age - a) / (b - a));
        }
    }
    return keys[keys.length - 1][1];
}

export function stashPose(age: number) {
    // Reach, contact, pull under the haunch, release, return empty.
    const x = track(age, [
        [0, 0],
        [24, -130],
        [38, -130],
        [80, -60],
        [100, -60],
        [135, 0]
    ]);
    const y = track(age, [
        [0, 0],
        [24, -8],
        [38, -8],
        [80, 12],
        [100, 12],
        [135, 0]
    ]);
    return {
        x,
        y,
        pawOpacity: 1,
        objectX: age < 30 ? -130 : age < 90 ? x : -60,
        objectY: age < 30 ? -8 : age < 90 ? y : 12,
        objectOpacity: age < 80 ? 1 : 0,
        attached: age >= 30 && age < 90,
        hidden: age >= 80
    };
}

export function seatedPugPose(frame: number, gesture: string, age: number, mouth: number, stashAge?: number) {
    const active = age >= 0 && age < (gesture === 'hide_object' ? 150 : 90);
    const pulse = active ? smooth(age / 12) * smooth((90 - age) / 12) : 0;
    const gaze = gesture === 'look_left' ? -pulse : gesture === 'look_right' ? pulse : 0;
    const tongue = gesture === 'tongue_out' ? pulse : 0;
    const opening = Math.max(mouth / 3, gesture === 'open_mouth' ? pulse : 0, tongue * 0.8);
    const moving = gesture === 'move_paws' && active;
    const leftY = moving ? -Math.pow(Math.sin((Math.PI * age) / 30), 2) * 22 * pulse : 0;
    const rightY = moving ? -Math.pow(Math.sin((Math.PI * (age + 15)) / 30), 2) * 22 * pulse : 0;
    const hidingAge = stashAge ?? (gesture === 'hide_object' ? age : -1);
    const stash = stashPose(hidingAge);
    const hiding = hidingAge >= 0 && hidingAge <= 135;
    return {
        gazeX: gaze * 8,
        tongue,
        opening,
        leftX: hiding ? stash.x : 0,
        leftY: hiding ? stash.y : leftY,
        rightY,
        leftOpacity: hiding ? stash.pawOpacity : 1,
        stash,
        // The dog remains seated; only a small breathing motion is applied by the shared rig.
        frame
    };
}
