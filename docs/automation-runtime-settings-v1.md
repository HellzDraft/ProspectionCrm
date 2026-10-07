# Paramètres d’automatisation métier — Phase 8.1

Cette phase fournit une configuration persistante par Workspace et une politique
pure d’autorisation. **Aucune AutomationRule n’est exécutée ou préparée** : aucun
Worker d’automatisation, compteur d’exécution, e-mail, candidature ou effet externe.
L’infrastructure de collecte Phase 7 est inchangée et n’est pas soumise à ce kill switch.

## Configuration et stockage

Table `AutomationRuntimeSettings`, clé primaire `WorkspaceId` (uuid), également
clé étrangère vers `Workspaces.Id`, relation un-à-un et suppression Restrict.
La FK garantit l’appartenance et la PK l’unicité ; la présence de la ligne est
assurée par migration et bootstrap, pas par une création implicite à la lecture.

| Champ | Type PostgreSQL | Défaut / contrainte |
|---|---|---|
| WorkspaceId | uuid | PK/FK, obligatoire |
| IsEnabled | boolean | false |
| OperatingModeCode | varchar(20) | manual ; manual, assist, automatic uniquement |
| MaxExecutionsPerMinute | integer | 10 ; 1 à 100 |
| MaxExecutionsPerDay | integer | 100 ; 1 à 10000 et >= limite par minute |
| MaxConsecutiveFailures | integer | 3 ; 1 à 20 |
| CreatedAt | timestamp with time zone | UTC courant |
| UpdatedAt | timestamp with time zone, nullable | null ; UTC à chaque PUT réussi |

Ces limites sont stockées et validées. Leur enforcement par comptage, limitation
de débit et arrêt après échecs appartient au futur moteur, absent en 8.1.

## Modes, kill switch et politique fixe

Codes sensibles à la casse, persistés/exposés en minuscules : `manual` (intervention
manuelle), `assist` (approbation humaine), `automatic` (autorisation automatique).
`IsEnabled=false` bloque toute préparation/exécution future, quel que soit le mode.
Le choix `manual` ne lance rien automatiquement.

| IsEnabled | Mode demandé | general : effectif / décision | email : effectif / décision | application : effectif / décision |
|---|---|---|---|---|
| false | tous | null / blocked | null / blocked | null / blocked |
| true | manual | manual / manual | manual / manual | manual / manual |
| true | assist | assist / approval-required | assist / approval-required | manual / manual |
| true | automatic | automatic / automatic | assist / approval-required | manual / manual |

`application` couvre candidatures et propositions externes. Les plafonds email
et application sont fixes et non modifiables par l’API. Une catégorie inconnue
retourne `blocked`, raison `unknown-category`, jamais un repli vers general.
Un mode corrompu actif retourne `blocked` / `invalid-mode`. Un blocage expose
`effectiveModeCode: null` pour ne suggérer aucune autorisation.

Autres raisons stables : `automation-disabled` (kill switch), `global-mode`
(application normale du mode), `email-assist-cap` (automatic plafonné à assist),
`application-manual-cap` (application toujours manuelle).
Ces décisions sont une politique, pas une preuve d’approbation ni une exécution.

## API

`GET /api/automation-settings` et `PUT /api/automation-settings` utilisent uniquement
`ICurrentWorkspaceProvider`. La sélection V1 exige exactement un Workspace actif.
Le client ne peut pas sélectionner/modifier le Workspace. GET ne modifie aucune donnée.

PUT exige les cinq champs ; propriétés inconnues (dont WorkspaceId), champs manquants,
types invalides et limites invalides donnent 400. Les plafonds ne figurent pas dans le DTO.

```http
PUT /api/automation-settings
Content-Type: application/json

{
  "isEnabled": true,
  "operatingModeCode": "automatic",
  "maxExecutionsPerMinute": 10,
  "maxExecutionsPerDay": 100,
  "maxConsecutiveFailures": 3
}
```

GET et PUT réussi retournent 200 avec la même structure, par exemple :

```json
{
  "workspaceId": "11111111-1111-1111-1111-111111111111",
  "isEnabled": true,
  "operatingModeCode": "automatic",
  "maxExecutionsPerMinute": 10,
  "maxExecutionsPerDay": 100,
  "maxConsecutiveFailures": 3,
  "createdAt": "2026-10-07T12:00:00+00:00",
  "updatedAt": "2026-10-07T12:01:00+00:00",
  "decisions": [
    {"categoryCode":"general","requestedModeCode":"automatic","effectiveModeCode":"automatic","decisionCode":"automatic","reasonCode":"global-mode"},
    {"categoryCode":"email","requestedModeCode":"automatic","effectiveModeCode":"assist","decisionCode":"approval-required","reasonCode":"email-assist-cap"},
    {"categoryCode":"application","requestedModeCode":"automatic","effectiveModeCode":"manual","decisionCode":"manual","reasonCode":"application-manual-cap"}
  ]
}
```

Erreurs `application/problem+json` avec extension `code` : 400 `InvalidRequest`,
`InvalidOperatingMode`, `InvalidAutomationLimits` ; 409 `WorkspaceUnavailable`
ou `AutomationSettingsMissing`. La ligne manquante est journalisée avec son
WorkspaceId, sans exposer de diagnostics internes. GET et PUT ne la recréent pas.
Une défaillance technique inattendue donne 500 `AutomationSettingsInternalError` ;
SQL, stack traces et valeurs rejetées ne sont pas recopiés dans les réponses.

## Migration et bootstrap

Migration `20261007124044_Phase81AutomationRuntimeSettings` : crée la table et ses contraintes, puis insère
une ligne sûre pour **chaque** Workspace existant, y compris archivé. Compatible
avec une base vierge. Aucune règle, exécution ni job n’est créé. Les migrations
historiques restent inchangées. Down supprime uniquement cette table ; un nouvel
Up recrée les valeurs par défaut (les personnalisations supprimées ne sont pas récupérées).

Le bootstrap crée les paramètres dans la transaction du Workspace. Répéter le
bootstrap conserve intégralement paramètres et dates personnalisés. Un bootstrap
explicite peut réparer une ligne absente dans une installation compatible ; le GET
ne le fait jamais. Tout échec de création annule toute la transaction.

## Validation et suite

Tests purs : matrice complète des catégories/modes/kill switch, catégories inconnues
et mode corrompu. Tests PostgreSQL/Testcontainers : bootstrap atomique/idempotent,
API, isolation, refus de champs non autorisés, contraintes SQL, migration et Down/Up.

La Phase 8.2 devra définir l’évaluation/exécution persistante des règles, l’application
des quotas et du coupe-circuit, les approbations et les garanties de concurrence.
Ce périmètre futur reste à préciser ; rien de cela n’est annoncé opérationnel ici.
Aucun frontend Blazor, BackgroundService d’automatisation, projet Worker, secret,
e-mail ou candidature n’est ajouté en 8.1.
