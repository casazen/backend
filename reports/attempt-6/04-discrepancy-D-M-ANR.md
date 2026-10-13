# D-M-ANR

Mitigated for this run. Immediate `back` after `launchApp` raced the UI. Helper `e2e/helpers/open-calendar.yaml` waits, then backs from booking detail or `Invia richiesta`, then asserts `Calendario`. `launchApp.stopApp: false` keeps the Auth0 session. Do not `am force-stop` between flows.
