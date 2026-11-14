# DepoSimulator — Evenimente (incidente de producție)

15 evenimente, definite în `Assets/WarehouseDemo/Assets/Scripts/Events/`. Apar aleatoriu în timpul turei, gestionate de `GameEventManager`. Fiecare implementează `IGameEvent` (Apply/Remove).

## Stivuitor / rohlă

**BrokenFork** — furca unui stivuitor (sau a rohlei) se blochează: nu mai poate fi ridicată/coborâtă. Nu se remediază singură — trebuie găsit trusa de scule și ținut apăsat [E] la vehicul. Apare doar dacă echipa are un Operator utilaje.

**BrokenForklift** — un vehicul (stivuitor sau rohlă, ales aleatoriu) capătă manevrabilitate proastă (derapaj, viteză redusă) timp de 45 sec. Se remediază automat după 45 sec, sau mai repede cu trusa de scule (10 sec pentru Operator, 25 sec pentru ceilalți).

## Curent și incendiu

**PowerFailure** — se ia curentul: iluminat de noapte, bancul de asamblare blocat. Trebuie găsită trusa de scule și reparate toate cele 3 tablouri electrice (ține [E] 5 sec la fiecare). Nu se remediază singur.

**FireAlarm** — alarmă de incendiu: ecran pulsează roșu, sirenă, bancul blocat. Trebuie reparate tablourile electrice vizate (fără trusă de scule, doar ține [E]). Nu se remediază singur.

**RadioInterference** — paraziți pe radio, sunet înăbușit până la 120 sec. Dispecerul trebuie să găsească trusa de scule și să țină [E] la panoul radio. Dacă nimeni nu-l repară, se oprește automat după 120 sec (plasă de siguranță pentru solo).

## Bancul de asamblare

**BenchMalfunction** — bancul de asamblare e blocat 30 sec, apoi se deblochează singur.

**DefectivePart** — o piesă deja montată pe banc se dovedește defectă și e scoasă automat; Asamblorul trebuie să aducă un înlocuitor. Eveniment o singură dată (one-shot). Apare doar dacă echipa are un Asamblor.

## Paleți și rafturi

**PalletDrop** — avertisment 1 sec, apoi o paletă explodează: cutiile zboară în toate direcțiile, jucătorii din apropiere pot cădea (ragdoll). Fugi din zonă la avertisment.

**ShelfCollapse** — o paletă aleasă la întâmplare își împrăștie cutiile (fără ragdoll de data asta). O singură dată.

## Comenzi și piese

**SupplyDelay** — 1-2 tipuri de piese necesare comenzii curente devin indisponibile la ridicare timp de 60 sec. Apare doar dacă echipa are un Dispecer.

**UrgentPart** — o piesă aleasă din orice comandă trebuie dusă la zona de expediere în 75 sec, pentru un bonus de +75. Nu ține cont de comanda activă — e o sarcină separată de livrare.

**RushOrder** — 2-4 cutii aleatorii din depozit încep să strălucească; trebuie cărate la zona de expediere în 120 sec, +50 puncte per cutie livrată.

**WrongOrder** — dispecerul anunță greșit o piesă suplimentară (care nu există cu adevărat) ca fiind necesară. După 75 sec vine o corecție — comanda reală nu s-a schimbat niciodată, doar timp pierdut căutând degeaba.

## Mediu

**WetFloor** — lichid vărsat pe jos, 60 sec: viteză -30%, prag de cădere (ragdoll) redus, șansă periodică de alunecare cât timp te miști (mai mare dacă sprintezi cu o cutie în brațe).

## Intruși

**Thief** — un raid: 1-3 intruși intră pe uși diferite, la 4 sec unul după altul. Fiecare are **o singură sarcină**, iar fiecare sarcină are un timp de execuție suficient cât echipa să audă anunțul, să alerge și să-l bată:

- **Vandal** (marker roșu) — face prăpăd: aruncă cutiile de pe paleți pe jos, una la 1.1 sec, până i se termină timpul sau până îl oprește cineva. Nu fură nimic, paguba e dezordinea și timpul de strâns.
- **Looter** (marker portocaliu) — scormonește într-un palet 6 sec, apoi ia o cutie în brațe și pleacă cu ea. Ajunge la ieșire → cutia e pierdută, -60.
- **PartThief** (marker mov) — stă 7 sec la bancul de asamblare, apoi ia o piesă deja montată. Ajunge la ieșire → -80 și comanda are nevoie de înlocuitor. Dacă e doborât, piesa se pune la loc pe banc.
- **Saboteur** (marker galben) — stă 9 sec la un tablou electric și declanșează `PowerFailure` (sau `WetFloor`, dacă lumina e deja stinsă), reutilizând evenimentele existente cu tot fluxul lor de reparație.

Fiecare intrus **traversează întâi hala** (1-2 opriri prin depozit) înainte să se apuce de treabă — așa echipa apucă să-l vadă venind. Singurul cronometru pe el e **bugetul de vizită: 65 sec în total**, prowl inclus; când expiră pleacă cu ce a apucat. Nu există cronometru pe bătaie: o urmărire se termină doar când jucătorul se rupe la peste 12 m.

Mărimea raidului crește cu progresul: 1 intrus la prima comandă, 2 după 1 comandă livrată, 3 după 2 (demo-ul se termină la 3 comenzi). Primul e mereu un Vandal; bancul e vizat doar dacă are piese montate pe el.

Lupta: click stânga, pumnii fac 1 damage, bâta 2, cuțitul 3, hoțul are 3 HP. **Orice lovitură care nu ucide resetează progresul sarcinii** — poți opri un furt chiar dacă nu reușești să-l dobori. Rănit (1 HP) abandonează tot și fuge. În fugă **șerpuiește** (și mai tare dacă îl urmărești de aproape), iar cu pradă în brațe e mai rapid (6.2 față de 5.2 m/s). Doborât = +40.

**Hoții ripostează.** Unii intră cu mâinile goale, alții cu bâtă (45% șansă, dar niciodată la primul raid al turei). Lovit, un hoț cu bâtă ripostează întotdeauna, unul cu mâinile goale în 60% din cazuri: se întoarce și atacă la fiecare 1.6 sec, cu un preaviz de 0.5 sec (markerul de deasupra capului clipește alb, bâta se ridică) — ieși din rază în acel interval și lovitura ratează. Dacă lovitura prinde: rănire prin `InjurySystem` (vânătaie de la pumn, entorsă/traumatism de la bâtă), iar cu 25% (pumn) / 50% (bâtă) șansă te trântește la pământ (ragdoll). **Cât ești la pământ hoțul așteaptă** — nu lovește un om căzut, stă deasupra și reia bătaia când te ridici. Renunță la bătaie doar dacă te îndepărtezi la peste 12 m — sau dacă i se termină bugetul de vizită și pleacă.

**Bâta cade din mâinile lui când e doborât** și se poate ridica cu [E]. Asta e singura sursă de arme din joc — nu se mai împrăștie bâte și cuțite pe podea la începutul evenimentului. Bucla e: primul raid îl bați cu pumnii (3 lovituri per hoț), îi iei bâta, raidurile următoare merg în 2 lovituri.

La intrarea raidului sună **sirena de incendiu** timp de 3.5 sec (același sunet, aceeași semnificație: lasă ce faci). Dacă e deja o alarmă de incendiu activă, raidul nu atinge sirena.

Evenimentul ține slotul ocupat până când toți intrușii sunt doborâți sau plecați, cu o limită dură de 170 sec.
