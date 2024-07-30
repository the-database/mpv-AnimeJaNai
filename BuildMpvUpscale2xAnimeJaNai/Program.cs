// See https://aka.ms/new-console-template for more information
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using SevenZipExtractor;
using System.Diagnostics;
using System.Management.Automation;
using System.Text;
using static Downloader;

if (args.Length < 1)
{
    throw new ArgumentException("Version is required.");
}

// Get the path of the currently executing assembly
var assemblyDirectory = AppContext.BaseDirectory;
var animejanaiDirectory = Path.Combine(assemblyDirectory, "mpv-upscale-2x_animejanai");
var installDirectory = Path.Combine(assemblyDirectory, $"mpv-upscale-2x_animejanai-v{args[0]}");
var vapourSynthPluginsPath = Path.Combine(installDirectory, "vs-plugins");
var vsmlrtModelsPath = Path.Combine(vapourSynthPluginsPath, "models");
var vapourSynthVersion = "R69";

async Task InstallPortableVapourSynth()
{
    // Download Python Installer
    Console.WriteLine("Downloading Portable VapourSynth Installer...");
    var downloadUrl = $"https://github.com/vapoursynth/vapoursynth/releases/download/{vapourSynthVersion}/Install-Portable-VapourSynth-{vapourSynthVersion}.ps1";
    var targetPath = Path.GetFullPath("installvs.ps1");
    await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading Portable VapourSynth Installer ({progress}%)...");
    });

    // Install Python 
    Console.WriteLine("Installing Embedded Python with Portable VapourSynth...");

    using (PowerShell powerShell = PowerShell.Create())
    {
        powerShell.AddScript("Set-ExecutionPolicy RemoteSigned -Scope Process -Force");
        powerShell.AddScript("Import-Module Microsoft.PowerShell.Archive");

        var scriptContents = File.ReadAllText(targetPath);

        powerShell.AddScript(scriptContents);
        powerShell.AddParameter("Unattended");
        powerShell.AddParameter("TargetFolder", installDirectory);

        PSDataCollection<PSObject> outputCollection = [];
        outputCollection.DataAdded += (sender, e) =>
        {
            Console.WriteLine(outputCollection[e.Index].ToString());
        };

        try
        {
            IAsyncResult asyncResult = powerShell.BeginInvoke<PSObject, PSObject>(null, outputCollection);
            powerShell.EndInvoke(asyncResult);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"An error occurred: {ex.Message}");
        }

        if (powerShell.Streams.Error.Count > 0)
        {
            foreach (var error in powerShell.Streams.Error)
            {
                Debug.WriteLine($"Error: {error}");
            }
        }
    }

    File.Delete(targetPath);
}

void FixPythonPth()
{
    using StreamWriter writer = new(Path.Join(installDirectory, "python312._pth"), true);
    writer.WriteLine("./animejanai/core\n");
}

async Task InstallPythonDependencies()
{
    string[] dependencies = { "packaging" };

    var cmd = $@".\python.exe -m pip install {string.Join(" ", dependencies)}";

    await RunInstallCommand(cmd);
}

async Task InstallPythonVapourSynthPlugins()
{
    string[] dependencies = { "ffms2" };

    var cmd = $@".\python.exe vsrepo.py -p update && .\python.exe vsrepo.py -p install {string.Join(" ", dependencies)}";

    await RunInstallCommand(cmd);
}

async Task InstallVapourSynthMiscFilters()
{
    Console.WriteLine("Downloading VapourSynth Misc Filters...");
    var downloadUrl = "https://github.com/vapoursynth/vs-miscfilters-obsolete/releases/download/R2/miscfilters-r2.7z";
    var targetPath = Path.GetFullPath("miscfilters.7z");
    await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading VapourSynth Misc Filters ({progress}%)...");
    });

    Console.WriteLine("Extracting VapourSynth Misc Filters...");
    var targetExtractPath = Path.Combine(vapourSynthPluginsPath, "temp");
    Directory.CreateDirectory(targetExtractPath);

    using (ArchiveFile archiveFile = new(targetPath))
    {
        archiveFile.Extract(targetExtractPath);

        File.Copy(
            Path.Combine(targetExtractPath, "win64", "MiscFilters.dll"),
            Path.Combine(vapourSynthPluginsPath, "MiscFilters.dll"),
            true
        );
    }
    Directory.Delete(targetExtractPath, true);
    File.Delete(targetPath);
}

async Task InstallVapourSynthAkarin()
{
    Console.WriteLine("Downloading VapourSynth Akarin...");
    var downloadUrl = "https://github.com/AkarinVS/vapoursynth-plugin/releases/download/v0.96/akarin-release-lexpr-amd64-v0.96g3.7z";
    var targetPath = Path.GetFullPath("akarin.7z");
    await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading VapourSynth Akarin ({progress}%)...");
    });

    Console.WriteLine("Extracting VapourSynth Akarin...");
    var targetExtractPath = Path.Combine(vapourSynthPluginsPath);
    Directory.CreateDirectory(targetExtractPath);

    using (ArchiveFile archiveFile = new(targetPath))
    {
        archiveFile.Extract(targetExtractPath);
    }
    File.Delete(targetPath);
}

