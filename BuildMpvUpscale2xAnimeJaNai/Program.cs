// Package builder for the native-filter mpv-upscale-2x_animejanai.
//
// The package has no VapourSynth, Python, or vs-mlrt plugins: upscaling and
// RIFE run inside the mpv fork's vf_animejanai filter, which loads the aji
// inference shim (github.com/the-database/animejanai-inference). Everything
// NVIDIA lives in one self-contained animejanai/inference/ directory (the
// filter resolves the shim's dependencies from its own directory).
//
// Target-parameterized: `--target win-x64|linux-x64` (default = host RID).
// All platform-varying names/sources live in the Platform descriptor below;
// the install steps read from it instead of hardcoding Windows assumptions.
// On Linux the DirectML/ONNX-Runtime backend and mpv.net are dropped (mpv.net
// does not run on Linux; DirectML is Windows-only); the player becomes
// upstream mpv/libmpv driven by portable_config via --config-dir.
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using SevenZipExtractor;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static Downloader;

// Third-party component versions. Bump these together when cutting a release.
// The inference runtime (TensorRT + trtexec) is reused from the vs-mlrt cuda
// release archives on Windows; publicly downloadable, license-precedented, and
// trtexec is version-matched to nvinfer by construction. aji_trt must be built
// against the SAME TensorRT major.minor (v16.x == TensorRT 11.0).
// NOTE: v16.test1 is vs-mlrt's TRT 11 PRE-release - recheck for a stable
// v16 tag before cutting the package release.
const string VsMlrtCudaVersion    = "v16.test1";
const string AjiVersion           = "v0.6.0";       // github.com/the-database/animejanai-inference release tag (DML 4:4:4 input; op21 SD preset; missing-model passthrough; configurable RIFE/upscale order; benchmark slots 1012/1013)

const string SevenZipVersion      = "2501";         // 7-zip "extra" (Windows) / linux-x64 standalone console version
const string MpvNetVersion        = "v7.1.2.0";
const string ManagerVersion       = "0.4.0";        // github.com/the-database/AnimeJaNaiManager release tag (AnimeJaNai Manager)

// DirectML backend runtime (backend=DirectML in animejanai.conf). These are
// the last DirectML-flavored releases: Microsoft moved DML to sustained
// engineering, so 1.24.x is the ORT ceiling until the WinML migration.
// Windows-only; not installed on Linux.
const string OrtDmlVersion        = "1.24.4";       // Microsoft.ML.OnnxRuntime.DirectML (NuGet)
const string DirectMLVersion      = "1.15.4";       // Microsoft.AI.DirectML (NuGet)
const string RifeModelsVersion    = "models-rife-fp16-1"; // animejanai-inference release tag (fp16 conversions)

// Custom libmpv fork build. On Windows from the github.com/the-database/
// mpv-winbuild release; on Linux from a github.com/the-database/mpv release
// (mpv + libmpv.so.2 carrying vf_animejanai, built in the old-baseline
// container). The vf_animejanai filter lives inside this libmpv build; rebuild
// + bump alongside AjiVersion when the filter changes.
//
// IMPORTANT: the filter now lives on the mpv fork's `master` branch (aji ABI
// v7); the old standalone `vf-animejanai` branch is stale (ABI v4) and must
// NOT be used. The pin below is a master commit.
const string MpvForkVersion       = "2026-06-18-9bb5fe9680"; // release tag (Windows winbuild)
const string MpvForkBuildDate     = "20260618";     // build date in the Windows dev archive filename
const string MpvForkGitHash       = "9bb5fe9680";   // git short hash (master; aji ABI v7)
// Linux mpv bundle: a github.com/the-database/mpv release asset (tar.zst).
// Overridable via MPV_LINUX_LOCAL (a local meson build dir, e.g. ~/src/mpv/build).
const string MpvForkLinuxVersion  = "2026-06-18-9bb5fe9680";

// ---------------------------------------------------------------------------
// Target / platform descriptor
// ---------------------------------------------------------------------------
TargetOs SelectTarget(string[] a)
{
    int ti = Array.IndexOf(a, "--target");
    string rid = (ti >= 0 && ti + 1 < a.Length) ? a[ti + 1]
               : (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64");
    return rid switch
    {
        "win-x64" => TargetOs.Windows,
        "linux-x64" => TargetOs.Linux,
        _ => throw new ArgumentException($"Unsupported --target '{rid}' (use win-x64 or linux-x64)"),
    };
}

var target = SelectTarget(args);
var plat = target == TargetOs.Windows ? Platform.Win : Platform.Linux;
Console.WriteLine($"Target: {plat.Rid}");

if (args.Length < 1 || args[0].StartsWith("--"))
{
    throw new ArgumentException("Version is required (first positional arg).");
}

var assemblyDirectory = AppContext.BaseDirectory;
var animejanaiDirectory = Path.Combine(assemblyDirectory, "mpv-upscale-2x_animejanai");
var installDirectory = Path.Combine(assemblyDirectory, $"mpv-upscale-2x_animejanai-v{args[0]}");

// --packs-only [dir]: emit component packs from an already-built install tree
// (default: the version-derived directory above) and exit, skipping the build.
int packsOnlyIndex = Array.IndexOf(args, "--packs-only");
if (packsOnlyIndex >= 0 && packsOnlyIndex + 1 < args.Length &&
    Directory.Exists(args[packsOnlyIndex + 1]))
{
    installDirectory = Path.GetFullPath(args[packsOnlyIndex + 1]);
}

var inferencePath = Path.Combine(installDirectory, "animejanai", "inference");
var onnxPath = Path.Combine(installDirectory, "animejanai", "onnx");
var rifePath = Path.Combine(installDirectory, "animejanai", "rife");

// Standalone 7-Zip console: used here to extract the multi-part vs-mlrt archive
// (Windows), and shipped at the install root for the updater (manifest
// archive_tool). 7za.exe on Windows, 7zz on Linux.
async Task InstallSevenZip()
{
    Console.WriteLine("Downloading 7-Zip standalone console...");
    if (plat.IsWindows)
    {
        var downloadUrl = $"https://www.7-zip.org/a/7z{SevenZipVersion}-extra.7z";
        var targetPath = Path.GetFullPath("7z-extra.7z");
        await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
        {
            Console.WriteLine($"Downloading 7-Zip ({progress}%)...");
        });

        var targetExtractPath = Path.GetFullPath("7z-extra-temp");
        Directory.CreateDirectory(targetExtractPath);
        using (ArchiveFile archiveFile = new(targetPath))
        {
            archiveFile.Extract(targetExtractPath);
        }
        File.Copy(Path.Combine(targetExtractPath, "x64", "7za.exe"),
                  Path.Combine(installDirectory, plat.ArchiveTool), true);
        Directory.Delete(targetExtractPath, true);
        File.Delete(targetPath);
    }
    else
    {
        // 7-Zip's linux-x64 console ships as tar.xz; extract with system tar
        // (universal on Linux) to avoid a chicken-and-egg archive dependency.
        var downloadUrl = $"https://www.7-zip.org/a/7z{SevenZipVersion}-linux-x64.tar.xz";
        var targetPath = Path.GetFullPath("7z-linux.tar.xz");
        await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
        {
            Console.WriteLine($"Downloading 7-Zip ({progress}%)...");
        });
        var tmp = Path.GetFullPath("7z-linux-temp");
        Directory.CreateDirectory(tmp);
        await RunProcess("tar", $"-xf \"{targetPath}\" -C \"{tmp}\"");
        var dst = Path.Combine(installDirectory, plat.ArchiveTool);
        File.Copy(Path.Combine(tmp, "7zz"), dst, true);
        await RunProcess("chmod", $"+x \"{dst}\"");
        Directory.Delete(tmp, true);
        File.Delete(targetPath);
    }
}

