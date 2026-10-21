# Phase 9.5.4.2 — n8n réel et fournisseur email simulé persistant

## Référence et périmètre

Validation Development du 21 octobre 2026, branche `main`, HEAD et origin/main
`f5b68dac73ed21550ac8fb71c39edeaf56114c28`. Arbre initial propre et index vide.
La référence distante est également vérifiée par `git ls-remote`.
La phase 9.5.4.1 et son rapport restent inchangés. Aucun staging, commit ou push.

La chaîne réelle CRM → HTTPS/HMAC → n8n → HTTPS/HMAC → CRM est validée avec
un fournisseur **simulé**, sans SMTP, Gmail, Outlook, OAuth ou API d'expédition.
Les deux messages outbound ajoutés sont des preuves fictives d'acceptation locale,
pas des emails envoyés ou livrés. Tous les nouveaux expéditeurs/destinataires sont
en `example.invalid`. Aucun worker d'automatisation, de collecte ou scheduler activé.

## Sauvegarde et migration

PostgreSQL et n8n étaient arrêtés ; aucun listener API/Blazor/proxy initial.
PostgreSQL est démarré avec le Compose existant, contrôlé healthy, puis sauvegardé
avant toute migration ou création de donnée. Archive privée PostgreSQL custom :
**256 809 octets**, **466 entrées** de catalogue. `pg_restore --list` et décodage
intégral `pg_restore --file=/dev/null` réussissent sans restauration. Le SHA-256
est vérifié avant migration ; dossier limité au compte Windows courant et SYSTEM.

Depuis `20261016093720_Phase951InboundEmailSync`, seules les migrations existantes
suivantes sont appliquées :

- `20261016123347_Phase953EmailSendPlans` ;
- `20261021075256_Phase9541EmailSendDeliveries`.

Aucune nouvelle migration, aucune migration historique ni snapshot EF modifié.
Les 52 tables applicatives préexistantes conservent leurs comptes et empreintes
immédiatement après migration, en excluant uniquement la nouvelle colonne
`EvidenceNumber` du hash des reçus. Le hash normalisé des dix reçus est identique.
Le nombre total de tables passe de 53 à 55, historique EF inclus.

Preuves privées hors Git :
`%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-5-4-2-20261021`.
Ce dossier contient sauvegarde, empreintes, réponses, TRX, journaux, exports privés
et copie cohérente SQLite/WAL n8n pour l'extraction hors ligne après arrêt.

## Transport email.send

`HttpEmailSendTransport` réutilise `IntegrationTransport`, TLS natif, HMAC v1,
limites de réponse et timeout. Activation séparée `Integrations:EmailSend:Enabled`,
avec endpoint HTTPS administrateur `Integrations:EmailSend:Endpoint`.
Les configurations versionnées gardent `Integrations:Enabled=false` et
`Integrations:EmailSend:Enabled=false`. Les endpoints HTTP, userinfo, query et
fragment sont refusés. L'endpoint générique et EmailSync restent indépendants.

Le transport revérifie workspace/connexion/compte/plan/opération/delivery,
ContentHash, identité de commande et corrélation avant réseau. Il ne prend pas
`IntegrationSendGate` une seconde fois : le dispatcher le possède déjà.
HTTP 202 complet signifie seulement Acknowledged. Tous les résultats réseau
Failed/Unknown du transport partagé deviennent Unknown, y compris HTTP 4xx et
erreurs TLS ; cette classification est volontairement conservatrice. NotSent
est réservé ici aux refus locaux de configuration/liaison avant appel HTTP.
Aucun retry, aucun nouvel envoi depuis la réconciliation.

Les activations temporaires sont exclusivement des variables des processus.
Les User Secrets sont inchangés par SHA-256, y compris par rapport à 9.5.2.
`AutomationSafetyPolicy`, plans et demandes d'approbation existants sont inchangés.

## Workflow et simulateur

