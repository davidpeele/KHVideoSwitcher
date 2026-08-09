# Release notes

`scripts\release.ps1 -Version X.Y.Z -NotesFile ...` commits notes here as
`vX.Y.Z.md` before tagging, so the `release.yml` GitHub Actions workflow can
find them at that commit and use them as the release body. If no file exists
for a given tag, the workflow falls back to GitHub's auto-generated notes.
