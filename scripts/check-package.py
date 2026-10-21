"""Check an actual release ZIP: python3 scripts/check-package.py artifacts/*.zip."""

import json
from pathlib import Path
import sys
import zipfile


if len(sys.argv) < 2:
    raise SystemExit("usage: check-package.py artifacts/BingeCat-Sync-<framework>.zip ...")

for filename in sys.argv[1:]:
    with zipfile.ZipFile(filename) as package:
        assert package.testzip() is None, f"Corrupt ZIP: {filename}"
        names = set(package.namelist())
        manifest = json.loads(package.read("meta.json"))
        managed = {name for name in names if name.endswith(".dll") and "/" not in name}
        assert set(manifest["assemblies"]) == managed, "Only the managed assemblies may be loaded"
        assert "Jellyfin.Plugin.BingeCat.dll" in managed
        assert "SQLitePCLRaw.batteries_v2.dll" in managed
        assert manifest["guid"] == "832bd9b3-75aa-4dd7-8477-8d13de65f9ee"
        abi = {"net8.0": "10.10.7.0", "net9.0": "10.11.8.0", "net10.0": "12.2.0.0"}
        framework = Path(filename).stem.removeprefix("BingeCat-Sync-")
        assert manifest["targetAbi"] == abi[framework]
        for runtime, native in (("linux-x64", "libe_sqlite3.so"), ("linux-arm64", "libe_sqlite3.so"),
                                ("osx-arm64", "libe_sqlite3.dylib"), ("win-x64", "e_sqlite3.dll")):
            assert f"runtimes/{runtime}/native/{native}" in names, f"Missing runtime: {runtime}"
    print(f"PASS {filename}: managed whitelist and native runtimes intact")
