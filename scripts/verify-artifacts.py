"""Independent schema, ZIP dependency and final-frame audit. No cloud requests."""
import json, sys, hashlib, zipfile
from pathlib import Path
root=Path(__file__).resolve().parent.parent
directory=Path(sys.argv[1]).resolve()
timeline=json.loads((directory/'timeline.json').read_text())
assert timeline['scenes'][-1]['overlays'][0]['end_frame_exclusive']==timeline['total_frames']
with zipfile.ZipFile(directory/'project.zip') as z:
    assert all(not name.startswith(('/', '\\')) and '..' not in Path(name).parts for name in z.namelist())
    manifest=json.loads(z.read('manifest.json'))
    for dep in manifest['dependencies']:
        assert hashlib.sha256(z.read(dep['path'])).hexdigest()==dep['sha256']
    assert json.loads(z.read('project.json'))==json.loads((directory/'project.json').read_text())
    assert json.loads(z.read('validation-report.json'))==json.loads((directory/'validation-report.json').read_text())
print('PASS complete final CTA, portable ZIP hashes and report consistency')
