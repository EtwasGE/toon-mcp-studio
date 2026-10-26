import fs from 'node:fs/promises';
const layers = [
    ['torso', [72, 190], [236, 250], [190, 300], 1, [-2, 2]],
    ['left_arm', [43, 220], [75, 180], [87, 232], 2, [-85, 10]],
    ['right_arm', [267, 220], [75, 180], [289, 232], 2, [-10, 50]],
    ['head', [110, 20], [168, 195], [191, 170], 3, [-15, 15]],
    ['hair', [105, 0], [172, 100], [191, 170], 4, [-15, 15]],
    ['eyes', [135, 103], [111, 34], [191, 170], 5, [-15, 15]],
    ['eyelids', [135, 103], [111, 34], [191, 170], 6, [-15, 15]],
    ['brows', [140, 90], [100, 22], [191, 170], 7, [-15, 15]],
    ['mouth', [168, 152], [50, 40], [191, 170], 8, [-15, 15]]
].map(([id, position, size, pivot, z, angles]) => ({ id, position, size, pivot, z, angles }));
await fs.writeFile(
    new URL('../styles/warm_editorial_2d/manifest.json', import.meta.url),
    JSON.stringify(
        {
            style_id: 'warm_editorial_2d',
            version: '1.0.0',
            renderer_version: '1.0.0',
            original: true,
            characters: ['buyer', 'advisor'].map((character_id) => ({
                character_id,
                version: '1.0.0',
                layers,
                poses: ['seated'],
                phone_attachment: [317, 359],
                mouth_states: ['closed', 'slightly_open', 'wide', 'rounded']
            })),
            backgrounds: ['restaurant_evening', 'home_office', 'neutral_dark'],
            gestures: { idle: 30, blink: 8, nod: 32, shake_head: 36, raise_hand: 54, shrug: 54, show_phone: 120 },
            emotions: ['neutral', 'concerned', 'annoyed', 'surprised', 'confident', 'pleased'],
            presets: ['high_contrast_ru', 'hook', 'cta'],
            safe_area: { x: 80, y: 180, width: 820, height: 1400 },
            provenance: 'Original SVG artwork authored for this repository; no reference video assets included'
        },
        null,
        2
    )
);
