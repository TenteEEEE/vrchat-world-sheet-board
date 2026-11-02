"""Validate and build the VPM ZIP and UnityPackage using only Python's standard library."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import re
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NAME = "com.tentee.vrc-sheet-board"
PACKAGE = ROOT / "Packages" / NAME
REPOSITORY = "TenteEEEE/vrchat-world-sheet-board"


def guid(meta: Path) -> str:
    match = re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(encoding="utf-8"), re.MULTILINE)
    if not match:
        raise ValueError(f"Invalid metadata GUID: {meta}")
    return match[1]


def validate(tag: str | None) -> tuple[dict, list[Path]]:
    manifest = json.loads((PACKAGE / "package.json").read_text(encoding="utf-8"))
    version = manifest["version"]
    assert manifest["name"] == NAME
    assert re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version), "Use a stable SemVer version"
    assert tag is None or tag == version, f"Tag {tag} differs from package version {version}"
    assert manifest["url"] == f"https://github.com/{REPOSITORY}/releases/download/{version}/{NAME}-{version}.zip"
    assert manifest["unity"] == "2022.3"
    assert manifest["vpmDependencies"]["com.vrchat.worlds"]
    assert "zipSHA256" not in manifest, "ZIP hash belongs in the listing, outside the ZIP"
    files = sorted(path for path in PACKAGE.rglob("*") if path.is_file())
    seen = {}
    for path in files:
        if path.suffix == ".meta":
            assert Path(str(path)[:-5]).exists(), f"Orphan metadata: {path}"
            value = guid(path)
            assert value not in seen, f"Duplicate GUID: {path} and {seen.get(value)}"
            seen[value] = path
        else:
            assert Path(str(path) + ".meta").is_file(), f"Missing metadata: {path}"
        assert not any(part in {"SheetBoardDev", ".git", "Library", "Logs"} for part in path.relative_to(PACKAGE).parts)
    for directory in PACKAGE.rglob("*"):
        if directory.is_dir():
            assert Path(str(directory) + ".meta").is_file(), f"Missing folder metadata: {directory}"
    for behavior in ("SheetBoardSource", "SheetBoardDisplay"):
        text = (PACKAGE / "Runtime" / (behavior + ".asset")).read_text(encoding="utf-8")
        match = re.search(r"serializedUdonProgramAsset:.*?guid: ([0-9a-f]{32})", text)
        assert match and match[1] in seen, f"Compiled Udon program is not bundled: {behavior}"
        compiled = Path(str(seen[match[1]])[:-5])
        assert compiled.stat().st_size > 100, f"Empty compiled program: {compiled}"
    for required in ("AppsScript/SheetBoard.gs", "AppsScript/appsscript.json", "Fonts/NotoSansJP-Regular.otf", "Fonts/OFL-1.1.txt", "LICENSE", "README.ja.md"):
        assert (PACKAGE / required).is_file(), f"Missing distribution file: {required}"
    font = (PACKAGE / "Fonts/SheetBoard JP Dynamic.asset").read_text(encoding="utf-8")
    for flag in ("m_AtlasPopulationMode: 1", "m_ClearDynamicDataOnBuild: 1", "m_GlyphTable: []", "m_CharacterTable: []"):
        assert flag in font, f"Export the font from Unity before releasing: missing {flag}"
    print(f"Package OK: {len(files)} files, unique GUIDs, both Udon programs bundled, empty dynamic font")
    return manifest, files


def write_tar_file(archive: tarfile.TarFile, name: str, content: bytes) -> None:
    entry = tarfile.TarInfo(name)
    entry.size = len(content)
    entry.mode = 0o644
    entry.mtime = 0
    archive.addfile(entry, io.BytesIO(content))


def build(output: Path, tag: str | None) -> None:
    manifest, files = validate(tag)
    output.mkdir(parents=True, exist_ok=True)
    stem = f"{NAME}-{manifest['version']}"
    zip_path = output / (stem + ".zip")
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in files:
            info = zipfile.ZipInfo(path.relative_to(PACKAGE).as_posix(), (1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes(), compresslevel=9)
    unity_path = output / (stem + ".unitypackage")
    with unity_path.open("wb") as file:
        with gzip.GzipFile(fileobj=file, mode="wb", filename="", mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w") as archive:
                metadata = [(ROOT / "Tools/SheetBoardRoot.meta", "Assets/SheetBoard", None)]
                metadata.extend((meta, "Assets/SheetBoard/" + Path(str(meta.relative_to(PACKAGE))[:-5]).as_posix(), Path(str(meta)[:-5])) for meta in files if meta.suffix == ".meta")
                for meta, pathname, asset in metadata:
                    value = guid(meta)
                    write_tar_file(archive, value + "/pathname", pathname.encode("utf-8"))
                    write_tar_file(archive, value + "/asset.meta", meta.read_bytes())
                    if asset is not None and asset.is_file():
                        write_tar_file(archive, value + "/asset", asset.read_bytes())
    manifest_path = output / "package.json"
    manifest_path.write_bytes((PACKAGE / "package.json").read_bytes())
    hashes = []
    for path in (zip_path, unity_path, manifest_path):
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        hashes.append(f"{digest}  {path.name}\n")
        print(f"{path.name}: {path.stat().st_size} bytes, SHA256 {digest}")
    with (output / "SHA256SUMS").open("w", encoding="utf-8", newline="\n") as file:
        file.write("".join(hashes))
    verify_archives(output, files, manifest)


def verify_archives(output: Path, files: list[Path], manifest: dict) -> None:
    stem = f"{NAME}-{manifest['version']}"
    with zipfile.ZipFile(output / (stem + ".zip")) as archive:
        assert "package.json" in archive.namelist(), "VPM manifest must be at archive root"
        assert set(archive.namelist()) == {path.relative_to(PACKAGE).as_posix() for path in files}
        for path in files:
            assert archive.read(path.relative_to(PACKAGE).as_posix()) == path.read_bytes()
    with tarfile.open(output / (stem + ".unitypackage"), "r:gz") as archive:
        members = {member.name: member for member in archive.getmembers()}
        count = 0
        for name in members:
            if not name.endswith("/pathname"):
                continue
            pathname = archive.extractfile(members[name]).read().decode("utf-8")
            assert pathname == "Assets/SheetBoard" or pathname.startswith("Assets/SheetBoard/")
            asset_name = name.rsplit("/", 1)[0] + "/asset"
            if asset_name in members:
                source = PACKAGE / pathname.removeprefix("Assets/SheetBoard/")
                assert archive.extractfile(members[asset_name]).read() == source.read_bytes()
                count += 1
        assert count == sum(path.suffix != ".meta" for path in files)
    print("Archive contents OK: VPM root manifest, preserved bytes and GUIDs, no SDK or development files")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", help="Require the release tag to match package.json exactly")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    args = parser.parse_args()
    build(args.output.resolve(), args.tag)
