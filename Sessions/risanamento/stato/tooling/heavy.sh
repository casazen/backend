#!/usr/bin/env bash
# usage: heavy.sh <command...>  -- runs command holding one of N slots (N=2)
mkdir -p /home/user/wt/locks
while true; do
  for s in 1 2; do
    exec 8>/home/user/wt/locks/heavy.$s
    if flock -n 8; then "$@"; rc=$?; flock -u 8; exit $rc; fi
  done
  sleep 5
done
