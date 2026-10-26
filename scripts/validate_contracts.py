"""Validate specification examples. Requires jsonschema; no network or paid calls."""
from copy import deepcopy
from pathlib import Path
import json
from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parent.parent


def read(relative):
    return json.loads((ROOT / relative).read_text(encoding="utf-8"))


def require_rejected(validator, value, label):
    if not list(validator.iter_errors(value)):
        raise AssertionError(f"Expected rejection: {label}")


def main():
    project_schema = read("contracts/project.schema.json")
    alignment_schema = read("contracts/audio-alignment.schema.json")
    Draft202012Validator.check_schema(project_schema)
    Draft202012Validator.check_schema(alignment_schema)
    project_validator = Draft202012Validator(project_schema)
    alignment_validator = Draft202012Validator(alignment_schema)
    project = read("examples/novostroykin/project.json")
    alignment = read("tests/fixtures/recorded-line.alignment.json")
    project_validator.validate(project)
    alignment_validator.validate(alignment)

    for mode in ["auto", "word", "utterance"]:
        variant = deepcopy(project)
        variant["captions"]["mode"] = mode
        project_validator.validate(variant)
    recording = deepcopy(project)
    recording["scenes"][0]["utterances"][0]["audio"] = {
        "mode": "recording",
        "asset": {"asset_id": "buyer_recording", "version": "1.0.0"},
    }
    project_validator.validate(recording)

    bad = deepcopy(project)
    bad["video"]["fps"] = 60
    require_rejected(project_validator, bad, "unsupported frame rate")
    bad = deepcopy(project)
    bad["scenes"][0]["start_frame"] = 0
    require_rejected(project_validator, bad, "compiled field in draft")
    bad = deepcopy(project)
    bad["scenes"][0]["utterances"][0]["audio"]["api_key"] = "test-not-a-key"
    require_rejected(project_validator, bad, "secret field in draft")
    bad = deepcopy(project)
    bad["scenes"][-1]["utterances"] = project["scenes"][0]["utterances"]
    require_rejected(project_validator, bad, "speech in hold scene")
    bad = deepcopy(project)
    bad["scenes"][1]["overlays"][0]["content"]["fit"] = "cover"
    require_rejected(project_validator, bad, "cropped screenshot")
    bad = deepcopy(project)
    bad["captions"]["mode"] = "invent_timing"
    require_rejected(project_validator, bad, "unknown captions mode")
    bad = deepcopy(alignment)
    bad["example_only"] = False
    require_rejected(alignment_validator, bad, "production without provenance")
    bad = deepcopy(alignment)
    bad["source"] = "manually_verified"
    require_rejected(alignment_validator, bad, "false provenance for example")

    words = alignment["normalized_words"]
    assert [w["index"] for w in words] == list(range(len(words)))
    previous_end = 0
    for word in words:
        assert previous_end <= word["start_ms"] < word["end_ms"] <= alignment["duration_ms"]
        previous_end = word["end_ms"]
    for span in alignment.get("display_spans", []):
        assert 0 <= span["start_word_index"] < span["end_word_index_exclusive"] <= len(words)

    print("PASS: 2 schemas, 2 examples, 3 caption modes, recording variant, 8 rejected invalid cases.")
    print("PASS: illustrative alignment indices, intervals and display ranges are coherent.")
    print("LIMIT: no audio is provided; alignment is illustrative, not production-ready.")
    print("LIMIT: no server, TTS request, asset resolution or video render has been executed.")


if __name__ == "__main__":
    main()

