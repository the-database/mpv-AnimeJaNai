// Package builder for the native-filter mpv-upscale-2x_animejanai.
//
// The package has no VapourSynth, Python, or vs-mlrt plugins: upscaling and
// RIFE run inside the mpv fork's vf_animejanai filter, which loads aji.dll
// (github.com/the-database/animejanai-inference). Everything NVIDIA lives in
// one self-contained animejanai/inference/ directory (the filter resolves
// the shim's dependencies from its own directory).
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using SevenZipExtractor;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using static Downloader;

// Third-party component versions. Bump these together when cutting a release.
// The inference runtime (TensorRT + trtexec) is reused from the vs-mlrt cuda
// release archives: publicly downloadable, license-precedented, and trtexec
// is version-matched to nvinfer by construction. aji_trt.dll must be built
// against the SAME TensorRT major.minor (v16.x == TensorRT 11.0).
// NOTE: v16.test1 is vs-mlrt's TRT 11 PRE-release - recheck for a stable
// v16 tag before cutting the package release.
const string VsMlrtCudaVersion    = "v16.test1";
const string AjiVersion           = "v0.4.1";       // github.com/the-database/animejanai-inference release tag (multi-GPU kernels + CUDA graphs off by default)
const string SevenZipVersion      = "2501";         // 7-zip "extra" standalone console version
const string MpvNetVersion        = "v7.1.2.0";
const string ManagerVersion       = "0.2.4";        // github.com/the-database/AnimeJaNaiConfEditor release tag (AnimeJaNai Manager)

// DirectML backend runtime (backend=DirectML in animejanai.conf). These are
// the last DirectML-flavored releases: Microsoft moved DML to sustained
// engineering, so 1.24.x is the ORT ceiling until the WinML migration.
const string OrtDmlVersion        = "1.24.4";       // Microsoft.ML.OnnxRuntime.DirectML (NuGet)
const string DirectMLVersion      = "1.15.4";       // Microsoft.AI.DirectML (NuGet)
const string RifeModelsVersion    = "models-rife-fp16-1"; // animejanai-inference release tag (fp16 conversions)

// Custom libmpv fork build (github.com/the-database/mpv-winbuild release).
// The release tag and the archive filename's date can differ (the tag is
// stamped at publish, the filename at build), so they are pinned separately.
const string MpvForkVersion       = "2026-06-13-c9cd49b01f"; // release tag
const string MpvForkBuildDate     = "20260613";     // build date in the dev archive filename
const string MpvForkGitHash       = "c9cd49b01f";   // git short hash in the dev archive filename

// TensorRT runtime files taken from the vs-mlrt cuda archive's vsmlrt-cuda/
// directory. Everything else in there (cuDNN, cuBLAS, onnxruntime, the lean
// and dispatch runtimes) serves backends/options the native filter does not
// use; engine builds run with --tacticSources=-CUDNN,-CUBLAS,-CUBLAS_LT.
string[] inferenceRuntimeFiles = [
    "nvinfer_11.dll",
    "nvinfer_plugin_11.dll",
    "nvonnxparser_11.dll",
    "trtexec.exe",
];
string[] inferenceRuntimePrefixes = [
    "cudart64_",
    "nvinfer_builder_resource_",
];

if (args.Length < 1)
{
    throw new ArgumentException("Version is required.");
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

// Standalone 7-Zip console (7za.exe): used here to extract the multi-part
// vs-mlrt archive, and shipped at the install root for the updater
// (manifest archive_tool).
async Task InstallSevenZip()
{
    Console.WriteLine("Downloading 7-Zip standalone console...");
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
              Path.Combine(installDirectory, "7za.exe"), true);
    Directory.Delete(targetExtractPath, true);
    File.Delete(targetPath);
}

async Task InstallInferenceRuntime()
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
    await RunProcess(Path.Combine(installDirectory, "7za.exe"),
                     $"x \"{targetPaths[0]}\" -o\"{tempDirectory}\" \"vsmlrt-cuda\\*\" -r- -y");

    Directory.CreateDirectory(inferencePath);
    var cudaDirectory = Path.Combine(tempDirectory, "vsmlrt-cuda");
    foreach (var file in Directory.GetFiles(cudaDirectory))
    {
        var name = Path.GetFileName(file);
        bool keep = inferenceRuntimeFiles.Contains(name) ||
                    inferenceRuntimePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
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

async Task InstallAji()
{
    Directory.CreateDirectory(inferencePath);

    // Dev override: point AJI_LOCAL_ZIP at a locally built archive.
    var localZip = Environment.GetEnvironmentVariable("AJI_LOCAL_ZIP");
    string targetPath;
    if (!string.IsNullOrEmpty(localZip))
    {
        Console.WriteLine($"Using local aji build: {localZip}");
        targetPath = localZip;
    }
    else
    {
        Console.WriteLine("Downloading aji (native inference shim)...");
        var downloadUrl = $"https://github.com/the-database/animejanai-inference/releases/download/{AjiVersion}/aji-windows-x64.zip";
        targetPath = Path.GetFullPath("aji-windows-x64.zip");
        await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
        {
            Console.WriteLine($"Downloading aji ({progress}%)...");
        });
    }

    ExtractZip(targetPath, inferencePath, (double progress) => { });

    if (string.IsNullOrEmpty(localZip))
    {
        File.Delete(targetPath);
    }
}