async Task InstallInferenceRuntime()
{
    Directory.CreateDirectory(inferencePath);
    if (plat.IsWindows)
    {
        Console.WriteLine("Downloading TensorRT runtime (from the vs-mlrt cuda release)...");
        var baseDownloadUrl = $"https://github.com/AmusementClub/vs-mlrt/releases/download/{VsMlrtCudaVersion}/";
        var fileNames = new[]
        {
            $"vsmlrt-windows-x64-cuda.{VsMlrtCudaVersion}.7z.001",
            $"vsmlrt-windows-x64-cuda.{VsMlrtCudaVersion}.7z.002",
        };
        var targetPaths = fileNames.Select(f => Path.GetFullPath(f)).ToArray();

        double lastProgress = -1;
        int updateThreshold = 5;

        for (int i = 0; i < fileNames.Length; i++)
        {
            string downloadUrl = baseDownloadUrl + fileNames[i];
            string targetPath = targetPaths[i];

            await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
            {
                if (progress >= lastProgress + updateThreshold)
                {
                    Console.WriteLine($"Downloading {fileNames[i]} ({progress}%)...");
                    lastProgress = progress;
                }
            });
        }

        Console.WriteLine("Extracting TensorRT runtime (this may take several minutes)...");
        var tempDirectory = Path.GetFullPath("vsmlrt-temp");
        Directory.CreateDirectory(tempDirectory);

        // Only vsmlrt-cuda/ is needed (a flat directory); extracting just that
        // subtree also skips the plugin DLLs (vstrt/vsort/...) entirely.
        await RunProcess(Path.Combine(installDirectory, plat.ArchiveTool),
                         $"x \"{targetPaths[0]}\" -o\"{tempDirectory}\" \"vsmlrt-cuda\\*\" -r- -y");

        var cudaDirectory = Path.Combine(tempDirectory, "vsmlrt-cuda");
        foreach (var file in Directory.GetFiles(cudaDirectory))
        {
            var name = Path.GetFileName(file);
            bool keep = plat.InferenceRuntimeFiles.Contains(name) ||
                        plat.InferenceRuntimePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                        name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase);
            if (keep)
            {
                File.Copy(file, Path.Combine(inferencePath, name), true);
            }
        }

        Directory.Delete(tempDirectory, true);
        foreach (var targetPath in targetPaths)
        {
            File.Delete(targetPath);
        }
    }
    else
    {
        // Linux: vs-mlrt publishes no Linux build, so the TensorRT runtime is
        // taken from an NVIDIA TensorRT install (the apt packages' userland
        // layout). Copy the .so set + trtexec from TRT_LINUX_ROOT (default
        // /usr) and cudart from CUDA_LINUX_LIB (default the cuda toolkit lib).
        // The per-SM builder resources (libnvinfer_builder_resource_sm*.so) feed
        // the same per-GPU component-pack split as on Windows.
        var trtRoot = Environment.GetEnvironmentVariable("TRT_LINUX_ROOT") ?? "/usr";
        var trtLib = Path.Combine(trtRoot, "lib", "x86_64-linux-gnu");
        var trtBin = Path.Combine(trtRoot, "bin");
        var cudaLib = Environment.GetEnvironmentVariable("CUDA_LINUX_LIB")
                      ?? "/usr/local/cuda-13.2/lib64";
        Console.WriteLine($"Copying TensorRT runtime from {trtLib} + cudart from {cudaLib}...");

        // Versioned runtime libraries (and the unversioned/.11 symlinks) the
        // filter + trtexec dlopen. Copy every matching file (resolving symlinks
        // to real files at the destination).
        var libGlobs = new[]
        {
            "libnvinfer.so*", "libnvinfer_plugin.so*", "libnvonnxparser.so*",
            "libnvinfer_builder_resource_*.so*",
        };
        foreach (var g in libGlobs)
            foreach (var f in Directory.GetFiles(trtLib, g))
                CopyResolvingSymlink(f, Path.Combine(inferencePath, Path.GetFileName(f)));
        foreach (var f in Directory.GetFiles(cudaLib, "libcudart.so*"))
            CopyResolvingSymlink(f, Path.Combine(inferencePath, Path.GetFileName(f)));

        var trtexecSrc = Path.Combine(trtBin, "trtexec");
        var trtexecDst = Path.Combine(inferencePath, "trtexec");
        CopyResolvingSymlink(trtexecSrc, trtexecDst);
        await RunProcess("chmod", $"+x \"{trtexecDst}\"");
    }
}

