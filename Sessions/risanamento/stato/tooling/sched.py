#!/usr/bin/env python3
# usage: sched.py set ID STATUS [note] | sched.py ready | sched.py summary
import json,sys,functools
P='/tmp/claude-0/-home-user/ff7c6e50-c922-54c6-adf8-152faa99238a/scratchpad/plan/'
T=json.load(open(P+'tasks.json')); S=json.load(open(P+'status.json'))
ids=[x['id'] for x in T]; X={x['id']:x for x in T}
dep={i:([j for j in ids if j!=i and not j.startswith('FN-')] if X[i]['deps']==['*'] else X[i]['deps']) for i in ids}
@functools.lru_cache(None)
def depth(i): return 0 if not dep[i] else 1+max(depth(d) for d in dep[i])
OK={'done','partial','already'}
cmd=sys.argv[1]
if cmd=='set':
    S[sys.argv[2]]=sys.argv[3]; json.dump(S,open(P+'status.json','w'),indent=0)
    if len(sys.argv)>4: open(P+'notes.log','a').write(f"{sys.argv[2]} {sys.argv[3]}: {sys.argv[4]}\n")
    print('ok')
elif cmd=='ready':
    r=[i for i in ids if S[i]=='pending' and all(S[d] in OK for d in dep[i])]
    r.sort(key=lambda i:(depth(i),X[i]['wave'],ids.index(i)))
    for i in r: print(i, '|', X[i]['repos'], '|', X[i]['title'][:90])
elif cmd=='summary':
    from collections import Counter; print(Counter(S.values()))
    print('running:',[i for i in ids if S[i]=='running'])
