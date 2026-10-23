# Phase 9.7.3.2 — Validation de la politique de rétention

23 octobre 2026. Référence `main` :
`084d360fbc29d8d96e804b2944ed26548e4ec56e`.
HEAD, origin/main et référence distante concordants ; dépôt initial propre.

Livrable : [politique v1](integration-retention-policy-v1.md), documentaire et
non destructive. Les délais proposés sont des délais de revue ou des objectifs
techniques, jamais des obligations juridiques annoncées ni des purges activées.

## Contrôles effectués

- Code des familles concernées, configurations EF, migrations historiques et
  registres n8n examinés. Aucun TTL CRM ou archivage transparent implémenté trouvé.
- Serilog : 14 fichiers / 14 jours, rolling quotidien déclarés ; chargement par
  Program.cs confirmé. Pas de surcharge Serilog dans appsettings.Development.json
  suivi ; overrides effectifs et rotation réellement exécutée non vérifiés.
- n8n 2.40.7 arrêté, laissé arrêté. Inspection filtrée : pruning=true,
  max_age=336, max_count=10000 identiques à Compose ; volume monté à
  `/home/node/.n8n`. Aucune lecture des secrets ni export d'environnement complet.
  Application historiquement validée en 9.2, pas de mesure de pruning aujourd'hui.
- Six modèles de workflows déclarent sauvegarde succès/erreur `all`, manuelles
  `true`. Les workflows importés, exécutions et registres du volume arrêté ne sont
  pas réinventoriés ; leur persistance est documentée par les validations 9.5/9.6.
- PostgreSQL déjà actif et healthy. Deux transactions explicitement READ ONLY,
  dont `transaction_read_only=on` vérifié : SELECT d'agrégats et catalogue uniquement.
  Aucun payload, snapshot, email, identifiant individuel ou credential projeté.

## Inventaire PostgreSQL observé en lecture seule

| Table | Lignes |
| --- | ---: |
| IntegrationOperations | 28 |
| IntegrationAttempts | 27 |
| IntegrationReceipts | 28 |
| EmailSendDeliveries | 5 |
| EmailSendPlans | 6 |
| CalendarEventDeliveries | 6 |
| CalendarEventPlans | 6 |
| EmailSyncJobs | 6 |
| CalendarSyncJobs | 7 |
| RemoteCollectionIntents | 3 |
| SourceCollectionJobs | 3 |
| SourceCollectionJobAttempts | 3 |
| SourceExecutions | 3 |
| SourceExecutionItems | 4 |
| SourceExecutionItemSources | 4 |
| AutomationActionRequests | 12 |
| ActivityEntries | 1 |

Constats : 24 tentatives acknowledged et 3 unknown ; 27 reçus traités et **1 non
traité**. Livraisons email : 2 sent, 1 pending, 1 failed, **1 send-unknown**.
Livraisons calendrier : 3 succeeded, 1 failed, **1 operation-unknown et 1 conflict**.
Ces comptes ne sont ni des candidats à supprimer ni une qualification automatique
du reçu non traité en preuve contradictoire.

Le catalogue corrobore les FK RESTRICT, les cascades
SourceExecutions → SourceExecutionItems → SourceExecutionItemSources et les
SET NULL de leurs liens métier. Il confirme huit triggers sur le périmètre
intégration/plan/livraison examiné, notamment les gardes calendrier et les gardes
email. Le code des migrations confirme les refus DELETE de l'historique calendrier.
ActivityEntries.EntityId reste une liaison logique ; ses FK SET NULL ne rendent
pas l'audit supprimable. Aucune tentative de DELETE pour tester ces protections.

## Contrôles de fichiers et limites

Métadonnées seules, sans lecture du contenu des journaux/dumps/archives :

| Famille locale examinée | Nombre de fichiers | Octets agrégés |
| --- | ---: | ---: |
| Journaux sous src/ProspectionCrm.Api/logs | 1 | 20 081 |
| Captures dans backups/ ignoré par Git | 2 | 11 397 120 |
| Racine privée ProspectionCrmOperations | 1 898 | 274 559 430 |
| Racine privée des sauvegardes n8n | 45 | 12 236 745 |

Ce n'est pas un inventaire exhaustif du disque ni un décompte de snapshots restic.
La racine de sauvegarde peut contenir des copies et captures en clair ; son nom
ne prouve pas le chiffrement de chaque fichier. Les dates de fichiers ne donnent
pas leur âge métier (des fichiers d'outillage ont des dates reproductibles anciennes).
Les preuves existantes restent intactes. Aucun dump, secret, manifest privé ou
contenu de données ajouté dans Git.

La stratégie 7 quotidiens / 4 hebdomadaires / 6 mensuels reste **proposée**, pas une
planification ou expiration démontrée. Les validations historiques de sauvegarde,
restauration et préparation d'une copie hors machine n'ont pas été rejouées.
Fréquence actuelle, copie hors machine effective et expirations restent à vérifier.

## Résultat et inventaire Git

Politique couvrant les familles demandées, les exclusions transitives, les risques
d'idempotence/FK/audit et la restauration CRM/n8n. Données non supprimables sûrement
classées **protection requise**. Procédures de contrôle reproductibles en lecture
seule incluses dans la politique ; aucun outil ni route de purge.

Modification documentaire seule : aucun build ou test lancé, aucune suite globale.
`git diff --check` et contrôle des espaces des nouveaux fichiers réussis.
Aucune migration, modification Development, purge, nouveau service, reset de volume,
changement de durée active, staging, commit ou push. Phase 9.7.3.3 non commencée.

**2 fichiers nouveaux, 0 fichier suivi modifié ; index vide :**

- `docs/integration-retention-policy-v1.md`
- `docs/phase-9-7-step-3-2-validation.md`
