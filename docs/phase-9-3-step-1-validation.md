# Phase 9.3.1 — Contrats et persistance CRM ↔ n8n

Première tranche backend locale, sur `main`, référence de départ
`4e43f7edee7c6872a83600251d46154f495c239a`. La clôture du périmètre local 9.2,
l'envoi manuel confirmé par l'utilisateur et l'absence de contrôle après
téléchargement sont consignés dans le [rapport 9.2.4](phase-9-2-step-4-validation.md).

## Périmètre implémenté

Le CRM possède le contexte et les décisions. Le seul échange est une sonde
`technical.probe` sans effet métier. `IntegrationStore` est un service interne
enregistré dans l'injection de dépendances, sans appelant de production dans
cette tranche : aucun endpoint, worker de transport, workflow ou appel réseau.
Il ne remplace ni les automatisations existantes ni leurs historiques.

| Stockage | Responsabilité |
| --- | --- |
| `IntegrationOperation` | Opération logique, workspace CRM, version, type, identifiants de commande/corrélation/sonde, état et révision |
| `IntegrationAttempt` | Tentative de transport numérotée et son issue technique ; plusieurs tentatives gardent la même commande |
| `IntegrationReceipt` | Résultat reçu durablement, contenu canonique et SHA-256, dates de réception et de traitement distinctes |

Ces noms proposés sont conservés car ils séparent ces trois responsabilités.
Pas de copie des historiques `SourceExecution` ou `AutomationExecution`, pas de
destination métier générique, ni de nouvelle dépendance d'infrastructure.

## Contrat v1 strict

Commande : `version`, `messageId`, `operationId`, `correlationId`, `type`,
`payload: { probeId }`. Résultat : mêmes champs, plus `commandMessageId`, et
`payload: { probeId, outcome }`. Le résultat possède son propre `messageId`.

- `version` : entier `1` ; `type` : exactement `technical.probe`.
- Identifiants : UUID non nuls, forme avec tirets `D` ; créés côté CRM pour la
  commande. `commandMessageId` du résultat référence la commande persistée.
- `outcome` : exactement `succeeded` ou `failed`, aucun texte d'erreur libre.
- JSON : 4 096 octets UTF-8 au maximum, noms sensibles à la casse, aucun champ
  inconnu/manquant/dupliqué, pas de conversion implicite, commentaire ou virgule
  finale. Les mêmes restrictions s'appliquent au payload.
- Aucun workspace, destination métier, URL, credential ou champ de texte libre
  accepté. Les erreurs de contrat ne reprennent ni l'entrée ni l'erreur du parseur.

Le futur transport devra passer le corps brut à `IntegrationContracts.ParseResult`
(utilisé par `ReceiveAsync`), et non compter sur la désérialisation permissive
par défaut d'un contrôleur. La sérialisation typée des commandes est également
validée. Aucun secret n'est requis par ces contrats.

## États et concurrence

| Action interne | État initial → état final de l'opération |
| --- | --- |
| Créer la sonde | → `pending`, révision 0 |
| Commencer une tentative | `pending` ou `awaiting-result` → `awaiting-result`, révision +1 |
| Terminer la tentative | Aucun changement de l'opération |
| Recevoir le premier résultat valide | `awaiting-result` → `result-received`, révision +1 |
| Traiter explicitement la sonde | `result-received` → `succeeded` ou `failed`, révision +1 |

Une tentative suit `started` → `acknowledged`, `failed` ou `unknown`. Une seule
tentative ouverte est permise par opération (index unique partiel PostgreSQL).
Une tentative interrompue reste `started` : aucun délai, retry ou déverrouillage
automatique. Sa résolution explicite en `unknown` peut permettre une nouvelle
tentative ; aucune tentative ni communication réelle n'est lancée ici.

Chaque mutation d'une opération existante prend un verrou de ligne PostgreSQL
`FOR UPDATE` dans une transaction. Le début de tentative et le traitement exigent
la révision attendue : un appel devenu obsolète est refusé. La fin de tentative
est autorisée une seule fois, même si l'opération a déjà reçu/traité son résultat.
Un ACK tardif ne change pas le résultat logique. Il n'existe pas de mutation
générique permettant d'imposer un état arbitraire.

Les contraintes SQL limitent les états, les dates de fin, les identifiants, les
révisions et les numéros ; les clés étrangères empêchent les références orphelines.
Les transitions sont contrôlées par le service transactionnel. Les accès SQL
directs d'un administrateur ne constituent pas l'interface de ce service.

