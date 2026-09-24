#!/usr/bin/env bash
# usage: integrate.sh TASK_ID repo sync|publish
#  sync    : merge the latest integration branch into fix/TASK (resolve conflicts, commit, rebuild, retest)
#  publish : fast-forward the integration branch to fix/TASK (only if it already contains the latest integration head) and push
set -uo pipefail
source /home/user/wt/bin/env.sh
TASK=$1; R=$2; MODE=${3:-sync}
MAIN=/home/user/$R; WT=/home/user/wt/$TASK/$R; BR=fix/$TASK
cd "$WT" || { echo "ERROR: missing worktree $WT"; exit 1; }
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then echo "ERROR: uncommitted changes in $WT: commit first"; exit 1; fi
case "$MODE" in
sync)
  if git merge-base --is-ancestor $INT_BRANCH HEAD; then echo "UP-TO-DATE: $BR already contains $INT_BRANCH"; exit 0; fi
  if git merge --no-edit $INT_BRANCH >/tmp/merge-$TASK-$R.log 2>&1; then
    echo "MERGED latest $INT_BRANCH into $BR. Rebuild + rerun tests, then: integrate.sh $TASK $R publish"; exit 0; fi
  echo "CONFLICT merging $INT_BRANCH into $BR. Files:"; git diff --name-only --diff-filter=U
  echo "Resolve (keep BOTH sides' intent), git add, git commit --no-edit, rebuild + rerun tests, then publish."; exit 2;;
publish)
  exec 9>/home/user/wt/locks/$R.lock; flock 9
  if ! git merge-base --is-ancestor $INT_BRANCH HEAD; then echo "MOVED: $INT_BRANCH advanced meanwhile. Run sync again (and retest)."; exit 3; fi
  if git merge-base --is-ancestor HEAD $INT_BRANCH; then echo "NOTHING-TO-PUBLISH: $BR already in $INT_BRANCH"; exit 0; fi
  if [ -n "$(git -C $MAIN status --porcelain --untracked-files=no)" ]; then echo "ERROR: main checkout $MAIN dirty; ask orchestrator"; exit 4; fi
  OLDLOCK=$(git -C $MAIN rev-parse -q --verify HEAD:package-lock.json 2>/dev/null || echo none)
  git -C $MAIN merge --ff-only -q $BR || { echo "ERROR: ff-only failed"; exit 4; }
  NEWLOCK=$(git -C $MAIN rev-parse -q --verify HEAD:package-lock.json 2>/dev/null || echo none)
  if [ "$R" != backend ] && [ "$OLDLOCK" != "$NEWLOCK" ]; then (cd $MAIN && npm install --no-audit --no-fund >/dev/null 2>&1) && echo "shared node_modules updated"; fi
  ok=0; for i in 1 2 3 4; do if git -C $MAIN push -q origin $INT_BRANCH 2>/tmp/push-$TASK.log; then ok=1; break; fi; sleep $((2**i)); done
  [ $ok = 1 ] && echo "PUBLISHED $BR -> $INT_BRANCH @ $(git -C $MAIN rev-parse --short HEAD) (pushed)" || echo "PUBLISHED locally @ $(git -C $MAIN rev-parse --short HEAD), PUSH FAILED (orchestrator will retry)";;
*) echo "usage: integrate.sh TASK repo sync|publish"; exit 1;;
esac
