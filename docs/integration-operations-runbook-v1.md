# Exploitation des intégrations — runbook v1

Référence : `6a65b0eeb58351ff15e905fbc99a50fdc6e663e6`, 23 octobre 2026.
Ce guide organise le diagnostic humain. Il n'autorise ni purge, retry, dispatch,
activation de transport/budget, redémarrage ou modification de preuve. La validation
9.7.3.3 n'exécute aucun POST. Les interventions métier conditionnelles ci-dessous
nécessitent une décision humaine distincte et les contrôles des routes existantes.

## Point d'entrée et règles communes

Si l'API est déjà disponible, utiliser une session humaine du workspace concerné :

- `GET /api/integration-supervision` ;
- `GET /api/integration-supervision/operations?offset=0&limit=50` ;
- `GET /api/integration-supervision/operations/{id}/diagnostic`.

La liste accepte `type`, `status`, `state` ; limit 1–100, offset 0–1 000 000.
Le diagnostic a `attemptOffset` et `receiptOffset` indépendants et le même limit.
La pagination peut se décaler entre requêtes : relire le diagnostic avant toute
décision. Un 404 ne permet pas de distinguer une ressource absente d'un autre workspace.
Un 401 demande de rétablir la session humaine, jamais de substituer une identité HMAC.
Si l'API est arrêtée, ne pas la démarrer pour ce contrôle : utiliser les agrégats
READ ONLY de la [politique de rétention](integration-retention-policy-v1.md), ou
reporter le parcours HTTP. Ne pas exporter corps métier, secrets ou signatures.

Conserver dans le dossier d'incident privé l'heure UTC, les codes et identifiants
techniques nécessaires, jamais les contenus bruts. `n8nHealth=not-assessed` signifie
que la supervision ne mesure pas la disponibilité de n8n. Une absence de résultat
ne prouve ni sa panne ni l'absence d'effet fournisseur. Ne pas confondre statut
technique, état métier et état calculé ; une ancienne tentative unknown peut
appartenir à une opération depuis résolue.

## 1. État normal ou terminé

- **Observation :** `waiting`/`NormalWaiting`, ou `completed`/`OperationCompleted`
  sans anomalie ; examiner aussi compteurs et dates des reçus.
- **Diagnostic :** un ACK est un accusé de transport, pas une preuve métier.
  `NoInterventionRequired` n'autorise pas la suppression ; une preuve tardive peut
  apparaître après la clôture.
- **Intervention :** consulter seulement ; attendre la preuve si recommandation
  `WaitForEvidence`. Aucun acquittement, retry ou réarmement.
- **Vérification finale :** nouveau GET si nécessaire, état et anomalies cohérents,
  conservation des reçus, références et approbations.
- **Escalade :** nouvelle anomalie, liaison incohérente, preuve non traitée après
  clôture ou dépassement des seuils d'attente ci-dessous.

## 2. Saturation du budget de départ

- **Observation :** réponse métier 409 `IntegrationDispatchMinuteLimitReached`
  ou `IntegrationDispatchDayLimitReached` lors d'une action humaine déjà autorisée.
  La supervision ne fournit pas un compteur de budget dédié ni une heure de retry.
- **Diagnostic :** vérifier les options non sensibles effectives sur chaque instance.
  Budget commun à email.send et calendar.event.create/update, par workspace/connexion.
  Compter les tentatives démarrées, même unknown/failed, avec PostgreSQL : minute
  `(now−60 s,now]`, jour `[minuit UTC,now]`. Limite minute prioritaire si les deux
  sont atteintes. Distinguer les limites Automation préexistantes et un gate occupé.
- **Intervention :** attendre la libération du budget ; ne pas augmenter/désactiver
  le plafond pour contourner le refus, ni supprimer des tentatives. Après expiration,
  un nouveau départ ne peut être envisagé que par décision humaine distincte sur
  une intention toujours pending/éligible avec approbation valide. Aucun retry
  automatique ni réarmement terminal. Aucun départ dans cette tranche.
