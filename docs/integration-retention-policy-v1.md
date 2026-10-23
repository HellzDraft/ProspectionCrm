# Politique de rétention des intégrations — v1

État examiné le 23 octobre 2026, référence
`084d360fbc29d8d96e804b2944ed26548e4ec56e`. Politique documentaire : aucune
durée active changée, aucune purge, migration, route ou tâche de nettoyage ajoutée.
Les durées proposées sont des choix techniques à valider, **pas des obligations
juridiques**. Cette politique n'autorise aucune suppression.

## Niveaux de preuve

- **Existant et appliqué** : mécanisme observé dans le code et, lorsqu'indiqué,
  confirmé dans le catalogue PostgreSQL ou dans une validation historique datée.
- **Déclaré, application non vérifiée** : valeur dans les fichiers ou dans
  l'environnement d'un conteneur arrêté ; ce n'est pas une preuve de purge effective.
- **Proposé** : règle future soumise à décision ; aucune activation implicite.
- **Non implémenté** : pas de mécanisme CRM de rétention par âge, d'archivage
  transparent, de tombstones de rétention ou de suppression coordonnée CRM/n8n.

« Protection requise » signifie que la suppression sûre n'est pas démontrée.
Ce classement impose une revue, pas une conservation illimitée justifiée par défaut.
Une fin métier, un reçu traité, une archive ou une sauvegarde ne prouvent jamais
à eux seuls qu'une ligne est supprimable.

## Durées réellement constatées

| Périmètre | Constat | Niveau et limite de vérification |
| --- | --- | --- |
| Historiques CRM ci-dessous | Aucun TTL ni worker de purge par âge trouvé dans les services concernés. Données persistantes sans échéance automatique. | Existant : code, FK et triggers examinés ; pas une garantie contre une intervention SQL externe. |
| Serilog, sink fichier | `rollingInterval=Day`, `retainedFileCountLimit=14`, `retainedFileTimeLimit=14.00:00:00`, chemin relatif `logs/prospectioncrm-.log`. | Déclaré dans appsettings.json et chargé par Program.cs ; pas de surcharge Serilog dans le fichier Development suivi. Configuration effective de tous les lancements/env/user-secrets et suppression effective **non vérifiées**. 14 fichiers n'est pas une garantie de 14 jours complets. |
| Console / journaux Docker | Sink console distinct ; conteneur n8n : driver `json-file`. | Pas de durée déduite de Serilog pour Docker ou les fichiers de validation redirigés. Rotation effective du daemon non vérifiée. |
| Exécutions n8n | Pruning `true`, âge `336` heures (14 jours), plafond `10000`. | Compose et trois variables du conteneur 2.40.7 vérifiés concordants. Application historiquement validée en 9.2 ; n8n arrêté lors de cette revue, aucune purge ni durée réellement conservée mesurée aujourd'hui. |
| Workflows n8n de démonstration | Sauvegarde succès/erreur `all`, exécutions manuelles `true`, timeout 12 s. | Déclaré dans les six workflow.json ; état des workflows importés non relu. Timeout d'exécution ≠ rétention. |
| Registres simulés email/calendrier | Fichiers JSON et SQLite persistants, aucun TTL/effacement par âge dans leurs modules. | Existant dans le code ; persistance/replay validés en 9.5/9.6. Contenu et compteurs du volume n8n non relus aujourd'hui. |
| Sauvegardes / captures / TRX | Pas de calendrier de destruction démontré. | Sauvegarde chiffrée et restauration historiquement validées en 9.2.3 ; cela ne prouve pas une fréquence ou rétention automatique actuelle. |

Le [guide n8n](n8n-operations.md) décrit pour l'image examinée en 9.2 les exclusions
de pruning (`new`, `running`, `waiting`, exécutions annotées) et son exécution
différée. Ces exclusions ne connaissent pas les statuts unknown/conflit du CRM.
Le seuil de 10 000 peut raccourcir la conservation par rapport à 14 jours.
Le pruning des exécutions ne purge pas les registres personnalisés de démonstration.
Ni la fenêtre HMAC de quelques minutes ni un timeout HTTP ne bornent la durée
pendant laquelle une preuve métier tardive doit rester explicable.

