"""Generate a focused test of the actual mpv.net install/hash methods.

python tests/mpvnet-packaging/generate.py OUTPUT_DIRECTORY
dotnet run --project OUTPUT_DIRECTORY/Checks.csproj -- RELEASE_ZIP NEW_TEST_DIRECTORY

Only downloading and extraction are substituted. The current release pins,
InstallMpvnet and VerifySha256 are copied unchanged from the production source.
"""
from pathlib import Path
import re
import sys

source = (Path(__file__).resolve().parents[2] /
          "BuildMpvUpscale2xAnimeJaNai/Program.cs").read_text(encoding="utf-8-sig")
out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=True)

def method(signature):
    start = source.index(signature)
    end = source.index("{", start) + 1
    depth = 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]

pins = "\n".join(line for line in source.splitlines()
                 if line.startswith(("const string MpvNetVersion ", "const string MpvNetSha256 ")))
assert re.search(r'MpvNetSha256\s*=\s*"[0-9a-f]{64}";', pins), "A full SHA-256 pin is required"
cases = r'''
string fixture = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) throw new Exception("Use a new test directory");
Directory.CreateDirectory(root);
Directory.SetCurrentDirectory(root);
string installDirectory = Path.Combine(root, "extract");
int extractions = 0;
bool tamper = false;

await InstallMpvnet();
if (extractions != 1 || File.Exists("mpvnet.zip"))
    throw new Exception("Verified archive was not extracted and cleaned up");
Console.WriteLine("PASS published archive accepted");

tamper = true;
extractions = 0;
try {
    await InstallMpvnet();
    throw new Exception("Modified archive was accepted");
} catch (InvalidOperationException e) when (e.Message.StartsWith("SHA-256 mismatch")) { }
if (extractions != 0) throw new Exception("Modified archive reached extraction");
Console.WriteLine("PASS one-byte-modified archive rejected before extraction");

Task DownloadFileAsync(string url, string target, Action<double> progress) {
    File.Copy(fixture, target, true);
    if (tamper) {
        using var file = File.Open(target, FileMode.Open, FileAccess.ReadWrite);
        int first = file.ReadByte();
        file.Position = 0;
        file.WriteByte((byte)(first ^ 1));
    }
    return Task.CompletedTask;
}
void ExtractZip(string archive, string destination, Action<double> progress) {
    extractions++;
}
'''
(out / "Program.cs").write_text("using System.Security.Cryptography;\n" + pins + cases +
    method("void VerifySha256(") + "\n" + method("async Task InstallMpvnet()"), encoding="utf-8")
(out / "Checks.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings><UseAppHost>false</UseAppHost></PropertyGroup>
</Project>
''', encoding="utf-8")
print(out / "Checks.csproj")