async Task InstallAji()
{
    Directory.CreateDirectory(inferencePath);

    // Dev override: AJI_LOCAL_BUILD points at a local CMake build dir
    // (libaji.so + libaji_trt.so + tools), or AJI_LOCAL_ZIP at a built archive.
    var localBuild = Environment.GetEnvironmentVariable("AJI_LOCAL_BUILD");
    if (!plat.IsWindows && !string.IsNullOrEmpty(localBuild))
    {
        Console.WriteLine($"Using local aji build dir: {localBuild}");
        foreach (var name in plat.AjiLibs.Concat(plat.AjiTools))
        {
            var src = Path.Combine(localBuild, name);
            if (File.Exists(src))
            {
                var dst = Path.Combine(inferencePath, name);
                File.Copy(src, dst, true);
                await RunProcess("chmod", $"+x \"{dst}\"");
            }
            else
            {
                Console.WriteLine($"  (aji artifact missing, skipped: {name})");
            }
        }
        return;
    }

    var localZip = Environment.GetEnvironmentVariable("AJI_LOCAL_ZIP");
    string targetPath;
    bool temp = false;
    if (!string.IsNullOrEmpty(localZip))
    {
        Console.WriteLine($"Using local aji build: {localZip}");
        targetPath = localZip;
    }
    else
    {
        Console.WriteLine("Downloading aji (native inference shim)...");
        var downloadUrl = $"https://github.com/the-database/animejanai-inference/releases/download/{AjiVersion}/{plat.AjiAsset}";
        targetPath = Path.GetFullPath(plat.AjiAsset);
        temp = true;
        await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
        {
            Console.WriteLine($"Downloading aji ({progress}%)...");
        });
    }

    if (plat.IsWindows)
        ExtractZip(targetPath, inferencePath, (double progress) => { });
    else
        await RunProcess("tar", $"--zstd -xf \"{targetPath}\" -C \"{inferencePath}\"");

    if (temp)
    {
        File.Delete(targetPath);
    }
}

// ONNX Runtime + DirectML for the DirectML backend (Windows only). The .nupkg
// files are plain zips; only the x64 runtime DLLs (and the DirectML license,
// which the redistribution terms require keeping intact) go into the package.
async Task InstallOrtDml()
{
    Directory.CreateDirectory(inferencePath);
    var packages = new (string Name, string Version, string[] CopyFromTo)[]
    {
        ("Microsoft.ML.OnnxRuntime.DirectML", OrtDmlVersion, new[]
        {
            "runtimes/win-x64/native/onnxruntime.dll", "onnxruntime.dll",
        }),
        ("Microsoft.AI.DirectML", DirectMLVersion, new[]
        {
            "bin/x64-win/DirectML.dll", "DirectML.dll",
            "LICENSE.txt", "DirectML_LICENSE.txt",
        }),
    };
    foreach (var (name, version, copies) in packages)
    {
        Console.WriteLine($"Downloading {name} {version}...");
        var downloadUrl = $"https://www.nuget.org/api/v2/package/{name}/{version}";
        var targetPath = Path.GetFullPath($"{name}.{version}.nupkg");
        await DownloadFileAsync(downloadUrl, targetPath, _ => { });

        var tempDirectory = Path.GetFullPath($"{name}-temp");
        ExtractZip(targetPath, tempDirectory, _ => { });
        for (var i = 0; i < copies.Length; i += 2)
        {
            var src = Path.Combine(tempDirectory,
                                   copies[i].Replace('/', Path.DirectorySeparatorChar));
            File.Copy(src, Path.Combine(inferencePath, copies[i + 1]), true);
        }
        Directory.Delete(tempDirectory, true);
        File.Delete(targetPath);
    }
}

async Task InstallRife()
{
    // fp16 conversions of vs-mlrt's rife v1 (video_player) models — one
    // model set for both backends (TensorRT 11's strong typing requires
    // fp16 onnx). Platform-agnostic models; lives outside onnx/ so the
    // heavy, deps-versioned models stay out of the overlay archive.
    Console.WriteLine("Downloading RIFE fp16 models...");
    var downloadUrl = "https://github.com/the-database/animejanai-inference/" +
                      $"releases/download/{RifeModelsVersion}/rife-fp16-1.7z";
    var targetPath = Path.GetFullPath("rife-fp16.7z");
    await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading RIFE fp16 models ({progress}%)...");
    });

    Directory.CreateDirectory(rifePath);
    // SevenZipExtractor wraps the Windows 7z.dll and is not cross-platform;
    // on Linux extract the .7z with the bundled 7zz console instead.
    if (plat.IsWindows)
    {
        using ArchiveFile archiveFile = new(targetPath);
        archiveFile.Extract(rifePath);
    }
    else
    {
        await RunProcess(Path.Combine(installDirectory, plat.ArchiveTool),
                         $"x \"{targetPath}\" -o\"{rifePath}\" -y");
    }
    File.Delete(targetPath);
}

// Windows player: mpv.net portable + the custom libmpv-2.dll fork.
async Task InstallMpvnet()
{
    var downloadUrl = $"https://github.com/mpvnet-player/mpv.net/releases/download/{MpvNetVersion}/mpv.net-{MpvNetVersion}-portable-x64.zip";
    var targetPath = Path.GetFullPath("mpvnet.zip");
    await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading mpv.net ({progress}%)...");
    });

    Console.WriteLine("Extracting mpv.net...");
    ExtractZip(targetPath, installDirectory, (double progress) =>
    {
        Console.WriteLine($"Extracting mpv.net ({progress}%)...");
    });

    File.Delete(targetPath);
}

