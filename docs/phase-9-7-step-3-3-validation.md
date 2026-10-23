# Phase 9.7.3.3 — Validation opérationnelle non destructive

23 octobre 2026. Base `main` : `6a65b0eeb58351ff15e905fbc99a50fdc6e663e6`.
HEAD, origin/main et référence distante concordants ; dépôt initial propre.
Livrable : [runbook v1](integration-operations-runbook-v1.md), adossé à la
[politique de rétention](integration-retention-policy-v1.md).

## Vérifié pendant cette session

Relecture des rapports 9.7.1, 9.7.2.1, 9.7.2.2, 9.7.3.1, 9.7.3.2, de la politique,
des guides n8n et des services IntegrationSupervisionService/IntegrationDispatchBudget.
Les contrôles supplémentaires ont porté uniquement sur paramètres non sensibles,
métadonnées, compteurs TRX et agrégats PostgreSQL, sans lecture de payload, credential,
contenu de sauvegarde ou de journal métier.

| Contrôle | Observation |
| --- | --- |
| PostgreSQL existant | Up, healthy ; aucun démarrage ni arrêt |
| n8n existant | Exited (0), laissé arrêté |
| API locale | Aucun processus ProspectionCrm.Api détecté, y compris lancement dotnet identifié par ce nom ; aucune écoute sur 5075/7075/5678 |
| Configuration suivie | Integrations, EmailSend/EmailSync/CalendarEvent/CalendarSync, DispatchLimits, AutomationWorker, SourceCollectionWorker et SourceCollectionScheduler : Enabled=false |
| Development suivi | Aucun override de ces options dans appsettings.Development.json |
| Environnement de cette session | Aucune variable Enabled correspondante trouvée ; aucune configuration temporaire active détectée dans ce périmètre |
| Serilog déclaré | Day, 14 fichiers, 14.00:00:00 ; rotation effective non mesurée |
| n8n déclaré dans le conteneur arrêté | PRUNE=true, MAX_AGE=336, PRUNE_MAX_COUNT=10000 ; aucune purge exécutée/observée par cette validation |

La configuration effective d'un futur lancement, les user-secrets et les variables
d'autres sessions ne sont **pas vérifiés**. Aucun démarrage pour les charger,
aucune lecture de secret. Les flags observés ne constituent pas une attestation
globale de toutes les machines ou de tous les futurs processus.

Une transaction explicite PostgreSQL READ ONLY (`transaction_read_only=on`) a
fourni les résultats suivants, sans projection d'identifiant individuel :

| Agrégat | Résultat |
| --- | --- |
| Opérations, groupées par type/statut | 28 ; 1 pending, 2 awaiting-result, 8 failed, 17 succeeded |
| Tentatives | 27 : 24 acknowledged, 3 unknown |
| Reçus | 28 : 27 traités, 1 non traité (plus ancien : 21 octobre 2026, 09:21:16 UTC) |
| Livraisons email | 5 : 2 sent, 1 pending, 1 failed, 1 send-unknown |
| Livraisons calendrier | 6 : 3 succeeded, 1 failed, 1 operation-unknown, 1 conflict |
| Tentatives/reçus sans opération | 0 / 0 |
| Liaisons livraison/opération invalides (workspace, connexion, type) | 0 email, 0 calendrier |
| FK publiques non validées | 0 |
| Gardes catalogue ciblées | 6 triggers activés normalement : plans/livraisons calendrier, opérations, tentatives, reçus et slot de preuve |
| Approbations / audit | 12 AutomationActionRequests, 1 ActivityEntry |

Le catalogue et ces anti-jointures ne remplacent pas une validation de toutes les
références logiques. Une tentative unknown ancienne ne prouve pas que son opération
est encore incertaine. Le reçu non traité n'a pas été qualifié de contradictoire.
Les états protégés restent présents, sans acquittement ou récupération.

Métadonnées de fichiers seulement, identiques à l'inventaire 9.7.3.2 :

