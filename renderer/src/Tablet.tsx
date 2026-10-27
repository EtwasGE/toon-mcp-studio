import React from 'react';

// Local tablet coordinates. Future recordings fit this portrait viewport (3:4).
export const tabletScreen = { x: -48, y: -132, width: 96, height: 128 };

export function PotatoMark() {
    return (
        <g>
            <path
                d="M8 -24 C-6 -33 -24 -22 -25 -5 C-29 13 -15 29 1 27 C18 27 28 16 25 6 C15 9 12 1 18 -5 C8 -5 6 -15 14 -20 Z"
                fill="#b58c53"
                stroke="#765731"
                strokeWidth={2}
            />
            <path
                d="M-13 -13 l3 2 M-17 4 l3 -1 M-6 16 l2 -3 M0 -9 l2 2 M10 17 l2 -1"
                stroke="#765731"
                strokeWidth={2.5}
                strokeLinecap="round"
            />
        </g>
    );
}

export function Tablet({ view = 'screen', pulse = false }: { view?: 'screen' | 'back'; pulse?: boolean }) {
    return (
        <g>
            <rect x={-57} y={-145} width={114} height={157} rx={10} fill="#b8bec7" stroke="#252b34" strokeWidth={3} />
            <path d="M57 -112 h2 v24 M57 -76 h2 v12" stroke="#747d89" strokeWidth={2} />
            {view === 'back' ? (
                <g>
                    <rect x={-48} y={-136} width={19} height={24} rx={6} fill="#8c95a2" />
                    <circle cx={-38.5} cy={-127} r={5} fill="#222b38" stroke="#596577" strokeWidth={2} />
                    <circle cx={-38.5} cy={-118} r={2} fill="#e8e1c4" />
                    <g transform="translate(0 -65) scale(0.8)">
                        <PotatoMark />
                    </g>
                    <path d="M-12 5 h24" stroke="#858f9d" strokeWidth={1.5} />
                </g>
            ) : (
                <g>
                    <rect x={-52} y={-140} width={104} height={146} rx={7} fill="#18202b" />
                    <svg {...tabletScreen} viewBox="0 0 960 1280" overflow="hidden">
                        <rect width={960} height={1280} fill="#eee1bd" />
                        <text
                            x={480}
                            y={180}
                            textAnchor="middle"
                            fontFamily="Toon, sans-serif"
                            fontWeight={700}
                            fontSize={90}
                            fill="#302922"
                        >
                            КАРТОХА OS
                        </text>
                        <path
                            d="M540 320C300 235 220 465 320 625C410 760 670 655 647 490C555 520 530 440 585 400C520 400 505 350 540 320Z"
                            fill="#b58c53"
                            stroke="#765731"
                            strokeWidth={18}
                        />
                        <path
                            d="M350 410l25 15m-45 85l25-5m100 100l15-25m20-140l20 15"
                            fill="none"
                            stroke="#765731"
                            strokeWidth={18}
                            strokeLinecap="round"
                        />
                        <text
                            x={480}
                            y={870}
                            textAnchor="middle"
                            fontFamily="Toon, sans-serif"
                            fontSize={92}
                            fontWeight={700}
                            fill="#302922"
                        >
                            БОТ ЗАПУЩЕН
                        </text>
                        <circle cx={480} cy={1050} r={94} fill="#4c7958" />
                        <path
                            d="M430 1050l35 35 65-75"
                            fill="none"
                            stroke="#fff5d9"
                            strokeWidth={22}
                            strokeLinecap="round"
                            strokeLinejoin="round"
                        />
                    </svg>
                    <circle cx={0} cy={-136} r={1.4} fill="#66778b" />
                    <path d="M-12 1 h24" stroke="#a0adbb" strokeWidth={1.5} strokeLinecap="round" />
                    {pulse && (
                        <circle
                            cx={18}
                            cy={-32}
                            r={8}
                            fill="#2aabee"
                            fillOpacity={0.15}
                            stroke="#2aabee"
                            strokeWidth={1.5}
                        />
                    )}
                </g>
            )}
        </g>
    );
}