async Task InstallVsmlrt()
{
    Console.WriteLine("Downloading vs-mlrt...");
    var downloadUrl = "https://github.com/AmusementClub/vs-mlrt/releases/download/v14.test3/vsmlrt-windows-x64-cuda.v14.test3.7z";
    var targetPath = Path.GetFullPath("vsmlrt.7z");

    double lastProgress = -1;
    int updateThreshold = 5;

    await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        if (progress >= lastProgress + updateThreshold)
        {
            Console.WriteLine($"Downloading vs-mlrt ({progress}%)...");
            lastProgress = progress;
        }
    });

    Console.WriteLine("Extracting vs-mlrt (this may take several minutes)...");
    using (ArchiveFile archiveFile = new(targetPath))
    {
        var targetDirectory = Path.Join(vapourSynthPluginsPath);
        Directory.CreateDirectory(targetDirectory);
        archiveFile.Extract(targetDirectory);
        File.Move(Path.Combine(targetDirectory, "vsmlrt.py"), Path.Combine(installDirectory, "vsmlrt.py"));
    }

    File.Delete(targetPath);
}

async Task InstallRife()
{
    List<string> models = [
        "rife_v4.7.7z",
        "rife_v4.8.7z",
        "rife_v4.9.7z",
        "rife_v4.10.7z",
        "rife_v4.11.7z",
        "rife_v4.12.7z",
        "rife_v4.12_lite.7z",
        "rife_v4.13.7z",
        "rife_v4.13_lite.7z",
        "rife_v4.14.7z",
        "rife_v4.14_lite.7z",
        "rife_v4.15.7z",
        "rife_v4.15_lite.7z",
        "rife_v4.16_lite.7z",
        "rife_v4.17.7z",
        "rife_v4.17_lite.7z",
        "rife_v4.18.7z",
        "rife_v4.19.7z",
        "rife_v4.20.7z",
    ];

    var downloadUrlBase = "https://github.com/AmusementClub/vs-mlrt/releases/download/external-models/";

    foreach (var model in models)
    {
        var downloadUrl = downloadUrlBase + model;
        var targetPath = Path.GetFullPath(model);
        await DownloadFileAsync(downloadUrl, targetPath, _ => { });

        using (ArchiveFile archiveFile = new(targetPath))
        {
            Directory.CreateDirectory(vsmlrtModelsPath);
            archiveFile.Extract(vsmlrtModelsPath);
            var onnxFiles = Directory.GetFiles(Path.Combine(vsmlrtModelsPath, "rife"));
        }

        File.Delete(targetPath);
    }
}

async Task InstallMpvnet()
{
    var downloadUrl = "https://github.com/mpvnet-player/mpv.net/releases/download/v7.1.1.1-beta/mpv.net-v7.1.1.1-beta-portable-x64.zip";
    var targetPath = Path.GetFullPath("mpvnet.zip");
    await DownloadFileAsync(downloadUrl, targetPath, (progress) =>
    {
        Console.WriteLine($"Downloading mpv.net ({progress}%)...");
    });

    Console.WriteLine("Extracting mpv.net models...");
    ExtractZip(targetPath, installDirectory, (double progress) =>
    {
        Console.WriteLine($"Extracting mpv.net ({progress}%)...");
    });

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

void Cleanup()
{
    List<string> dirs = ["doc", "vs-temp-dl", "Scripts", "sdk", "wheel"];

    foreach (var dir in dirs)
    {
        var targetDir = Path.Combine(installDirectory, dir);
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, true);
        }
    }

    foreach (var dir in Directory.GetDirectories(vsmlrtModelsPath))
    {
        if (Path.GetFileName(dir) != "rife")
        {
            Directory.Delete(dir, true);
        }
    }
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
            // to remove the folder from the entry:
            //entryFileName = Path.GetFileName(entryFileName);
            // Optionally match entrynames against a selection list here
            // to skip as desired.
            // The unpacked length is available in the zipEntry.Size property.

            // Manipulate the output filename here as desired.
            var fullZipToPath = Path.Combine(outFolder, entryFileName);
            var directoryName = Path.GetDirectoryName(fullZipToPath);
            if (directoryName?.Length > 0)
            {
                Directory.CreateDirectory(directoryName);
            }

            // 4K is optimum
            var buffer = new byte[4096];

            // Unzip file in buffered chunks. This is just as fast as unpacking
            // to a buffer the full size of the file, but does not waste memory.
            // The "using" will close the stream even if an exception occurs.
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

async Task<string[]> RunInstallCommand(string cmd)
{
    Debug.WriteLine(cmd);

    // Create a new process to run the CMD command
    using (var process = new Process())
    {
        process.StartInfo.FileName = "cmd.exe";
        process.StartInfo.Arguments = @$"/C {cmd}";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        process.StartInfo.WorkingDirectory = installDirectory;

        var result = string.Empty;

        // Create a StreamWriter to write the output to a log file
        try
        {
            //using var outputFile = new StreamWriter("error.log", append: true);
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.WriteLine(e.Data);
                }
            };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    result = e.Data;
                    Console.WriteLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine(); // Start asynchronous reading of the output
            await process.WaitForExitAsync();
        }
        catch (IOException) { }
    }

    return [];
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
    await InstallPortableVapourSynth();
    FixPythonPth();
    await InstallPythonDependencies();
    await InstallPythonVapourSynthPlugins();
    await InstallVapourSynthMiscFilters();
    await InstallVapourSynthAkarin();
    await InstallVsmlrt();
    await InstallRife();
    await InstallMpvnet();
    await InstallYtDlp();
    InstallAnimeJaNaiCore();
    Cleanup();
}

await Main();