| Racine contrôlée | Fichiers | Octets |
| --- | ---: | ---: |
| src/ProspectionCrm.Api/logs | 1 | 20 081 |
| backups/ ignoré par Git | 2 | 11 397 120 |
| Racine privée ProspectionCrmOperations | 1 898 | 274 559 430 |
| Racine privée des sauvegardes n8n | 45 | 12 236 745 |

Ces agrégats ne démontrent ni chiffrement de chaque copie, ni intégrité octet par
octet, ni ancienneté métier ou restaurabilité ; aucun contenu de sauvegarde consulté.

## Déjà prouvé par les tests historiques, non rejoué

Les compteurs des TRX ci-dessous ont été relus sous
`%LOCALAPPDATA%/ProspectionCrmOperations/`, sans afficher sorties ni données des tests.
La correspondance fonctionnelle vient des rapports historiques. Les suites se
recouvrent : leurs totaux ne doivent pas être additionnés comme scénarios distincts.

| Preuve existante | Réussis / échoués / ignorés | Couverture réutilisée |
| --- | --- | --- |
| phase-9-7-1-20261023/targeted-final.trx | 259 / 0 / 0 | Supervision, pagination, workspace, non-divulgation, lecture sans mutation |
| phase-9-7-2-1-20261023/targeted-final.trx | 125 / 0 / 0 | Diagnostic, unknown, preuves tardives, conflits et recommandations |
| phase-9-7-2-2-20261023/recovery-final.trx | 30 / 0 / 0 | Récupération calendrier contrôlée, conservation, isolation, concurrence, audit, familles exclues |
| phase-9-7-3-1-20261023/targeted-final.trx | 129 / 0 / 0 | 17 nouveaux + 112 régressions : minute/jour UTC, saturation, admission entre hôtes et email/calendrier, refus sans tentative, unknown compté, expiration, désactivation |

Ces scénarios historiques utilisaient PostgreSQL jetable et transports simulés.
Aucune lacune concrète justifiant du nouveau code identifiée dans ce périmètre.
La consigne finale réservant build/tests aux modifications de code est appliquée :
**aucun test ni build lancé pendant cette session**, aucune suite globale.

## Non vérifié / restant à faire séparément

- Parcours HTTP humain sur une instance active : API non démarrée, aucune connexion
  opérateur sollicitée, aucun GET/POST opérationnel exercé ici.
- Saturation/récupération en direct : historique de tests réutilisé ; aucun budget
  activé en Development. Toute réexécution future doit rester sur base jetable et
  transports simulés, avec filtres existants IntegrationSupervision,
  IntegrationDiagnostic, IntegrationDispatchBudgetTests et IntegrationRecoveryTests.
- Préconditions et résultat d'une récupération précise : pas de lecture de payload,
  pas de traitement de preuve. Le runbook n'invente aucune récupération email/sync/collecte.
- Disponibilité n8n/fournisseur, exécutions et registres du volume n8n, rotation
  réellement appliquée, sauvegarde/restauration actuelle et copie hors machine :
  non testées. Déclaré/historiquement testé/observé sont distingués dans le runbook.
- Les procédures de sauvegarde ou de correction nécessitant des mutations réelles
  sont reportées à une intervention distincte ; exercice isolé sans dispatch d'abord.

## Cohérence et Git

Les sept procédures comportent observation, diagnostic, intervention permise ou
interdite, vérification finale et escalade. Pas de ligne terminale automatiquement
supprimable ; protection des reçus, idempotence, approbations, audits et références.
Le traitement contrôlé de preuves reste exclusivement CREATE/UPDATE calendrier,
avec confirmation humaine et contrôles transactionnels, jamais exécuté ici.

Liens internes et espaces vérifiés, `git diff --check` réussi, aucun secret ou
artefact privé ajouté. Aucun changement de données/configuration Development,
preuve, durée active, migration, purge, retry, dispatch, redémarrage ou reset.
Aucun staging/commit/push. Phase 9.7.4 non commencée.

Inventaire exact : **2 fichiers nouveaux, 0 fichier suivi modifié ; index vide**.

- `docs/integration-operations-runbook-v1.md`
- `docs/phase-9-7-step-3-3-validation.md`
