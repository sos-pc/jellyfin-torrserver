#!/usr/bin/env python3
"""Package the Jellyfin 12.4 build of a release: python3 scripts/package.py <version>."""
import datetime
import hashlib
import json
from pathlib import Path
import sys
import zipfile

version = sys.argv[1]
root = Path(__file__).resolve().parent.parent
repository = 'sos-pc/jellyfin-torrserver'
abi = '12.4.0.0'
dist = root / 'dist'
dist.mkdir(exist_ok=True)
dll = root / 'Jellyfin.Plugin.TorrServer/bin/Release/net10.0/Jellyfin.Plugin.TorrServer.dll'
if not dll.is_file():
    raise SystemExit('Compile with dotnet build -c Release first.')
archive = dist / f'TorrServer-{version}.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
    for path in [dll, root / 'LICENSE']:
        info = zipfile.ZipInfo(path.name, (2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        output.writestr(info, path.read_bytes())
data = archive.read_bytes()
(dist / (archive.name + '.sha256')).write_text(hashlib.sha256(data).hexdigest() + '  ' + archive.name + '\n')
print(f'Packaged {archive.name} ({len(data)} bytes) for Jellyfin {abi}.')
manifest = [{
    'guid': '655fbf1b-525b-4f82-bdeb-f27dd0bdd606', 'name': 'TorrServer', 'owner': 'sos-pc',
    'category': 'Channels', 'overview': 'Catalogue TMDB et lecture de torrents avec choix de la source.',
    'description': 'Catalogue TMDB, recherche Prowlarr sur les indexeurs publics, choix des torrents et fichiers, lecture TorrServer. Configurer les services après installation. Cible : Jellyfin 12.4.',
    'imageUrl': f'https://raw.githubusercontent.com/{repository}/main/assets/logo.png',
    'versions': [{'version': version, 'targetAbi': abi,
        'changelog': 'Catalogue TMDB, recherche limitée aux indexeurs publics, liens magnet de Prowlarr et lecture TorrServer.',
        'sourceUrl': f'https://github.com/{repository}/releases/download/v{version}/{archive.name}',
        'checksum': hashlib.md5(data).hexdigest(),
        'timestamp': datetime.datetime.now(datetime.timezone.utc).isoformat()}]
}]
(dist / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