## Exclusions obligatoires avant toute décision future

Toutes les familles persistantes ci-dessous sont **protection requise** tant que
l'ensemble des dépendances et des conditions suivantes n'est pas résolu :

1. Opération pending/awaiting-result/result-received, tentative started, livraison
   dispatching/awaiting-result/send-unknown/operation-unknown, job actif ou
   dispatch-unknown ; un délai écoulé ne constitue pas une preuve de non-effet.
2. Reçu non traité, preuve invalide/incohérente/contradictoire ou conflit persistant.
   Ne pas qualifier un reçu non évalué de contradictoire sur ses seules métadonnées.
3. Clé ou historique encore nécessaire à l'idempotence, aux réponses tardives,
   aux curseurs, à une réconciliation ou à une récupération humaine. Conserver
   aussi les tentatives nécessaires au budget minute/journée UTC de 9.7.3.1.
4. Approbation, snapshot lié à une empreinte, décision opérateur, audit d'effet
   ou investigation dont la chaîne de justification dépend de ces données.
5. Registre fournisseur simulé, référence FK, référence logique sans FK,
   point de restauration ou copie de sauvegarde pouvant réintroduire un ancien état.

Les blocages sont transitifs : conserver un reçu exige de conserver son contexte,
pas seulement son identifiant. Un statut succeeded/failed ou ProcessedAt renseigné
ne lève aucune exclusion automatiquement. Ne pas désactiver un trigger ni une FK.

## Inventaire par famille

Les vérifications V1–V6 sont décrites plus bas. « Archive possible » désigne
une **copie** protégée et vérifiée, jamais un déplacement suivi de suppression.

