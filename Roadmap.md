# Roadmap – Idee Future RealBattery Recharged

## Fix
- [x] In background applicare consumo/usura batteria anche in base a scarica/ricarica quando la nave è **atterrata** o in **ombra orbitale**.
- [x] In background, correggere etichette "Day" e "Night" nel log di SolarSim.
- [ ] ~~Stato di carica (**SOC**) in background pesato sul **parziale di orbita** (fasi luce/ombra), non applicato in blocco.~~
- [ ] In caso di runaway la batteria o si **spegne**, oppure genera **calore ad ogni frame** finché non viene disabilitata.
- [ ] **Verificare/mostrare Discharge Rate** corretto nel PAW in editor.
- [x] Usare **un unico flow** coerente in `ApplyThermalEffects` (carica e scarica trattate nello stesso percorso logico).

## Miglioramenti simulazione e calcolo
- [x] `ModuleEnergyEstimator` più preciso: output pannelli in base a posizione, rotazione, angolo di incidenza; distinzione tracking/static; su superficie.
- [ ] Rilevamento automatico durata del giorno su Kerbin. Lo slider nelle impostazioni permette override con valori 0-24, dove 0 attiva il rilevamento automatico.
- [ ] Avviso batterie scariche dopo uscita scena, con annullamento se si rientra prima della scadenza. Opzione in impostazioni.
- [ ] Campi PAW visibili solo se tecnologia/upgrade sbloccati (`PartTechAvailable`).
- [ ] Nascondere gruppo PAW se `moduleActive = false` (non-batteria).
- [ ] Bonus Ingegnere: migliora performance termica e rallenta degrado (`ThermalLoss`, `WearCounter`).
- [ ] **Compatibilità BonVoyage**: Mod helper converte temporaneamente **SC → EC** per permettere a BonVoyage di stimare correttamente l’autonomia.

## Termiche e failure
- [ ] Disattivazione automatica batteria in overheat/runaway. Impostazione globale (`AutoShutdownOnOverheat`).
- [ ] Batterie termiche: producono calore ma non vanno in runaway (TempRunaway altissimo o esclusione dal wear termico).

## Nuovi tipi di batterie
- [ ] Hf-178m2 (ispirata alla _Hafnium controversy_): sostituisce le NukeCell.
- [ ] KERBA (ispirata a ZEBRA): ricaricabile, C-rate alto, bassa efficienza >60% SOC, durata cicli 5–10.
- [ ] TBat: non disattivabile, drain fisso ogni ciclo (`BatteryDisabled = false` + override `FixedUpdate`).
- [ ] Attivazione batteria tramite staging (`KSPAction` dedicata, campo `activateOnStaging`).

## UI e interfaccia
- [ ] Switch in PAW tra `BatteryHealth` e `CyclesLeft` (toggle o `UI_ChooseOption`). Formattazione decimali (<1) con `F1` o `F2`.
- [x] Meccaniche SystemHeat opzionali: fallback a calore stock o disattivazione totale.

## Documentazione e supporto
- [ ] KSPedia: panoramica sistema SC/EC, tipi batterie, uso in volo, simulazione in background, integrazione mod terze, icone/texture per chimiche.

## Estetica
- [ ] Texture switch per batterie, solo con ReStock/Restock+/NFE (via B9PartSwitch o `ModulePartVariants`).

