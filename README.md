# Grove

A visual Git client for .NET, modelled on [Fork](https://git-fork.com/) — built so the same
workflow is available under an MIT licence with no procurement barrier.

Cross-platform (Windows, macOS, Linux) via Avalonia. Talks to your own `git` binary, so it honours
your existing config, credential helpers, hooks and LFS setup exactly.

![Grove showing a repository's commit graph, detail pane and diff](docs/screenshot.png)

The staging pane, with hunk- and line-level staging:

![The working copy pane with staged and unstaged files, a diff and the commit box](docs/screenshot-staging.png)

The light theme:

![The same repository in the light theme](docs/screenshot-light.png)

Side-by-side, with word-level and syntax colouring:

![A side-by-side diff with syntax colouring and word-level highlights](docs/screenshot-sidebyside.png)

A conflicted merge, with the way out offered up front:

![A merge in progress, with a banner offering Continue or Abort](docs/screenshot-conflict.png)

## Current state

All six milestones are complete. See [docs/ROADMAP.md](docs/ROADMAP.md) for what was built and
what was deliberately left out.

**Browsing**

- Coloured, laned commit graph with merge rings and branch/tag badges
- Sidebar of local branches, remotes, tags and stashes with ahead/behind indicators
- Commit detail pane with the full message and changed-file list
- Unified diff with per-side line numbers and add/remove colouring

**Working copy**

- "Uncommitted changes" pinned at the top of the history, as Fork does
- Staged / unstaged split, with stage, unstage and discard
- Hunk-level and line-level staging
- Commit box with an amend toggle and recent-message recall
- Automatic refresh when the repository changes on disk

**Branches, remotes and history**

- Fetch, pull (merge or rebase) and push, with streamed progress and cancellation
- Checkout, create, rename and delete branches; create and delete tags
- Merge and rebase, with a banner offering Continue or Abort while conflicts remain
- Cherry-pick, revert and reset from the commit context menu
- Stash push, apply, pop and drop

**Reading code**

- Side-by-side and unified diffs, switchable without re-reading anything
- Word-level highlighting, so a one-word edit reads as one word
- Syntax colouring for the common languages, with no extra dependencies
- Adjustable context and whitespace-ignoring modes
- Blame with per-line attribution, and per-file history that follows renames
- Image diffs shown before and after

**Rewriting and recovering**

- Interactive rebase: reorder, squash, fixup, edit and drop, with nothing touched until you start
- Reflog browser that flags commits no branch can reach, and recovers them onto a branch
- Submodule listing and update

**Browsing and integrations**

- Repository file tree at any revision, with contents and syntax colouring
- Git-flow start and finish for features, releases and hotfixes
- Submodule listing and update, and LFS tracked files and locks

**Getting around**

- Several repositories open at once as tabs, with a recent list
- Search history by message, author or path; load more rather than truncating at a fixed cap
- Light and dark themes
- Keyboard shortcuts, and a command palette (⌘K / Ctrl+K) listing every action with its binding

## Running it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and `git` on your `PATH`.

```bash
dotnet run --project src/Grove.App
```

Then use **Open Repository…** and pick any folder inside a git work tree.

## Building a release

```bash
./scripts/publish.sh            # for this machine
./scripts/publish.sh win-x64    # or any runtime identifier
./scripts/publish-all.sh        # every supported platform
```

Builds are self-contained, so the result runs without a .NET runtime installed — which is the
point, given the licensing constraint this project exists for. Windows and Linux get a single
executable; macOS gets a `.app` bundle, because recent macOS kills a bare adhoc-signed executable
on launch without explanation.

## Tests

```bash
dotnet test
```

The suite includes integration tests that create throwaway repositories in your temp directory and
run the real `git` binary against them.

## Screenshots

Every screenshot above is generated headlessly, so they can be refreshed without launching the app:

```bash
GROVE_SCREENSHOT=docs/screenshot.png GROVE_SCREENSHOT_REPO=/path/to/repo dotnet test tests/Grove.App.Tests --filter WriteScreenshot
```

`GROVE_SCREENSHOT_THEME=light` captures the light palette, and `GROVE_SCREENSHOT_VIEW` picks the
pane: `working` for the staging view, `sidebyside` for the two-column diff. Any other value is an
error rather than a silent fall-back, so a typo cannot overwrite one screenshot with another. The
conflict banner builds its own throwaway repository:

```bash
GROVE_SCREENSHOT_CONFLICT=docs/screenshot-conflict.png dotnet test tests/Grove.App.Tests --filter WriteConflictScreenshot
```

## Installing

No package manager, no installer, no administrator rights, and nothing written outside your own
home directory:

```bash
./scripts/install-local.sh
```

On macOS that puts `Grove.app` in `~/Applications`, which Spotlight and Launchpad index the same
as the system folder. On Linux it installs under `~/.local/share/grove` with a `grove` launcher
and a desktop entry. On Windows (from Git Bash) it installs to `%LOCALAPPDATA%\Programs\Grove` —
the path a per-user installer would use, and so the one most likely to already be permitted where
execution policy restricts it.

The build is self-contained, so the machine needs no .NET runtime — only `git` on `PATH`, since
Grove drives your own git rather than reimplementing it. Building needs the .NET SDK, which also
installs per-user without administrator rights:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
```

## The icon

The grove mark is drawn in code, in [GroveIcon](src/Grove.App/Controls/GroveIcon.cs), so one
definition covers every size from a 16px tray glyph to a 1024px macOS icon with no resampling in
between. After changing it, repack the assets:

```bash
./scripts/make-icons.sh
```

That rasterises the control and writes `grove.png` (the window icon), `grove.ico` (stamped into
the Windows executable) and `grove.icns` (the macOS bundle). The outputs are committed, so an
ordinary build or publish needs neither the script nor a Mac.

## Static analysis

`SonarAnalyzer.CSharp` and the .NET analyzers run on every build at `AnalysisMode=Recommended`, so
`dotnet build` surfaces Sonar findings locally with no server involved. To publish to a SonarQube
or SonarCloud instance:

```bash
SONAR_TOKEN=… SONAR_HOST_URL=… ./scripts/sonar-scan.sh
```

## Layout

```
src/Grove.Core   git process wrapper, output parsers, commit-graph layout (no UI dependencies)
src/Grove.App    Avalonia views, view models, custom graph renderer
tests/Grove.Core.Tests  parser, graph-layout and real-git integration tests
tests/Grove.App.Tests   headless Avalonia tests that render the window and inspect pixels
docs/SPEC.md       architecture and design rationale
docs/ROADMAP.md    milestone plan
```

## Credentials

Grove never handles your credentials. Git is run with terminal prompting disabled, so
authentication comes from your existing credential helper or SSH key — and a missing credential
fails immediately with an explanation instead of hanging.

## Licence

MIT. See [LICENSE](LICENSE).