async Task InstallCustomLibmpv()
{
    Console.WriteLine("Downloading custom libmpv fork...");
    var downloadUrl = $"https://github.com/the-database/mpv-winbuild/releases/download/{MpvForkVersion}/mpv-dev-x86_64-{MpvForkBuildDate}-git-{MpvForkGitHash}.7z";
    var targetPath = Path.GetFullPath("mpv-dev.7z");
    await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading custom libmpv fork ({progress}%)...");
    });

    Console.WriteLine("Extracting custom libmpv fork...");
    var targetExtractPath = Path.Combine(installDirectory, "temp-libmpv");
    Directory.CreateDirectory(targetExtractPath);

    using (ArchiveFile archiveFile = new(targetPath))
    {
        archiveFile.Extract(targetExtractPath);

        File.Copy(
            Path.Combine(targetExtractPath, "libmpv-2.dll"),
            Path.Combine(installDirectory, "libmpv-2.dll"),
            true // overwrite the stock mpv.net libmpv-2.dll
        );
    }
    Directory.Delete(targetExtractPath, true);
    File.Delete(targetPath);
}

// Linux player: the mpv fork bundle (mpv + libmpv.so.2 carrying vf_animejanai,
// plus the .so deps mpv needs). From a github.com/the-database/mpv release
// (tar.zst) or a local meson build dir via MPV_LINUX_LOCAL. The portable_config
// is driven via --config-dir by the launcher (Workstream C / packaging).
async Task InstallLinuxMpv()
{
    var localBuild = Environment.GetEnvironmentVariable("MPV_LINUX_LOCAL");
    var mpvRoot = Path.Combine(installDirectory, "mpv");
    Directory.CreateDirectory(mpvRoot);
    if (!string.IsNullOrEmpty(localBuild))
    {
        Console.WriteLine($"Using local mpv build dir: {localBuild}");
        // The mpv binary + the libmpv shared objects it produced.
        var mpvBin = Path.Combine(localBuild, "mpv");
        if (!File.Exists(mpvBin))
            throw new FileNotFoundException($"mpv binary not found at {mpvBin}");
        File.Copy(mpvBin, Path.Combine(mpvRoot, "mpv"), true);
        await RunProcess("chmod", $"+x \"{Path.Combine(mpvRoot, "mpv")}\"");
        foreach (var so in Directory.GetFiles(localBuild, "libmpv.so*"))
            CopyResolvingSymlink(so, Path.Combine(mpvRoot, Path.GetFileName(so)));
        // Bundle non-system deps that won't be present on a clean host. Full
        // ldd-based dependency bundling is the packaging step (Workstream F /
        // linuxdeploy); here we carry libplacebo, which is newer than any
        // distro package. Overridable via MPV_LINUX_EXTRA_LIBS (':'-separated).
        var extra = Environment.GetEnvironmentVariable("MPV_LINUX_EXTRA_LIBS");
        if (!string.IsNullOrEmpty(extra))
            foreach (var dir in extra.Split(':', StringSplitOptions.RemoveEmptyEntries))
                foreach (var so in Directory.GetFiles(dir, "libplacebo.so*"))
                    CopyResolvingSymlink(so, Path.Combine(mpvRoot, Path.GetFileName(so)));
    }
    else
    {
        Console.WriteLine("Downloading Linux mpv fork bundle...");
        var asset = $"mpv-linux-x64-{MpvForkLinuxVersion}.tar.zst";
        var downloadUrl = $"https://github.com/the-database/mpv/releases/download/{MpvForkLinuxVersion}/{asset}";
        var targetPath = Path.GetFullPath(asset);
        await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
        {
            Console.WriteLine($"Downloading Linux mpv ({progress}%)...");
        });
        await RunProcess("tar", $"--zstd -xf \"{targetPath}\" -C \"{mpvRoot}\"");
        File.Delete(targetPath);
    }
}

async Task InstallYtDlp()
{
    var downloadUrl = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{plat.YtDlpName}";
    var targetPath = Path.Combine(installDirectory, plat.YtDlpName);
    await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading {plat.YtDlpName}... ({progress})%");
    });
    if (!plat.IsWindows)
        await RunProcess("chmod", $"+x \"{targetPath}\"");
}

void InstallAnimeJaNaiCore()
{
    CopyDirectory(animejanaiDirectory, installDirectory);
}

// mpv's input.conf has no `include` (unlike mpv.conf) AND mpv.net builds its
// right-click menu by parsing input.conf's #menu: annotations - so the managed
// AnimeJaNai keybindings must physically live in input.conf. To still let users
// own input.conf (edit it, keep changes across updates) it is generated as a
// regenerable managed block (sourced from input-animejanai.conf, refreshed by
// the updater) followed by the user's own section. Bindings below the END
// marker override the managed ones above (mpv applies later bindings last).
void GenerateInputConf()
{
    var pc = Path.Combine(installDirectory, "portable_config");
    var managed = File.ReadAllText(Path.Combine(pc, "input-animejanai.conf"))
        .Replace("\r\n", "\n").TrimEnd('\n');
    // Markers must match AnimeJaNaiUpdater's regenerator exactly.
    var conf =
        "# Your keybindings. Safe to edit - updates never overwrite your section.\n" +
        "#\n" +
        "# The block between the markers below is managed by AnimeJaNai and refreshed\n" +
        "# on every update - do not edit inside it (changes there are replaced). Add\n" +
        "# your own keybindings UNDER the END marker; they survive updates and override\n" +
        "# the defaults above (mpv applies later bindings last). Syntax: one\n" +
        "# \"KEY  command\" per line, same as the managed block.\n" +
        "\n" +
        "#@ANIMEJANAI-MANAGED-BEGIN (do not edit this line or the block below)\n" +
        managed + "\n" +
        "#@ANIMEJANAI-MANAGED-END (add your keybindings below this line)\n" +
        "\n" +
        "# ===== Your keybindings below =====\n";
    File.WriteAllText(Path.Combine(pc, "input.conf"), conf);
}

