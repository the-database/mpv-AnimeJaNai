"""Rebuild the frozen API 1.0 example with its original SDK and compiler.

Javy's preinitialized runtime image is not byte deterministic. Keep the frozen
binary for compatibility tests; this produces a separately named review build.
"""
from pathlib import Path
import hashlib
import json
import subprocess
import sys
import tempfile
import zipfile

compiler, destination = map(Path, sys.argv[1:])
if hashlib.sha256(compiler.read_bytes()).hexdigest() != "4b298b147ba2d8656af174e266ca8f9de1a4d963795a95f4ce82f685b34d3c73":
    raise SystemExit("Use the pinned Javy 9.1.0 Windows x64 compiler from addons/tools/bootstrap.ps1")
source = Path(__file__).parent / "counter-api-1.0-source"
with tempfile.TemporaryDirectory() as temporary:
    work = Path(temporary)
    # Preserve source line endings, just as the original .NET builder did.
    (work / "addon.js").write_bytes((source / "ajn.js").read_bytes() + b"\n" +
                                  (source / "addon.js").read_bytes() + b"\n__ajnSdk.run(onEvent);\n")
    subprocess.run([str(compiler.resolve()), "build", str(work / "addon.js"), "-o", str(work / "module.wasm")], check=True)
    module = (work / "module.wasm").read_bytes()
    manifest = json.loads((source / "manifest.json").read_bytes())
    manifest["moduleSha256"] = hashlib.sha256(module).hexdigest()
    with zipfile.ZipFile(destination, "x", compression=zipfile.ZIP_DEFLATED) as package:
        package.writestr("manifest.json", json.dumps(manifest, separators=(",", ":")))
        package.writestr("module.wasm", module)
    print(manifest["moduleSha256"], destination)
