# Workspace artifact archive

Run from a project checkout with PowerShell. The default is a read-only inventory:

```powershell
./tools/windows/Archive-WorkspaceArtifacts.ps1 -Workspace 'E:\Light Control'
```

Add `-Apply` to move old, Git-ignored `.artifacts-*` directories and `opus-*` / `fable-*` root logs into `artifacts/workspace-archive/<timestamp>/`. A JSON manifest records every original and new path; log hashes are checked after moving. Nothing is deleted. Source, scenes, assets, runtime directories, active executable/task/command paths, configuration references, and registered worktrees are excluded. The default cutoff is the start of today; use `-Before` to change it.

Inspect the reported manifest before restoring. Restore also defaults to a read-only check:

```powershell
./tools/windows/Restore-WorkspaceArtifacts.ps1 -Manifest '<reported manifest path>'
./tools/windows/Restore-WorkspaceArtifacts.ps1 -Manifest '<reported manifest path>' -Apply
```

Restoration refuses missing archive entries, path escapes, changed log hashes, and existing destination paths. It never overwrites newer files. These tools organize historical local evidence; they do not stop services, change device state, remove vendor software, or deploy program binaries.
