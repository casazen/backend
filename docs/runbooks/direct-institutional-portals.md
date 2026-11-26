# Direct institutional submissions for the Como pilot

## Environments and tests

| Service | Public verification | Local end-to-end test |
| --- | --- | --- |
| Alloggiati Web | The official single-structure and apartment SOAP services expose authentication, record validation, submission and receipt retrieval. The Test operation validates records on the authenticated service. No public test tenant or separate sandbox for Como was found. [Police service](https://alloggiatiweb.poliziadistato.it/service/) | Run the DirectAlloggiati tests with PostgreSQL and the simulated SOAP transport. They exercise the submission ledger and private receipt storage. |
| ROSS1000 Lombardia | The regional WSDL is publicly readable. Its movement operations submit data. Sardinia publishes a GIES test environment with credentials issued on request. Its endpoint is specific to that installation. [Lombardia WSDL](https://www.flussituristici.servizirl.it/Turismo5/app/ws/checkinV2?wsdl) · [Sardinia test environment](https://operatori.sardegnaturismo.it/it/allineamento-ross-1000-e-programma-gestionale-pms) | Run the Ross1000 tests against PostgreSQL with the simulated regional transport. In Development or Testing, `Ross1000:LocalPreview=true` enables offline preview with the synthetic structure code. |
| StayTour Como | The operator portal offers SPID/CIE for hosts. No public Como filing API, file specification or sandbox credentials were identified. [Municipal procedure](https://www.comune.como.it/servizi/Imposta-di-soggiorno/) · [Portal](https://imposta-soggiorno.org/como/index_op.php) | Test the monthly working figures and document the municipality's confirmation separately from payment. |

## Enable one property

1. Open the Italian property's documents and institutional portals. Store its technical account for each service. For a Questura apartment account, use the apartment ID assigned by the police. For ROSS1000, check the regional structure code and confirm the municipality belongs to the Como pilot.
2. Reconcile previous submissions, including open stays and ROSS1000 check-in identifiers. Turn off the previous provider's remote schedules for police and statistics, including any ROSS1000 forwarding to the police. Choose a date whose records have not already been sent. Ambiguous legacy submissions need reconciliation before the switch.
3. Live transport requires Production and the relevant explicit setting: `Alloggiati:DirectEnabled=true` or `Ross1000:Enabled=true`. Keep both settings off in development. No external credentials are shipped. A deployment alone leaves existing properties on their previous sender.
4. Activate each service separately in the property panel. Activation records today's Europe/Rome date. The first cutover blocks institutional guest payloads to Octorate for both services, including drafts and retries. The other service is handled manually until activated. Booking and inventory synchronization use separate paths.
5. Alloggiati checks the account before activation. The dispatcher checks eligible arrivals every five minutes and retrieves receipts hourly. After rotating a secret, verify the account again. A partial or uncertain outcome needs reconciliation against the official portal before any new filing. Keep the receipt PDF and its date.
6. ROSS1000 is operator-confirmed. Review the daily movement, including capacity, and confirm the same preview before sending. Use the regional portal for uncertain results, removals requiring manual correction and stays that arrived before cutover. An accepted daily movement is specific to that day's data.
7. For Como tax, review the host's figures by property and checkout month, resolve uncertain departures and reconcile intermediary statements. Submit in StayTour with SPID/CIE. Archive the confirmed declaration in CasaZen separately from the payment evidence.

Disabling live transport stops new calls. It does not reassign a migrated property to Octorate. Check both ledgers for in-flight work before changing a sender externally.