## Réception, idempotence et reprise

La portée de `IntegrationReceipt.MessageId` est globale à la base, pour le seul
flux de résultats n8n de cette tranche, tous workspaces confondus. Les identifiants
de commande sont uniques dans leur propre espace ; les tentatives sont uniques
par `(OperationId, Number)`. Une seule réception finale est autorisée par sonde.

Le JSON est validé puis sérialisé dans l'ordre des propriétés du contrat v1 :
UUID normalisés, formes numériques et échappements normalisés. L'ordre des
propriétés, les espaces et la casse des UUID ne changent pas le contenu logique.
L'égalité compare **le contenu canonique complet et son SHA-256**, pas seulement
le hash. Toute différence sémantique sous le même identifiant produit
`message-content-conflict` ; une copie identique renvoie `AlreadyRecorded`,
y compris après traitement. Un autre identifiant ne remplace jamais un résultat.

Le verrou de l'opération et la clé primaire du message, avec
`INSERT … ON CONFLICT (MessageId) DO NOTHING`, arbitrent les réceptions concurrentes.
L'insertion et le passage à `result-received` sont atomiques. Le workspace est lu
depuis l'opération ; corrélation, commande, sonde, version et type doivent correspondre.
Les appels internes liés à un workspace filtrent également sur ce contexte CRM.
Ces contrôles de cohérence ne constituent pas une authentification de l'émetteur.

Après interruption, un nouveau contexte peut relire `PendingReceiptsAsync` par
workspace (100 maximum) et traiter le résultat durable. La mise à jour de
`ProcessedAt` et l'état terminal sont dans une transaction distincte et atomique.
Une réception durable n'est donc ni un traitement terminé ni un succès métier.
Ce traitement concerne uniquement la sonde ; l'atomicité de futurs effets métier
devra être définie avant de les ajouter.

## Migration et validation locale

Migration additive : `20261013143800_Phase931IntegrationPersistence`, trois nouvelles
tables, clés étrangères, contraintes et index ; aucune modification des tables
métier existantes ni reprise de données. Snapshot EF actualisé. Les assertions
de fin de chaîne des tests existants sont mises à jour, leurs références historiques
de montée/descente restant inchangées. Les comparaisons avant/après des anciennes
migrations excluent les trois nouvelles tables, qui n'existaient pas avant ;
les nouvelles tables sont vérifiées par les tests 9.3.1.

Les tests utilisent PostgreSQL 18 via Testcontainers, comme les tests existants,
avec connexions fournies par les conteneurs jetables. Aucune migration n'est
appliquée à la base Development réelle ; aucune instance n8n n'est utilisée.

Résultats locaux du 13 octobre 2026 :

**Suite complète locale non exécutée ; validation globale attendue dans la CI du commit.**

| Contrôle exécuté | Résultat |
| --- | --- |
| `dotnet build ProspectionCrm.slnx --no-restore` | Réussi ; dernier passage : 0 erreur, 0 avertissement. Le premier build avait signalé deux avertissements CS8618 existants dans `Settings.razor`, hors périmètre |
| Premier passage ciblé : `Integration*`, migrations AutomationActionRequest/Circuit/Job/Runtime et SourceCollectionJob | 70 tests : 69 réussis, un échec de comparaison des tables de l'ancienne migration, corrigé ensuite |
| Passage complémentaire : test en échec, injection de panne SQL, migration Scheduling et assertions de schéma Dispatcher/RSS/Ingestion/Phase6 | 8/8 réussis ; aucun test ignoré |
| Bilan des tests distincts couverts par ces passages | **77 tests distincts désormais réussis**, dont le test initialement en échec ; la suite complète n'a pas été lancée |
| `dotnet ef migrations has-pending-model-changes --project src/ProspectionCrm.Api --no-build` | Aucun changement de modèle depuis la migration ; environnement `Testing`, connexion de conception inerte vers `127.0.0.1:1`, sans connexion Development |
| Cohérence EF et migrations dans les tests | Base vierge, montée depuis 8.6 peuplée, descente/remontée sur base jetable, données préexistantes conservées, aucune migration en attente |
| `git diff --check` et contrôle des nouveaux fichiers non suivis | Réussis |

L'échec initial provenait du snapshot historique de `SourceCollectionJobMigrationTests` :
il incluait les trois nouvelles tables vides dans la comparaison avec le schéma
antérieur. Les exclure de cette comparaison des anciennes tables conserve son
objet ; la migration 9.3.1 vérifie séparément leur création et leur état initial.
Les assertions d'ordre de la chaîne ont également été décalées d'une migration.
Les outils EF 10.0.10 ont émis un avertissement de version face au runtime 10.0.12 ;
la génération et le contrôle se sont terminés avec le code 0, sans mise à jour d'outil.