// Keep the runtime configs a single source: on non-Windows targets, rewrite
// the handful of platform-specific literals in the laid-down portable_config
// (the vf=@aji line's library/tool filenames, the OSD font, the Manager
// launcher path) rather than forking the whole files. Runs before
// GenerateInputConf so the managed input.conf block inherits the ported line.
// hwdec=nvdec / vo=gpu-next / gpu-api=vulkan already work on Linux as-is.
void PortConfigsForTarget()
{
    if (plat.IsWindows) return;
    var pc = Path.Combine(installDirectory, "portable_config");

    var mpvConf = Path.Combine(pc, "mpv-animejanai.conf");
    var c = File.ReadAllText(mpvConf)
        .Replace("inference/aji.dll", "inference/libaji.so")
        .Replace("inference/trtexec.exe", "inference/trtexec")
        .Replace("'Segoe UI'", "'sans-serif'");
    File.WriteAllText(mpvConf, c);

    var inputConf = Path.Combine(pc, "input-animejanai.conf");
    var ic = File.ReadAllText(inputConf)
        .Replace("~~\\\\..\\\\AnimeJaNaiManager.exe", "~~/../AnimeJaNaiManager");
    File.WriteAllText(inputConf, ic);
}

// Linux launcher: runs the bundled mpv against portable_config via
// --config-dir, with the bundled shared objects (libplacebo next to mpv; the
// TensorRT/cudart runtime next to aji) on the loader path so the tree is
// self-contained. The .desktop entry / install.sh symlink (Workstream F) point
// here. On Windows mpv.net is the launcher, so this is Linux-only.
void WriteLinuxLauncher()
{
    if (plat.IsWindows) return;
    var path = Path.Combine(installDirectory, "mpv-animejanai");
    var sh =
        "#!/bin/sh\n" +
        "# AnimeJaNai launcher - runs the bundled mpv fork against the\n" +
        "# portable_config tree with the bundled libraries on the loader path.\n" +
        "HERE=\"$(dirname \"$(readlink -f \"$0\")\")\"\n" +
        "export LD_LIBRARY_PATH=\"$HERE/mpv:$HERE/animejanai/inference:${LD_LIBRARY_PATH}\"\n" +
        "exec \"$HERE/mpv/mpv\" --config-dir=\"$HERE/portable_config\" \"$@\"\n";
    File.WriteAllText(path, sh);
    File.SetUnixFileMode(path,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
}

async Task InstallAnimeJaNaiManager()
{
    var localBuild = Environment.GetEnvironmentVariable("MANAGER_LOCAL");
    if (!string.IsNullOrEmpty(localBuild))
    {
        Console.WriteLine($"Using local AnimeJaNai Manager: {localBuild}");
        if (Directory.Exists(localBuild))
            CopyDirectory(localBuild, installDirectory);
        else if (plat.IsWindows)
            ExtractZip(localBuild, installDirectory, _ => { });
        else
            await RunProcess("tar", $"--zstd -xf \"{localBuild}\" -C \"{installDirectory}\"");
        return;
    }

    Console.WriteLine("Downloading AnimeJaNai Manager...");
    var downloadUrl = $"https://github.com/the-database/AnimeJaNaiManager/releases/download/{ManagerVersion}/{plat.ManagerAsset}";
    var targetPath = Path.GetFullPath(plat.ManagerAsset);
    try
    {
        await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
        {
            Console.WriteLine($"Downloading AnimeJaNai Manager ({progress}%)...");
        });
    }
    catch (Exception e) when (!plat.IsWindows)
    {
        // The linux-x64 Manager release (A4) may not be published yet; the tree
        // is still runnable without it (Ctrl+E editor is the only thing missing).
        Console.WriteLine($"  WARNING: Linux Manager not available ({e.Message}); skipping.");
        return;
    }

    Console.WriteLine("Extracting AnimeJaNai Manager...");
    if (plat.IsWindows)
        ExtractZip(targetPath, installDirectory, (double progress) =>
        {
            Console.WriteLine($"Extracting AnimeJaNai Manager ({progress}%)...");
        });
    else
        await RunProcess("tar", $"--zstd -xf \"{targetPath}\" -C \"{installDirectory}\"");

    File.Delete(targetPath);
}

