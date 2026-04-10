Update docs, commit all changes, and push to remote.

1. Review all changes made this session. Check whether CLAUDE.md or any READMEs need updates to reflect the work done — new features, changed architecture, new parameters, new commands, updated workflows, etc. There may be a root-level README.md plus module-level READMEs (e.g., `Docs/README.md`, `Installer/README.md`). Update whichever are affected. Keep updates minimal and avoid bloat — only add what's necessary to keep docs accurate. Don't rewrite sections that are already correct.
2. Run `git status` and `git diff` to review all changes. If there are no changes, report that and stop.
3. Run `git log --oneline -5` to see recent commit message style.
4. Stage all changed/new files (be specific — don't use `git add -A`). Do NOT stage files matching gitignore patterns or sensitive files (.env, credentials).
5. Write a concise commit message that summarizes the changes, following the style of recent commits. End the message with:
   `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`
6. Commit and then push to the current branch.
7. Report what was committed and pushed.
