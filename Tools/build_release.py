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
    if manifest["name"] != NAME:
        raise SystemExit(f"Package name must be {NAME}")
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
        raise SystemExit("Use a stable SemVer version")
    if tag is not None and tag != version:
        raise SystemExit(f"Tag {tag} differs from package version {version}")
    if manifest["url"] != f"https://github.com/{REPOSITORY}/releases/download/{version}/{NAME}-{version}.zip":
        raise SystemExit("Package release URL does not match its version")
    if manifest["unity"] != "2022.3":
        raise SystemExit("Package must target Unity 2022.3")
    if not manifest["vpmDependencies"]["com.vrchat.worlds"]:
        raise SystemExit("Package must declare its VRChat Worlds dependency")
    if "zipSHA256" in manifest:
        raise SystemExit("ZIP hash belongs in the listing, outside the ZIP")
    release_notes_path = ROOT / "docs" / "RELEASE_NOTES.md"
    release_notes = release_notes_path.read_text(encoding="utf-8")
    first_line = release_notes.splitlines()[0] if release_notes.splitlines() else ""
    declaration = re.fullmatch(r"<!-- version: ([^<>\s]+) -->", first_line)
    declared_version = declaration[1] if declaration else first_line or "(なし)"
    if first_line != f"<!-- version: {version} -->":
        raise SystemExit(f"docs/RELEASE_NOTES.md の版宣言 ({declared_version}) が package.json の版 ({version}) と一致しません。リリースノートを書き換えてください。")
    changelog = (PACKAGE / "CHANGELOG.md").read_text(encoding="utf-8")
    changelog_heading = re.compile(rf"^## \[{re.escape(version)}\](?:\s|$)", re.MULTILINE)
    if not changelog_heading.search(changelog):
        raise SystemExit(f"CHANGELOG.md に {version} の見出しがありません。")
    files = sorted(path for path in PACKAGE.rglob("*") if path.is_file())
    for path in files:
        filename = path.name
        if "競合コピー" in filename or "conflicted copy" in filename.lower():
            raise SystemExit(f"Conflicted copy filename is not allowed in release archives: {path}")
        if filename.endswith((".orig", ".rej", ".bak", "~")):
            raise SystemExit(f"Backup filename is not allowed in release archives: {path}")
    seen = {}
    for path in files:
        if path.suffix == ".meta":
            if not Path(str(path)[:-5]).exists():
                raise SystemExit(f"Orphan metadata: {path}")
            value = guid(path)
            if value in seen:
                raise SystemExit(f"Duplicate GUID: {path} and {seen.get(value)}")
            seen[value] = path
        else:
            if not Path(str(path) + ".meta").is_file():
                raise SystemExit(f"Missing metadata: {path}")
        if any(part in {"SheetBoardDev", ".git", "Library", "Logs"} for part in path.relative_to(PACKAGE).parts):
            raise SystemExit(f"Development or generated file is not allowed in release archives: {path}")
    for directory in PACKAGE.rglob("*"):
        if directory.is_dir():
            if not Path(str(directory) + ".meta").is_file():
                raise SystemExit(f"Missing folder metadata: {directory}")
    for behavior in ("SheetBoardSource", "SheetBoardDisplay"):
        text = (PACKAGE / "Runtime" / (behavior + ".asset")).read_text(encoding="utf-8")
        match = re.search(r"serializedUdonProgramAsset:.*?guid: ([0-9a-f]{32})", text)
        if not match or match[1] not in seen:
            raise SystemExit(f"Compiled Udon program is not bundled: {behavior}")
        compiled = Path(str(seen[match[1]])[:-5])
        if compiled.stat().st_size <= 100:
            raise SystemExit(f"Empty compiled program: {compiled}")
    for required in ("AppsScript/SheetBoard.gs", "AppsScript/appsscript.json", "Fonts/NotoSansJP-Regular.otf", "Fonts/OFL-1.1.txt", "LICENSE", "README.ja.md"):
        if not (PACKAGE / required).is_file():
            raise SystemExit(f"Missing distribution file: {required}")
    font = (PACKAGE / "Fonts/SheetBoard JP Dynamic.asset").read_text(encoding="utf-8")
    for flag in ("m_AtlasPopulationMode: 1", "m_ClearDynamicDataOnBuild: 1", "m_GlyphTable: []", "m_CharacterTable: []"):
        if flag not in font:
            raise SystemExit(f"Export the font from Unity before releasing: missing {flag}")
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
        if "package.json" not in archive.namelist():
            raise SystemExit("VPM manifest must be at archive root")
        if set(archive.namelist()) != {path.relative_to(PACKAGE).as_posix() for path in files}:
            raise SystemExit("VPM ZIP contents do not match package files")
        for path in files:
            if archive.read(path.relative_to(PACKAGE).as_posix()) != path.read_bytes():
                raise SystemExit(f"VPM ZIP file content differs from package: {path}")
    with tarfile.open(output / (stem + ".unitypackage"), "r:gz") as archive:
        members = {member.name: member for member in archive.getmembers()}
        count = 0
        for name in members:
            if not name.endswith("/pathname"):
                continue
            pathname = archive.extractfile(members[name]).read().decode("utf-8")
            if pathname != "Assets/SheetBoard" and not pathname.startswith("Assets/SheetBoard/"):
                raise SystemExit(f"UnityPackage path is outside Assets/SheetBoard: {pathname}")
            asset_name = name.rsplit("/", 1)[0] + "/asset"
            if asset_name in members:
                source = PACKAGE / pathname.removeprefix("Assets/SheetBoard/")
                if archive.extractfile(members[asset_name]).read() != source.read_bytes():
                    raise SystemExit(f"UnityPackage file content differs from package: {pathname}")
                count += 1
        if count != sum(path.suffix != ".meta" for path in files):
            raise SystemExit("UnityPackage asset count does not match package files")
    print("Archive contents OK: VPM root manifest, preserved bytes and GUIDs, no SDK or development files")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", help="Require the release tag to match package.json exactly")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    args = parser.parse_args()
    build(args.output.resolve(), args.tag)
