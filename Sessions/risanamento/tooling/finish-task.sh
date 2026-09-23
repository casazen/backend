#!/usr/bin/env bash
# usage: finish-task.sh TASK_ID  -> removes worktrees whose branch is fully merged into the integration branch
source /home/user/wt/bin/env.sh
TASK=$1
for WT in /home/user/wt/$TASK/*; do
  [ -e "$WT/.git" ] || continue; R=$(basename $WT); MAIN=/home/user/$R; BR=fix/$TASK
  if git -C $MAIN merge-base --is-ancestor $BR $INT_BRANCH 2>/dev/null; then
    git -C $MAIN worktree remove --force $WT && git -C $MAIN branch -q -D $BR && echo "CLEANED $R"
  else echo "KEPT $WT (branch $BR not merged)"; fi
done
rmdir /home/user/wt/$TASK 2>/dev/null; true