// ONNX Runtime + DirectML for the DirectML backend. The .nupkg files are
// plain zips; only the x64 runtime DLLs (and the DirectML license, which the
// redistribution terms require keeping intact) go into the package. Load
// order at runtime is handled by aji_dml.dll (DirectML.dll before
// onnxruntime.dll, both from this directory).
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
    // model set for both backends (DirectML runs them faster than fp32
    // at reference-class quality; TensorRT 11's strong typing requires
    // fp16 onnx). Converted by animejanai-inference's
    // tools/convert_rife_fp16.py (GridSample grid math kept fp32) and
    // hosted as a single release asset. Lives outside onnx/ so the
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
    using (ArchiveFile archiveFile = new(targetPath))
    {
        archiveFile.Extract(rifePath);
    }
    File.Delete(targetPath);
}

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

async Task InstallYtDlp()
{
    var downloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    var targetPath = Path.Combine(installDirectory, "yt-dlp.exe");
    await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading yt-dlp.exe... ({progress})%");
    });
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

async Task InstallAnimeJaNaiManager()
{
    Console.WriteLine("Downloading AnimeJaNai Manager...");
    var downloadUrl = $"https://github.com/the-database/AnimeJaNaiConfEditor/releases/download/{ManagerVersion}/AnimeJaNaiManager-portable-x64.zip";
    var targetPath = Path.GetFullPath("AnimeJaNaiManager-portable-x64.zip");
    await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading AnimeJaNai Manager ({progress}%)...");
    });

    Console.WriteLine("Extracting AnimeJaNai Manager...");
    // The zip is flat (AnimeJaNaiManager.exe + native DLLs) and lands at the install root,
    // next to mpvnet.exe, for discoverability. It finds its data in animejanai/.
    ExtractZip(targetPath, installDirectory, (double progress) =>
    {
        Console.WriteLine($"Extracting AnimeJaNai Manager ({progress}%)...");
    });

    File.Delete(targetPath);
}

