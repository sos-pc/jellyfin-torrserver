# Jellyfin TorrServer

**En développement : la lecture ne fonctionne pas encore et le catalogue n’a pas été testé dans Jellyfin.**

L'objectif est d'afficher un catalogue TMDB dans Jellyfin, de rechercher les torrents via Prowlarr et de les lire avec TorrServer. Le projet contient pour l'instant le squelette du plugin Jellyfin 10.10.7 une page de configuration, un catalogue TMDB expérimental utilisant les chaînes de Jellyfin, et des points d’accès administrateur préliminaires pour TMDB et Prowlarr. Ne l'utilisez pas comme solution de streaming fonctionnelle.

## Compilation

Avec le SDK .NET 8 :

```sh
dotnet build Jellyfin.Plugin.TorrServer/Jellyfin.Plugin.TorrServer.csproj
```

Il faudra une clé API TMDB, une instance Prowlarr avec sa clé API et une instance TorrServer accessibles par Jellyfin. Les points d’accès préliminaires n’ont pas encore été testés avec des services réels ; la présentation dans Jellyfin et la lecture restent à valider et à compléter. Les chaînes Jellyfin ne constituent pas une bibliothèque native et le catalogue se limite actuellement aux premières pages populaires.
