# Release authority and checks

Publish only to **Hangeulkim/PathOfIdleInGameSearch**, the standalone repository.
Never publish from a `path-of-idle-stats` checkout or force-push that project's
history over this repository. `scripts/publish-release.ps1` checks the exact
origin, GitHub repository identity, non-fork status, clean tree, version and
checksums before publishing.

1. Update the source plugin version, installer message, README and changelog.
2. Run `scripts/build.ps1` with .NET 6 SDK or newer. It updates the payload and
   installer checksum together. A custom SDK executable can be supplied with
   `-DotNet`.
3. Run focused regressions and the effect audit. Preserve historical receipts
   and exact-save signatures; do not relabel them as newly executed tests.
4. Run `scripts/package.ps1`. Only allowlisted source, installer, documentation
   and production payload files enter the ZIP. No saves, personal settings,
   game binaries, runtime requests, debug-enabled DLL or PDB are distributed.
5. Extract and test the actual ZIP: checksum mismatch, incomplete BepInEx,
   game-running rejection, existing settings preservation and real game load.
   A compile or process launch alone does not establish functional correctness.
6. Commit source, payload, installer hash and documentation together. Push only
   to the explicit standalone repository, then use the guarded publish script.
7. Download public assets again and verify checksums, source commit, README and
   Steam source/download/guide links before reporting deployment complete.

Runtime tests remain opt-in with `PATHOFIDLE_RUNTIME_TEST`; their existing
v1.1.8 protocol is retained for saved runners. It is independent of the public
1.1.9 plugin version. These hooks are absent from the production DLL.

Images in `docs/images` are historical early UI captures. They are deliberately
not embedded in the current README because their layout and set display are
outdated. Do not substitute another project's screenshots.