// The TensorRT SLA requires this attribution when redistributing the
// runtime; keep it next to the redistributed files.
void WriteThirdPartyNotices()
{
    var notice = """
        Third-party components in this directory
        ========================================

        NVIDIA TensorRT runtime (nvinfer_11.dll, nvinfer_plugin_11.dll,
        nvonnxparser_11.dll, nvinfer_builder_resource_*.dll, trtexec.exe)
        and NVIDIA CUDA runtime (cudart64_*.dll), redistributed under the
        NVIDIA TensorRT Software License Agreement and CUDA Toolkit EULA:

            This software contains source code provided by NVIDIA Corporation.

        These files are obtained from the vs-mlrt project's release archives
        (https://github.com/AmusementClub/vs-mlrt), which redistributes them
        under the same terms.

        ONNX Runtime (onnxruntime.dll), (c) Microsoft Corporation,
        redistributed under the MIT license
        (https://github.com/microsoft/onnxruntime/blob/main/LICENSE).

        DirectML (DirectML.dll), (c) Microsoft Corporation, redistributed
        as the DirectML Redistributable Package under the Microsoft
        Software License Terms shipped alongside it as
        DirectML_LICENSE.txt (use on Windows and Xbox only).

        aji.dll / aji_trt.dll / aji_dml.dll / aji_harness.exe /
        aji_harness_dml.exe / aji_kernel_test.exe:
        https://github.com/the-database/animejanai-inference
        """;
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

    var manifest = new
    {
        package_version = version,
        // Platform-specific names the updater needs. Each platform's builder emits its own values
        // (a future Linux builder would use e.g. "mpv" / "7zz") so the same updater code works
        // cross-platform without hardcoding Windows assumptions.
        player_executable = "mpvnet.exe",
        archive_tool = "7za.exe",
        // Heavy dependencies. If these are unchanged between releases the updater applies the small
        // overlay; if any differ it falls back to the full package. ManagerVersion is omitted on
        // purpose: the editor ships inside the overlay, so it updates without a full download.
        deps = new
        {
            mpvnet = MpvNetVersion,
            mpvfork = $"{MpvForkVersion}-{MpvForkGitHash}",
            inference_runtime = VsMlrtCudaVersion,
            ort_dml = $"{OrtDmlVersion}+{DirectMLVersion}",
            sevenzip = SevenZipVersion,
            rife = RifeModelsVersion,
        },
        // Managed program files (relative to install root) that make up the overlay update and are
        // overwritten on update. Extraction overlays these without deleting extras (e.g. user onnx).
        // aji.dll and its tools are small and update often, so they ride the overlay; the TensorRT
        // runtime files in the same directory are deps-versioned and only change on full updates.
        overlay_paths = new[]
        {
            "version.txt",
            "manifest.json",
            "AnimeJaNaiUpdater.exe",
            "animejanai/onnx",
            "animejanai/benchmarks",
            "animejanai/inference/aji.dll",
            "animejanai/inference/aji_trt.dll",
            "animejanai/inference/aji_dml.dll",
            "animejanai/inference/aji_harness.exe",
            "animejanai/inference/aji_harness_dml.exe",
            "animejanai/inference/aji_kernel_test.exe",
            "AnimeJaNaiManager.exe",
            "av_libglesv2.dll",
            "libHarfBuzzSharp.dll",
            "libSkiaSharp.dll",
            "portable_config/scripts",
            "portable_config/shaders",
            // Managed defaults files, overwritten on update. The user-facing
            // mpv.conf/input.conf (which carry these) are preserved (user_preserve);
            // for input.conf the updater refreshes its managed block from
            // input-animejanai.conf while keeping the user's keybindings.
            "portable_config/mpv-animejanai.conf",
            "portable_config/input-animejanai.conf",
        },
        // User data never overwritten by an update (full updates preserve these explicitly).
        user_preserve = new[]
        {
            "animejanai/animejanai.conf",
            "animejanai/currentanimejanai.log",
            // mpv.conf and input.conf are the user's own files now (they carry the
            // managed defaults - mpv.conf via include, input.conf via a managed block
            // the updater refreshes). Never overwrite them. Upgrades from <=3.3.x
            // still get the new versions because those releases list them under
            // overlay; the updater then folds the old mpv-user.conf / input-user.conf
            // into them and deletes those retired files.
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
    await InstallOrtDml();
    await InstallRife();
    await InstallMpvnet();
    await InstallCustomLibmpv();
    await InstallYtDlp();
    InstallAnimeJaNaiCore();
    GenerateInputConf();
    await InstallAnimeJaNaiManager();
    WriteThirdPartyNotices();
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
// "full-package" because it is the complete release - and the 3.3.x
// updater downloads that asset name blindly, after which upgraders get
// the same Manager first-run flow as new users.
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
// rooted 7z archives + a packs.json index. The AnimeJaNai Manager (the
// updater's component modes) downloads these from the release so a slim
// install can add - and an older full install can shed - the heavy,
// hardware-specific pieces: the TensorRT runtime, per-GPU-generation
// builder resources, and the RIFE models. Archive paths are relative to
// the install root, so extraction over an install IS installation.
// Returns the expanded per-file list of everything packed; under --packs
// the caller then strips those files from the tree (SlimInstallTree).
async Task<List<string>> EmitComponentPacks()
{
    Console.WriteLine("Emitting component packs...");
    var version = args[0];
    var packsDir = Path.Combine(assemblyDirectory, $"packs-v{version}");
    Directory.CreateDirectory(packsDir);
    var sevenZa = Path.Combine(installDirectory, "7za.exe");
    if (!File.Exists(sevenZa))
    {
        sevenZa = Path.Combine(assemblyDirectory, "7za.exe"); // --packs-only on a partial tree
    }

    var packs = new List<(string Name, string[] Files)>
    {
        ("trt-runtime", Directory.GetFiles(inferencePath)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return (n.StartsWith("nvinfer_") && !n.Contains("builder_resource")) ||
                       n.StartsWith("nvonnxparser_") || n.StartsWith("cudart64_") ||
                       n == "trtexec.exe" ||
                       n.Contains("LICENSE", StringComparison.OrdinalIgnoreCase);
            })
            .Select(f => Path.GetRelativePath(installDirectory, f)).ToArray()),
        ("rife", new[] { Path.GetRelativePath(installDirectory, rifePath) }),
    };
    foreach (var f in Directory.GetFiles(inferencePath, "nvinfer_builder_resource_*"))
    {
        // nvinfer_builder_resource_sm120_11.dll -> trt-sm120
        var m = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileName(f), @"builder_resource_([a-z0-9]+)_");
        if (m.Success)
        {
            packs.Add(($"trt-{m.Groups[1].Value}",
                       new[] { Path.GetRelativePath(installDirectory, f) }));
        }
    }

    var index = new List<object>();
    var packedFiles = new List<string>();
    foreach (var (name, files) in packs)
    {
        if (files.Length == 0 ||
            !files.Any(f => File.Exists(Path.Combine(installDirectory, f)) ||
                            Directory.Exists(Path.Combine(installDirectory, f))))
        {
            Console.WriteLine($"  component-{name}: nothing to pack in this tree, skipped");
            continue;
        }
        var archive = Path.Combine(packsDir, $"component-{name}.7z");
        File.Delete(archive);
        // -spf2: store the relative paths as given (rooted at install dir)
        var fileArgs = string.Join(' ', files.Select(f => $"\"{f}\""));
        var psi = new ProcessStartInfo
        {
            FileName = sevenZa,
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
            throw new InvalidOperationException($"7za failed for pack {name}");
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
            asset = Path.GetFileName(archive),
            bytes = new FileInfo(archive).Length,
            files = allFiles,
        });
        packedFiles.AddRange(allFiles);
        Console.WriteLine($"  component-{name}.7z ({new FileInfo(archive).Length / 1048576} MB, {allFiles.Length} files)");
    }
    File.WriteAllText(Path.Combine(packsDir, "packs.json"),
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
