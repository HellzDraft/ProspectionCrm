# Phase 9.7.2.2 — Récupération humaine contrôlée

23 octobre 2026. Base `main` / `origin/main` :
`aaed65046527838b9b0e1b0d6569a84f09bee6e0`, dépôt initial propre.

## Contrat et périmètre

`POST /api/integration-supervision/operations/{id:guid}/recover`

Action unique : `process-recorded-evidence`. Familles supportées :
`calendar.event.create`, `calendar.event.update`, sur livraison non terminée avec
preuve persistée non traitée. Aucun envoi fournisseur, retry ou récupération sans
preuve. Une preuve `unknown` peut laisser l'opération incertaine ; `not-applied`
peut terminer en échec. Le succès HTTP signifie « traitement exécuté », pas
« opération métier réussie ».

Le diagnostic GET fournit `recoveryConfirmation` quand les métadonnées conviennent.
Ce contexte n'est ni une approbation ni une garantie de validité des preuves.
Le client soumet explicitement :

```json
{
  "action": "process-recorded-evidence",
  "expectedRevision": 3,
  "confirmation": {
    "operationId": "<id-opération>",
    "businessId": "<id-livraison>",
    "type": "calendar.event.create",
    "businessStatus": "result-received",
    "revision": 3,
    "receiptIds": ["<id-message-preuve>"]
  }
}
```

Le corps est limité à 16 Kio, les champs sont requis et les champs inconnus refusés.
La liste contient 1 à 100 identifiants distincts. Elle couvre toutes les preuves
en attente, indépendamment de la pagination du diagnostic.

- 200 : `operationId`, `outcomeCode=recorded-evidence-processed`, `diagnosticUrl`.
- 400 : corps/action/contexte invalides ou antiforgery absent/invalide.
- 404 : opération absente ou étrangère, sans audit révélant son existence.
- 409 `RecoveryRejected` : famille exclue, liaison/état/révision/preuves modifiés,
  conflit, preuve invalide, préconditions métier refusées, concurrence ou verrou
  indisponible (attente bornée à cinq secondes dans cette commande).
- Authentification humaine requise ; anonyme/HMAC seul : 401. Une panne technique,
  notamment d'audit, reste un 500 générique, jamais présentée comme un succès.

Cookie humain, antiforgery et propriétaire du workspace sont vérifiés. Les réponses
et l'audit ne contiennent aucun payload, email, secret, signature ou exception brute.

## Transactions et conservation

La nouvelle commande appelle une variante interne de la réconciliation calendrier
existante, sans recopier ses algorithmes. Les routes historiques conservent leur
comportement. L'approbation, le plan, le calendrier et la liaison sont toujours
contrôlés par le service métier. Sous ses verrous habituels, la commande compare
l'opération, la livraison, le type, le statut, la révision et l'ensemble exact des
preuves non traitées à la confirmation client. Les états terminaux, dont `conflict`,
et les livraisons `pending`/`dispatching` sont exclus.

Les preuves invalides/contradictoires et les conflits détectés pendant l'application
entraînent un rollback, avant tout acquittement durable. Le diagnostic de conflit
des routes historiques n'est pas forcé par cette commande : celle-ci refuse et
conserve les preuves pour investigation. Seul `ProcessedAt` est renseigné pour les
preuves effectivement traitées ; contenu, empreinte, identité et réception restent
inchangés. Aucun état succeeded n'est imposé hors de la réconciliation existante.

Deux commandes identiques concurrentes sont sérialisées par les verrous métier :
la seconde constate la révision/les preuves modifiées et répond 409. Un rejeu après
perte de réponse ne redouble pas l'effet ; relire le diagnostic et l'audit.

## ActivityEntry et limites d'audit

Le schéma existant suffit : `EntityTypeCode=integration-operation`, opération dans
`EntityId`, `EventTypeCode=integration.recovery`, opérateur dans `ActorUserId`,
workspace et date. `MetadataJson` contient seulement `operatorId`, `operationId`,
`action`, `outcome`. L'identifiant opérateur est aussi conservé dans les métadonnées
car la FK existante peut mettre `ActorUserId` à null lors d'une suppression utilisateur.
Les entrées sont consultables par la lecture ActivityEntry existante.

**Action acceptée :** mutation métier, acquittement et audit `processed:<statut>`
sont écrits dans la même transaction. Une défaillance d'insertion de l'audit annule
l'ensemble, vérifié par injection de panne dans la base de test jetable.

