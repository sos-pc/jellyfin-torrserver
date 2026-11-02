# Jellyfin TorrServer

**En développement : ce plugin ne fournit pas encore de catalogue ni de lecture.**

L'objectif est d'afficher un catalogue TMDB dans Jellyfin, de rechercher les torrents via Prowlarr et de les lire avec TorrServer. Le projet contient pour l'instant le squelette du plugin Jellyfin 10.10.7 une page de configuration pour les adresses et clés API, et des points d’accès administrateur préliminaires pour TMDB et Prowlarr. Ne l'utilisez pas comme solution de streaming fonctionnelle.

## Compilation

Avec le SDK .NET 8 :

```sh
dotnet build Jellyfin.Plugin.TorrServer/Jellyfin.Plugin.TorrServer.csproj
```

Il faudra une clé API TMDB, une instance Prowlarr avec sa clé API et une instance TorrServer accessibles par Jellyfin. Les points d’accès préliminaires n’ont pas encore été testés avec des services réels ; la création de la bibliothèque et la lecture restent à implémenter.
