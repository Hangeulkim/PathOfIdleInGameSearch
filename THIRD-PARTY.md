# Third-party runtime

The player ZIP includes an unmodified BepInEx Unity IL2CPP Windows x64 archive. The installer verifies and installs it only when BepInEx is absent; existing installations are reused. The mod uses the runtime as a shared library and does not modify its binaries. BepInEx is copyright (C) 2020 BepInEx Team, licensed under LGPL-2.1-or-later, without warranty.

- Project: BepInEx Bleeding Edge
- Version: `6.0.0-be.760+a1afbfb`
- Official build page: <https://builds.bepinex.dev/projects/bepinex_be>
- Archive SHA-256: `9753B825578A3C3A31CC10067CD45A44A7BF56D3C34C4679E24D6ADFD0FBA8EA`
- Archive size: 34,324,795 bytes. The complete archive must match the pinned SHA-256 before extraction.
- Official CI provenance: <https://github.com/BepInEx/BepInEx/actions/runs/27338321206>, artifact `BepInEx_CI_BleedingEdge_a1afbfb_760`. The Windows IL2CPP ZIP in that artifact matches the original build-server archive checksum above exactly.
- Source commit: <https://github.com/BepInEx/BepInEx/tree/a1afbfb169610ffbc1813d83e946c9e2cc0d2d63>.
- Matching source archive: <https://codeload.github.com/BepInEx/BepInEx/zip/a1afbfb169610ffbc1813d83e946c9e2cc0d2d63>.
- Source archive SHA-256: `83292B82805F86904150E4CE4EBBF76B80EBE9F228C9BAC95DA85AA1BD1D1E4D`.
- Every player ZIP includes `payload/BepInEx-runtime.zip`, the corresponding upstream source with build scripts in `payload/BepInEx-source.zip`, and the unmodified full license/notice in `payload/BepInEx-LICENSE.txt`.
- `payload/BepInEx-notices.zip` contains dependency license notices and their upstream provenance, the .NET 6.0.7 third-party notices, and corresponding sources for Il2CppInterop `6d9007c18cc8440830379c5e1d5714085e7ec577` (LGPL-3.0) and UnityDoorstop `33dab9a6733862eb81869ff08431d9478b28784b` / v4.5.0 (LGPL-2.1). These are unmodified shared runtime dependencies; their own license terms continue to apply. Notices bundle SHA-256: `67137DBDB57D6BEE42C92FCB8F05E4B92FDA6BEA59846BC30931BFB07308C62A`.

BepInEx binaries are not committed to Git. They are included in player release ZIPs with their source and license, so installation does not depend on a second build-server download. Game binaries and user data are never bundled. Developers can use the pinned archives from an extracted player ZIP as packaging inputs; packaging rejects archives with different checksums.