Export expurgé : `docs/integrations/email-send-demo/workflow.json`.
Nom : **Phase 9.5.4.2 - simulated outbound email delivery**.
Identifiant local : `phase9542SimulatedEmailSend`.
Webhook : `/webhook/crm-email-send-demo`.

```text
CRM → https://localhost:7443/webhook/crm-email-send-demo
    → proxy Windows loopback → http://127.0.0.1:5678 → n8n
    → https://localhost:7096/api/integrations/results → CRM
```

Le certificat localhost déjà approuvé est réutilisé ; aucune exception TLS,
aucune exportation de clé privée. Callback TLS standard avec CA publique existante,
`rejectUnauthorized=true`, TLS 1.2 minimum, nom TLS localhost résolu dans Docker
via host.docker.internal. Tous les listeners applicatifs observés sont loopback.

Installer `CrmEmailSend.node.js` avec le dossier frère `shared` et le credential
natif `CrmProbe.credentials.js` déjà utilisé. Renseigner explicitement accountRef,
workspaceRef et connectionRef dans le nœud. L'export versionné ne contient ni ID
de credential, ni ID d'installation, ni pinData/exécution et reste `active=false`.
L'export réel final correspond à cet export après retrait des valeurs locales.

`CrmLocalHmac.js` conserve les octets bruts, timingSafeEqual, timestamp, audience,
TLS et limite 4 Kio des anciens profils. Seul email.send accepte 65 536 octets et
un snapshot direct. Le contrôle des propriétés dupliquées est local à chaque
objet, gère les échappements JSON et borne la profondeur à six ; formes et champs
du snapshot sont strictement vérifiés. Les callbacks email.send restent à 4 Kio.
Le proxy privé dédié est également borné à 65 536 octets sur ce seul webhook.

Le simulateur ne fait aucun réseau fournisseur. État dans le volume privé n8n,
`/home/node/.n8n/email-send-demo-state/email-send-{deliveryId}.json`, jamais dans
PostgreSQL CRM. Création exclusive `wx`, écriture synchrone et fsync ; une même
clé retrouve la même preuve. Un contenu différent ou un fichier incomplet provoque
un refus, jamais une nouvelle acceptation. Quatre fichiers existent, avec deux
acceptations au total. Le redémarrage/rechargement du profil est testé hors réseau.

Scénarios locaux sélectionnés uniquement par les sujets fermés `phase9542-accepted`,
`phase9542-late`, `phase9542-not-sent`, `phase9542-failed` ; adresses example.invalid
obligatoires. Ce profil n'est pas un adaptateur fournisseur généraliste.
Pour la preuve tardive et le replay, l'outil privé lit la preuve existante et
utilise `sendCallback` du helper partagé, sans nouvelle commande d'envoi.
Le credential déchiffré temporaire reste privé et est supprimé après ces contrôles.

## Compte et approbations humaines

Compte dédié `ada164d6-4309-4c5c-9831-bdcc7a3720d6`, provider `local-demo`, identité
externe `phase9542-simulated-outbound`, From `sender@example.invalid`.
Workspace `20000000-0000-0000-0000-000000000001` ; connexion logique autorisée
`2500eecc-06eb-4568-92e2-00e9038610ac`.

Chaque plan est créé draft puis soumis via les API humaines. La demande passe
pending puis est approuvée via AutomationActionRequest avec la véritable session
opérateur et l'antiforgery. L'utilisateur a renouvelé sa session dans Edge ;
aucune approbation SQL fabriquée. Le navigateur est fermé en fin de validation.
Settings temporairement enabled/manual, limites 10/minute, 100/jour, trois échecs ;
les cinq valeurs initiales sont restaurées. UpdatedAt reflète ces changements.

