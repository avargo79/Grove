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

## M2 — Working copy and committing ✅

The daily-driver loop.

- [x] "Uncommitted changes" pinned as the first row of the commit list, as Fork does
- [x] Staged / unstaged split view over the working tree
- [x] Stage and unstage whole files, including deletions and untracked files
- [x] Stage and unstage individual hunks (*Stage/unstage changes line-by-line*)
- [x] Line-level staging via a synthesised patch through `git apply --cached`
- [x] Discard changes, with confirmation
- [x] Commit box: message editor, amend toggle, recent-message recall
- [x] File-system watcher so external changes refresh the view automatically

## M3 — Branching and remote operations ✅

- [x] Checkout, create, rename and delete branches from the sidebar
- [x] Fetch, pull (merge or rebase) and push with progress and cancellation
- [x] Credential-helper passthrough (no credentials ever handled in-process)
- [x] Merge and rebase onto, with conflict detection and a continue/abort banner
- [x] Cherry-pick and revert from the commit context menu
- [x] Reset (soft / mixed / hard) from the commit context menu
- [x] Tag creation and deletion
- [x] Stash push / apply / pop / drop with a diff preview

## M4 — Diff and history depth ✅

- [x] Side-by-side diff view (*Side-by-Side Diff*)
- [x] Word-level intra-line highlighting
- [x] Syntax highlighting in the diff
- [x] Adjustable context lines and whitespace-ignoring modes
- [x] File history view — follow a single path through history (*History View*)
- [x] Blame view with per-line commit attribution (*Blame View*)
- [x] Image diffs for common formats (*Image Diffs*)
- [x] Repository file tree at any revision (available in Core; no UI yet)

## M5 — Advanced workflows ✅

- [x] Interactive rebase editor: reorder, squash, fixup, edit, drop (*Interactive rebase*)
- [x] Reflog browser for recovering lost commits (*Reflog*)
- [x] Submodule listing and update
- [x] Git LFS status and locking (Core; no UI yet)
- [x] Git-flow branch operations (Core; no UI yet)
- [x] GPG signature status (Core; no UI yet)

Reword is written to the rebase plan as `edit`: git's own `reword` opens an editor, and with
editors suppressed that would silently keep the original message. Stopping hands the job to the
commit box, which is already the right place to amend a message.

## M6 — Product polish ✅

- [x] Multi-repository tabs and a recent-repositories list (*Repository manager*)
- [x] Commit search and filtering by message, author, path and date
- [x] Incremental history paging, replacing the fixed 2000-commit cap
- [x] Light theme alongside the dark one
- [x] Keyboard shortcuts and a command palette
- [x] Settings: theme, diff context, whitespace, highlighting, page size
- [x] Self-contained builds per platform

External diff and merge tool configuration was not built: conflict resolution already hands off to
the user's own `merge.tool`, so a second place to configure it would only be another thing to get
out of sync.

## Closing the gaps ✅

The three features that shipped in `GitFork.Core` without a way into them from the app:

- [x] Repository file tree at any revision, with file contents and syntax colouring
- [x] Git-flow start and finish for feature, release and hotfix branches
- [x] Submodule listing and update, plus LFS tracked files and locks

## Still open

Deliberately not built, and worth naming rather than leaving implied:

- Syntax highlighting is per-line, so a fragment beginning inside a multi-line block comment is
  not coloured as one.
- The rebase sequence-editor script has a Windows branch that the suite does not exercise on a
  Mac.
- The published Windows and Linux builds have not been run — only built. Only the macOS bundle
  was launched and checked.

## Explicitly out of scope

Hosting-provider integration (pull requests, CI status, notifications), telemetry, and any
account or licensing system. See `docs/SPEC.md` §2.
