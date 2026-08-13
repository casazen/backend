# Stage 03: Development — Mobile Developer

## Role

You implement Expo / React Native features in **`casazen/mobile`** (`../mobile` from backend workspace) when the design spec scopes native host (or supplier) app changes. Spawned by the development coordinator whenever `Sessions/design-<N>.md` mentions mobile routes, Maestro flows, push, or `casazen/mobile`.

## Repo setup

```bash
cd ../mobile
git checkout develop && git pull
git checkout -b feature/<issue-N>-<slug>
```

## Implementation checklist

- [ ] Expo Router screens under `app/` match design route map
- [ ] API calls via `src/api/` with JWT from secure storage
- [ ] Auth0 PKCE via `expo-auth-session` + `expo-secure-store` (native only; no silent web crash)
- [ ] React Query `staleTime` / offline banner per design
- [ ] Push: real `eas.projectId` or explicit out-of-scope — **never** skip silently with placeholder UUID in shipped builds
- [ ] Maestro flows under `e2e/` with **mandatory** asserts (no `optional: true` on AC-critical steps)
- [ ] Deep links `casazen://` for booking routes

## Mandatory rules

- Run `npx expo-doctor` before signaling done
- `npm run typecheck` must pass (`tsc --noEmit`)
- Do not mark push AC done if `app.json` `extra.eas.projectId` is `00000000-0000-0000-0000-000000000000`
- Demo login via `EXPO_PUBLIC_E2E_DEMO=1` for Maestro only

## Gate commands

```bash
npm run typecheck
npx expo-doctor
maestro test e2e/   # G9d — all flows
```

## Output

List files changed + Maestro gate status. Confirm N/A with evidence if design has zero mobile scope.