- **Vérification finale :** le refus ne crée ni tentative, ni unknown, ni changement
  de révision/approbation/intention. Recompter avant une future décision ; expiration
  temporelle ne garantit pas une place si d'autres départs consomment le budget.
- **Escalade :** refus incohérent avec le comptage, configuration divergente entre
  instances, mutation malgré refus ou appel transport observé. Validation future
  uniquement sur PostgreSQL jetable et transports simulés ; jamais activation en
  Development pour démontrer la limite. Ce budget ne garantit pas les quotas fournisseur.

## 3. Attente anormalement longue

- **Observation :** `IntegrationAttemptOld` (started depuis au moins 5 minutes),
  `IntegrationWaitingOld` (waiting depuis au moins 30 minutes), dates les plus anciennes.
- **Diagnostic :** relire chronologie et état métier ; vérifier séparément l'état
  des services déjà présents. Les seuils sont informatifs, pas des timeouts de
  récupération. L'âge seul ne transforme pas waiting en unknown.
- **Intervention :** observation et investigation ; aucun démarrage n8n arrêté,
  réarmement de circuit, reconciliation, redispatch ou résolution forcée.
- **Vérification finale :** confirmer si une preuve est apparue et si l'état a
  changé, sans inférer une guérison de la seule disparition d'une alerte.
- **Escalade :** attente persistante au-delà de ces seuils ou chronologie incohérente,
  avec codes et dates techniques ; disponibilité fournisseur/n8n non déduite du CRM.

## 4. Résultat inconnu

- **Observation :** `uncertain`, `IntegrationResultUncertain`,
  `UncertainWithoutEvidence`/`UncertainAfterEvidence`, send-unknown ou operation-unknown.
- **Diagnostic :** distinguer tentative unknown historique et incertitude métier
  actuelle. Lire métadonnées et recommandations ; absence de preuve ≠ absence d'effet.
- **Intervention :** attendre une preuve ou investigation manuelle, conserver clés,
  reçus et registre simulé. Ne jamais renvoyer un email ou recréer l'événement.
  Aucune récupération sans preuve ni récupération email/sync/collecte via 9.7.2.2.
- **Vérification finale :** un résultat reste inconnu tant qu'une conclusion métier
  n'est pas démontrée. Une future preuve conduit à la procédure 5, pas à un succès présumé.
- **Escalade :** absence durable de preuve, perte de registre, réponse incohérente,
  ou besoin d'un accès fournisseur réel ; reporter ce dernier hors de cette validation.

## 5. Preuve reçue mais non traitée

- **Observation :** `evidence-pending`, `IntegrationEvidencePending`,
  `IntegrationEvidenceOld` à 30 minutes, ou `EvidencePendingAfterCompletion`.
- **Diagnostic :** relever nombre, dates et `evidenceAssessment`. `not-assessed`
  n'est ni une preuve valide ni contradictoire. `ExamineExistingEvidence` signifie
  examiner le contexte autorisé ; ne pas extraire de payload dans le support courant.
- **Intervention :** dans cette tranche, GET seulement. Hors de cette tranche et
  après décision humaine explicite, seule la commande de récupération 9.7.2.2 pour
  **CREATE/UPDATE calendrier** peut traiter une preuve enregistrée. Elle exige
  propriétaire humain, cookie + antiforgery, liaison métier valide, état compatible
  non terminal (pas pending/dispatching), absence de conflit bloquant, révision
  attendue et contexte `recoveryConfirmation` récent couvrant exactement les reçus
  non traités (1–100). Soumettre l'action `process-recorded-evidence` à
  `POST /api/integration-supervision/operations/{id}/recover` selon le
  [contrat 9.7.2.2](phase-9-7-step-2-2-validation.md). Le GET ne réserve aucun droit
  et le POST revérifie les préconditions sous transaction. Aucune action automatique.
