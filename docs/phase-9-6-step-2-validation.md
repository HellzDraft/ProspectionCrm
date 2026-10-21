# Phase 9.6.2 — Synchronisation calendrier réelle via n8n simulé

## Référence et bilan

Reprise sur `main`, HEAD et origin/main après fetch :
`0bd40ce7cf166ff4825a8af95cf4c2a7c4f56fc3`. Arbre initial propre et index vide.
La CI [Build #70](https://github.com/HellzDraft/ProspectionCrm/actions/runs/39604175996)
est confirmée `completed / success` sur cette référence (9.6.1 : 1726/1726).

La chaîne CRM → HTTPS/HMAC → n8n → HTTPS/HMAC → CRM → IntegrationReceipt →
CalendarSyncService → CalendarEvent/CalendarSyncState a été exercée réellement sur
Development, exclusivement avec des fixtures `local-demo`. Sept exécutions n8n réussies,
sept opérations, sept tentatives et sept reçus traités ; quatre événements fictifs
persistants sur deux calendriers. Aucun fournisseur externe, OAuth, RRULE, envoi d'email
ou calendrier réel. Aucun début de 9.6.3, staging, commit ou push.

**Validation locale complète : globale finale 1742/1742, 0 échec, 0 ignoré ; build Release 0/0 et HasPendingModelChanges=false.**

Audit : rapport 9.6.1, contrats/services/dispatcher calendrier, transport partagé,
sécurité HMAC, HttpEmailSyncTransport, rapport 9.5.2, helper HMAC et workflow email.
Les conventions existantes sont conservées. Aucun changement de AutomationSafetyPolicy,
du métier Automation, des contrats C# calendar.sync, d'IntegrationSyncDispatch ou
d'IntegrationTransport. Aucune nouvelle migration.

## Sauvegarde et migration Development

PostgreSQL était déjà healthy, API/Blazor/proxy/n8n arrêtés.
Une sauvegarde complète privée précède la migration : **291666 octets**, **512 entrées**,
SHA-256 contrôlé, catalogue lisible, archive entièrement décodée par pg_restore sans
restauration. Dossier hors Git, ACL limitées au compte Windows courant et SYSTEM.
La première collecte d'empreintes a refusé une erreur de quoting dans le script privé ;
elle a été corrigée avant l'application de la migration.

Migration existante uniquement : `20261021110428_Phase961InboundCalendarSync`, depuis
`20261021075256_Phase9541EmailSendDeliveries`. Aucun volume supprimé, aucune migration
historique modifiée. Comparaison immédiatement avant/après : **54/54 tables applicatives
préexistantes identiques**, quatre nouvelles tables vides ; seule l'histoire EF avance.
L'empreinte CalendarEvents exclut uniquement les six colonnes nullable ajoutées en 9.6.1.

## Adaptateur et sécurité

`HttpCalendarSyncTransport` réutilise IntegrationTransport, son client HTTP, la validation
TLS native, HMAC, les timeouts, les bornes de réponse et IntegrationSendGate. Une seule
acquisition de la porte partagée ; le test avec MaxConcurrentSends=1 vérifie aussi le
passage nominal après libération de la porte. Aucun worker, scheduler ni retry ajouté.

Double activation obligatoire : `Integrations:Enabled` et `Integrations:CalendarSync:Enabled`.
Les deux restent false par défaut. L'endpoint administrateur HTTPS est distinct de celui
des sondes, sans userinfo/query/fragment. Aucune URL ne vient d'une requête ou d'un callback.
Après le Begin durable, l'adaptateur vérifie job, calendrier, compte, workspace, connexion,
opération, IDs de commande/corrélation, curseur, limite et révisions actives avant HTTP.
La configuration de transport réutilise la classe d'options commune déjà utilisée par email.

L'ACK ne vaut jamais import : seuls les appels humains explicites à process-result
modifient événements et curseur. Une issue ambiguë reste unknown ; une réception tardive
peut ensuite être traitée sans nouveau dispatch.

## Workflow et limites

Workflow : **Phase 9.6.2 - deterministic calendar sync**,
ID local `phase962DeterministicCalendarSync`, webhook `crm-calendar-sync-demo`.
Sources expurgées sous `docs/integrations/calendar-sync-demo/` : workflow, nœud JavaScript,
tests et instructions. Le credential HMAC local existant est réutilisé, sans nouveau
credential fournisseur. Le workflow versionné ne contient aucune association privée.

Le helper partagé vérifie signature sur octets bruts, audience, cible, clé et timestamp.
Le parseur fermé contrôle calendarRef/jobRef, previousCursor/maxItems et refuse champs
inattendus, doublons (y compris noms échappés), JSON invalide et UTF-8 invalide.
Limites par protocole : commande calendrier 16 Kio inclus, résultat 512 Kio inclus ;
probe/email.sync conservent leur limite antérieure, email.send également.
ACK 202 uniquement après callback POST /api/integrations/results accepté, signé n8n-to-crm
et certificat TLS validé. Les preuves des sept exécutions attestent HMAC/TLS valides.

Proxy et applications écoutent uniquement localhost ; n8n est publié sur 127.0.0.1:5678.
Le proxy termine HTTPS local et relaie en HTTP loopback vers n8n, conformément au montage
local existant ; ce n'est pas du TLS de bout en bout jusqu'au processus n8n.
Aucun contournement de validation de certificat. Le pilote navigateur a été relancé avec
le certificat public local de confiance, puis l'utilisateur a renouvelé sa session.

## Comptes et calendriers fictifs

Créés par API humaine avec cookie et antiforgery, jamais par INSERT SQL.
Workspace `20000000-0000-0000-0000-000000000001` ; connexion logique locale
`2500eecc-06eb-4568-92e2-00e9038610ac`. Comptes providerCode `local-demo`.

| Compte | ID compte | ID calendrier | Identité externe calendrier |
| --- | --- | --- | --- |
| phase-9-6-2-account-001 | `c11fad62-81d3-4bf9-bbd4-92ad25835dcc` | `58256f85-8996-4794-94e0-1b6c99bdc5a4` | phase-9-6-2-calendar-001 |
| phase-9-6-2-account-002 | `69175395-f151-4332-85ad-e7c80c9ade3b` | `61c6c752-a1d4-4711-b99d-1341aeadb0e8` | phase-9-6-2-calendar-002 |

Les comptes restent actifs. Le calendrier principal a été désactivé puis restauré pour
le contrôle de sécurité ; sa révision de configuration passe à 2. Le secondaire reste
à sa révision de configuration initiale. Aucun Contact ou Opportunity créé pour la démo.

## Parcours, compteurs et preuves

Chaque enqueue est observé queued. Chaque parcours a une opération calendar.sync et
exactement une tentative. Réception et import sont séparés ; les états intermédiaires
sont conservés hors Git. Tous les reçus suivants sont traités, avec état terminal du job
et de l'opération concordant. Ordre des compteurs : Created / Existing / Updated.

| Parcours | Job | Opération | MessageId du reçu | État | Compteurs | Curseur / révision |
| --- | --- | --- | --- | --- | --- | --- |
| A | `4a011ac8-8289-4f0a-8082-74e5b5384a41` | `0103cc7e-a03b-4531-9f79-fba2192b4c86` | `fe7a084e-155b-4ade-9d78-f9c48bbb029f` | succeeded | 3/0/0 | cursor-001 / 1 |
| B | `b3d2fcd1-4a46-49e6-9c58-50dee28a5ed4` | `a2a10f8a-5709-4a1b-b35e-3e3d41e42b1c` | `70375105-8cf2-4841-8912-7019e51e5e3a` | succeeded | 0/1/2 | cursor-002 / 2 |
| C | `45d01aa0-7ed7-42e3-a00a-f171c511ab55` | `e2c259a8-fb9b-4b42-89d5-c8a6a9885a2b` | `1190a4cf-b056-4bd9-950e-f5e14004fe79` | succeeded | 0/0/0 | cursor-003 / 3 |
| D | `5de78c16-7ae3-46cc-9e66-595e8bdbffdd` | `059c2d93-6e06-43ff-b7c0-d42298c303d1` | `d121f2ea-cc98-429e-acac-9caf30425d52` | failed | 0/0/0 | cursor-003 / 3 |
| E | `57ea641e-299d-448b-ba8e-d746016d890e` | `fad133de-ded9-44c2-b45d-c2e6717aa711` | `9173b822-2fac-435d-bccd-04c4114f0d01` | succeeded | 1/0/0 | cursor-001 / 1 |
| LATE | `43c22880-e11d-44e0-8292-6f897f94e5a3` | `a124a3c4-8985-4c63-a0d8-7eddc28f14a9` | `06683d8c-5049-412f-9287-f7056d9ecce7` | succeeded | 0/0/0 | cursor-002 / 2 |
| SECURITY-DISABLED | `f09146b1-4dca-4102-bc7e-e2a92885620c` | `0a91ca46-1c47-4968-9fda-a515dba79185` | `8703fca5-0e94-4a3b-afa7-7bf7fb67e486` | failed | 0/0/0 | cursor-003 / 3 |

- A : événement simple confirmé, all-day Europe/Paris et occurrence avec seriesId/occurrenceId.
- B : rejeu exact du simple, titre et révision du all-day modifiés, occurrence annulée
  explicitement à révision supérieure. Trois IDs CRM conservés, aucune suppression physique.
- C : succès vide ; reçu persistant observé avant/après arrêt et redémarrage de l'API seule,
  puis process-result réussi. Une seule exécution n8n, aucun redispatch.
- D : lot mêlant une modification admissible et un contenu divergent à même révision.
  Refus atomique CalendarEventContentConflict : événements inchangés, curseur/révision inchangés.
- E : même ExternalEventId que le simple du principal, avec ID CRM et contenu indépendants.
- LATE : délai privé temporaire de 18 secondes injecté après authentification dans le
  helper n8n, timeout proxy à 14 secondes. Preuve intermédiaire dispatch-unknown,
  tentative unknown, aucun reçu et révision 1. Callback tardif, process-result réussi,
  révision 2, toujours une seule tentative. Délai retiré ensuite ; timeout workflow restauré.
- SECURITY-DISABLED : calendrier désactivé après réception ; CalendarUnavailable au
  traitement, aucun événement modifié, aucune avance de curseur. Calendrier restauré.

Exécutions n8n 17 à 23, toutes success. Les fichiers privés associent chaque exécution
à son opération et aux validations HMAC/TLS. Un callback exact du lot A est rejoué avec
MessageId et contenu canonique conservés, nouvelle signature temporelle : alreadyRecorded=true,
pas d'événement supplémentaire, pas de nouvelle révision de curseur. Le process-result
répété après succès est également sans effet supplémentaire.

## Événements et changements d'heure

| Calendrier | ExternalEventId | ID CRM | Révision finale | Statut |
| --- | --- | --- | --- | --- |
| `58256f85-8996-4794-94e0-1b6c99bdc5a4` | calendar-demo-001 | `43acf18c-f81e-425f-a810-221b381e4f65` | 1 | confirmed |
| `58256f85-8996-4794-94e0-1b6c99bdc5a4` | calendar-demo-002 | `7a9a0241-1287-4df1-bff5-b31ee718b441` | 2 | confirmed |
| `58256f85-8996-4794-94e0-1b6c99bdc5a4` | calendar-demo-003 | `ef7d06d1-ff4c-4a97-8aa9-0d19c36be452` | 2 | cancelled |
| `61c6c752-a1d4-4711-b99d-1341aeadb0e8` | calendar-demo-001 | `adffe6c0-e029-43b1-a980-b64c4826419f` | 1 | confirmed |

All-day : 2026-03-29T00:00:00+01:00 jusqu'à 2026-03-30T00:00:00+02:00, fin exclusive,
soit 23 heures réelles. Le 25 octobre, le simple utilise +02:00 et l'occurrence l'heure
02:30 avec +01:00, sans ambiguïté. Fuseau Europe/Paris conservé, stockage UTC normalisé.
Pas de RRULE ni génération implicite d'occurrences.

Les IDs CRM et les liens Contact/Opportunity sont comparés entre A et B : inchangés.
Les liens des fixtures sont null. La lecture ciblée de l'UPDATE d'import confirme qu'il
ne réécrit ni ContactId ni OpportunityId ; aucun cas Development de mise à jour avec
liens non nuls n'a été fabriqué. Le test de migration couvre un ancien événement lié
à une opportunité, et l'événement historique Development conserve toutes ses valeurs.

## Refus de sécurité observés

| Cas | Réponse |
| --- | --- |
| HMAC absent / invalide / clé de connexion inconnue | 401 |
| Cookie humain seul sur callback | 401 |
| HMAC machine seul sur route humaine | 401 |
| calendarRef ou jobRef incohérent, correctement signé | 409 |
| JSON mal formé, correctement signé | 400 |
| Résultat hors limite, correctement signé | 413 |
| Calendrier désactivé avant process-result | 409 CalendarUnavailable |
| Révision humaine périmée | 409 CalendarRevisionConflict |

Mauvaises liaisons opération/connexion/message/curseur et concurrence saturée également
couvertes par les tests .NET ; timestamps périmés et octets altérés par les tests JavaScript.
Le script privé de contrôle TLS a initialement lu le socket après sa fermeture ; il a
été corrigé pour capturer l'état TLS à réception des headers, puis tous les contrôles
ci-dessus ont été enregistrés. Aucun changement applicatif pour ce problème de pilote.

## Préservation et état final

Comparaison après scénarios : **54/54 tables historiques identiques**, après exclusion
uniquement des nouvelles lignes calendar.sync et CalendarEvents avec CalendarId non nul.
Les sept EmailMessages, EmailSendPlans, AutomationExecutions, demandes humaines, reçus
email.send et autres données préexistantes sont conservés. La preuve falsifiée antérieure
9.5.4.2 n'est pas modifiée. Les quatre nouveaux événements sont conservés pour inspection.
Users/credentials n8n et ancien workflow probe ont les mêmes empreintes qu'avant.

Workflow calendrier désactivé, paramètres temporaires restaurés, injection de délai retirée.
L'export privé déchiffré du credential HMAC a été supprimé du conteneur après les sondes.
Activations limitées aux processus temporaires, aucun User Secret édité. API/Blazor/proxy/n8n
arrêtés, aucune écoute 5678/7075/7096/7443 ; PostgreSQL healthy. Les conteneurs jetables
utilisés par les tests ne sont pas les services Development.

## Tests et journaux

- Première invocation JS avec l'ancien Node du PATH : option --test indisponible ; reprise
  avec le runtime Node fourni par l'environnement. Aucun test exécuté par l'ancien binaire.
- JavaScript calendrier initial : 23/23 ; ensemble initial : 69/69.
- JavaScript final, couverture parsing étendue : **75/75**, dont **29 calendrier**, aucun échec/ignoré.
- Premier build Release : 0 erreur, 2 avertissements historiques CS8618 dans Settings.razor.
- Transport .NET initial : 9/9 ; couverture étendue à **16/16**, incluse dans les régressions.
- Régressions calendrier/email/intégrations : **309/309**, 0 échec, 0 ignoré, 52 secondes.
- Build Release final : **0 erreur, 0 avertissement** (build-02.log).
- Suite globale finale unique : **1742/1742**, **0 échec, 0 ignoré**, **22 min 55 s**,
  code de sortie 0. TRX Completed, 1742 résultats individuels Passed ; aucune erreur
  technique, aucun timeout ni abandon. Début `2026-10-21T15:39:16.9370490+02:00`,
  fin `2026-10-21T16:02:13.7452041+02:00`.
- EF CLI Release --no-build : **HasPendingModelChanges=false**, sortie 0. Avis de version
  outil 10.0.10/runtime 10.0.12 sans incidence sur le modèle ; aucune nouvelle migration.
- **10/10 empreintes SHA-256 inchangées** pour les fichiers du périmètre (C#, JavaScript,
  workflow et instructions ; rapport exclu), **3/3 DLL Release inchangées** pendant la passe.
- Après tous les tests : **54/54 tables historiques toujours préservées**, contrôlées à
  nouveau en lecture seule. PostgreSQL running/healthy ; n8n exited ; aucune écoute
  applicative 5678/7075/7096/7443. `git diff --check` réussi, index vide.
- Aucun secret réel, credential, certificat privé, dump, log, TRX ou artefact privé
  identifié dans le périmètre Git. Aucun fichier de migration ni AutomationSafetyPolicy modifié.

Preuves privées : `%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-6-2-20261021/`.
Sauvegarde et catalogue, manifestes SQL avant/après, preuves de chaque scénario, résultats
n8n expurgés, logs build/tests, TRX, empreintes source et DLL. Aucun de ces artefacts privés
n'est inclus dans Git. Les scripts privés servent seulement à la conduite de cette validation.

## Limites et suite 9.6.3

Simulation déterministe locale uniquement. Pas d'interopérabilité Google/Microsoft, OAuth,
credential fournisseur, scheduler, retry ou expansion de récurrences. Le futur adaptateur
devra conserver révisions monotones, identités et limites existantes. 9.6.3 non commencée.
La démonstration réseau utilise le proxy TLS local existant, pas une publication externe.

## Inventaire Git

Quatre fichiers modifiés et sept nouveaux, **11 fichiers** avec ce rapport. Index vide.
HEAD/origin/main inchangés. Aucun staging, commit ou push.

```text
 M docs/integrations/shared/CrmLocalHmac.js
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs
 M src/ProspectionCrm.Api/appsettings.json
?? docs/integrations/calendar-sync-demo/CrmCalendarSync.node.js
?? docs/integrations/calendar-sync-demo/CrmCalendarSync.test.js
?? docs/integrations/calendar-sync-demo/README.md
?? docs/integrations/calendar-sync-demo/workflow.json
?? docs/phase-9-6-step-2-validation.md
?? src/ProspectionCrm.Api/Services/Calendar/HttpCalendarSyncTransport.cs
?? tests/ProspectionCrm.Api.Tests/CalendarSyncTransportTests.cs
```
