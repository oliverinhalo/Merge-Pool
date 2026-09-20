# Changelog

All notable changes to MergePool are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Two compatibility contracts are versioned separately from the app and are called out on every
change:

- **Data format** — the `.PoolPart-{GUID}` layout on disk. Pool files are plain files in mirrored
  paths, readable with MergePool uninstalled.
- **Config schema** — `config.json` `schemaVersion`, migrated forward only, backed up first,
  unknown fields preserved.

## [Unreleased]

### Added

- Core pool library: pool-part layout, volume-GUID drive identity, union view, write operations,
  placement scoring and optional adoption of existing drive content.
- Configuration store with `schemaVersion`, forward migrations, pre-migration backup and
  unknown-field preservation.
- Unit tests covering path handling, merged listings, degraded/rejoining drives, placement and
  config migration.

### Compatibility

- Data format: **1** (`.PoolPart-{GUID}` folders, mirrored paths, advisory `poolpart.json`).
- Config schema: **1**.
