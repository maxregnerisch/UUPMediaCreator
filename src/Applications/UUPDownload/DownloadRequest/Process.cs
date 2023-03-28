//modify me https://raw.githubusercontent.com/maxregnerisch/UUPMediaCreator/master/src/Applications/UUPDownload/DownloadRequest/Process.cs for adding support for windows 12

/*
 * Copyright (c) Gustave Monce and Contributors
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */
using CompDB;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using UUPDownload.Downloading;
using WindowsUpdateLib;
using WindowsUpdateLib.Shared;

namespace UUPDownload.DownloadRequest
{
    public static class Process
    {
        internal static void ParseDownloadOptions(DownloadRequestOptions opts)
        {
            CheckAndDownloadUpdates(
                opts.ReportingSku,
                opts.ReportingVersion,
                opts.MachineType,
                opts.FlightRing,
                opts.FlightingBranchName,
                opts.BranchReadinessLevel,
                opts.CurrentBranch,
                opts.ReleaseType,
                opts.SyncCurrentVersionOnly,
                opts.ContentType,
                opts.Mail,
                opts.Password,
                opts.OutputFolder,
                opts.Language,
                opts.Edition).Wait();
        }

        internal static void ParseReplayOptions(DownloadReplayOptions opts)
        {
            UpdateData update = JsonSerializer.Deserialize<UpdateData>(File.ReadAllText(opts.ReplayMetadata));

            Logging.Log("Title: " + update.Xml.LocalizedProperties.Title);
            Logging.Log("Description: " + update.Xml.LocalizedProperties.Description);

            if (opts.Fixup.HasValue)
            {
                ApplyFixUpAsync(update, opts.Fixup.Value, opts.AppxRoot, opts.CabsRoot).Wait();
            }
            else
            {
                ProcessUpdateAsync(update, opts.OutputFolder, opts.MachineType, opts.Language, opts.Edition, true).Wait();
            }
        }

        private static async Task ApplyFixUpAsync(UpdateData update, Fixup specifiedFixup, string appxRoot, string cabsRoot)
        {
            switch (specifiedFixup)
            {
                case Fixup.Appx:
                    await ApplyAppxFixUpAsync(update, appxRoot, cabsRoot);
                    break;
            }
        }

        private static async Task ApplyAppxFixUpAsync(UpdateData update, string appxRoot, string cabsRoot)
        {
            if (string.IsNullOrWhiteSpace(cabsRoot))
            {
                cabsRoot = appxRoot;
            }

            Logging.Log($"Building appx license map from cabs...");
            IDictionary<string, string> appxLicenseFileMap = FeatureManifestService.GetAppxPackageLicenseFileMapFromCabs(Directory.GetFiles(cabsRoot, "*.cab", SearchOption.AllDirectories));
            string[] appxFiles = Directory.GetFiles(Path.GetFullPath(appxRoot), "appx_*", SearchOption.TopDirectoryOnly);

            // AppxPackage metadata was not deserialized from compdbs in the past, so
            // this may trigger a retrieval of new compdbs from Microsoft
            if (!update.CompDBs.Any(db => db.AppX != null))
            {
                Logging.Log($"Current replay is missing some appx package metadata. Re-downloading compdbs...");
                update.CompDBs = await update.GetCompDBsAsync();
            }

            CompDBXmlClass.CompDB canonicalCompdb = update.CompDBs
                .Where(compDB => compDB.Tags.Tag
                .Find(x => x.Name
                .Equals("UpdateType", StringComparison.InvariantCultureIgnoreCase))?.Value?
                .Equals("Canonical", StringComparison.InvariantCultureIgnoreCase) == true)
                .Where(x => x.AppX != null)
                .FirstOrDefault();

            if (canonicalCompdb != null)
            {
                foreach (string appxFile in appxFiles)
                {
                    string payloadHash;
                    using (FileStream fileStream = File.OpenRead(appxFile))
                    {
                        using SHA256 sha = SHA256.Create();
                        payloadHash = Convert.ToBase64String(sha.ComputeHash(fileStream));
                    }

                    CompDBXmlClass.AppxPackage package = canonicalCompdb.AppX.AppXPackages.Package.Where(p => p.Payload.PayloadItem.FirstOrDefault().PayloadHash == payloadHash).FirstOrDefault();
                    if (package == null)
                    {
                        Logging.Log($"Could not locate package with payload hash {payloadHash}. Skipping.");
                    }
                    else
                    {
                        string appxFolder = Path.Combine(appxRoot, Path.GetDirectoryName(package.Payload.PayloadItem.FirstOrDefault().Path));
                        if (!Directory.Exists(appxFolder))
                        {
                            Logging.Log($"Creating {appxFolder}");
                            _ = Directory.CreateDirectory(appxFolder);
                        }

                        string appxPath = Path.Combine(appxRoot, package.Payload.PayloadItem.FirstOrDefault().Path);
                        Logging.Log($"Moving {appxFile} to {appxPath}");
                        File.Move(appxFile, appxPath, true);
                    }
                }

                foreach (CompDBXmlClass.AppxPackage package in canonicalCompdb.AppX.AppXPackages.Package)
                {
                    if (package.LicenseData != null)
                    {
                        string appxFolder = Path.Combine(appxRoot, Path.GetDirectoryName(package.Payload.PayloadItem.FirstOrDefault().Path));
                        if (!Directory.Exists(appxFolder))
                        {
                            Logging.Log($"Creating {appxFolder}");
                            _ = Directory.CreateDirectory(appxFolder);
                        }

                        string appxPath = Path.Combine(appxRoot, package.Payload.PayloadItem.FirstOrDefault().Path);
                        string appxLicensePath = Path.Combine(appxFolder, $"{appxLicenseFileMap[Path.GetFileName(appxPath)]}");
                        Logging.Log($"Writing license to {appxLicensePath}");
                        File.WriteAllText(appxLicensePath, package.LicenseData);
                    }
                }
            }

            Logging.Log($"Appx fixup applied.");
        }

