# Development and releases

The distributable package is `Packages/com.tentee.vrc-utsushi`. Samples, preview code and regression checks are in `Assets/UtsushiDev` and are excluded from both release archives. The normative specification is `Packages/com.tentee.vrc-utsushi/docs/SPEC.md`.

Open the project with Unity 2022.3.22f1 after restoring VRChat Worlds SDK 3.10.5 through VCC and importing TMP Essential Resources. The dependency packages and TMP Essential Resources are not committed here.

Before tagging a version, run these Unity batch entry points in order:

1. `UtsushiDev.UtsushiBatchBuild.RebuildAndPreview` to compile UdonSharp, regenerate prefabs, validate permissions and render samples.
2. `UtsushiDevEditor.UtsushiReleaseValidation.VerifyBatch` to test timestamp bounds, CDN rollback, in-flight status, prefab wiring, installation, Japanese/English messages, and render 20 sample images including both themes, 101 chips, large tables and literal markup. Inspect `Logs/UtsushiPreview*.png`.
3. `UtsushiDev.UtsushiDevMenu.ExportUnityPackageBatch` to clear the dynamic font before saving distribution assets. The package contains its two serialized Udon programs under `Runtime/SerializedPrograms`.

Run `node --test docs/Utsushi/tests/*.test.cjs` and `python Tools/build_release.py` from the repository root. Python validates the package metadata, preserved GUIDs, bundled Udon programs and empty font data, then builds deterministic ZIP and UnityPackage archives in `dist/`.

Update `package.json` version, its download URL, the changelog and `docs/RELEASE_NOTES.md` before creating a matching bare SemVer tag (for example `0.1.0`). The release workflow tests and builds the files, then publishes a GitHub Release with the VPM ZIP, UnityPackage, package.json and SHA256SUMS. It refuses to overwrite an existing release.

`TenteEEEE/vpm-repos/source.json` lists this repository. After a release, run that repository's **Build Repo Listing** workflow. Optionally configure a fine-grained `VPM_REPOS_TOKEN` secret with Actions write access to that repository for automatic rebuilds. Never commit that token or embed it in an archive.