// The TensorRT SLA requires this attribution when redistributing the
// runtime; keep it next to the redistributed files.
void WriteThirdPartyNotices()
{
    var trtFiles = plat.IsWindows
        ? "nvinfer_11.dll, nvinfer_plugin_11.dll, nvonnxparser_11.dll, " +
          "nvinfer_builder_resource_*.dll, trtexec.exe) and NVIDIA CUDA runtime " +
          "(cudart64_*.dll)"
        : "libnvinfer.so.11, libnvinfer_plugin.so.11, libnvonnxparser.so.11, " +
          "libnvinfer_builder_resource_*.so, trtexec) and NVIDIA CUDA runtime " +
          "(libcudart.so.13)";
    var trtSource = plat.IsWindows
        ? "These files are obtained from the vs-mlrt project's release archives\n" +
          "        (https://github.com/AmusementClub/vs-mlrt), which redistributes them\n" +
          "        under the same terms."
        : "These files are obtained from NVIDIA's TensorRT and CUDA Linux packages,\n" +
          "        redistributed under the same terms.";
    var ajiNames = plat.IsWindows
        ? "aji.dll / aji_trt.dll / aji_dml.dll / aji_harness.exe / aji_kernel_test.exe"
        : "libaji.so / libaji_trt.so / aji_harness / aji_kernel_test";
    var dmlBlock = plat.IsWindows
        ? "\n        ONNX Runtime (onnxruntime.dll), (c) Microsoft Corporation,\n" +
          "        redistributed under the MIT license\n" +
          "        (https://github.com/microsoft/onnxruntime/blob/main/LICENSE).\n\n" +
          "        DirectML (DirectML.dll), (c) Microsoft Corporation, redistributed\n" +
          "        as the DirectML Redistributable Package under the Microsoft\n" +
          "        Software License Terms shipped alongside it as\n" +
          "        DirectML_LICENSE.txt (use on Windows and Xbox only).\n"
        : "";
    var notice =
        "Third-party components in this directory\n" +
        "========================================\n\n" +
        $"NVIDIA TensorRT runtime ({trtFiles},\n" +
        "        redistributed under the NVIDIA TensorRT Software License Agreement\n" +
        "        and CUDA Toolkit EULA:\n\n" +
        "            This software contains source code provided by NVIDIA Corporation.\n\n" +
        $"        {trtSource}\n" +
        dmlBlock + "\n" +
        $"        {ajiNames}:\n" +
        "        https://github.com/the-database/animejanai-inference\n";
    File.WriteAllText(Path.Combine(inferencePath, "THIRD_PARTY_NOTICES.txt"), notice);
}

// Writes version.txt + manifest.json into the install root. The updater (AnimeJaNaiUpdater) reads
// these to know the installed version, decide overlay-vs-full updates (by comparing deps), and know
// which paths to overwrite (overlay_paths) vs preserve (user_preserve). deploy.yml reads
// overlay_paths from manifest.json to build the lightweight overlay archive.
void WriteVersionAndManifest()
{
    var version = args[0];
    File.WriteAllText(Path.Combine(installDirectory, "version.txt"), version);

    // Platform-specific managed program files for the overlay update. aji and
    // its tools are small and update often, so they ride the overlay; the
    // TensorRT runtime files in the same directory are deps-versioned and only
    // change on full updates. Names come from the Platform descriptor.
    var overlayPaths = new List<string>
    {
        "version.txt",
        "manifest.json",
        plat.Exe("AnimeJaNaiUpdater"),
        "animejanai/onnx",
        "animejanai/benchmarks",
    };
    overlayPaths.AddRange(plat.AjiLibs.Select(n => "animejanai/inference/" + n));
    overlayPaths.AddRange(plat.AjiTools.Select(n => "animejanai/inference/" + n));
    overlayPaths.AddRange(plat.ManagerOverlay);
    overlayPaths.Add("portable_config/scripts");
    overlayPaths.Add("portable_config/shaders");
    // Managed defaults files, overwritten on update. The user-facing
    // mpv.conf/input.conf (which carry these) are preserved (user_preserve).
    overlayPaths.Add("portable_config/mpv-animejanai.conf");
    overlayPaths.Add("portable_config/input-animejanai.conf");

    var manifest = new
    {
        package_version = version,
        // Platform-specific names the updater needs (mpv / 7zz on Linux,
        // mpvnet.exe / 7za.exe on Windows) so the same updater code works
        // cross-platform without hardcoding Windows assumptions.
        player_executable = plat.PlayerExecutable,
        // What the updater launches (vs. player_executable, the process name it
        // waits on): mpvnet.exe on Windows, the mpv-animejanai launcher on Linux.
        player_launcher = plat.PlayerLauncher,
        archive_tool = plat.ArchiveTool,
        platform = plat.Rid,
        // Heavy dependencies. If these are unchanged between releases the updater applies the small
        // overlay; if any differ it falls back to the full package.
        deps = new
        {
            mpvnet = plat.IsWindows ? MpvNetVersion : (string?)null,
            mpvfork = plat.IsWindows ? $"{MpvForkVersion}-{MpvForkGitHash}"
                                     : $"{MpvForkLinuxVersion}",
            inference_runtime = plat.IsWindows ? VsMlrtCudaVersion : $"trt-{AjiVersion}",
            ort_dml = plat.IsWindows ? $"{OrtDmlVersion}+{DirectMLVersion}" : (string?)null,
            sevenzip = SevenZipVersion,
            rife = RifeModelsVersion,
        },
        overlay_paths = overlayPaths.ToArray(),
        // User data never overwritten by an update (full updates preserve these explicitly).
        user_preserve = new[]
        {
            "animejanai/animejanai.conf",
            "animejanai/currentanimejanai.log",
            "portable_config/mpv.conf",
            "portable_config/input.conf",
            "portable_config/saved-props.json",
            "portable_config/settings.xml",
            "portable_config/screenshots",
        },
    };

    var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(installDirectory, "manifest.json"), json);
}

// Copy a file, resolving a symlink to its real target (so the package carries
// real files, not dangling links to system paths). Keeps the link's own name.
void CopyResolvingSymlink(string src, string dst)
{
    var info = new FileInfo(src);
    var real = info.LinkTarget != null ? Path.GetFullPath(info.ResolveLinkTarget(true)!.FullName) : src;
    File.Copy(real, dst, true);
}

void ExtractZip(string archivePath, string outFolder, ProgressChanged progressChanged)
{

    using (var fsInput = File.OpenRead(archivePath))
    using (var zf = new ZipFile(fsInput))
    {

        for (var i = 0; i < zf.Count; i++)
        {
            ZipEntry zipEntry = zf[i];

            if (!zipEntry.IsFile)
            {
                // Ignore directories
                continue;
            }
            String entryFileName = zipEntry.Name;

            var fullZipToPath = Path.Combine(outFolder, entryFileName);
            var directoryName = Path.GetDirectoryName(fullZipToPath);
            if (directoryName?.Length > 0)
            {
                Directory.CreateDirectory(directoryName);
            }

            var buffer = new byte[4096];

            using (var zipStream = zf.GetInputStream(zipEntry))
            using (Stream fsOutput = File.Create(fullZipToPath))
            {
                StreamUtils.Copy(zipStream, fsOutput, buffer);
            }

            var percentage = Math.Round((double)i / zf.Count * 100, 0);
            progressChanged?.Invoke(percentage);
        }
    }
}

