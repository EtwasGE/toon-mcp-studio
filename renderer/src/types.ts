export type Camera = { shot: 'wide' | 'two_shot' | 'close_up' | 'screen'; subject_character_id?: string };
export type Cast = { character_id: string; placement: string; pose_id: string; emotion_id: string };
export type Cue = {
    frame: number;
    action: {
        kind: string;
        character_id?: string;
        gesture_id?: string;
        emotion_id?: string;
        camera?: Camera;
        transition_ms?: number;
    };
};
export type Overlay = {
    start_frame: number;
    end_frame_exclusive: number;
    content: {
        kind: string;
        text?: string;
        placement: string;
        asset?: { asset_id: string; version: string };
        preset_id?: string;
    };
};
export type Scene = {
    scene_id: string;
    start_frame: number;
    end_frame_exclusive: number;
    background: { asset_id: string; version: string };
    camera: Camera;
    cast: Cast[];
    events: Cue[];
    overlays: Overlay[];
};
export type Caption = {
    text: string;
    start_frame: number;
    end_frame_exclusive: number;
    mode: string;
    font_size: number;
    rect: { x: number; y: number; width: number; height: number };
};
export type Timeline = {
    rigs?: Record<string, import('./CharacterRig').CharacterRig>;
    environments?: Record<string, import('./Environment').EnvironmentDefinition>;
    total_frames: number;
    seed: number;
    scenes: Scene[];
    utterances: {
        utterance_id: string;
        speaker_character_id: string;
        start_sample: number;
        end_sample_exclusive: number;
        envelope: number[];
    }[];
    captions: Caption[];
    assets: Record<string, string>;
};
