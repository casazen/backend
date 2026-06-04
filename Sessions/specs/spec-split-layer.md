# Analisi strutturata del layer switch per ruoli e contesti in una web application

## Panoramica

Per una web application con aree distinte come **affitti brevi**, **affitti lungo termine** e **pannello admin**, l'approccio più solido non è trattare queste sezioni come semplici pagine abilitate da ruoli globali, ma come **contesti applicativi** o **workspace** separati.[1][2] Questo modello è coerente con le architetture multi-tenant moderne, dove l'accesso viene valutato nel contesto attivo e non solo sul ruolo astratto dell'utente.[1][2]

Il problema per cui l'utente deve inserire manualmente la route giusta nasce di solito da una combinazione di routing piatto, redirect post-login poco strutturato e controllo accessi distribuito in modo incoerente tra menu, frontend e backend.[2][3] Un approccio strutturato risolve questo problema introducendo un livello esplicito di contesto, una route architecture coerente e una sorgente unica di verità per navigazione e autorizzazioni.[1][3]

## Problema architetturale

Quando l'applicazione usa solo RBAC classico, il modello implicito diventa `utente -> ruolo -> pagina`.[2][3] Questo schema funziona in applicazioni semplici, ma tende a rompersi quando uno stesso utente può operare in più aree con privilegi diversi, perché il ruolo non basta più a rappresentare il perimetro funzionale corretto.[1][2]

Nel caso descritto, “affitti brevi”, “affitti lungo termine” e “admin” non sono semplicemente tre viste, ma tre **aree operative** con obiettivi, menu, flussi e permessi distinti.[1][4] Se queste aree non hanno un'identità architetturale propria, l'app tende a delegare all'utente la responsabilità di conoscere la route corretta, che è esattamente il comportamento da evitare in una UX solida.[4][3]

## Modello consigliato

Il modello raccomandato separa chiaramente identità, contesto e permessi.[1][2] In pratica, l'utente ha una identità globale, ma i suoi ruoli e permessi vengono valutati all'interno di un contesto attivo come `short-rent`, `long-rent` o `admin`.[1][2]

Una struttura concettuale efficace è la seguente:

- **User**: identità globale dell'utente.[1]
- **Context / Workspace**: area operativa selezionabile, per esempio `short-rent`, `long-rent`, `admin`.[1][4]
- **Membership**: relazione tra utente e contesto.[1][2]
- **Role**: ruolo assegnato nel contesto specifico, non necessariamente globale.[1][2]
- **Permission**: capability fine-grained, come `booking.read`, `contract.create`, `admin.users.manage`.[2][3]

Questo approccio evita la cosiddetta **role explosion**, cioè la proliferazione di ruoli globali che cercano di codificare tutte le combinazioni possibili di responsabilità e moduli.[2] Inoltre rende più semplice evolvere il sistema nel tempo, perché i permessi possono crescere per capability invece che per profili monolitici.[2][3]

## Routing e navigazione

L'applicazione dovrebbe adottare URL canonici basati sul contesto, ad esempio `/app/short-rent/*`, `/app/long-rent/*` e `/app/admin/*`.[4][3] In questo modo il contesto corrente diventa parte esplicita della navigazione e tutte le route figlie ereditano in modo naturale il perimetro funzionale corretto.[4][3]

È utile introdurre una route manifest centralizzata, dove ogni route dichiara almeno questi metadati:

| Campo | Significato |
|---|---|
| `path` | Path canonico della route |
| `context` | Contesto di appartenenza, ad esempio `short-rent` |
| `requiredPermissions` | Permessi richiesti per accedere |
| `navLabel` | Etichetta da mostrare nel menu |
| `isDefault` | Indica se è la landing predefinita del contesto |

Una configurazione di questo tipo permette di derivare da un unico punto sia il menu laterale sia i redirect di default sia le guardie di accesso.[3][2] La centralizzazione riduce il rischio di incoerenza tra ciò che l'utente vede nel menu e ciò che può realmente aprire via URL.[2][3]

## Context switcher

Lo switcher non dovrebbe essere pensato come un semplice “role switcher” visibile solo quando un utente ha due o più ruoli, ma come un vero **context switcher** o **workspace switcher** integrato nell'application shell.[4] Questo pattern viene usato nelle app modulari perché rende esplicito il fatto che l'utente sta cambiando area operativa, non semplicemente privilegi astratti.[4][2]

Il comportamento consigliato è il seguente:

