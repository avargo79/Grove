# Grove — Specification

A visual Git client for .NET, modelled on the interaction design of [Fork](https://git-fork.com/).

## 1. Why this exists

Fork is an excellent Git GUI, but its commercial licence makes it unusable in workplaces that
restrict paid third-party developer tooling. This project reimplements the parts of that experience
that matter day to day, under a licence that has no procurement barrier:

| Component | Licence | Notes |
| --- | --- | --- |
| This project | MIT | No per-seat cost, no activation, no telemetry |
| .NET 10 | MIT | Runtime and SDK |
| Avalonia UI | MIT | Cross-platform XAML UI |
| CommunityToolkit.Mvvm | MIT | Source-generated MVVM primitives |
| git | GPL-2.0 | Invoked as an external process, never linked |

Because git is executed as a separate process rather than linked as a library, this codebase carries
no GPL obligation. That was a deliberate choice — see §3.

## 2. Goals and non-goals

### Goals

- Read a repository fast and render its history the way Fork does: a coloured, laned commit graph
  with ref badges, an inline commit detail pane, and a readable diff.
- Behave exactly like the user's own `git` — same config, same credential helpers, same hooks, same
  LFS setup. No surprises when the GUI and the terminal disagree.
- Run identically on Windows, macOS and Linux from a single codebase.
- Stay keyboard-navigable; the commit list is the primary surface.

### Non-goals

- Not a git reimplementation. There is no object database code here.
- Not a hosting-provider client. Pull requests, code review and CI dashboards are out of scope.
- Not a merge tool. Conflict resolution hands off to the user's configured `merge.tool`.
- No telemetry, no account, no update service.

## 3. Architecture

```
Grove.App  (Avalonia, MVVM)          — views, view models, custom graph renderer
      │  project reference
      ▼
Grove.Core (no UI dependencies)      — git process wrapper, parsers, graph layout
      │  Process.Start
      ▼
git (the user's own binary)
```

`Grove.Core` deliberately references nothing from Avalonia. Everything it exposes is a plain
record or interface, which is what makes the whole parsing and layout surface unit-testable without
a UI harness.

### 3.1 Git access: CLI, not libgit2

Every git operation shells out through `GitCommandRunner`. The alternative — LibGit2Sharp — was
rejected because:

- libgit2 lags git on newer features (partial clone, sparse checkout, newer index formats).
- Credential helpers, `core.hooksPath`, conditional includes and LFS smudge filters all live in the
  git CLI's behaviour. Reimplementing them is a bug source.
- Native binaries per platform complicate a "copy the folder and run it" deployment.

The cost is process overhead per call, which is mitigated by batching (one `git log` for the whole
graph) and cancelling superseded work.

Invocations are pinned for parseability:

- `--no-optional-locks` so a read never fights a concurrent terminal session.
- `-c color.ui=false` and `LC_ALL=C` so output is stable regardless of user config or locale.
- `GIT_TERMINAL_PROMPT=0` so a credential prompt fails fast instead of hanging the UI.
- NUL-delimited output (`-z`) wherever git offers it, and the ASCII unit separator (``) as the
  field delimiter in custom `--pretty=format:` strings, so paths and subjects containing spaces,
  commas or quotes need no unquoting.

### 3.2 The commit graph

`CommitGraphBuilder` converts a topologically ordered commit list into per-row drawing instructions.

Lanes are **stable**: once a lane index is allocated it never shifts sideways, so a branch renders as
an unbroken vertical line and only genuine branch/merge points curve. The algorithm walks commits
newest-first, maintaining a sparse array of lanes where each lane holds the sha it is waiting for:

1. If a lane is already waiting for this commit, take that lane over and emit an `Incoming` edge.
   Otherwise allocate the leftmost free lane and a fresh colour — this is a branch tip.
2. Every other occupied lane emits a straight `Through` edge across the row.
3. Reserve a lane per parent, emitting an `Outgoing` edge to each. The **first parent inherits the
   commit's own lane and colour**, which is what keeps mainline history reading as one continuous
   line. Additional parents (merges) take new lanes and new colours.

A parent lane is only ever reserved once, so when several branch tips share a parent the extra tips
bend into the single reserved lane rather than running parallel columns down to a join. Freed lanes
are reused from the left, which bounds graph width to the number of *concurrently live* branches
rather than the total branch count.

`GraphRowControl` renders one row: straight lines for same-lane segments, cubic béziers for lane
changes, a filled dot for a normal commit and a hollow ring for a merge.

### 3.3 Staging a selection of lines

Git has no "stage these lines" command. Partial staging works by describing the selection as a
patch and feeding it to `git apply --cached` over stdin, which is what `PatchBuilder` produces.

The rules follow from which side of the index git is matching against:

- **Staging** applies forward, so the file on disk is the "new" side and the index is the "old"
  side. An unselected addition is simply left out of the patch; an unselected *removal* has to be
  rewritten as a context line, because that line stays in the index.
- **Unstaging** reverse-applies against the index, so the roles swap and so do the rules: an
  unselected addition becomes context, an unselected removal is dropped.

Hunk headers are then recomputed from the rewritten line counts, and each emitted hunk shifts the
far side's numbering by the running delta of everything already in the patch. Getting this wrong
produces a patch that either fails to apply or silently stages the wrong lines, which is why the
integration suite asserts on the resulting index contents rather than on the generated patch text.

Patches go over stdin rather than through a temporary file so a cancelled operation leaves nothing
behind.

### 3.4 Operations that can stop part-way

A merge or rebase that hits conflicts has not failed — the user is now mid-operation and expected
to resolve, then continue or abort. `OperationResult` models that directly, with outcomes of
`Succeeded`, `Conflicted`, `Failed` and `Cancelled` (the user backing out of a confirmation), so
conflicts never surface as exceptions or as red error text.

`GitHistoryOperations.GetStateAsync` detects an in-progress operation from the marker files git
leaves in `.git` (`MERGE_HEAD`, `rebase-merge/`, `CHERRY_PICK_HEAD`, `REVERT_HEAD`, `BISECT_LOG`)
and lists the still-conflicted paths from the index. The UI turns that into a banner offering
Continue or Abort.

Two things this depends on:

- **Editor suppression.** `GIT_EDITOR`, `GIT_SEQUENCE_EDITOR` and `EDITOR` are all pinned to
  `true`. Without them `git rebase --continue`, `git merge` and `git commit` launch an editor and
  block forever with nothing on screen to explain why.
- **Credentials stay out of process.** `GIT_TERMINAL_PROMPT=0` means git may use the user's
  credential helper but can never fall back to prompting on a terminal that does not exist. A
  missing credential fails fast with an explanation rather than hanging the UI.

Long-running network commands stream their stderr through `RunWithProgressAsync`, which splits on
both `\n` and `\r` because git writes progress as carriage-return updates on a single line.

### 3.5 Reading a diff

Three pieces sit between git's unified diff and what ends up on screen.

**Word-level diffing.** Git reports a one-word edit as a whole line removed and a whole line added.
`WordDiff` pairs each removal with the addition that follows it and runs an LCS over word-ish
tokens, where punctuation is its own token — otherwise a changed bracket marks the whole expression
as changed. Only runs of removals immediately followed by runs of additions are paired; an
unrelated insertion has nothing to be compared against.

**Side-by-side.** `SideBySideDiff` puts each removal opposite its replacement and pads whichever
side runs out, so the two columns never drift apart. Every row has at least one populated side.

**Syntax highlighting.** `SyntaxHighlighter` is deliberately small — comments, strings, numbers and
keywords, per line, no grammar files and no TextMate engine. A diff shows fragments of files out of
context, so a real parser would not have the surrounding code it needs anyway. Multi-line block
comments are not tracked across calls for the same reason: guessing wrong would mis-colour every
line after an unclosed quote.

These two colourings partition the same line at different boundaries, so `DiffRunBuilder` cuts runs
at the union of both. A changed word that spans a keyword boundary keeps its syntax colour *and* its
highlight rather than having to pick one.

Options — context lines, whitespace modes, rename detection — travel as a `DiffOptions` record and
change what git is asked for, so the view re-reads when they change. Presentation-only toggles
(syntax on/off, unified/side-by-side) do not: both layouts are built at load time so switching is
free.

### 3.6 Refreshing

A debounced `RepositoryWatcher` watches the work tree and `.git`, coalescing bursts (editor
autosaves, git's multi-file writes) into a single reload. The work-tree watcher ignores `.git`
entirely — the dedicated watcher covers it, and git's lock files would otherwise fire constantly.

### 3.7 Interactive rebase

Git expects a human to edit the plan file it writes. Rather than opening an editor,
`GIT_SEQUENCE_EDITOR` is pointed at a generated script whose only job is to overwrite that file
with the plan built in the UI.

Two things this depends on, both of which caught the implementation out first:

- **The environment beats config.** Setting `sequence.editor` per invocation does nothing, because
  this codebase pins `GIT_SEQUENCE_EDITOR=true` for every call (see §3.4) and the environment wins.
  The override goes through `RunWithEnvironmentAsync` instead.
- **Exit code 0 does not mean finished.** Git exits successfully when it stops at an `edit`: a
  deliberate pause, not a failure. The repository state has to be consulted either way, or a paused
  rebase is reported as a completed one.

The editor value is simply `cp "<our plan file>"`. Git runs it through a shell and appends its own
plan file as the argument, so that one command is the whole program — no script file, no execute
bit, and one code path on every platform Git itself runs on.

`reword` is written to the plan as `edit` for the same reason editors are suppressed everywhere
else — git's reword opens one, and silently keeping the old message would be worse than stopping.

### 3.8 The shell

`MainWindow` is a tab host over `ShellViewModel`; one `MainViewModel` per open repository, with the
whole repository UI extracted into `RepositoryView`. The window keeps only what needs a window:
the folder picker, the confirm and prompt dialogs, and the blame, history, rebase, reflog, settings
and palette windows. Opening a repository that is already open selects its tab rather than adding
a second view of the same thing.

Settings persist as JSON under the user's application data. Every failure path — missing file,
corrupt JSON, unreadable directory — returns defaults, because settings going bad must never stop
the app from starting.

The palette and the keyboard shortcuts are built from one `CommandCatalog`, so a binding and its
palette entry cannot disagree about what a key does.

### 3.9 Theming

Both palettes live in `ThemeDictionaries` under identical keys, and every theme brush is resolved
with `DynamicResource`. That is what allows the switch at runtime: a `StaticResource` would bind
the dark value permanently. The syntax colours in `DiffTextBlock` are the one place this is easy to
get wrong, since they are code rather than XAML.

### 3.10 Threading

All git calls are async over `Process`. Selection changes cancel the in-flight detail/diff load
through a `CancellationTokenSource`, so holding an arrow key down does not queue up work.

## 4. Data model

| Type | Source command |
| --- | --- |
| `Commit` | `git log --topo-order --pretty=format:…` |
| `GitRef` | `git for-each-ref` |
| `WorkingTreeStatus` | `git status --porcelain=v2 --branch -z -uall` |
| `CommitDetail` | `git log -1 --format=%B` + `git diff-tree --root --name-status -r -M -z` |
| `DiffLine` | `git show --format= --patch -M` → `DiffParser` |
| `RepositoryState` | `.git` marker files + `git diff --diff-filter=U` |
| `StashEntry` | `git stash list --format=…` (the reflog, not `for-each-ref`) |
| `GitRemote` | `git remote -v` |
| `BlameLine` | `git blame --porcelain` |
| `TreeEntry` | `git ls-tree -r --long -z` |
| `ReflogEntry` | `git reflog --no-abbrev --format=…` |
| `RebaseTodoItem` | `git log --reverse --no-merges` |
| `SignatureStatus` | `git log --format=%H %G?` |
| `Submodule` | `git submodule status --recursive` |
| `CommitPage` | `git log --max-count --skip` plus a `CommitFilter` |
| `FileDiff` / `DiffHunk` | `git diff [--cached] -M` → `DiffParser.ParseFiles` |

The stash list comes from `git stash list` rather than `git for-each-ref`, because the latter only
ever reports `refs/stash` — the top of the stack. Reading the reflog is what makes every stash
addressable as `stash@{n}`.

Porcelain v2 is used for status rather than v1 because it reports staged and unstaged state as
separate fields, reports rename sources as their own NUL field, and carries branch/upstream headers
inline — all three of which v1 forces you to infer.

## 5. Testing strategy

Three layers:

- **Unit tests** over the pure functions: graph layout, diff parsing, and the porcelain parsers.
  These pin down behaviour that is easy to break silently — line numbering across hunks, rename
  fields, lane reuse, colour wraparound.
- **Integration tests** that build a real throwaway repository in a temp directory and run the real
  git binary against it (`TestRepository`). These exist so that a change in git's output format
  fails the build rather than the app. They cover merges, renames, stashes, detached HEAD, binary
  files, untracked directories and paths containing spaces. The write path is covered the same way:
  every staging test asserts on the resulting **index contents**, not on the patch text, so a patch
  that applies but stages the wrong thing still fails.
- **Headless UI tests** in `Grove.App.Tests` boot the real application on Avalonia's headless
  platform with Skia, so the actual XAML, styles and custom controls are exercised. They assert on
  the realised visual tree, on layout (does the file list still fit on screen?), and on captured
  pixels. `ScreenshotGenerator` reuses the same path to produce the images in this repository.

Static analysis runs on every build via `SonarAnalyzer.CSharp` plus the .NET analyzers at
`AnalysisMode=Recommended`. `scripts/sonar-scan.sh` drives the full scanner against a real
SonarQube or SonarCloud instance when one is configured.

## 6. Known limitations

- History loads a page at a time; the graph is built over the commits actually loaded, so parents
  beyond the current page render as dangling lane ends until more is loaded.
- Diffs are text-only; image diffs are on the roadmap, not implemented.
- The file tree builds its nesting from git's flat path list; git has no notion of a folder object
  to read here.
- On macOS the published build is a `.app` bundle rather than a bare executable: recent macOS
  kills an adhoc-signed loose binary on launch with no crash report and nothing in the log.
- The Windows and Linux builds have been produced but never launched; only the macOS bundle was
  run and checked.
- The rebase sequence-editor script is written per platform (a shell script, or a batch file on
  Windows); only the Unix path is covered by the test suite on this machine.
- Syntax highlighting is per-line and extension-based; it will not colour a fragment that begins
  inside a multi-line block comment.
- Conflict resolution hands off to the user's own editor or `merge.tool`; there is no built-in
  three-way merge view.
- Discarding changes is genuinely destructive and is always gated behind a confirmation dialog.
