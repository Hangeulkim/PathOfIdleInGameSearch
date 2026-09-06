# Owned cross-job skill / auto-equip regression

Game fixture: Path of Idle 1.0.12, 2026-09-06. The Priest named `healer`
(save UID 38278) owns two Alien grid talents from other jobs. The failure also
occurs independently of the character's display name or chosen theme.

## Failure and contract

The original reproduction had six occupied equipment slots, two empty slots,
and 106 unallocated points. Gear comparison succeeded and eight replacements
were applied, but the skill step discarded the cross-job rows when translating
the remembered plan through a job-only catalog. It rejected the plan and rolled
all equipment back. The level planner had also simulated those skills without
including their saved-point destinations.

The authoritative set is now the character's actual saved talent grid plus its
own job's legal transformation catalog. Equipment-granted extra skills remain
separate; arbitrary unowned skills from other jobs are not admitted. Every
selected learned active skill must map to a positive saved target before any
equipment changes. Alien rows cannot be borrowed as replaceable skill slots.

## Repeatable checks

These commands require an opt-in `PATHOFIDLE_RUNTIME_TEST` build. They are not
available in the public DLL. Use a save backup for any apply/rollback test.

```powershell
# Read-only: exact repeated baseline plus positive saved targets for both Alien rows.
powershell -File scripts/run-auto-build-regressions.ps1 -ManifestPath scripts/auto-build-regressions-healer.json -Repeat 2

# Read-only broad matrix after live save progression: checks current native point
# budgets and before/after invariants, NOT equality with an older save baseline.
powershell -File scripts/run-auto-build-regressions.ps1 -InvariantOnly -OutputDirectory runtime-healer-regression

# Mutating: normal Auto Build for this fixture, followed by an exported snapshot.
powershell -File scripts/run-runtime-auto-build-case.ps1 -Theme auto -HeroId 38278 -OutputPrefix runtime-healer-apply

# Mutating but rolled back: inject failure after skill application, before commit.
powershell -File scripts/run-runtime-auto-build-case.ps1 -Theme auto -HeroId 38278 -OutputPrefix runtime-healer-rollback -VerifyRollback
```

The rollback test must observe the injected failure, exact restored gear/skills/
progress/preferences, and a verified refund of only this transaction's debit.
Other parties' Blood income during search is preserved. Extracted Vault items
may remain in ordinary Warehouse under the existing safe rollback policy; they
are not lost or sold by rollback.

Do not replace an older baseline just to hide a mismatch. The healer manifest
records this live fixture separately from the original 17-case release matrix.
`-Record` is for an explicitly reviewed new fixture; `-InvariantOnly` explicitly
does not prove that an old build-selection signature stayed identical.