| Famille : rôle / sensibilité | Durée actuelle ; proposition | Dépendances et blocages particuliers | Archive / vérification |
| --- | --- | --- | --- |
| IntegrationOperations / Attempts : corrélation, révisions, chronologie et budget ; identifiants techniques et workspace | Sans échéance. Proposé : revue des terminées après 90 jours, sans échéance de suppression définie. | FK Attempts → Operations RESTRICT ; livraisons/jobs/intents → Operations, dont liaisons composites workspace/connexion/type. Unknown et budget de départ protégés. Triggers calendrier interdisant leur effacement. | Copie du graphe entier ; V1, V2. |
| IntegrationReceipts : preuve, rejeu par MessageId, hash et contenu canonique potentiellement personnel | Sans échéance. Même revue à 90 jours seulement après clôture démontrée ; pas de durée de purge. | FK vers Operations RESTRICT ; unicité opération/numéro de preuve. Preuves tardives/contradictoires et slots historiques nécessaires. Trigger calendrier : identité/contenu immuables, suppression interdite, ProcessedAt monotone. | Copie chiffrée incluant les octets canoniques ; pas de troncature du payload sous le même hash ; V1, V2. |
| EmailSendPlans / Deliveries : destinataires, sujet/corps, snapshot approuvé, effet d'envoi | Sans échéance. Revue annuelle du besoin métier, pas de purge par âge proposée. | FK RESTRICT vers plan, approbation, compte, opération, message produit et références CRM du plan. Unicités plan/approbation/opération/clé. Triggers protégeant le plan et l'identité de livraison ; ils ne garantissent pas tous un refus DELETE. send-unknown et preuve tardive bloquent. | Copie restreinte de toute la chaîne ; V1, V2, V4. |
| CalendarEventPlans / Deliveries : titres, description, lieu, dates, références et snapshot de cible | Sans échéance. Revue annuelle ; suppression sûre non disponible dans le schéma actuel. | FK RESTRICT vers calendrier/compte, plan, approbation, opération, événement produit/cible et références CRM. Triggers refusant DELETE plans/livraisons et historique associé. Version-conflict et preuve tardive protégés. | Copie avec cible/révision et preuves ; V1, V2, V4. |
| EmailSyncJobs / CalendarSyncJobs et SyncStates : résultats, curseurs et révisions de synchronisation | Sans échéance. Revue technique à 90 jours pour jobs clos ; pas de TTL pour le curseur actif. | Jobs liés par FK RESTRICT aux comptes/calendriers et opérations, liaison composite et unicité opération ; index d'activité. États courants et messages/événements importés nécessaires à la continuité et à la déduplication. | Copier jobs, états et objets importés de façon cohérente ; V1, V2. |
| RemoteCollectionIntents, SourceCollectionJobs/Attempts, SourceExecutions/Items/ItemSources : provenance, ingestion, rejets et recherche, contenus potentiellement personnels | Sans échéance. Revue à 90 jours des exécutions closes ; historique métier revu annuellement. | Intents → job/source/opération RESTRICT ; jobs/tentatives → exécution RESTRICT. Executions → Items → ItemSources en CASCADE ; liens vers opportunités/sources en SET NULL. Supprimer une exécution peut détruire sa provenance, même si des FK l'autorisent. Clés de déclenchement et déduplication à préserver. | Copie avec provenance et associations ; V1, V2. |
| AutomationActionRequests et ActivityEntries : consentement opérationnel, auteur, décision, récupération et issue ; notes/snapshots/identifiants personnels | Sans échéance. Revue annuelle, conservation liée à la chaîne métier ; aucune échéance automatique. | Requests : RESTRICT vers utilisateur, workspace, job/règle et depuis plans/livraisons/exécutions. Activity : workspace RESTRICT, utilisateur/opportunité SET NULL ; EntityId est une référence logique, pas une FK vers l'opération. Metadata de récupération conserve operatorId. Audit accepté atomique avec l'effet, refus séparés ; journal non présenté comme inviolable. | Copie conservant les liens logiques et l'auteur ; V1, V2. |
| Registre n8n email simulé : JSON par clé avec commande/hash, acceptation et preuve | Aucun TTL. Protection requise sans durée de suppression proposée. | `/home/node/.n8n/email-send-demo-state` ; disparition d'une clé peut permettre une nouvelle acceptation au rejeu. Données du snapshot sensibles même dans un mécanisme technique. | Capture cohérente du volume, jamais simple export workflow ; V3, V6. |
| Registre n8n calendrier simulé : SQLite ledger/effects/events/calendars/policies/metadata | Aucun TTL. Protection requise, y compris après succès ou remise du callback. | `calendar-event-demo-state/provider.sqlite` : effects référence ledger/calendars, events référence calendars. Ledger contient commande, hash, preuve ; effects avant/après. Suppression/recréation rompt idempotence et versionnement. | Capture cohérente de toute la base avec journaux nécessaires, pas extraction isolée du ledger ; V3, V6. |
| Exécutions n8n : entrées/sorties/erreurs pouvant inclure corps métier | 336 h / 10 000 déclarés et historiquement appliqués. Proposition : conserver ce réglage, sans le changer ici, et réserver hors exécutions les preuves nécessaires au CRM. | Aucune FK inter-systèmes ; une exécution réussie n'est pas une preuve CRM traitée. Pruning natif peut éliminer une trace d'un unknown CRM ; divergence explicitement non résolue. | Archive sélective protégée à concevoir avant expiration si une enquête en dépend ; V3. |
| Serilog fichier, console et sorties opérateur : diagnostics pouvant contenir identifiants, erreurs ou données accidentelles | Fichier 14 fichiers / 14 jours déclarés ; autres sorties sans durée vérifiée. Proposition : 14 jours pour diagnostics ordinaires, exception d'incident documentée et revue. | Rotation n'est pas audit métier ; les TRX, sorties redirigées, journaux Docker et copies ne sont pas couverts par le sink fichier. | Copie minimisée/chiffrée si incident, avec accès restreint ; V5. |
| Temporaires, exports, captures en clair, TRX et fixtures : souvent aussi sensibles que la source | Pas de durée automatique vérifiée ; nettoyages de fixtures de test ≠ politique opérateur. Proposition : revue à 7 jours après validation du transfert/restauration. | Une preuve unique ou un registre simulateur n'est pas temporaire par son emplacement. Ne rien effacer si transfert, restauration, incident ou validation dépend encore du fichier. | Copie utile protégée avant toute décision, jamais accumulation automatique de copies ; V5, V6. |
| Sauvegardes PostgreSQL et n8n, dépôts restic et copies exportées : copie complète de données et parfois secrets nécessaires à restaurer | Pas d'expiration active démontrée. Proposition existante restic : 7 quotidiens, 4 hebdomadaires, 6 mensuels + point avant migration non validée ; PostgreSQL à coordonner, non appliqué. | Copies/captures en clair distinctes du dépôt chiffré ; mots de passe/clés disponibles séparément. Périodes de snapshots non additives ; restauration doit préserver idempotence et cohérence inter-systèmes. | Sauvegarde chiffrée vérifiée et copie hors machine à confirmer ; V6. |