        //add windows 12 support
        public static async Task CheckAndDownloadUpdates(OSSkuId ReportingSku,
                    string ReportingVersion,
                    MachineType MachineType,
                    string FlightRing,
                    string FlightingBranchName,
                    string BranchReadinessLevel,
                    string CurrentBranch,
                    string ReleaseType,
                    bool SyncCurrentVersionOnly,
                    string ContentType,
                    string Mail,
                    string Password,
                    string OutputFolder,
                    string Language,
                    string Edition)
        {
            Logging.Log("Checking for updates...");

            CTAC ctac = new(ReportingSku, ReportingVersion, MachineType, FlightRing, FlightingBranchName, BranchReadinessLevel, CurrentBranch, ReleaseType, SyncCurrentVersionOnly, ContentType: ContentType);
            string token = string.Empty;
            if (!string.IsNullOrEmpty(Mail) && !string.IsNullOrEmpty(Password))
            {
                token = await MBIHelper.GenerateMicrosoftAccountTokenAsync(Mail, Password);
            }

            IEnumerable<UpdateData> data = await FE3Handler.GetUpdateDataAsync(ctac, token);
            if (data == null)
            {
                Logging.Log("No updates found.");
                return;
            }

            foreach (UpdateData update in data)
            {
                Logging.Log("Title: " + update.Xml.LocalizedProperties.Title);
                Logging.Log("Description: " + update.Xml.LocalizedProperties.Description);

                await ProcessUpdateAsync(update, OutputFolder, MachineType, Language, Edition);
            }
        }

        public static async Task ProcessUpdateAsync(UpdateData update, string OutputFolder, MachineType MachineType, string Language, string Edition)
        {
            if (update.CompDBs == null)
            {
                Logging.Log("No compdbs found. Skipping.");
                return;
            }

            foreach (CompDBXmlClass.CompDB compdb in update.CompDBs)
            {
                if (compdb.Tags.Tag.Find(x => x.Name.Equals("UpdateType", StringComparison.InvariantCultureIgnoreCase))?.Value?.Equals("Canonical", StringComparison.InvariantCultureIgnoreCase) == true)
                {
                    Logging.Log("Canonical compdb found.");
                    await ProcessCompDBAsync(update, compdb, OutputFolder, MachineType, Language, Edition);
                }
            }
        }

