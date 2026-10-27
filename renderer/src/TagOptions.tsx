import React from 'react';
import { Composition, registerRoot } from 'remotion';

const labels = ['1. Картонная бирка', '2. Жёлтый ценник', '3. Контурная бирка', '4. Наклейка с загнутым уголком'];
const shapes = [
    <g>
        <path
            d="M130 64H670L720 135L670 206H130Q110 206 110 186V84Q110 64 130 64Z"
            fill="#dfbd82"
            stroke="#30271f"
            strokeWidth={6}
        />
        <circle cx={670} cy={135} r={12} fill="#604938" stroke="#30271f" strokeWidth={4} />
        <path d="M675 135Q766 63 744 39Q722 25 705 70" fill="none" stroke="#d9b67c" strokeWidth={7} />
    </g>,
    <g>
        <path d="M110 60H655L720 135L655 210H110Z" fill="#ffd34f" stroke="#30271f" strokeWidth={6} />
        <path d="M130 80H643L691 135L643 190H130Z" fill="none" stroke="#9f7425" strokeWidth={2} strokeDasharray="9 8" />
        <circle cx={669} cy={135} r={10} fill="#604938" />
    </g>,
    <g>
        <path d="M110 72H660L720 135L660 198H110Z" fill="#30271f" fillOpacity={0.24} stroke="#fff2ce" strokeWidth={6} />
        <circle cx={678} cy={135} r={10} fill="none" stroke="#fff2ce" strokeWidth={4} />
    </g>,
    <g>
        <path d="M110 65H720V163L674 209H110Z" fill="#fff1cf" stroke="#30271f" strokeWidth={6} />
        <path d="M674 209V163H720Z" fill="#cfb582" stroke="#30271f" strokeWidth={5} />
    </g>
];
function Options() {
    return (
        <div
            style={{
                width: 1600,
                height: 880,
                background: '#eee3ce',
                padding: 32,
                boxSizing: 'border-box',
                display: 'grid',
                gridTemplateColumns: '1fr 1fr',
                gap: 28
            }}
        >
            {shapes.map((shape, i) => (
                <div key={i} style={{ background: '#72563e', borderRadius: 18, overflow: 'hidden' }}>
                    <div
                        style={{
                            height: 60,
                            background: '#f7edda',
                            padding: '18px 24px',
                            boxSizing: 'border-box',
                            font: 'bold 23px Arial',
                            color: '#30271f'
                        }}
                    >
                        {labels[i]}
                    </div>
                    <svg viewBox="0 0 820 290" width="100%" style={{ display: 'block', marginTop: 20 }}>
                        <path d="M0 25L820 130M0 135L820 240M0 245L390 290" stroke="#5e4532" strokeWidth={5} />
                        <path d="M120 0L45 290M500 0L425 290M780 0L705 290" stroke="#86674c" strokeWidth={3} />
                        {shape}
                        <text
                            x={385}
                            y={152}
                            textAnchor="middle"
                            fontFamily="Arial,sans-serif"
                            fontWeight={700}
                            fontSize={47}
                            fill={i === 2 ? '#fff2ce' : '#30271f'}
                        >
                            @novostroykin_bot
                        </text>
                    </svg>
                </div>
            ))}
        </div>
    );
}
registerRoot(() => (
    <Composition id="TagOptions" component={Options} width={1600} height={880} fps={30} durationInFrames={1} />
));
