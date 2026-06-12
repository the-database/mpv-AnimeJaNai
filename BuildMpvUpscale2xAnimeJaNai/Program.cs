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
const string AjiVersion           = "v0.1.0";       // github.com/the-database/animejanai-inference release tag
const string SevenZipVersion      = "2501";         // 7-zip "extra" standalone console version
const string MpvNetVersion        = "v7.1.2.0";
const string ConfEditorVersion    = "0.0.8";        // github.com/the-database/AnimeJaNaiConfEditor release tag

// DirectML backend runtime (backend=DirectML in animejanai.conf). These are
// the last DirectML-flavored releases: Microsoft moved DML to sustained
// engineering, so 1.24.x is the ORT ceiling until the WinML migration.
const string OrtDmlVersion        = "1.24.4";       // Microsoft.ML.OnnxRuntime.DirectML (NuGet)
const string DirectMLVersion      = "1.15.4";       // Microsoft.AI.DirectML (NuGet)
const string RifeModelsVersion    = "models-rife-fp16-1"; // animejanai-inference release tag (fp16 conversions)

// Custom libmpv fork build (github.com/the-database/mpv-winbuild release).
const string MpvForkVersion       = "20260611";     // release tag (= build date)
const string MpvForkGitHash       = "ac1ce81871";   // git short hash in the dev archive filename

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
    var downloadUrl = $"https://github.com/the-database/mpv-winbuild/releases/download/{MpvForkVersion}/mpv-dev-x86_64-{MpvForkVersion}-git-{MpvForkGitHash}.7z";
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

async Task InstallAnimeJaNaiConfEditor()
{
    Console.WriteLine("Downloading AnimeJaNaiConfEditor...");
    var downloadUrl = $"https://github.com/the-database/AnimeJaNaiConfEditor/releases/download/{ConfEditorVersion}/AnimeJaNaiConfEditor-portable-x64.zip";
    var targetPath = Path.GetFullPath("AnimeJaNaiConfEditor-portable-x64.zip");
    await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading AnimeJaNaiConfEditor ({progress}%)...");
    });

    Console.WriteLine("Extracting AnimeJaNaiConfEditor...");
    // The zip is flat (AnimeJaNaiConfEditor.exe + native DLLs at root) and lands directly in
    // animejanai/, alongside the overlay's own animejanai.conf and onnx/ (which it does not contain).
    var targetExtractPath = Path.Combine(installDirectory, "animejanai");
    ExtractZip(targetPath, targetExtractPath, (double progress) =>
    {
        Console.WriteLine($"Extracting AnimeJaNaiConfEditor ({progress}%)...");
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
        // overlay; if any differ it falls back to the full package. ConfEditorVersion is omitted on
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
            "animejanai/inference/aji.dll",
            "animejanai/inference/aji_trt.dll",
            "animejanai/inference/aji_dml.dll",
            "animejanai/inference/aji_harness.exe",
            "animejanai/inference/aji_harness_dml.exe",
            "animejanai/inference/aji_kernel_test.exe",
            "animejanai/AnimeJaNaiConfEditor.exe",
            "animejanai/av_libglesv2.dll",
            "animejanai/libHarfBuzzSharp.dll",
            "animejanai/libSkiaSharp.dll",
            "portable_config/scripts",
            "portable_config/shaders",
            "portable_config/mpv.conf",
            "portable_config/input.conf",
        },
        // User data never overwritten by an update (full updates preserve these explicitly).
        user_preserve = new[]
        {
            "animejanai/animejanai.conf",
            "animejanai/currentanimejanai.log",
            "portable_config/mpv-user.conf",
            "portable_config/input-user.conf",
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
    await InstallAnimeJaNaiConfEditor();
    WriteThirdPartyNotices();
    WriteVersionAndManifest();
}

await Main();
