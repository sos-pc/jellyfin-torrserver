import urllib.request, json, time, os
base=os.environ.get('JELLYFIN_SMOKE_URL', 'http://127.0.0.1:18196')
def call(path, data=None, headers=None):
    req=urllib.request.Request(base+path, data=json.dumps(data).encode() if data is not None else None, headers={'Content-Type':'application/json', **(headers or {})})
    with urllib.request.urlopen(req, timeout=20) as r:
        body=r.read()
        return json.loads(body) if body else None
for i in range(20):
    try: info=call('/System/Info/Public'); break
    except OSError: time.sleep(1)
if not info['StartupWizardCompleted']:
    call("/Startup/User")
    call('/Startup/Configuration',{'UICulture':'fr','MetadataCountryCode':'FR','PreferredMetadataLanguage':'fr'})
    call('/Startup/User',{'Name':'plugin-smoke','Password':'local-fixture-password'})
    call('/Startup/Complete',{})
login=call('/Users/AuthenticateByName',{'Username':'plugin-smoke','Pw':'local-fixture-password'},{'Authorization':'MediaBrowser Client="Plugin tests", Device="Local", DeviceId="fixture", Version="1"'})
headers={'X-Emby-Token':login['AccessToken']}
plugins=call('/Plugins',headers=headers)
plugin=next(p for p in plugins if p['Id'].replace('-','')=='655fbf1b525b4f82bdebf27dd0bdd606')
assert plugin['Status']=='Active',plugin
channels=call('/Channels',headers=headers)
channel=next(x for x in channels['Items'] if x['Name']=='Catalogue TMDB')
items=call('/Channels/'+channel['Id']+'/Items',headers=headers)
assert len(items['Items'])==2,items
assert {x['Name'] for x in items['Items']}=={'Films populaires','Séries populaires'}
config=call('/Plugins/655fbf1b-525b-4f82-bdeb-f27dd0bdd606/Configuration',headers=headers)
assert config['JellyfinInternalUrl']=='http://127.0.0.1:8096'
try: call('/TorrServerPlugin/play/unknown')
except urllib.error.HTTPError as e: assert e.code==404,e.code
print('PASS: plugin active, configuration loaded, native catalogue folders present, unknown playback ticket denied')
