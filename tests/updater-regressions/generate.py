"""Generate an offline .NET test project using current updater methods unchanged.

Usage: python tests/updater-regressions/generate.py OUTPUT_DIRECTORY
Then: dotnet run --project OUTPUT_DIRECTORY/Checks.csproj
The HTTP-client factory is substituted; filesystem operations are real.
"""
from pathlib import Path
import re
import sys

here=Path(__file__).resolve().parent
source=(here.parents[1]/'AnimeJaNaiUpdater/Program.cs').read_text(encoding='utf-8-sig')
out=Path(sys.argv[1]).resolve(); out.mkdir(parents=True,exist_ok=True)
def method(signature):
    start=source.index(signature); end=source.index('{',start)+1; depth=1
    while depth:
        depth+=(source[end]=='{')-(source[end]=='}'); end+=1
    return source[start:end]
methods=[method(s) for s in ('void SyncInputConf()', 'string ReadLocalVersion()',
    'static string ReadManifestString(', 'string? PackVersionMismatch(',
    'Task<Release> GetInstalledReleaseAsync()', 'async Task<Release> GetReleaseAsync(string url)',
    'async Task<PackIndex> GetReleasePackIndexAsync()', 'static void EnsureReplaceable(',
    'static bool IsPreserved(', 'static void TryDelete(', 'static HashSet<string> ReadUserPreserve(',
    'static bool IsSetupInstall(', 'static bool SamePath(', 'static StoreRef? ParseOwnRef(',
    'static StoreLocation? ParseLocation(')]
for one_line in ('string ReadComponentVersion() =>', 'static string NormalizeRel('):
    start=source.index(one_line)
    methods.append(source[start:source.index(';',start)+1])
latest=next(s for s in source.splitlines() if s.startswith('Task<Release> GetLatestReleaseAsync()'))
repo=next(s for s in source.splitlines() if s.startswith('const string Repo ='))
# the assembler's user_preserve list, as the package's manifest.json carries it
builder=(here.parents[1]/'BuildMpvUpscale2xAnimeJaNai/Program.cs').read_text(encoding='utf-8-sig')
block=builder[builder.index('user_preserve = new[]'):]
block=block[:block.index('},')]
preserve=re.findall(r'^\s*"([^"]+)",', block, re.M)
marker=next(s for s in source.splitlines() if s.startswith('const string SetupMarker ='))
wrapper='''using System.Text.Json;
using System.Text.RegularExpressions;
class Production
{
    MARKER
    private readonly string installDir;
    private string localManifest => Path.Combine(installDir, "manifest.json");
    private readonly HttpMessageHandler handler;
    private readonly bool isWinRid;
    private readonly string platformRid;
    REPO
    private readonly string apiLatest = $"https://api.github.com/repos/{Repo}/releases/latest";
    public Production(string root, HttpMessageHandler handler, bool windows = true)
    { installDir=root; this.handler=handler; isWinRid=windows; platformRid=windows ? "win-x64" : "linux-x64"; }
    HttpClient NewClient() => new HttpClient(handler, disposeHandler:false);
    public void Sync() => SyncInputConf();
    public Task<PackIndex> Packs() => GetReleasePackIndexAsync();
    public Task<Release> Latest() => GetLatestReleaseAsync();
    public string? Mismatch(PackIndex index) => PackVersionMismatch(index);
    public static void Preflight(string source, string target, HashSet<string> preserve, string ownName) =>
        EnsureReplaceable(source, target, preserve, ownName);
    public static bool SetupInstall(string dir) => IsSetupInstall(dir);
    public static StoreRef? OwnRef(string json, string appPath) => ParseOwnRef(json, appPath);
    public static StoreLocation? Location(string json) => ParseLocation(json);
    public static bool Preserved(string rel, HashSet<string> preserve) => IsPreserved(rel, preserve);
    public static HashSet<string> UserPreserve(string manifest) => ReadUserPreserve(manifest);
    public static readonly string[] BuilderUserPreserve = [PRESERVE];
'''.replace('REPO',repo).replace('MARKER',marker).replace('PRESERVE',', '.join('"%s"' % v for v in preserve))
(out/'Production.cs').write_text(wrapper+'\n'.join(methods)+'\n'+latest+'\n}\n'+
    source[source.index('record Release('):],encoding='utf-8')
(out/'Program.cs').write_bytes((here/'Cases.cs').read_bytes())
(out/'Checks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UseAppHost>false</UseAppHost>
  </PropertyGroup>
</Project>
''',encoding='utf-8')
print(out/'Checks.csproj')
