# Jellyfin TorrServer

<img src="assets/logo.png" width="160" alt="Logo TorrServer">

Plugin serveur Jellyfin : parcourir les affiches TMDB sans télécharger les films, choisir un torrent dans Prowlarr, choisir un fichier dans ce torrent, puis le lire via TorrServer. Aucun changement du code source de Jellyfin.

## Installation

Dans **Tableau de bord → Extensions → Dépôts**, ajouter ce dépôt :

```
https://raw.githubusercontent.com/jonathan-pp/jellyfin-torrserver/main/manifest.json
```

Ouvrir le catalogue des extensions, installer **TorrServer**, redémarrer Jellyfin, puis ouvrir sa page de configuration. Le JSON référence des archives versionnées et leur checksum MD5 exigé par Jellyfin ; une empreinte SHA-256 est aussi fournie dans `dist/`.

Versions ciblées : **Jellyfin 10.10.7** (.NET 8, plugin 0.1.0.0) et **Jellyfin 10.11.11** (.NET 9, plugin 0.1.0.1). Les autres versions n'ont pas été validées. Le catalogue choisit la version compatible selon l'ABI annoncée.

## Configuration après installation

- Clé API **TMDB v3**, fournie par votre compte TMDB (ne pas utiliser un jeton Bearer à sa place).
- Adresse de **Prowlarr** et sa clé API ; les indexeurs de torrents doivent être configurés et accessibles dans Prowlarr.
- Adresse de **TorrServer**, avec utilisateur/mot de passe facultatifs si son authentification est activée.
- Adresse interne de Jellyfin : conserver `http://127.0.0.1:8096` sauf port personnalisé, HTTPS uniquement ou chemin de base. Le serveur Jellyfin et FFmpeg doivent pouvoir atteindre cette adresse.

Enregistrer, puis cliquer sur **Tester les connexions enregistrées**. Les adresses sont vues depuis le serveur Jellyfin : dans Docker, `localhost` pour Prowlarr/TorrServer désigne le conteneur Jellyfin ; utiliser les noms des services ou leurs adresses réseau.

## Utilisation

Ouvrir **Catalogue TMDB** parmi les chaînes de Jellyfin. L'interface utilise les écrans natifs de Jellyfin, pas les maquettes dessinées durant la conception. Ce n'est pas une bibliothèque de fichiers locale.

- Film : ouvrir le film, choisir un torrent, puis ouvrir le fichier vidéo et lancer la lecture.
- Série : choisir une saison et un épisode, puis une recherche d'épisode, de pack de saison ou de série. Choisir un torrent, puis le fichier voulu.
- Tous les fichiers vidéo du pack restent visibles : le choix manuel fonctionne même si les noms ne permettent pas de reconnaître les épisodes. Les correspondances `S02E03` ou `2x03` sont signalées lors d'une recherche d'épisode.
- Chaque résultat affiche le titre brut, l'indexeur, la taille et le nombre de sources annoncé par Prowlarr. La langue et la qualité restent celles indiquées dans ce titre ; aucune correspondance automatique n'est garantie.
- Les pages suivantes du catalogue sont des dossiers « Page suivante ». Les recherches et listes sont mises en cache environ cinq minutes. Le catalogue commence par les titres populaires ; il ne comporte pas encore de saisie libre de recherche TMDB dans les clients Jellyfin.

La simple consultation des affiches ne contacte pas TorrServer. Ouvrir un torrent récupère ses métadonnées ; la lecture démarre le transfert du fichier choisi. TorrServer télécharge et met des morceaux en cache pendant la lecture : il ne faut pas attendre le téléchargement complet, mais une source sans pairs ou trop lente peut échouer. Les torrents ajoutés sont conservés dans TorrServer ; leur suppression et la gestion du cache se font dans TorrServer.

Les choix sont conservés dans le dossier de données du plugin et survivent aux redémarrages. Le relais de lecture prend en charge les requêtes HTTP Range et évite de transmettre les identifiants TorrServer aux clients. Les liens de lecture sont temporaires et cessent de fonctionner après un redémarrage de Jellyfin ; relancer la lecture dans ce cas. Les URL de trackers et les clés restent des données sensibles : protéger les dossiers de configuration et de données de Jellyfin.

Le catalogue est classé R et respecte les restrictions d'accès aux chaînes de Jellyfin. Le champ XML avancé `AllowedUserIds` permet de limiter le catalogue à des identifiants utilisateurs (tableau vide : tous les utilisateurs autorisés par Jellyfin).

## Développement et validation

Installer les SDK .NET 8 et 9, puis :

```sh
dotnet test tests/TorrServer.Tests.csproj -c Release
python3 scripts/package.py
```

Les tests utilisent des réponses contrôlées pour TMDB, Prowlarr et TorrServer : parcours de sélection d'un pack et d'un fichier, persistance, correspondance d'épisodes, upload `.torrent`, authentification, annulation, jetons de lecture et relais Range. Ces tests ne prouvent pas la disponibilité des indexeurs ou des pairs réels.

`tests/jellyfin_smoke.py` vérifie un serveur Jellyfin **de test isolé** sur `127.0.0.1:18196` (ou `JELLYFIN_SMOKE_URL`) : initialisation du serveur, chargement du plugin, configuration et dossiers natifs du catalogue. Ne pas l'utiliser sur une installation personnelle existante. Les clés personnelles et une lecture avec vos indexeurs réels ne sont pas incluses dans les tests.