async Task RunProcess(string fileName, string arguments)
{
    Debug.WriteLine($"{fileName} {arguments}");

    var process = new Process()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        }
    };

    process.Start();
    string output = await process.StandardOutput.ReadToEndAsync();
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
    {
        throw new Exception($"{fileName} failed (exit {process.ExitCode}): {error}");
    }
}

void CopyDirectory(string srcDir, string targetDir)
{
    Directory.CreateDirectory(targetDir);

    foreach (string file in Directory.GetFiles(srcDir))
    {
        string targetFilePath = Path.Combine(targetDir, Path.GetFileName(file));
        File.Copy(file, targetFilePath, true); // true to overwrite existing files
    }

    foreach (string subDir in Directory.GetDirectories(srcDir))
    {
        string newTargetDir = Path.Combine(targetDir, Path.GetFileName(subDir));
        CopyDirectory(subDir, newTargetDir);
    }
}

async Task Main()
{
    if (Directory.Exists(installDirectory))
    {
        Directory.Delete(installDirectory, true);
    }
    Directory.CreateDirectory(installDirectory);
    await InstallSevenZip();
    await InstallInferenceRuntime();
    await InstallAji();
    if (plat.HasDirectML)
        await InstallOrtDml();
    await InstallRife();
    if (plat.IsWindows)
    {
        await InstallMpvnet();
        await InstallCustomLibmpv();
    }
    else
    {
        await InstallLinuxMpv();
    }
    await InstallYtDlp();
    InstallAnimeJaNaiCore();
    PortConfigsForTarget();
    GenerateInputConf();
    await InstallAnimeJaNaiManager();
    WriteThirdPartyNotices();
    WriteLinuxLauncher();
    WriteVersionAndManifest();
    if (args.Contains("--packs"))
    {
        var packFiles = await EmitComponentPacks();
        SlimInstallTree(packFiles);
    }
}

// The released package is the slim core: everything hardware-specific
// (TensorRT runtime, per-GPU kernel packs, RIFE models) ships only as
// component packs, installed on demand by the AnimeJaNai Manager (the
// first-run dialog offers the hardware-matched set in one click). This
// keeps the one download people grab small; it is still named
// "full-package" because it is the complete release.
void SlimInstallTree(List<string> packFiles)
{
    Console.WriteLine("Slimming install tree (component packs ship separately)...");
    long removed = 0;
    foreach (var rel in packFiles)
    {
        var abs = Path.Combine(installDirectory, rel);
        if (!File.Exists(abs))
        {
            continue;
        }
        removed += new FileInfo(abs).Length;
        File.Delete(abs);
    }
    // prune directories the packs emptied (e.g. animejanai/rife)
    foreach (var dir in Directory.GetDirectories(installDirectory, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.Length))
    {
        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
        }
    }
    Console.WriteLine($"Slimmed {removed / 1048576} MB out of the package tree.");
}