| Parcours | Plan | Approval | Delivery |
| --- | --- | --- | --- |
| A/B | `94bf99d3-952b-4cdf-8912-9b55d0113397` | `bfb3e3a0-1023-4be5-a9ac-b3a8ef10d4f1` | `0f36dbc2-846f-4598-a426-11fdaecd27dc` |
| C | `b16a7292-612a-4297-8650-9bdea04fd830` | `e72f34e3-8510-4c3d-a321-3c3807864079` | `e3c87812-4ecc-40eb-a118-fdd648d0ae40` |
| D | `0182e0e3-2f9c-4ef7-8d2e-9f5e38d91baa` | `7a7d5c00-1bd3-44f0-9170-a526914575a4` | `f8a501a5-d812-41e1-9a78-f70ccb889a40` |
| E | `c3141e6d-3903-4aed-8bd4-988aa106dc2a` | `cf3ab0f0-79e6-456d-81bc-b15ddbf73b14` | `ace70d36-2382-4acc-b02a-289cd8014f78` |
| Pending refusé | `8ab18f8e-b7ce-4cfe-8740-fd320cdbd33f` | `73529c8a-e4e5-4642-ab64-9cb37df70fe7` | aucune |
| Compte/From refusés | `ab6df7b9-ff39-4a1a-a38c-ed888fa717f2` | `9cb33b39-af80-40fe-8ab0-04e45111d0a5` | `4dd801ed-534e-40fe-993d-995c13c196a6` |

## Parcours réels

| Parcours | Exécution n8n | HTTP n8n | État après réconciliation | Nouveaux messages |
| --- | ---: | ---: | --- | ---: |
| A : accepted | 13 | 202 | sent, opération succeeded | 1 |
| B : replay exact | aucune nouvelle | callback 202 alreadyRecorded | sent inchangé | 0 |
| C : acceptation mémorisée, réponse ambiguë | 14 | 503 | send-unknown puis sent avec preuve tardive | 1 |
| D : refus explicite avant acceptation | 15 | 202 | failed / EmailDefinitelyNotSent | 0 |
| E : failed générique | 16 | 202 | send-unknown / EmailSendUnknown | 0 |

Quatre exécutions n8n success, incluant le 503 intentionnel du scénario C.
Les trois callbacks initiaux confirment signatureValidated, callbackTlsValidated
et callbackStatus=202. Les commandes A/D font 6 700 octets et E 6 698 octets :
le parcours réel dépasse donc effectivement l'ancienne limite 4 Kio.

A : intention unique, hash égal au plan, aucune EmailMessage avant dispatch ni
après réception de preuve avant reconcile. Après reconcile : reçu traité, un
message exactement égal au snapshot approuvé (From/To/Cc, sujet/corps, compte,
références CRM), provider ID fictif et date d'acceptation. L'ACK seul ne crée rien.
Replay exact avec même messageId/contenu et nouvelle signature : 202 idempotent.
from-plan retrouve la même intention, dispatch répété est 409, reconcile répété
est 204 ; toujours un reçu initial, une tentative et un message.

C : le simulateur mémorise une acceptation mais ne transmet pas son callback et
retourne 503. Dispatch et rapprochement laissent send-unknown, sans nouveau message.
Redispatch 409. Une consultation du fichier local retrouve la même preuve ; son
callback HTTPS/HMAC est durablement reçu. L'API est arrêtée puis redémarrée ; les
lignes delivery/opération/tentative/reçu/messages sont identiques avant/après.
La réconciliation suivante crée le second message, avec une seule tentative,
une seule commande n8n et une seule acceptation pour cette clé.

D : preuve `not-sent`, aucun provider ID, aucune acceptation simulée, aucun message.
E : preuve générique `failed`, aucune preuve certaine de non-envoi ; le diagnostic
EmailSendUnknown et l'incertitude sont conservés. Redispatch refusé dans les deux cas.

## Sécurité ciblée et preuve invalide