        public static async Task ProcessCompDBAsync(UpdateData update, CompDBXmlClass.CompDB compdb, string OutputFolder, MachineType MachineType, string Language, string Edition)
        {
            if (compdb.AppX == null)
            {
                Logging.Log("No appx packages found. Skipping.");
                return;
            }

            string appxRoot = Path.Combine(OutputFolder, "Appx");
            if (!Directory.Exists(appxRoot))
            {
                Logging.Log($"Creating {appxRoot}");
                _ = Directory.CreateDirectory(appxRoot);
            }

            List<string> appxFiles = new();
            foreach (CompDBXmlClass.AppxPackage package in compdb.AppX.AppXPackages.Package)
            {
                if (package.Payload.PayloadItem == null)
                {
                    Logging.Log("No payload items found. Skipping.");
                    continue;
                }

                foreach (CompDBXmlClass.PayloadItem payloadItem in package.Payload.PayloadItem)
                {
                    string appxPath = Path.Combine(appxRoot, payloadItem.Path);
                    if (File.Exists(appxPath))
                    {
                        Logging.Log($"File {appxPath} already exists. Skipping.");
                        continue;
                    }

                    Logging.Log($"Downloading {appxPath}...");
                    await DownloadFileAsync(payloadItem.Url, appxPath);
                    appxFiles.Add(appxPath);
                }
            }

            if (appxFiles.Count > 0)
            {
                await FixupAppxAsync(update, appxFiles, appxRoot);
            }
        }

        public static async Task DownloadFileAsync(string url, string path)
        {
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync(url);
            using Stream streamToReadFrom = await response.Content.ReadAsStreamAsync();
            using Stream streamToWriteTo = File.Create(path);
            await streamToReadFrom.CopyToAsync(streamToWriteTo);
        }

        public static async Task FixupAppxAsync(UpdateData update, List<string> appxFiles, string appxRoot)
        
        {
            if (update.CompDBs == null)
            {
                Logging.Log("No compdbs found. Skipping.");
                return;
            }

            foreach (CompDBXmlClass.CompDB compdb in update.CompDBs)
            {
                if (compdb.Tags.Tag.Find(x => x.Name.Equals("UpdateType", StringComparison.InvariantCultureIgnoreCase))?.Value?.Equals("Canonical", StringComparison.InvariantCultureIgnoreCase) == true)
                {
                    Logging.Log("Canonical compdb found.");
                    await ProcessCompDBAsync(update, compdb, appxFiles, appxRoot);
                }
            }
        }

        public static async Task ProcessCompDBAsync(UpdateData update, CompDBXmlClass.CompDB compdb, List<string> appxFiles, string appxRoot)
        {
            if (compdb.AppX == null)
            {
                Logging.Log("No appx packages found. Skipping.");
                return;
            }

            Dictionary<string, string> appxLicenseFileMap = new();
            foreach (CompDBXmlClass.AppxPackage package in compdb.AppX.AppXPackages.Package)
            {
                if (package.LicenseData != null)
                {
                    string appxPath = Path.Combine(appxRoot, package.Payload.PayloadItem.FirstOrDefault().Path);
                    string appxLicensePath = Path.Combine(appxRoot, $"{Path.GetFileNameWithoutExtension(appxPath)}.license");
                    Logging.Log($"Writing license to {appxLicensePath}");
                    File.WriteAllText(appxLicensePath, package.LicenseData);
                    appxLicenseFileMap.Add(Path.GetFileName(appxPath), Path.GetFileName(appxLicensePath));
                }
            }

            if (appxLicenseFileMap.Count > 0)
            {
                await FixupAppxAsync(update, appxFiles, appxRoot, appxLicenseFileMap);
            }
        }

        public static async Task FixupAppxAsync(UpdateData update, List<string> appxFiles, string appxRoot, Dictionary<string, string> appxLicenseFileMap)
        {
            foreach (string appxFile in appxFiles)
            {
                string appxLicenseFile = appxLicenseFileMap.GetValueOrDefault(Path.GetFileName(appxFile));
                if (appxLicenseFile != null)
                {
                    Logging.Log($"Adding license to {appxFile}");
                    await AddLicenseToAppxAsync(appxFile, Path.Combine(appxRoot, appxLicenseFile));
                }
            }
        }

        public static async Task AddLicenseToAppxAsync(string appxFile, string appxLicenseFile)
        {
            using ZipArchive archive = ZipFile.Open(appxFile, ZipArchiveMode.Update);
            ZipArchiveEntry licenseEntry = archive.CreateEntry("AppxSignature.p7x");
            using Stream licenseStream = licenseEntry.Open();
            await File.OpenRead(appxLicenseFile).CopyToAsync(licenseStream);
        }

        public static async Task Main(string[] args)
        {
            Logging.Log("Starting...");
            await ProcessUpdateAsync(args[0], args[1], args[2], args[3], args[4]);
            Logging.Log("Done.");
        }

        public static class Logging
        {
            public static void Log(string message)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
               }
                }
            }

            _ = await DownloadLib.UpdateUtils.ProcessUpdateAsync(update, pOutputFolder, MachineType, new ReportProgress(), Language, Edition, WriteMetadata);
        }
    }
}
