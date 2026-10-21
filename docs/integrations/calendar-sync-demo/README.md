# Démonstration calendar.sync locale — Phase 9.6.2

Ce workflow n'accède à aucun calendrier externe. Il authentifie une commande CRM et
renvoie des événements fictifs déterministes. Aucun OAuth ni credential fournisseur.

## Installation et activation explicites

1. Conserver TLS natif et les écoutes loopback. Le proxy HTTPS local écoute sur
   `https://localhost:7443`, relaie uniquement `/webhook/crm-calendar-sync-demo`
   vers n8n sur `127.0.0.1:5678`. Le callback CRM est
   `https://localhost:7096/api/integrations/results` ; le certificat public local
   de confiance est fourni par le credential HMAC existant `crmProbe`.
2. Copier `CrmCalendarSync.node.js` dans un dossier custom n8n et le helper
   `../shared/CrmLocalHmac.js` dans le dossier frère `shared`. Importer `workflow.json`.
   Associer manuellement le credential HMAC local existant ; ne jamais l'exporter dans Git.
3. Créer deux comptes `local-demo` et deux calendriers fictifs via les API humaines.
   Renseigner leurs IDs dans `primaryCalendarRef` et `secondaryCalendarRef` du nœud.
   Le workflow expurgé ne contient ni ces IDs locaux, ni association de credential.
4. Configurer uniquement le processus de validation : `Integrations:Enabled=true`,
   `Integrations:CalendarSync:Enabled=true`,
   `Integrations:CalendarSync:Endpoint=https://localhost:7443/webhook/crm-calendar-sync-demo`.
   Le workspace, la connexion et les deux clés distinctes restent ceux de la configuration
   locale autorisée. Les activations versionnées restent `false`.
5. Publier le workflow, puis utiliser les routes humaines enqueue, dispatch et
   process-result. Aucun GET, ACK ou callback n'importe automatiquement les événements.

## Jeux de données

| Calendrier | Curseur précédent | Résultat |
| --- | --- | --- |
| Principal | null | Trois événements, cursor-001 |
| Principal | cursor-001 | Un rejeu, une modification, une annulation, cursor-002 |
| Principal | cursor-002 | Succès vide, cursor-003 |
| Principal | cursor-003 | Conflit volontaire ; le CRM conserve cursor-003 et tous les événements |
| Secondaire | null | Une même identité externe indépendante, cursor-001 |
| Secondaire | cursor-001 | Succès vide, cursor-002 |

Dates fixes Europe/Paris : all-day du 29 mars 2026, fin exclusive au 30 mars
(23 heures réelles), et occurrences proches du retour à l'heure d'hiver du 25 octobre
avec offsets +02:00 et +01:00 explicites. Aucune RRULE ni expansion.

Le contrat fermé refuse champs inconnus/répétés, mauvais identifiants et limites invalides.
Les commandes calendar.sync sont limitées à 16 Kio et leurs résultats à 512 Kio ; les
limites probe/email.sync restent inchangées. HMAC porte sur les octets bruts, l'audience,
la cible, la clé et le timestamp. ACK 202 seulement après callback accepté et TLS validé.

## Tests et arrêt

Avec une version de Node prenant en charge `node:test` :
`node --test docs/integrations/calendar-sync-demo/CrmCalendarSync.test.js`.
Les tests utilisent un transport synthétique et des clés aléatoires éphémères.

La simulation d'un callback tardif décrite dans le rapport utilise une injection de délai
privée et temporaire après authentification, absente du workflow livré. Aucun retry.
À la fin : dépublier le workflow, restaurer ses paramètres, désactiver les intégrations
et le transport calendrier, arrêter API/Blazor/proxy/n8n et conserver PostgreSQL healthy.
Conserver les fixtures et preuves synthétiques. Ne jamais versionner dumps, journaux,
TRX, tokens, cookies, clés privées ou exports de credentials.