**Refus métier contrôlé :** après rollback, une entrée séparée enregistre
`unsupported-family`, `binding-unavailable` ou `state-or-evidence-rejected`.
Cet audit de refus n'est pas atomique avec la tentative : panne ou annulation peut
l'empêcher. Les rejets d'authentification, d'antiforgery, de syntaxe et les 404 ne
créent pas d'activité. Aucun audit ne prétend couvrir une panne avant son écriture.
ActivityEntry n'est pas un registre inviolable ou append-only garanti par PostgreSQL.

## Familles exclues de cette route

- EmailSend : la réconciliation existante peut acquitter certains résultats après
  un état `sent` sans qualifier une contradiction entre toutes les preuves. Pas de
  réutilisation aveugle au regard de l'interdiction d'acquitter une contradiction.
- EmailSync / CalendarSync : certains rejets/conflits sont finalisés et commités
  par le processeur avant de lever l'erreur ; un simple wrapper ne peut garantir
  ici un refus sans mutation ni un audit atomique.
- Collecte : les chemins de rejet/ingestion possèdent leurs propres commits et
  finalisations ; le même contrat de confirmation transactionnelle n'est pas établi.
- Sondes techniques : aucune récupération métier proposée.

Leurs routes existantes restent inchangées. Étendre cette tranche nécessiterait
un travail transactionnel explicite, sans affaiblir les règles actuelles.

## Validation locale

| Vérification | Résultat |
| --- | --- |
| Build Release final, `--no-restore` | 0 erreur, 0 avertissement |
| Nouveaux tests récupération, dernière passe | 30 réussis, 0 échec, 0 ignoré |
| Régressions ciblées existantes | 126 réussies, 0 échec, 0 ignoré |
| Total des scénarios validés, sans compter les répétitions | 156 |
| `git diff --check`, y compris nouveaux fichiers | Réussi |

Commandes : `dotnet build ProspectionCrm.slnx --configuration Release --no-restore`,
puis `dotnet test tests/ProspectionCrm.Api.Tests/ProspectionCrm.Api.Tests.csproj
--configuration Release --no-build --no-restore` avec filtre `FullyQualifiedName~`
sur `IntegrationRecoveryTests`, `IntegrationDiagnostic`, `IntegrationSupervision`,
`CalendarEventDeliveryTests`, `CalendarEventDeliverySecurityTests`,
`CalendarSyncLifecycleTests`, `OperatorSecurityTests` (union).

La passe élargie (`targeted-final.trx`) contient 155 succès et un échec intermédiaire :
le timeout de verrou était enveloppé par EF et renvoyait 500 plutôt que 409. Le
mapping des erreurs PostgreSQL enveloppées a été corrigé ; `lock-check.trx` passe,
puis `recovery-final.trx` valide les 30 tests de récupération. Les 126 régressions
déjà réussies n'ont pas été relancées. Build final : `build-final-02.log`.
Les deux avertissements CS8618 historiques de Settings.razor vus au premier build
n'ont pas fait l'objet d'une correction hors périmètre.

Tests : CREATE/UPDATE valides, répétition, deux récupérations concurrentes, verrou
indisponible, révision/liaison/statut/preuves modifiés, traitement historique intervenu,
preuve invalide/contradictoire, conflit de version et conflit connu, preuve absente,
familles exclues, unknown/not-applied, 401/404/antiforgery, JSON strict, conservation
des preuves, absence d'appel fournisseur, audit succès/refus et rollback sur panne
d'audit. Empreintes avant/après pour les refus ; absence de mutation des GET conservée.

Aucun service Development, fournisseur réel, invitation, migration ou suite globale
locale. La fonction/trigger de panne d'audit est créée puis supprimée uniquement
dans PostgreSQL Testcontainers. Preuves hors Git :
`%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-7-2-2-20261023/`.

## Inventaire Git

10 fichiers : 5 modifiés, 5 nouveaux ; index vide, aucun commit/push.

```text
 M src/ProspectionCrm.Api/Controllers/IntegrationSupervisionController.cs
 M src/ProspectionCrm.Api/Dtos/Integrations/IntegrationDiagnosticDtos.cs
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/Calendar/CalendarEventReconciliation.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationOperationDiagnostic.cs
?? docs/phase-9-7-step-2-2-validation.md
?? src/ProspectionCrm.Api/Dtos/Integrations/IntegrationRecoveryDtos.cs
?? src/ProspectionCrm.Api/Services/Integrations/IntegrationRecoveryAudit.cs
?? src/ProspectionCrm.Api/Services/Integrations/IntegrationRecoveryService.cs
?? tests/ProspectionCrm.Api.Tests/IntegrationRecoveryTests.cs
```
