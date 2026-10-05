# Contributing to Butterfly

## Branches

| Branch | Purpose | How it changes |
|---|---|---|
| `release` | What is published. Default branch. Every merge is a release candidate. | **Only** through a pull request from `dev`. |
| `dev` | Integration: everything that is finished and tested, waiting for the next release. | Direct `--no-ff` merges of work branches. |
| `feature/<issue>-<slug>` | New functionality, for example `feature/2-http-query`. | Commits from its author. |
| `fix/<issue>-<slug>` | Bug fixes. | Commits from its author. |
| `docs/<issue>-<slug>` | Documentation only. | Commits from its author. |

Work branches always start from `dev` and are deleted once merged. Nobody commits or force-pushes to `release` or `dev` directly.

## Flow

```
issue #N ──► feature/N-slug (from dev) ──► merge --no-ff into dev ──► PR dev → release ──► merge
```

1. **Open an issue** describing the change, its scope and its acceptance criteria. Its number is the id used everywhere else.
2. **Branch from `dev`:**

   ```bash
   git switch dev
   git pull
   git switch -c feature/N-short-name
   ```

3. **Commit in coherent steps**, one area per commit when the change spans several (client, server, docs...). See [Commit messages](#commit-messages).
4. **Verify before merging:** the solution builds without errors or warnings, and the tests and samples pass:

   ```bash
   ./build.ps1 Test
   ./build.ps1 Samples
   ```

5. **Merge into `dev`** keeping the branch visible in the history, then push and delete the branch:

   ```bash
   git switch dev
   git merge --no-ff feature/N-short-name -m "Merge feature/N-short-name into dev: <summary> (#N)"
   git push origin dev
   git branch -d feature/N-short-name
   git push origin --delete feature/N-short-name
   ```

6. **Release:** open a pull request from `dev` to `release` that lists every issue it delivers with `Closes #N`. Merging it closes them.

## Commit messages

```
<Area>: <imperative summary> (#N)

Why the change is needed and what it does, wrapped at 72 columns.
List the notable decisions and anything a reviewer should know.

Refs #N
```

- `<Area>` is the library or part touched: `Chrysalis`, `Communication.Http`, `Serialization`, `README`, `build`...
- **Use `Refs #N` in commits, never `Closes #N`.** `release` is the default branch, so GitHub closes an issue when a commit or pull request carrying `Closes #N` reaches it. The issue should close when its change is released, through the `dev → release` pull request.

## Pull requests to `release`

- Title: what the release delivers, for example `Release: HTTP QUERY support`.
- Body: `Closes #N` for each issue, a summary per issue, behavior changes, and the local test results until CI exists.
- Merge with a merge commit (no squash, no rebase) so the `dev` history and its feature merges are kept.