1. Dopo il login, l'app recupera i contesti disponibili e i permessi associati.[1][2]
2. Se esiste un solo contesto disponibile, esegue redirect automatico verso la landing di quel contesto.[1]
3. Se esistono più contesti, apre una schermata di selezione iniziale oppure riattiva l'ultimo contesto usato.[1][4]
4. Una volta scelto il contesto, menu, breadcrumb, dashboard e route tree vengono derivati da quel contesto.[4][3]
5. Se l'utente prova ad aprire una route non compatibile con il contesto o senza permessi, l'app effettua redirect verso la home del contesto valido oppure mostra un 403 gestito.[2][3]

Questo elimina la necessità di digitare manualmente la route e trasforma il cambio area in un flusso intenzionale, comprensibile e consistente.[4][3]

## Backend e autorizzazione

Dal lato backend è preferibile evitare ruoli globali come `Admin`, `AffittiBrevi`, `AffittiLunghi` se questi rappresentano in realtà aree operative.[2] Le architetture di autorizzazione scalabili suggeriscono di modellare i permessi in modo contestuale, così da poter valutare policy e capability nel perimetro corretto.[2]

Un modello dati minimo può essere:

```text
User
UserContextMembership
- UserId
- ContextKey
- RoleKey

Role
- RoleKey

RolePermission
- RoleKey
- PermissionKey
```

In alternativa, una variante ancora più flessibile è basata su `User`, `Workspace`, `Membership` e `PermissionGrant`, soprattutto se il sistema deve crescere con feature flag, tenant o regole più articolate.[1][2] In entrambi i casi, il backend dovrebbe essere la fonte autorevole per la decisione finale di accesso, mentre il frontend dovrebbe limitarsi a riflettere tali decisioni nella navigazione e nell'interfaccia.[2][3]

## Anti-pattern da evitare

Gli anti-pattern principali in questo scenario sono i seguenti:

- Route piatte senza namespace di contesto, ad esempio `/prenotazioni`, `/contratti`, `/users`.[3]
- Redirect post-login fisso, scollegato dai contesti realmente disponibili.[1]
- Menu generato da ruoli globali invece che dal contesto attivo.[2][4]
- Verifica permessi solo nel frontend, con backend permissivo o incoerente.[2][3]
- Switcher concepito come eccezione UX invece che come elemento strutturale dell'application shell.[4]

Questi problemi tendono a produrre inconsistenza tra esperienza utente, struttura delle route e logica autorizzativa.[2][3] Nel tempo diventano particolarmente costosi da mantenere perché ogni nuova area o variante di accesso obbliga ad aggiungere condizioni sparse nel codice invece di estendere un modello coerente.[2]

## Raccomandazione operativa

Per il caso in esame, la soluzione più ordinata è trasformare i tre layer in tre **app context ufficiali**: `short-rent`, `long-rent` e `admin`.[1][4] Da qui discendono alcune scelte implementative molto concrete.[2][3]

- Introdurre route canoniche con prefisso di contesto, ad esempio `/app/:context/...`.[3]
- Mantenere uno `activeContext` nello stato dell'applicazione.[4]
- Usare una route manifest centralizzata come sorgente per route, menu e redirect.[3]
- Mostrare sempre uno switcher di area nell'header o nella sidebar, non solo in casi eccezionali.[4]
- Calcolare la landing page in funzione del contesto attivo e dei permessi effettivi.[1][2]
- Demandare il controllo finale dei permessi al backend o a un authorization layer dedicato.[2]

Dal punto di vista del naming, espressioni come **workspace switcher**, **area switcher** o **context switcher** sono più precise di “role switcher”, perché descrivono meglio ciò che accade dal punto di vista del dominio applicativo.[4][2]

## Esempio di comportamento atteso

Si consideri un utente che ha accesso a `short-rent` e `admin`, ma non a `long-rent`.[1][2] Dopo il login, l'app può riaprire `short-rent` come ultimo contesto usato e portare l'utente a `/app/short-rent/dashboard`.[1][3]

A quel punto la sidebar mostra soltanto le voci rilevanti per `short-rent`, mentre il context switcher permette il passaggio intenzionale a `admin`.[4][3] Se l'utente prova a navigare manualmente verso `/app/long-rent/contracts`, il sistema dovrebbe riconoscere l'assenza di membership o permessi e reindirizzare verso una route valida o rispondere con un 403 gestito.[2][3]

## Conclusione

L'approccio più strutturato consiste nel trattare i tre layer come contesti applicativi distinti, con routing contestuale, navigazione derivata da configurazione centralizzata e autorizzazione valutata nel perimetro corretto.[1][2][3] In questo modo l'utente smette di dover conoscere la route corretta e l'applicazione acquisisce una base molto più robusta per crescere senza accumulare logica fragile e duplicata.[2][4][3]