- Callback anonyme : 401 ; callback avec cookie humain seul : 401.
- HMAC machine valide seul sur route humaine : 401, TLS vérifié.
- Plan non approuvé : 409 à from-plan, aucune intention ni tentative.
- Compte désactivé et From modifié avant dispatch : 409, aucune tentative réseau.
  Compte synthétique restauré actif/From original, Revision=3.
- Modification du contenu approved par PUT : 409 EmailSendPlanFrozen.
- Faux operationId signé : refus, aucun reçu ajouté.
- Faux providerCode signé : reçu durable accepté par l'authentification, mais
  réconciliation **409 EmailProviderProofInvalid** ; aucun message supplémentaire.

La preuve fournisseur volontairement invalide est conservée non traitée sur A,
après sa réussite initiale : deuxième receipt, aucun effet sur le message ou l'état
sent. Ce diagnostic négatif fait partie des données synthétiques conservées.
Une nouvelle réconciliation de A rencontrera encore ce reçu invalide ; aucun
mécanisme de suppression ou d'acceptation forcée n'a été ajouté. Une signature
valide prouve la provenance technique, pas la validité métier de la preuve.
Un diagnostic manuel ultérieur est nécessaire pour examiner cette preuve falsifiée
et déterminer son traitement. Elle reste conservée non traitée dans Development ;
le message confirmé reste inchangé et aucun envoi n'est réactivé.

## Préservation des données

Les 134 empreintes de lignes relevées avant scénarios sont comparées après :
132 identiques ; seuls le compte nouvellement créé pour cette phase (trois PUT
de sécurité) et AutomationRuntimeSettings (valeurs restaurées, UpdatedAt changé)
diffèrent. Les deux comptes historiques, quatre messages inbound, ancien message
outbound, contacts, sociétés, opportunités, calendrier, règles et exécutions
historiques sont inchangés. Les utilisateurs, MFA, credentials et ancien workflow
technical.probe n8n restent identiques par empreinte.

| Table | Avant migration/scénarios | Après scénarios |
| --- | ---: | ---: |
| EmailAccounts / EmailSyncStates | 2 / 2 | 3 / 3 |
| EmailMessages inbound / outbound | 4 / 1 | 4 / 3 |
| EmailSendPlans / EmailSendDeliveries | tables absentes | 6 / 5 |
| AutomationActionRequests | 0 | 6 (5 approved, 1 pending) |
| IntegrationOperations / Attempts / Receipts | 10 / 10 / 10 | 15 / 14 / 15 |
| Contacts / Companies / Opportunities / CalendarEvents | 1 / 1 / 3 / 1 | identiques |
| AutomationExecutions / AutomationJobs | 1 / 0 | identiques |

## Tests et incidents conservés

| Contrôle | Résultat |
| --- | --- |
| Build Release final (`build-02`) | 0 erreur, 0 avertissement |
| JavaScript (`js-final`) | 46/46, aucun échec/ignoré |
| Transport email.send, delivery, sécurité/transport intégration (`targeted-01`) | 93/93, aucun échec/ignoré, 40 s |
| Régressions email/plan/compte/intégration/collecte/opérateur/policy (`regressions-01`) | 389/389, aucun échec/ignoré, 1 min 28 s |
| Suite globale finale unique (`global-final`) | **1 641/1 641**, 0 échec, 0 ignoré, **21 min 42 s** |
| HasPendingModelChanges | false, commande EF sans build |
| Empreintes source | 9/9 identiques au manifeste final ; correction JS finale tracée ci-dessous |
| git diff --check | Réussi, code 0 ; fichiers nouveaux également contrôlés |
| Préservation Development pendant les tests | Empreintes et comptes des 55 tables identiques avant/après |

Les nouveaux tests .NET vérifient HTTPS/HMAC réel en serveur de test, commande
>4 Kio, gate de capacité 1, 202 sans message, 400/401/500 conservés unknown,
absence de redispatch, endpoints dangereux et deux activations indépendantes.
Les tests JS vérifient acceptation persistante unique, conflit de clé, reprise,
late/not-sent/failed, scope, adresses, propriétés inconnues/dupliquées, taille et HMAC.
Les bases automatisées sont jetables, jamais Development.