Les tests 9.3.1 vérifient les enveloppes invalides, les transitions refusées,
l'identité du contenu canonique, les conflits et le rattachement au contexte CRM.
Huit transactions de réception sont bloquées simultanément sur un verrou
PostgreSQL avant libération : une seule réception durable, sept réponses déjà
enregistrées. Les courses entre contenus différents, débuts de tentative et
traitements vérifient les refus concurrents. Un nouveau contexte reprend un
résultat durable non traité. L'injection de contraintes SQL défaillantes sur la
base jetable démontre le rollback de l'insertion/révision et du traitement/état
terminal, puis leur reprise. Un ACK précoce ou tardif ne suffit jamais à réussir
l'opération. Aucun test de sauvegarde ou de connexion n8n n'a été relancé.

## Fichiers et état Git avant commit

13 fichiers suivis modifiés et 13 fichiers non suivis, dont le rapport 9.2.4 déjà
présent au départ. Index vide ; branche `main` et HEAD de départ inchangés.
Le périmètre Git complet est le suivant :

```text
docs/phase-9-2-step-4-validation.md
docs/phase-9-3-step-1-validation.md
src/ProspectionCrm.Api/Dtos/Integrations/IntegrationEnvelopes.cs
src/ProspectionCrm.Api/Entities/IntegrationOperation.cs
src/ProspectionCrm.Api/Entities/IntegrationAttempt.cs
src/ProspectionCrm.Api/Entities/IntegrationReceipt.cs
src/ProspectionCrm.Api/Services/Integrations/IntegrationStore.cs
src/ProspectionCrm.Api/Data/Configurations/IntegrationConfiguration.cs
src/ProspectionCrm.Api/Data/ProspectionCrmDbContext.cs
src/ProspectionCrm.Api/Data/Migrations/20261013143800_Phase931IntegrationPersistence.cs
src/ProspectionCrm.Api/Data/Migrations/20261013143800_Phase931IntegrationPersistence.Designer.cs
src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs
src/ProspectionCrm.Api/Program.cs
tests/ProspectionCrm.Api.Tests/IntegrationContractTests.cs
tests/ProspectionCrm.Api.Tests/IntegrationPersistenceTests.cs
tests/ProspectionCrm.Api.Tests/IntegrationPersistenceMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationCircuitMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs
tests/ProspectionCrm.Api.Tests/AutomationJobMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs
tests/ProspectionCrm.Api.Tests/IngestionHistoryReadTests.cs
tests/ProspectionCrm.Api.Tests/Phase6ReconstructionTests.cs
tests/ProspectionCrm.Api.Tests/RssCollectionTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionJobMigrationTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionSchedulingMigrationTests.cs
```

Les dix fichiers de tests existants ne changent que pour les assertions de schéma
et de chaîne de migrations. Aucun changement de `compose.yml`, configuration,
authentification, ancien service métier ou migration historique. Aucun secret,
archive, binaire, dépôt restic, cache ou script opérateur n'est ajouté au périmètre
Git ; les scripts opérateur restent hors dépôt. Les sorties de build restent ignorées.

## Limites et suite

La communication réelle reste désactivée : absence d'endpoint, client n8n et
worker de transport. Ni email, événement calendrier, collecte, modification
d'authentification globale, configuration de service ou secret ajouté.
Les sauvegardes et l'instance n8n réelle ne sont pas manipulées.

La prochaine tranche devra définir et valider l'authentification du transport,
les autorisations de l'émetteur et le contexte CRM permis, les protections contre
rejeu/abus et les limites de requête, puis raccorder l'envoi et la réception de la
sonde avec reprise des tentatives incertaines. La 2FA de l'éditeur n8n ne remplace
pas ces protections. Aucun branchement ne découle de la seule présence de ces tables.

Aucun staging, commit ou push pendant l'implémentation locale initiale.

## CI #58 et correction ciblée du test historique — 13 octobre 2026

État initial du correctif : branche `main`, HEAD
`b5d1f3d7de99259099bdbab16a923a9dbc4a387e`, working tree propre et index vide.
Les résultats locaux et le périmètre du commit initial ci-dessus sont conservés
comme constats historiques.

