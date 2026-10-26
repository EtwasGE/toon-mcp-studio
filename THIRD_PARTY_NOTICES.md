# Provenance and third-party notices

- Toon character drawings, backgrounds, gestures, screen fixture, scripts and example texts were authored for this repository. No codil.ya video frames, audio, fonts or proprietary source code are included.
- `examples/offline-dialogue/*.wav`: local-only synthetic Russian speech generated with the machine's installed Microsoft Irina SAPI voice; original dialogue and two pitch/rate presets. These are not human recordings and not ElevenLabs responses. WAV files are excluded from public Git because redistribution rights have not been confirmed. Generate them locally with `scripts/prepare-fixtures.ps1` before running the demo or acceptance tests. The installed speech engine/voice software is not included or relicensed. `provenance.json` records the local fixture source.
- Noto Sans: Google/Noto contributors, SIL Open Font License 1.1. Installed font license in `renderer/node_modules/@fontsource/noto-sans/LICENSE`; fonts are bundled into local renderer output, never fetched during a render. Source: https://github.com/notofonts/latin-greek-cyrillic.
- Remotion 4.0.538: Remotion's own license applies. See https://www.remotion.dev/docs/license/pricing and installed package LICENSE. Eligibility depends on organization/use; commercial use is not unconditionally free.
- React/React DOM, TypeScript, fontkit and the official MCP SDK: their upstream licenses apply; see installed npm/NuGet metadata and LICENSE files. Package versions and transitive dependencies are captured by lock files.
- FFmpeg/FFprobe: binaries installed by ffmpeg-static and ffprobe-static. The verified Windows FFmpeg identifies as 6.1.1 essentials from gyan.dev; FFprobe identifies as 4.0.2. FFmpeg licensing depends on build options, including GPL components such as libx264. See https://ffmpeg.org/legal.html and the binary distributors' corresponding-source notices. No claim of an entirely MIT-licensed stack is made.
- Chrome Headless Shell is downloaded by the pinned Remotion installation. Chromium licenses apply: https://www.chromium.org/Home/chromium-privacy/.
- Music: none included. Optional music must be operator-supplied with appropriate rights.
- Novostroykin interface: no real screenshot supplied; the screenshot reference in its draft remains unresolved. The included synthetic PNG visibly says it is not the bot interface.

Generated project ZIPs contain media and metadata, not installed proprietary voice software or API keys. Asset import records the original filename and operator provenance; it does not certify ownership or perform rights clearance.