- **Vérification finale :** après une action future acceptée, relire diagnostic et
  audit `integration.recovery` ; HTTP 200 signifie traitement exécuté, pas succeeded.
  Unknown peut subsister, not-applied peut échouer. Sur 409, réponse perdue ou 500,
  relire avant toute décision ; ne pas renvoyer automatiquement la commande.
  Audit accepté atomique avec l'effet ; refus audité séparément, absence d'audit
  possible en cas de panne/rejet préalable. Ne pas prétendre à un journal inviolable.
- **Escalade :** conflit/invalidité, plus de 100 preuves, contexte périmé, échec
  technique, famille exclue ou opération terminée. Les routes historiques éventuellement
  citées par le diagnostic ne deviennent pas une récupération contrôlée pour email,
  EmailSync, CalendarSync, collecte ou sonde. `requires-validation` n'est pas une
  permission d'exécuter ; même `available-at-observation` doit être revérifié.

## 6. Conflit persistant, échec ou métadonnées incohérentes

- **Observation :** `IntegrationConflict`, `BusinessConflict`, `EvidenceInconsistent`,
  `EvidenceContradictory`, `MetadataInconsistent` ou `OperationFailed`.
- **Diagnostic :** distinguer conflit métier, contradiction déjà persistée et échec
  certain. Une contradiction ne se déduit pas d'un simple reçu non évalué.
- **Intervention :** investigation humaine ; aucune substitution/acquittement de
  preuve contradictoire, aucune suppression, aucun forced succeeded, aucun reset de
  révision ou retour terminal → pending. Le recover contrôlé refuse un conflit.
- **Vérification finale :** préserver état, preuves, approbations, références et
  trace d'investigation. Un conflit non résolu reste signalé, pas masqué par un nettoyage.
- **Escalade :** immédiate au responsable technique/métier avec métadonnées seules.
  Toute correction doit faire l'objet d'un travail distinct et d'une validation isolée.

## 7. Rétention, journaux et sauvegardes

- **Observation :** appliquer V1–V6 de la [politique v1](integration-retention-policy-v1.md) :
  agrégats READ ONLY, catalogue FK/triggers, paramètres filtrés et métadonnées de fichiers.
- **Diagnostic :** séparer déclaré, historiquement testé, observé maintenant et non
  vérifié. Serilog déclare 14 fichiers/14 jours ; n8n 336 h/10 000 exécutions.
  Le pruning n8n ignore les conflits/unknown CRM ; les registres personnalisés sont
  distincts des exécutions. CRM n'a pas de purge par âge. Backups 7/4/6 restent proposés.
- **Intervention :** aucun DELETE, purge, VACUUM, forget/prune, reset, déplacement
  destructif, changement de durée ou restauration réelle. Classer « protection requise »
  les lignes liées à idempotence, preuves tardives, audit, approbation, conflit ou
  restauration. Terminée/traitée ne signifie pas supprimable. Ne pas lire les sauvegardes.
- **Vérification finale :** inventaire conservé sans contenus sensibles, états et
  configurations inchangés. Des nombres/taille identiques ne prouvent pas à eux seuls
  l'intégrité des octets. Conserver des copies cohérentes CRM/n8n, ne jamais perdre
  les clés d'idempotence lors d'une restauration future.
- **Escalade :** absence de copie hors machine vérifiée, perte de clé de restauration,
  croissance non maîtrisée, dépendance inconnue ou purge n8n menaçant une enquête.
  Planifier séparément un exercice isolé sans dispatch ; aucune obligation légale
  de durée n'est inventée. Voir [exploitation n8n](n8n-operations.md) et
  [sauvegardes chiffrées](n8n-encrypted-backups.md).

## Validation et limites

Les parcours ci-dessus réutilisent les contrats et résultats historiques
[supervision](phase-9-7-step-1-validation.md),
[diagnostic](phase-9-7-step-2-1-validation.md),
[récupération](phase-9-7-step-2-2-validation.md) et
[budget](phase-9-7-step-3-1-validation.md). Les chiffres de tests ne sont pas des
réexécutions de cette session. Voir le [rapport 9.7.3.3](phase-9-7-step-3-3-validation.md)
pour les contrôles réellement effectués, les limites et l'inventaire Git.
