# Implementation ledger: docs/superpowers/plans/2026-10-07-easybuild-shipit.md

Native execution approved. Base efd8e4f. No callable SageFs tools; CLAUDE.md CLI workflow applies.
Pre-flight: Task 1 Cmd builders feed Task 4 stages; signatures consistent. Task 2 executor seam feeds setup stages; runtime-only execution. Task 3 configure feeds project-init stages; changelog must exist first. Task 4 command tree feeds Task 5 docs; names consistent.
Ruling: Use this tracked ledger instead of shell-specific skill scripts; preserves evidence on Windows without requiring bash helpers. Cost: manual task bookkeeping.
Task 1: complete; missing-API compile failure observed, then 5/5 operation tests passed. Argument list, optional flags, init commands and secrets verified.
Task 2: complete; missing ToolSetup failure observed, then 8/8 tests passed. Existing tool version retained; nested boundary, explicit manifest, repeat setup and malformed JSON covered.
Task 3: missing ProjectSetup failure observed; 11/11 tests passed after XML/YAML configuration. Preserves BOM/CRLF/comments/body, validates all projects before writes, idempotent.
Task 4: missing stages failure followed by 14/14 tests passed. Optional F# options require explicit custom parser; direct command help requires root composition, now tested through Command.root.
Ruling: Expose tool setup via Stages.setup instead of a context-free public ToolSetup.run; the stage runner supplies cancellation, environment and output. Cost: consumers compose the stage rather than calling a standalone installer.
Ruling: Configuration accepts block-style updaters; flow/empty updaters fail with guidance rather than lossy YAML reserialization. Cost: consumers using compact YAML must normalize the updater key explicitly.
