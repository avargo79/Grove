# Roadmap

Milestones are ordered so that each one is independently useful. Feature names in parentheses are
the Fork equivalents being targeted.

## M1 — Repository browser and commit graph ✅

The visual core: open a repository and read its history.

- [x] `GitCommandRunner` — async, non-interactive, locale-pinned git process wrapper
- [x] Repository discovery from any path inside a work tree
- [x] Commit log parsing with parents, identities, dates and ref decoration
- [x] Ref enumeration: local branches, remotes, tags, stashes, upstream tracking
- [x] Working tree status via porcelain v2
- [x] Lane-assignment graph layout with stable lanes and colour cycling
- [x] `GraphRowControl` — béziers, merge rings, virtualised per-row rendering
- [x] Sidebar grouped by branches / remotes / tags / stashes, click to jump to a ref's tip
- [x] Commit list with ref badges, author, relative date, short sha (*Commit List*)
- [x] Commit detail pane with message body and changed-file list
- [x] Unified diff with per-side line numbers and add/remove colouring (*Advanced Diff Viewer*)
- [x] Unit and integration test suites, SonarAnalyzer on every build

## M2 — Working copy and committing

The daily-driver loop.

- [ ] "Uncommitted changes" pinned as the first row of the commit list, as Fork does
- [ ] Staged / unstaged split view over the working tree
- [ ] Stage and unstage whole files
- [ ] Stage and unstage individual hunks (*Stage/unstage changes line-by-line*)
- [ ] Line-level staging via a synthesised patch through `git apply --cached`
- [ ] Discard changes, with confirmation
- [ ] Commit box: message editor, amend toggle, recent-message recall
- [ ] File-system watcher so external changes refresh the view automatically

## M3 — Branching and remote operations

- [ ] Checkout, create, rename and delete branches from the sidebar
- [ ] Fetch, pull and push with progress and cancellation
- [ ] Credential-helper passthrough (no credentials ever handled in-process)
- [ ] Merge and rebase onto, with conflict detection
- [ ] Cherry-pick and revert from the commit context menu
- [ ] Tag creation and deletion
- [ ] Stash push / apply / pop / drop with a diff preview

## M4 — Diff and history depth

- [ ] Side-by-side diff view (*Side-by-Side Diff*)
- [ ] Word-level intra-line highlighting
- [ ] Syntax highlighting in the diff
- [ ] Adjustable context lines and whitespace-ignoring modes
- [ ] File history view — follow a single path through history (*History View*)
- [ ] Blame view with per-line commit attribution (*Blame View*)
- [ ] Image diffs for common formats (*Image Diffs*)
- [ ] Repository file tree at any revision

## M5 — Advanced workflows

- [ ] Interactive rebase editor: reorder, squash, edit, drop (*Interactive rebase*)
- [ ] Reflog browser for recovering lost commits (*Reflog*)
- [ ] Submodule listing and update
- [ ] Git LFS status and locking
- [ ] Git-flow branch operations
- [ ] GPG signature verification indicators

## M6 — Product polish

- [ ] Multi-repository tabs and a recent-repositories list (*Repository manager*)
- [ ] Commit search and filtering by author, message, path and date
- [ ] Incremental history paging beyond the current 2000-commit cap
- [ ] Light theme alongside the current dark one
- [ ] Keyboard shortcut map and a command palette
- [ ] Settings: diff context, date format, external diff/merge tool
- [ ] Single-file self-contained builds per platform

## Explicitly out of scope

Hosting-provider integration (pull requests, CI status, notifications), telemetry, and any
account or licensing system. See `docs/SPEC.md` §2.