La [CI Build #58](https://github.com/HellzDraft/ProspectionCrm/actions/runs/38418301282),
run `38418301282`, job `115368542782`, est terminée **en échec** sur ce SHA.
Le statut et le journal du job ont été lus : compilation réussie (0 erreur,
2 avertissements), **1 259 tests réussis, 1 échec, 0 ignoré, 1 260 au total**.
Ces chiffres concernent la suite CI, distincte des 77 tests locaux ciblés décrits
plus haut ; ils ne constituent pas une validation globale réussie.

L'unique échec concerne `IngestionHistoryTests.FreshReconstructionHasNoPendingMigrationOrModelChange`,
ligne 476 de `tests/ProspectionCrm.Api.Tests/IngestionHistoryTests.cs`.
L'attente utilisait encore `AutomationRuntimeMigrationTests.Current`, soit
`20261009144211_Phase86AutomationCircuitResets`, alors que la reconstruction
complète applique désormais `20261013143800_Phase931IntegrationPersistence`.

Avant modification, cet échec a été reproduit en Release sur PostgreSQL 18
jetable : 1 test exécuté, 1 échec sur la même comparaison, code de sortie 1.

```powershell
dotnet test tests/ProspectionCrm.Api.Tests/ProspectionCrm.Api.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName=ProspectionCrm.Api.Tests.IngestionHistoryTests.FreshReconstructionHasNoPendingMigrationOrModelChange' --logger 'console;verbosity=minimal'
```

La correction remplace uniquement la référence attendue par
`IntegrationPersistenceMigrationTests.Current`. L'assertion d'égalité explicite
reste présente, ainsi que les assertions de base vierge, d'absence de migration
en attente, d'absence de changement du modèle EF et de vacuité des tables
`SourceExecutionItems` et `SourceExecutionItemSources`. Aucun test ignoré,
assertion supprimée ou comparaison d'une valeur avec elle-même.

La recherche ciblée des autres attentes de dernière migration ne révèle pas
d'autre référence obsolète. Les références historiques à la 8.6 sont conservées :
borne des scénarios de montée/descente dans `IntegrationPersistenceMigrationTests`
et avant-dernière migration de la chaîne dans `SourceCollectionJobMigrationTests`.

Vérification après correction, avec compilation du projet API et des tests en
Release, puis exécution des deux classes sur des bases PostgreSQL 18 jetables :

```powershell
dotnet test tests/ProspectionCrm.Api.Tests/ProspectionCrm.Api.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~ProspectionCrm.Api.Tests.IngestionHistoryTests|FullyQualifiedName~ProspectionCrm.Api.Tests.IntegrationPersistenceMigrationTests' --logger 'console;verbosity=minimal'
```

Résultat : **19/19 tests réussis, 0 échec, 0 ignoré**, durée affichée 1 min 46 s,
code de sortie 0. Cela couvre les 18 cas d'`IngestionHistoryTests` et le test de
migration 9.3.1 : base vierge, montée depuis la 8.6 peuplée, descente/remontée,
conservation des données existantes, absence de migration en attente et cohérence
du modèle EF. Ce passage ciblé ne remplace pas une nouvelle exécution globale.

Périmètre final du correctif : ce rapport et `IngestionHistoryTests.cs`, uniquement.
`git diff --check` réussi ; branche et HEAD inchangés, index vide, aucun fichier
non suivi ajouté. Les autres assertions du test et le contenu historique du
rapport sont conservés.

**La validation définitive de 9.3.1 reste attendue dans une nouvelle CI réussie
après le futur commit correctif.** La suite complète locale n'est pas relancée.
Aucun changement métier, de migration ou de workflow ; aucune migration sur
Development, aucune manipulation de n8n réel ou des sauvegardes. Aucun staging,
commit ou push dans cette correction, aucune Phase 9.3.2 engagée.

## Clôture après CI #59 — vérification du 14 octobre 2026

La [CI Build #59](https://github.com/HellzDraft/ProspectionCrm/actions/runs/38425587075)
du commit `45ef44b4480f73f29d666e3c3b7fcb6ba760306f` sur `main` est terminée
avec la conclusion **success**. Le statut a été relu via l'API GitHub pendant
la préparation de 9.3.2. Le run a démarré le 13 octobre 2026 à 17 h 51 et s'est
terminé à 18 h 01 (Europe/Paris).

Cette réussite clôt la validation de 9.3.1, correctif de migration inclus.
Les résultats ciblés locaux et l'échec historique de la CI #58 ci-dessus restent
conservés. Ce constat ne vaut pas validation des modifications ultérieures 9.3.2.
