import React from 'react';
import { Composition, registerRoot } from 'remotion';

const ink = '#302922';
const text = (y: number, value: string, size = 76, color = ink) =>
    `<text x="480" y="${y}" text-anchor="middle" font-family="Arial,sans-serif" font-weight="900" font-size="${size}" fill="${color}">${value}</text>`;
const house = `<path d="M300 580V380L480 250L660 380V580Z" fill="#fff3c7" stroke="${ink}" stroke-width="20" stroke-linejoin="round"/><path d="M420 580V440H540V580M335 360L480 250L625 360" fill="none" stroke="${ink}" stroke-width="20"/>`;
const bell = `<g transform="translate(610 480)"><circle r="115" fill="#302922"/><path d="M-58 36H58L43 9V-26Q43-72 0-72Q-43-72-43-26V9Z" fill="#ffd44d"/><circle cy="57" r="14" fill="#ffd44d"/></g>`;
const designs = [
    `<rect width="960" height="1280" fill="#ffd44d"/>${house}${bell}${text(805, 'НОВО', 132)}${text(950, 'СТРОЙКИН', 112)}${text(1110, 'НОВОСТРОЙКИ', 46)}`,
    `<rect width="960" height="1280" fill="#e7eddf"/>${text(160, 'НОВОСТРОЙКИН', 56)}<rect x="75" y="250" width="810" height="880" rx="52" fill="#fff9e8" stroke="${ink}" stroke-width="15"/>${text(460, 'ЦЕНА', 148)}${text(630, 'УПАЛА', 148)}<path d="M480 730V960M350 850L480 980L610 850" fill="none" stroke="#3c7957" stroke-width="65" stroke-linecap="round" stroke-linejoin="round"/>`,
    `<rect width="960" height="1280" fill="#ffdc79"/>${text(390, '2', 330)}${text(520, 'ПОДПИСЧИКА', 82)}<g stroke="${ink}" stroke-width="12"><circle cx="270" cy="800" r="158" fill="#f1bf8d"/><path d="M130 730Q270 600 410 730" fill="none"/><ellipse cx="270" cy="824" rx="38" ry="32" fill="#d47a62"/><path d="M192 865Q270 825 348 865Q270 916 192 865" fill="#68513e"/><path d="M180 765h65m50 0h65"/><circle cx="690" cy="800" r="158" fill="#dba45a"/><path d="M565 690Q470 660 560 810L604 720M815 690Q910 660 820 810L776 720" fill="#62472f"/><ellipse cx="690" cy="860" rx="99" ry="65" fill="#69503d"/><ellipse cx="690" cy="825" rx="25" ry="18" fill="${ink}"/><ellipse cx="635" cy="777" rx="31" ry="37" fill="#fff5df"/><ellipse cx="750" cy="777" rx="31" ry="37" fill="#fff5df"/><circle cx="651" cy="778" r="13"/><circle cx="734" cy="778" r="13"/></g>${text(1070, 'Я И РОЗЕТКА', 70)}`,
    `<rect width="960" height="1280" fill="#eee1bd"/>${text(180, 'КАРТОХА OS', 90)}<path d="M540 320C300 235 220 465 320 625C410 760 670 655 647 490C555 520 530 440 585 400C520 400 505 350 540 320Z" fill="#b58c53" stroke="#765731" stroke-width="18"/><path d="M350 410l25 15m-45 85l25-5m100 100l15-25m20-140l20 15" stroke="#765731" stroke-width="18" stroke-linecap="round"/>${text(870, 'БОТ ЗАПУЩЕН', 92)}<circle cx="480" cy="1050" r="94" fill="#4c7958"/><path d="M430 1050l35 35 65-75" fill="none" stroke="#fff5d9" stroke-width="22" stroke-linecap="round" stroke-linejoin="round"/>`
];
const labels = ['1. Название и уведомления', '2. Цена упала', '3. Два подписчика', '4. Картоха OS'];
function Options() {
    return (
        <div
            style={{
                width: 1600,
                height: 730,
                background: '#f0e7d6',
                display: 'flex',
                gap: 28,
                padding: '45px 34px',
                boxSizing: 'border-box'
            }}
        >
            {designs.map((design, i) => (
                <div key={i} style={{ width: 362, textAlign: 'center' }}>
                    <div
                        style={{
                            background: '#b8bec7',
                            border: '7px solid #252b34',
                            borderRadius: 29,
                            padding: '24px 13px 20px',
                            boxShadow: '0 9px 0 #d0c4af'
                        }}
                    >
                        <div
                            style={{
                                width: 9,
                                height: 9,
                                borderRadius: 9,
                                background: '#252b34',
                                margin: '-14px auto 8px'
                            }}
                        />
                        <svg
                            viewBox="0 0 960 1280"
                            style={{
                                display: 'block',
                                width: '100%',
                                border: '5px solid #18202b',
                                boxSizing: 'border-box'
                            }}
                            dangerouslySetInnerHTML={{ __html: design }}
                        />
                        <div
                            style={{
                                width: 66,
                                height: 5,
                                borderRadius: 4,
                                background: '#747d89',
                                margin: '12px auto -9px'
                            }}
                        />
                    </div>
                    <div style={{ fontFamily: 'Arial', fontSize: 21, fontWeight: 700, color: ink, marginTop: 25 }}>
                        {labels[i]}
                    </div>
                </div>
            ))}
        </div>
    );
}
registerRoot(() => (
    <Composition id="TabletOptions" component={Options} width={1600} height={730} fps={30} durationInFrames={1} />
));
