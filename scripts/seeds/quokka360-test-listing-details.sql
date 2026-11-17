-- Verified listing enrichment for Quokka360 TEST only. Rates unavailable without travel dates.
-- Sources: https://quokka.kross.travel/lario-suite-brienno
-- https://quokka.kross.travel/urban-residence-06
-- https://www.airbnb.com/rooms/1578282631029758166 (Florida source URL unavailable)
BEGIN;
UPDATE casazen_test."Properties"
SET "ListingTitle"='Lario Suite Brienno — bilocale con balcone e garage a Como',
"Description"='Bilocale di 56 m² al secondo piano di un edificio con ascensore a Como. Ospita fino a quattro persone: camera matrimoniale e divano letto nel soggiorno. La cucina aperta sulla zona giorno è attrezzata con forno, microonde, frigorifero, lavastoviglie e macchina per il caffè. Il bagno dispone di doccia. Completano l’alloggio balcone privato, garage, Wi-Fi, aria condizionata e lavatrice. Biancheria, asciugamani e kit di cortesia inclusi. Il divano letto viene lasciato in configurazione divano; la biancheria si trova nell’armadio vicino. Accesso autonomo dopo il check-in online.',
"AccommodationType"=0,"Beds"=2,"SmokingPolicy"=0,"PetsPolicy"=2,
"CheckInTo"='23:59',"PostalCode"='22100',
"Amenities"=ARRAY[1,2,10,11,20,21,22,23,24,26,30,31,32,44,50,52,71,80,100],
"HouseRules"='Vietato fumare. Animali ammessi. Vietate le attività commerciali. Ospiti registrati tramite check-in online prima dell’arrivo. Arrivo dalle 15:00 alle 23:59; partenza entro le 10:00. Restituire le chiavi secondo le istruzioni. Divano letto non preparato all’arrivo; biancheria nell’armadio vicino.',
"PhotoUrls"=array_prepend('https://cdn.krossbooking.com/quokka360/images/3/626/17627875987684.jpg',array_remove("PhotoUrls",'https://cdn.krossbooking.com/quokka360/images/3/626/17627875987684.jpg')),
"UpdatedAt"=now()
WHERE "Id"='36e556c2-943e-44d7-86db-dea0567ea8a1' AND "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' AND "IsTestData" AND NOT "IsDeleted";
UPDATE casazen_test."Properties"
SET "ListingTitle"='Urban Residence 06 — bilocale con balcone a Lugano',
"Description"='Bilocale a Viganello, Lugano, al primo piano di un edificio con ascensore. Può accogliere quattro persone nella camera matrimoniale e nel divano letto del soggiorno. La zona giorno comprende cucina attrezzata, tavolo da pranzo e televisore; il bagno ha una doccia e una lavatrice. Balcone privato, Wi-Fi e dotazioni per il bucato. Biancheria, asciugamani e kit di cortesia inclusi. Il divano letto non è preparato prima dell’arrivo: la biancheria è riposta nell’armadio vicino. Nessun parcheggio privato; disponibili parcheggi pubblici in Via Emilio Rava. Accesso autonomo dopo il check-in online.',
"AccommodationType"=0,"Beds"=2,"SmokingPolicy"=0,"PetsPolicy"=2,"CheckInTo"='23:59',
"Amenities"=ARRAY[1,2,11,20,21,23,24,26,27,30,31,44,71,80,100],
"HouseRules"='Vietato fumare. Animali ammessi. Vietate le attività commerciali. Check-in online prima dell’arrivo e accesso autonomo. Arrivo dalle 15:00 alle 23:59; partenza entro le 10:00. Nessun posto auto privato. Divano letto non preparato all’arrivo; biancheria nell’armadio vicino.',
"PhotoUrls"=array_prepend('https://cdn.krossbooking.com/quokka360/images/3/692/17482463957843.jpg',array_remove("PhotoUrls",'https://cdn.krossbooking.com/quokka360/images/3/692/17482463957843.jpg')),
"UpdatedAt"=now()
WHERE "Id"='36e556c2-943e-44d7-86db-dea0567ea8a2' AND "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' AND "IsTestData" AND NOT "IsDeleted";
UPDATE casazen_test."Properties"
SET "ListingTitle"='Florida Ascona Room 10 — camera con balcone e parcheggio',
"Description"='Camera ad Ascona per due ospiti, con letto matrimoniale, bagno dedicato e balcone privato. Dispone di armadio, scrivania con poltrona, Wi-Fi e posto auto esterno riservato nell’area condominiale. Biancheria, asciugamani e kit di cortesia inclusi. Il frigorifero non è presente. La posizione permette di raggiungere il centro e il lungolago di Ascona. Accesso autonomo con cassetta portachiavi dopo il completamento del check-in online.',
"AccommodationType"=4,"Beds"=1,"SmokingPolicy"=0,"PetsPolicy"=2,"PartiesPolicy"=0,
"CheckInTo"='23:59',"QuietHoursFrom"='22:00',"QuietHoursTo"='07:00',
"Amenities"=ARRAY[1,4,44,50,80,100],
"HouseRules"='Massimo 2 ospiti registrati. Vietato fumare o usare sigarette elettroniche all’interno. Vietati feste, eventi disturbanti e rumori eccessivi. Silenzio dalle 12:00 alle 14:00 e dalle 22:00 alle 07:00. Animali ammessi secondo le condizioni dell’annuncio; non lasciarli incustoditi. Check-in online obbligatorio prima dell’arrivo. Arrivo dalle 15:00; partenza entro le 10:00. Frigorifero non presente. Riprese e attività commerciali richiedono autorizzazione.',
"UpdatedAt"=now()
WHERE "Id"='36e556c2-943e-44d7-86db-dea0567ea8a3' AND "OrgId"='ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8' AND "IsTestData" AND NOT "IsDeleted";
COMMIT;