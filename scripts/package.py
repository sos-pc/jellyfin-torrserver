#!/usr/bin/env python3
"""Package new versions; retain published archives. Bump the version to ship a changed build."""
import datetime
import hashlib
import json
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parent.parent
base = 'https://raw.githubusercontent.com/jonathan-pp/jellyfin-torrserver/main/'
dist = root / 'dist'
dist.mkdir(exist_ok=True)
versions = []
for framework, version, abi in [('net10.0', '0.1.0.2', '12.4.0.0'), ('net9.0', '0.1.0.1', '10.11.11.0'), ('net8.0', '0.1.0.0', '10.10.7.0')]:
    dll = root / 'Jellyfin.Plugin.TorrServer/bin/Release' / framework / 'Jellyfin.Plugin.TorrServer.dll'
    if not dll.is_file():
        raise SystemExit('Compile all targets with dotnet build -c Release first.')
    archive = dist / f'TorrServer-{version}.zip'
    if not archive.exists():
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
            for path in [dll, root / 'LICENSE']:
                info = zipfile.ZipInfo(path.name, (2026, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                output.writestr(info, path.read_bytes())
    data = archive.read_bytes()
    versions.append({'version': version, 'targetAbi': abi,
        'changelog': 'Catalogue TMDB, choix manuel des torrents et fichiers, lecture TorrServer et configuration.',
        'sourceUrl': base + 'dist/' + archive.name, 'checksum': hashlib.md5(data).hexdigest(),
        'timestamp': datetime.datetime.now(datetime.timezone.utc).isoformat()})
    (dist / (archive.name + '.sha256')).write_text(hashlib.sha256(data).hexdigest() + '  ' + archive.name + '\n')
    print(f'Packaged {archive.name} ({len(data)} bytes) for Jellyfin {abi}.')
manifest = [{
    'guid': '655fbf1b-525b-4f82-bdeb-f27dd0bdd606', 'name': 'TorrServer', 'owner': 'jonathan-pp',
    'category': 'Channels', 'overview': 'Catalogue TMDB et lecture de torrents avec choix de la source.',
    'description': 'Catalogue TMDB, recherche Prowlarr, choix des torrents et fichiers, lecture TorrServer. Configurer les services après installation. Cibles : Jellyfin 10.10.7, 10.11.11 et 12.4.',
    'imageUrl': base + 'assets/logo.png', 'versions': versions
}]
(root / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