Aucun passage rouge des tests exécutés. Incidents de préparation conservés :
Node système 14 refusait `--test`/Playwright ; utilisation du runtime Node 24.19.0.
Le premier build avait les deux CS8618 historiques de Settings.razor ; build final
sans avertissement. Le pilote navigateur a nécessité la CA publique existante
via NODE_EXTRA_CA_CERTS, sans désactivation TLS, puis une reconnexion utilisateur
après adaptation des routes. Un GET de compte individuel rendait 405 : utilisation
du GET de liste existant. La première extraction SQLite sans WAL n'exposait pas
les dernières exécutions : extraction cohérente avec WAL/SHM après arrêt. Aucun
défaut métier découvert, aucune assertion historique supprimée, aucune suite
globale initiale relancée inutilement.

Pendant la passe globale .NET, la revue du profil JS a relevé une borne de corps
à 50 000 au lieu des 32 000 caractères du contrat CRM. Elle est réduite à 32 000,
avec assertion de refus à 32 001 ; les 46 tests JS sont relancés et réussissent.
Les quatre commandes/preuves réelles conservées sont aussi revalidées et rejouées
hors réseau avec ce profil final, sans changement de preuve. La copie installée
est actualisée pendant que n8n reste arrêté. Seuls ces deux fichiers JS changent
par rapport au lancement global : 7/9 autres empreintes identiques, notamment
les quatre sources C# ; aucun binaire .NET changé, donc aucune seconde suite
globale justifiée. Un second manifeste de neuf empreintes conserve l'état final.

La suite globale termine proprement avec 1 641 résultats individuels réussis,
zéro échec et zéro non-exécuté. Elle utilise les binaires Release finaux, sans build
ni restore et sans filtre ; TRX/journal restent hors dépôt. Aucune seconde suite
globale lancée. Le contrôle final confirme PostgreSQL seul running/healthy,
aucun listener 5678/7075/7096/7443 et aucune modification de Development par les
tests automatisés. HEAD et origin/main restent au SHA de référence ; index vide.

## État final et limites

Workflow dédié et workflows historiques désactivés. Transport email.send et
intégrations désactivés par défaut, activations temporaires disparues à l'arrêt.
Settings restaurés ; API, Blazor, proxy, navigateur et n8n arrêtés ; PostgreSQL
Development healthy avec preuves conservées. Aucun fournisseur réel configuré,
aucun email envoyé, aucun raccordement Gmail/Outlook commencé.

Exactly-once externe n'est pas démontré. L'état du simulateur est un fichier local
privé ; fichier partiel après crash = refus conservateur, pas récupération automatique.
Le scénario réseau ambigu utilise HTTP 503 après mémorisation, pas une coupure
physique. L'acceptation/absence d'acceptation reste une preuve simulée. La validation
d'un fournisseur réel, de son idempotence et de ses preuves reste hors périmètre.

## Inventaire Git

10 fichiers : 4 modifiés, 6 nouveaux. Aucun secret réel, credential, certificat
privé, cookie, profil, dump, log ou artefact de test dans ce périmètre. Index vide.

```text
 M docs/integrations/shared/CrmLocalHmac.js
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs
 M src/ProspectionCrm.Api/appsettings.json
?? docs/integrations/email-send-demo/CrmEmailSend.node.js
?? docs/integrations/email-send-demo/CrmEmailSend.test.js
?? docs/integrations/email-send-demo/workflow.json
?? docs/phase-9-5-step-4-2-validation.md
?? src/ProspectionCrm.Api/Services/Email/HttpEmailSendTransport.cs
?? tests/ProspectionCrm.Api.Tests/EmailSendTransportTests.cs
```