## Données personnelles, archivage et restauration

Pour les données personnelles, proposer une revue de nécessité tous les 90 jours
avec le responsable métier : finalité, accès, copies, liens et éventuelle minimisation.
Ce délai de revue n'est pas une durée légale de conservation. Ne pas altérer des
snapshots approuvés ou preuves canoniques pour anonymiser : leurs empreintes et
réconciliations deviendraient incohérentes. Une procédure dédiée reste à concevoir
pour concilier minimisation et préservation des preuves ; décision tracée requise.

Pour un archivage futur, vérifier le graphe complet, les hashes, l'accès restreint,
le chiffrement, la recherche par identifiants et la restauration isolée. Un fichier
d'archive seul ne remplace pas les recherches d'idempotence en ligne. Aucun mécanisme
de tombstone interdisant un ancien replay après suppression n'est implémenté.

Restaurer CRM plus ancien que n8n peut faire disparaître une réception/approbation ;
restaurer le registre n8n plus ancien que CRM peut perdre une clé et reproduire un
effet. Un couple de sauvegardes doit donc porter un manifeste commun (versions,
instants, identités techniques, empreintes), être restauré isolément sans dispatch,
et faire l'objet d'un diagnostic avant remise en service. Ne jamais fusionner ou
rejouer automatiquement les preuves. Un backup chiffré ne chiffre pas sa capture
source ni les copies restaurées ; `.gitignore` n'est pas un contrôle d'accès.

## Procédures de vérification non destructives

V1 — Catalogue et agrégats PostgreSQL : transaction **READ ONLY**, aucune projection
de payload, snapshot, adresse, note, signature, clé ou identifiant individuel.
Utiliser une connexion existante autorisée sans démarrer Development. Exemple
exécuté pour les agrégats durant cette revue :

```sql
BEGIN TRANSACTION READ ONLY;
SELECT current_setting('transaction_read_only');
SELECT "Type", "StatusCode", count(*) FROM "IntegrationOperations" GROUP BY 1,2;
SELECT "StatusCode", count(*), min("StartedAt") FROM "IntegrationAttempts" GROUP BY 1;
SELECT ("ProcessedAt" IS NULL) AS unprocessed, count(*), min("ReceivedAt")
FROM "IntegrationReceipts" GROUP BY 1;
SELECT "StatusCode", count(*) FROM "EmailSendDeliveries" GROUP BY 1;
SELECT "StatusCode", count(*) FROM "CalendarEventDeliveries" GROUP BY 1;
COMMIT;
```

Compléter par `SELECT count(*)` sur chaque table de l'inventaire du rapport.
Ces agrégats sont un contrôle, **pas une liste de candidats à supprimer**.

