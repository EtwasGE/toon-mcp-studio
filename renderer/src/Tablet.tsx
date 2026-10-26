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
                        <rect width={960} height={1280} fill="#e6eef3" />
                        <circle cx={480} cy={480} r={165} fill="#2aabee" />
                        <path
                            d="M375 472 L589 389 Q611 380 604 409 L566 587 Q562 605 541 591 L480 545 L447 574 L451 524 L559 425 L422 510 Z"
                            fill="#fff"
                        />
                        <text
                            x={480}
                            y={750}
                            textAnchor="middle"
                            fontFamily="Toon, sans-serif"
                            fontSize={59}
                            fill="#243647"
                        >
                            @novostroykin_bot
                        </text>
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
