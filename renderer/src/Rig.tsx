import React from 'react';
const ink = '#282e3a';
export const gestureDuration: Record<string, number> = {
    idle: 30,
    blink: 8,
    nod: 32,
    shake_head: 36,
    raise_hand: 54,
    shrug: 54,
    show_phone: 120
};
export function Rig({
    id,
    frame,
    seed,
    emotion,
    mouth,
    gesture,
    age
}: {
    id: string;
    frame: number;
    seed: number;
    emotion: string;
    mouth: number;
    gesture: string;
    age: number;
}) {
    const buyer = id === 'buyer',
        skin = buyer ? '#e4a77a' : '#d49a77',
        shirt = buyer ? '#da7b49' : '#429a9b';
    const duration = gestureDuration[gesture] ?? 30,
        active = age >= 0 && age < duration;
    const hold = active ? Math.min(1, age / 12, (duration - age) / 12) : 0;
    const blink =
        (frame + (buyer ? 23 : 71) + (seed % 83)) % (buyer ? 137 : 169) < 5 || (gesture === 'blink' && active);
    const nod = gesture === 'nod' && active ? Math.sin((age / 32) * Math.PI * 2) * 7 : 0;
    const shake = gesture === 'shake_head' && active ? Math.sin((age / 36) * Math.PI * 4) * 8 : 0;
    const headAngle =
        (emotion === 'concerned' ? -5 : emotion === 'confident' ? 3 : 0) +
        Math.sin((frame + (buyer ? 0 : 60)) / 42) * 1.4 +
        nod;
    const shrug = gesture === 'shrug' ? hold : 0,
        hand = gesture === 'raise_hand' ? hold : 0,
        phone = gesture === 'show_phone' ? hold : 0;
    const wide = emotion === 'surprised';
    const brow = emotion === 'concerned' ? 10 : emotion === 'annoyed' ? -12 : wide ? -8 : 0;
    return (
        <g
            transform={`translate(${shake},${Math.sin((frame + (buyer ? 0 : 55)) / 39) * 2})`}
            stroke={ink}
            strokeWidth="6"
            strokeLinejoin="round"
            strokeLinecap="round"
        >
            <ellipse cx="190" cy="535" rx="140" ry="25" fill="#192832" opacity=".15" stroke="none" />
            <path d="M100 375 Q100 480 120 530 L180 530 L190 455 L205 530 L268 530 Q295 470 275 375" fill="#3c495c" />
            <path d="M82 220 Q190 170 296 220 L308 414 Q190 442 72 414Z" fill={shirt} />
            <path d="M169 205 L170 166 L215 166 L220 205 Q196 229 169 205" fill={skin} />
            {buyer ? (
                <path d="M170 220 L190 250 L219 216 M190 250 L190 408" fill="none" stroke="#f6cc8a" strokeWidth="5" />
            ) : (
                <>
                    <path d="M163 209 L188 264 L222 211" fill="#f9edd8" />
                    <path d="M148 211 L169 390 M237 214 L220 390" stroke="#256d79" fill="none" />
                </>
            )}
            <g transform={`rotate(${-shrug * 22 - hand * 80} 87 232)`}>
                <path d="M90 222 Q56 223 53 267 L43 346 Q47 373 82 367 L111 274" fill={shirt} />
                <ellipse cx="62" cy="365" rx="24" ry="31" fill={skin} />
            </g>
            <g transform={`rotate(${shrug * 22 + phone * 48} 289 232)`}>
                <path d="M287 221 Q322 227 326 266 L337 344 Q330 372 298 364 L267 268" fill={shirt} />
                <ellipse cx="317" cy="359" rx="24" ry="31" fill={skin} />
                {phone > 0 && (
                    <g transform="translate(297 305) rotate(-48)">
                        <rect width="58" height="108" rx="12" fill="#283446" />
                        <rect x="7" y="12" width="44" height="78" rx="4" fill="#c9e5e2" />
                        <path d="M17 32H41 M17 46H36 M17 60H41" stroke="#609e9e" strokeWidth="4" />
                        <circle cx="29" cy="98" r="3" fill="#e8d5ad" stroke="none" />
                    </g>
                )}
            </g>
            <g transform={`rotate(${headAngle} 191 170)`}>
                {buyer ? (
                    <path d="M121 60 Q95 133 130 177 Q190 221 253 172 L263 88 Q212 25 121 60" fill={skin} />
                ) : (
                    <path
                        d="M127 52 Q104 104 117 151 Q136 196 193 202 Q244 197 266 154 L263 74 Q222 27 127 52"
                        fill={skin}
                    />
                )}
                <ellipse cx="114" cy="130" rx="13" ry="20" fill={skin} />
                <ellipse cx="266" cy="130" rx="12" ry="20" fill={skin} />
                {buyer ? (
                    <path
                        d="M118 103 L111 69 Q107 18 174 29 Q225 8 259 57 L270 100 L247 83 L239 57 Q187 87 135 66 L132 101"
                        fill="#6a4035"
                    />
                ) : (
                    <>
                        <path
                            d="M114 99 Q87 72 117 49 Q107 16 145 20 Q158 -2 184 18 Q211 -2 234 23 Q274 19 277 55 L269 102 L248 77 L245 52 Q193 72 137 53 L132 97Z"
                            fill="#303644"
                        />
                        <path d="M133 169 Q190 205 248 169 Q219 214 183 211 Q147 203 133 169" fill="#303644" />
                    </>
                )}
                <path
                    d={`M143 ${98 + brow / 2}L171 ${98 - brow / 2} M210 ${98 - brow / 2}L238 ${98 + brow / 2}`}
                    fill="none"
                    strokeWidth="7"
                />
                {blink ? (
                    <path d="M142 121H170 M213 121H240" fill="none" />
                ) : (
                    <>
                        <ellipse cx="157" cy="122" rx="14" ry={wide ? 19 : 14} fill="#fff7e9" />
                        <ellipse cx="225" cy="122" rx="14" ry={wide ? 19 : 14} fill="#fff7e9" />
                        <circle cx="161" cy="124" r="5" fill={ink} stroke="none" />
                        <circle cx="221" cy="124" r="5" fill={ink} stroke="none" />
                    </>
                )}
                {!buyer && (
                    <g fill="none" strokeWidth="4">
                        <rect x="135" y="103" width="45" height="34" rx="10" />
                        <rect x="201" y="103" width="45" height="34" rx="10" />
                        <path d="M180 115H201" />
                    </g>
                )}
                <path d="M190 123L182 144L196 147" fill="none" strokeWidth="4" />
                {mouth === 0 ? (
                    <path
                        d={
                            emotion === 'pleased' || emotion === 'confident'
                                ? 'M171 163Q192 182 214 161'
                                : emotion === 'concerned' || emotion === 'annoyed'
                                  ? 'M174 172Q192 161 211 170'
                                  : 'M177 168Q191 171 208 167'
                        }
                        fill="none"
                        strokeWidth="5"
                    />
                ) : (
                    <>
                        <ellipse
                            cx="193"
                            cy="171"
                            rx={mouth === 3 ? 13 : mouth === 2 ? 25 : 20}
                            ry={mouth === 2 ? 20 : mouth === 3 ? 17 : 9}
                            fill="#673b46"
                            strokeWidth="4"
                        />
                        <path d={mouth === 2 ? 'M178 164H208' : 'M182 167H204'} stroke="#fff5df" strokeWidth="5" />
                    </>
                )}
                <ellipse cx="143" cy="148" rx="13" ry="6" fill="#d97c6d" opacity=".5" stroke="none" />
            </g>
        </g>
    );
}