V2 — Schéma : lire `Data/Configurations` et les migrations historiques 9.4–9.6,
puis corroborer les FK et triggers du catalogue, sans tester un DELETE :

```sql
BEGIN TRANSACTION READ ONLY;
SELECT c.conrelid::regclass AS child, c.confrelid::regclass AS parent,
       c.confdeltype AS delete_action
FROM pg_constraint c WHERE c.contype='f' AND c.connamespace='public'::regnamespace;
SELECT c.relname, t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
WHERE NOT t.tgisinternal AND c.relnamespace='public'::regnamespace;
COMMIT;
```

`r` = RESTRICT, `c` = CASCADE, `n` = SET NULL, `a` = NO ACTION.
Inclure les références entrantes et les références logiques Activity/JSON ; un
catalogue de FK seul ne démontre pas une suppression sûre.

V3 — n8n : comparer Compose à une inspection filtrée **uniquement** sur
`EXECUTIONS_DATA_PRUNE`, `EXECUTIONS_DATA_MAX_AGE`, `EXECUTIONS_DATA_PRUNE_MAX_COUNT`.
Ne pas afficher tout l'environnement, exporter les credentials ou déclencher un
workflow. Si n8n est arrêté, le laisser arrêté et noter les compteurs non vérifiés.
Pour un futur inventaire des registres/exécutions, utiliser une capture cohérente
autorisée, ouverte explicitement en lecture seule, avec seulement nombres et âges.
Ne pas appeler initialize/execute/policy du simulateur : ce sont des écritures.

V4 — Diagnostic humain existant : GET authentifié et limité au workspace pour les
cas à examiner, sans recover, reconcile ou dispatch. Les GET ne constituent pas
une autorisation de suppression. Non invoqués durant cette revue documentaire.

V5 — Fichiers/config : lire uniquement les sections de rétention et les métadonnées
des racines connues (nombre/taille/dates), sans contenu de journal ni dump.
Distinguer chaque sink et chaque copie ; vérifier les overrides lors d'une future
revue du processus actif. Date de fichier ≠ date métier ni preuve d'expiration.

V6 — Sauvegardes : consulter les rapports 9.2.3/9.2.4 et les manifestes non secrets
existants ; confirmer séparément fréquence, copies hors machine, garde des clés et
restaurabilité. Aucune invocation backup/restore/forget/prune/check, aucune capture
ou restauration dans cette tranche. Les essais précédents ne prouvent pas des mois
de rétention effective. Voir [stratégie chiffrée](n8n-encrypted-backups.md).

## Sources internes examinées

- `src/ProspectionCrm.Api/Services/Integrations/IntegrationDispatchBudget.cs`,
  `IntegrationOperationDiagnostic.cs`, `IntegrationRecoveryAudit.cs` ;
  services email/calendrier/collecte et configurations EF des familles inventoriées.
- Migrations `Phase953EmailSendPlans`, `Phase9541EmailSendDeliveries`,
  `Phase963CalendarEventPlans`, `Phase9641CalendarEventDeliveries` : protections
  historiques, sans modification de ces fichiers.
- `src/ProspectionCrm.Api/appsettings.json`, `appsettings.Development.json`,
  `Program.cs`, `compose.yml` et les six `docs/integrations/*/workflow.json`.
- `CrmEmailSend.node.js`, `CrmCalendarEvent.node.js`, `CalendarEventStore.js` ;
  rapports [9.5.4.2](phase-9-5-step-4-2-validation.md),
  [9.6.4.2](phase-9-6-step-4-2-validation.md),
  [9.7.2.2](phase-9-7-step-2-2-validation.md),
  [9.7.3.1](phase-9-7-step-3-1-validation.md).

Le [rapport de contrôle 9.7.3.2](phase-9-7-step-3-2-validation.md) fixe l'inventaire
observé, les limites de vérification et le périmètre Git de cette version.
