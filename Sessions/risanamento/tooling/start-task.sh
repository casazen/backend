#!/usr/bin/env bash
# usage: start-task.sh TASK_ID repo [repo...]   (repo = backend|frontend|mobile)
set -euo pipefail
source /home/user/wt/bin/env.sh
TASK=$1; shift
for R in "$@"; do
  MAIN=/home/user/$R; WT=/home/user/wt/$TASK/$R; BR=fix/$TASK
  mkdir -p /home/user/wt/$TASK
  if [ -d "$WT/.git" ] || [ -f "$WT/.git" ]; then echo "EXISTS $WT (branch $(git -C $WT branch --show-current))"; continue; fi
  if git -C $MAIN show-ref --verify -q refs/heads/$BR; then git -C $MAIN worktree add -q $WT $BR; else git -C $MAIN worktree add -q -b $BR $WT $INT_BRANCH; fi
  if [ "$R" != backend ]; then ln -sfn $MAIN/node_modules $WT/node_modules; fi
  echo "READY $WT  branch=$BR  base=$(git -C $WT rev-parse --short HEAD)"
done
