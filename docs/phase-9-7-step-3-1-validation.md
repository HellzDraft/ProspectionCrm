# Phase 9.7.3.1 — Budget des départs d'intégration

Référence : `main`, `3e2ccd9d1a24398fa7623bf16db28a369ec0628b` (HEAD,
origin/main et référence distante vérifiés). Travail local, sans indexation,
commit ni push.

## Contrat

`Integrations:DispatchLimits` :

| Option | Défaut | Bornes inclusives |
| --- | --- | --- |
| Enabled | false | booléen |
| MaxStartsPerMinute | 60 | 1–10 000 |
| MaxStartsPerDay | 1 000 | 1–1 000 000 |

Les bornes sont validées même quand le budget est désactivé, via la validation
existante d'IntegrationOptions au démarrage et lors des contrôles métier.
Les deux plafonds sont indépendants. Cette protection CRM ne garantit aucun
quota Google/Microsoft. Les limites Automation et les approbations restent inchangées.

Budget **commun** à `email.send`, `calendar.event.create`, `calendar.event.update`,
compté séparément pour chaque couple workspace/connexion. Chaque ligne
IntegrationAttempt compte, sans filtre sur son statut ou celui de l'opération :
`started`, `acknowledged`, `failed` et `unknown` consomment le budget.
Un départ enregistré avant une interruption du processus reste comptabilisé,
même si l'appel réseau n'a finalement pas eu lieu. Les autres types sont exclus.

Horloge `statement_timestamp()` PostgreSQL, échantillonnée après acquisition des
verrous. Le même instant est utilisé pour le comptage et StartedAt :

- minute glissante : `(instant − 60 secondes, instant]` ;
- journée UTC : `[minuit UTC, instant]`, indépendamment du fuseau de session SQL.

Refus HTTP 409 via les exceptions métier existantes :
`IntegrationDispatchMinuteLimitReached` ou `IntegrationDispatchDayLimitReached`
(priorité minute si les deux plafonds sont atteints). Aucun compteur, tentative,
statut unknown, révision, preuve, plan ou approbation n'est écrit lors du refus.
L'intention pending reste soumise aux règles métier ordinaires pour un nouveau
départ humain après expiration du budget. Aucun retry ni réarmement automatique.

## Concurrence et limites

Les deux BeginAsync réutilisent l'ordre existant : verrou workspace,
AutomationRuntimeLock PostgreSQL, puis verrous métier. Le budget est consulté
après les contrôles métier, avant l'INSERT IntegrationAttempt, dans la même
transaction explicitement READ COMMITTED. Le verrou transactionnel commun au
workspace sérialise les trois types entre connexions SQL et processus API ;
le comptage après attente voit les commits précédents. La granularité du verrou
reste le workspace, mais les compteurs sont isolés par connexion.

Le commit précède le transport. IntegrationSendGate reste acquis une seule fois
par le dispatcher ; le budget ne l'acquiert pas. Aucun appel fournisseur ni
changement des mécanismes de réconciliation/récupération.

Toutes les instances API doivent déployer cette version et une configuration
cohérente : une instance où la protection est désactivée ne garantit pas le
plafond configuré ailleurs. Aucun benchmark de très gros historiques n'est
revendiqué. Le comptage réutilise les index et le schéma existants ; pas de
migration, compteur durable supplémentaire, purge ou rétention.

## Validation locale

Tests PostgreSQL 18 jetables via les fixtures existantes et transports simulés.
Les migrations historiques ne servent qu'à initialiser ces bases de test.
Aucun service, paramètre ou base Development modifié ; aucun fournisseur réel.

Couverture nouvelle : bornes de configuration ; bornes exactes minute/minuit UTC
avec fuseau SQL différent ; isolation workspace/connexion et exclusion sync ;
CREATE/UPDATE refusés sans mutation puis départ humain après expiration ;
unknown compté ; désactivation ; budget partagé email/calendrier dans les deux
sens et en concurrence ; deux hôtes API avec gates indépendants ; disponibilité
du verrou SQL pendant le transport. Comparaison des opérations, tentatives,
intentions, plans et approbations avant/après refus.

Les journaux/TRX sont conservés hors dépôt dans
`%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-7-3-1-20261023/`.
Premier passage : 120 réussites et 8 échecs de montage des nouveaux tests
(options changées après sérialisation de la configuration de l'hôte).
Après correction du montage : `budget-02.trx`, 16/16 réussis.
Le passage final ajoute la concurrence email/calendrier et vérifie l'isolation
transactionnelle explicite. Aucun lancement de suite globale locale.

Résultats finaux :

- `build-final.log` : build Release réussi, 0 erreur, 0 avertissement.
  Le premier build avait signalé les deux CS8618 préexistants de Settings.razor ;
  le build final est incrémental, ces avertissements historiques n'ont pas été corrigés.
- `targeted-final.trx` : **129 réussis, 0 échoué, 0 ignoré**,
  dont **17 nouveaux et 112 régressions** (EmailSendDeliveryTests,
  CalendarEventDeliveryTests/ConcurrencyTests/SecurityTests, IntegrationRecoveryTests).
- `git diff --check` et contrôle des espaces des trois nouveaux fichiers réussis.
- Index vide ; aucun commit/push. Aucun changement des preuves précédentes.

## Inventaire Git

7 fichiers : 4 modifiés et 3 nouveaux. Aucun historique, migration, preuve ou
artefact privé dans le diff ; les adresses des tests sont synthétiques.

Modifiés :

- `src/ProspectionCrm.Api/Services/Calendar/CalendarEventDeliveryService.cs`
- `src/ProspectionCrm.Api/Services/Email/EmailSendDeliveryService.cs`
- `src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs`
- `src/ProspectionCrm.Api/appsettings.json`

Nouveaux :

- `src/ProspectionCrm.Api/Services/Integrations/IntegrationDispatchBudget.cs`
- `tests/ProspectionCrm.Api.Tests/IntegrationDispatchBudgetTests.cs`
- `docs/phase-9-7-step-3-1-validation.md`