// Component packs: subsets of the freshly built install tree, emitted as
// rooted 7z archives + a packs.json index. The AnimeJaNai Manager downloads
// these so a slim install can add - and an older full install can shed - the
// heavy, hardware-specific pieces: the TensorRT runtime, per-GPU-generation
// builder resources, and the RIFE models. Archive paths are relative to the
// install root, so extraction over an install IS installation.
async Task<List<string>> EmitComponentPacks()
{
    Console.WriteLine("Emitting component packs...");
    var version = args[0];
    var packsDir = Path.Combine(assemblyDirectory, $"packs-v{version}");
    Directory.CreateDirectory(packsDir);
    var sevenZ = Path.Combine(installDirectory, plat.ArchiveTool);
    if (!File.Exists(sevenZ))
    {
        sevenZ = Path.Combine(assemblyDirectory, plat.ArchiveTool); // --packs-only on a partial tree
    }

    // Dep = the manifest.json deps key that governs a pack's content, emitted into packs.json so
    // the updater can skip re-downloading an already-present pack when that dep is unchanged across
    // releases (TensorRT runtime + builder resources are versioned by inference_runtime; RIFE by rife).
    // The base runtime libs (nvinfer/nvonnxparser/cudart/trtexec) but NOT the per-SM builder
    // resources, which split into their own per-GPU packs below.
    var packs = new List<(string Name, string Dep, string[] Files)>
    {
        ("trt-runtime", "inference_runtime", Directory.GetFiles(inferencePath)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                bool builderResource = n.Contains("builder_resource");
                bool runtime = n.StartsWith("nvinfer") || n.StartsWith("libnvinfer") ||
                               n.StartsWith("nvonnxparser") || n.StartsWith("libnvonnxparser") ||
                               n.StartsWith("cudart64_") || n.StartsWith("libcudart") ||
                               n == "trtexec.exe" || n == "trtexec";
                return (runtime && !builderResource) ||
                       n.Contains("LICENSE", StringComparison.OrdinalIgnoreCase);
            })
            .Select(f => Path.GetRelativePath(installDirectory, f)).ToArray()),
        ("rife", "rife", new[] { Path.GetRelativePath(installDirectory, rifePath) }),
    };
    foreach (var f in Directory.GetFiles(inferencePath, "*builder_resource_*"))
    {
        // Windows: nvinfer_builder_resource_sm120_11.dll -> trt-sm120
        // Linux:   libnvinfer_builder_resource_sm120.so.11.0.0 -> trt-sm120
        var m = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileName(f), @"builder_resource_([a-z0-9]+)[._]");
        if (m.Success)
        {
            packs.Add(($"trt-{m.Groups[1].Value}", "inference_runtime",
                       new[] { Path.GetRelativePath(installDirectory, f) }));
        }
    }

    var index = new List<object>();
    var packedFiles = new List<string>();
    foreach (var (name, dep, files) in packs)
    {
        if (files.Length == 0 ||
            !files.Any(f => File.Exists(Path.Combine(installDirectory, f)) ||
                            Directory.Exists(Path.Combine(installDirectory, f))))
        {
            Console.WriteLine($"  component-{name}: nothing to pack in this tree, skipped");
            continue;
        }
        var archive = Path.Combine(packsDir, $"component-{name}{plat.PackSuffix}.7z");
        File.Delete(archive);
        // -spf2: store the relative paths as given (rooted at install dir)
        var fileArgs = string.Join(' ', files.Select(f => $"\"{f}\""));
        var psi = new ProcessStartInfo
        {
            FileName = sevenZ,
            Arguments = $"a -spf2 -mx=3 \"{archive}\" {fileArgs}",
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{plat.ArchiveTool} failed for pack {name}");
        }
        // expand directories to the concrete file list for clean uninstall
        var allFiles = files.SelectMany(f =>
        {
            var abs = Path.Combine(installDirectory, f);
            return Directory.Exists(abs)
                ? Directory.GetFiles(abs, "*", SearchOption.AllDirectories)
                    .Select(x => Path.GetRelativePath(installDirectory, x))
                : new[] { f };
        }).Select(f => f.Replace('\\', '/')).ToArray();
        index.Add(new
        {
            name,
            dep,
            asset = Path.GetFileName(archive),
            bytes = new FileInfo(archive).Length,
            files = allFiles,
        });
        packedFiles.AddRange(allFiles);
        Console.WriteLine($"  component-{name}.7z ({new FileInfo(archive).Length / 1048576} MB, {allFiles.Length} files)");
    }
    File.WriteAllText(Path.Combine(packsDir, $"packs{plat.PackSuffix}.json"),
        JsonSerializer.Serialize(new { package_version = version, packs = index },
            new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Packs written to {packsDir}");
    return packedFiles;
}

if (packsOnlyIndex >= 0)
{
    await EmitComponentPacks();
}
else
{
    await Main();
}

// ---------------------------------------------------------------------------
// Platform descriptor: every platform-varying name/source in one place.
// ---------------------------------------------------------------------------
enum TargetOs { Windows, Linux }

record Platform(
    TargetOs Os,
    string Rid,
    string PlayerExecutable,
    string PlayerLauncher,
    string ArchiveTool,
    string YtDlpName,
    string AjiAsset,
    string ManagerAsset,
    string[] AjiLibs,
    string[] AjiTools,
    string[] ManagerOverlay,
    string[] InferenceRuntimeFiles,
    string[] InferenceRuntimePrefixes,
    bool HasDirectML)
{
    public bool IsWindows => Os == TargetOs.Windows;
    // exe suffix helper: "AnimeJaNaiUpdater" -> "AnimeJaNaiUpdater.exe" / "AnimeJaNaiUpdater"
    public string Exe(string stem) => IsWindows ? stem + ".exe" : stem;
    // Release-asset RID suffix so Windows + Linux assets coexist on one release
    // tag (Windows keeps legacy unsuffixed names; Linux gets "-linux-x64").
    public string PackSuffix => IsWindows ? "" : "-" + Rid;

    public static readonly Platform Win = new(
        Os: TargetOs.Windows,
        Rid: "win-x64",
        PlayerExecutable: "mpvnet.exe",
        PlayerLauncher: "mpvnet.exe",
        ArchiveTool: "7za.exe",
        YtDlpName: "yt-dlp.exe",
        AjiAsset: "aji-windows-x64.zip",
        ManagerAsset: "AnimeJaNaiManager-portable-x64.zip",
        AjiLibs: new[] { "aji.dll", "aji_trt.dll", "aji_dml.dll" },
        AjiTools: new[] { "aji_harness.exe", "aji_harness_dml.exe", "aji_kernel_test.exe" },
        // Avalonia native libs + the Manager exe ride the overlay on Windows.
        ManagerOverlay: new[]
        {
            "AnimeJaNaiManager.exe", "av_libglesv2.dll",
            "libHarfBuzzSharp.dll", "libSkiaSharp.dll",
        },
        InferenceRuntimeFiles: new[]
        {
            "nvinfer_11.dll", "nvinfer_plugin_11.dll", "nvonnxparser_11.dll", "trtexec.exe",
        },
        InferenceRuntimePrefixes: new[] { "cudart64_", "nvinfer_builder_resource_" },
        HasDirectML: true);

    public static readonly Platform Linux = new(
        Os: TargetOs.Linux,
        Rid: "linux-x64",
        PlayerExecutable: "mpv",
        PlayerLauncher: "mpv-animejanai",
        ArchiveTool: "7zz",
        YtDlpName: "yt-dlp_linux",
        AjiAsset: "aji-linux-x64.tar.zst",
        ManagerAsset: "AnimeJaNaiManager-portable-linux-x64.tar.zst",
        AjiLibs: new[] { "libaji.so", "libaji_trt.so" },
        AjiTools: new[] { "aji_harness", "aji_kernel_test" },
        // The self-contained Avalonia Manager ships flat at the install root
        // (binary + Skia/HarfBuzz native libs), mirroring Windows; all of it
        // rides the overlay.
        ManagerOverlay: new[] { "AnimeJaNaiManager", "libSkiaSharp.so", "libHarfBuzzSharp.so" },
        // Copied from the system TRT install in InstallInferenceRuntime (the
        // glob handles the versioned .so names); these literals are unused on
        // Linux but kept non-empty for symmetry.
        InferenceRuntimeFiles: Array.Empty<string>(),
        InferenceRuntimePrefixes: Array.Empty<string>(),
        HasDirectML: false);
}
