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
            string appxLicenseFile = Path.Combine(appxRoot, "AppxLicense.xml");
            if (File.Exists(appxLicenseFile))
            {
                Logging.Log($"Deleting {appxLicenseFile}");
                File.Delete(appxLicenseFile);
            }

            string appxSignatureFile = Path.Combine(appxRoot, "AppxSignature.p7x");
            if (File.Exists(appxSignatureFile))
            {
                Logging.Log($"Deleting {appxSignatureFile}");
                File.Delete(appxSignatureFile);
            }

            string appxBlockMapFile = Path.Combine(appxRoot, "AppxBlockMap.xml");
            if (File.Exists(appxBlockMapFile))
            {
                Logging.Log($"Deleting {appxBlockMapFile}");
                File.Delete(appxBlockMapFile);
            }

            string appxBundleManifestFile = Path.Combine(appxRoot, "AppxBundleManifest.xml");
            if (File.Exists(appxBundleManifestFile))
            {
                Logging.Log($"Deleting {appxBundleManifestFile}");
                File.Delete(appxBundleManifestFile);
            }

            string appxBundleBlockMapFile = Path.Combine(appxRoot, "AppxBundleBlockMap.xml");
            if (File.Exists(appxBundleBlockMapFile))
            {
                Logging.Log($"Deleting {appxBundleBlockMapFile}");
                File.Delete(appxBundleBlockMapFile);
            }

            string appxBundleSignatureFile = Path.Combine(appxRoot, "AppxBundleSignature.p7x");
            if (File.Exists(appxBundleSignatureFile))
            {
                Logging.Log($"Deleting {appxBundleSignatureFile}");
                File.Delete(appxBundleSignatureFile);
            }

            string appxBundleMetadataFile = Path.Combine(appxRoot, "AppxBundleMetadata.xml");
            if (File.Exists(appxBundleMetadataFile))
            {
                Logging.Log($"Deleting {appxBundleMetadataFile}");
                File.Delete(appxBundleMetadataFile);
            }

            string appxBundleMetadataSignatureFile = Path.Combine(appxRoot, "AppxBundleMetadataSignature.p7x");
            if (File.Exists(appxBundleMetadataSignatureFile))
            {
                Logging.Log($"Deleting {appxBundleMetadataSignatureFile}");
                File.Delete(appxBundleMetadataSignatureFile);
            }

            string appxBundleManifestSignatureFile
                = Path.Combine(appxRoot, "AppxBundleManifestSignature.p7x");
            if (File.Exists(appxBundleManifestSignatureFile))
            {
                Logging.Log($"Deleting {appxBundleManifestSignatureFile}");
                File.Delete(appxBundleManifestSignatureFile);
            }

            string appxBundleBlockMapSignatureFile
                = Path.Combine(appxRoot, "AppxBundleBlockMapSignature.p7x");
            if (File.Exists(appxBundleBlockMapSignatureFile))
            {
                Logging.Log($"Deleting {appxBundleBlockMapSignatureFile}");
                File.Delete(appxBundleBlockMapSignatureFile);
            }   

            string appxBundleBlockMapFile2 = Path.Combine(appxRoot, "AppxBlockMap.xml");
            if (File.Exists(appxBundleBlockMapFile2))
            {
                Logging.Log($"Deleting {appxBundleBlockMapFile2}");
                File.Delete(appxBundleBlockMapFile2);
            }

            string appxBundleSignatureFile2 = Path.Combine(appxRoot, "AppxSignature.p7x");
            if (File.Exists(appxBundleSignatureFile2))
            {
                Logging.Log($"Deleting {appxBundleSignatureFile2}");
                File.Delete(appxBundleSignatureFile2);
            }

            string appxBundleMetadataFile2 = Path.Combine(appxRoot, "AppxMetadata.xml");
            if (File.Exists(appxBundleMetadataFile2))
            {
                Logging.Log($"Deleting {appxBundleMetadataFile2}");
                File.Delete(appxBundleMetadataFile2);
            }

            string appxBundleMetadataSignatureFile2
                = Path.Combine(appxRoot, "AppxMetadataSignature.p7x");
            if (File.Exists(appxBundleMetadataSignatureFile2))
            {
                Logging.Log($"Deleting {appxBundleMetadataSignatureFile2}");
                File.Delete(appxBundleMetadataSignatureFile2);
            }

            string appxBundleManifestSignatureFile2
                = Path.Combine(appxRoot, "AppxManifestSignature.p7x");
            if (File.Exists(appxBundleManifestSignatureFile2))
            {
                Logging.Log($"Deleting {appxBundleManifestSignatureFile2}");
                File.Delete(appxBundleManifestSignatureFile2);
            }

            string appxBundleBlockMapSignatureFile2
                = Path.Combine(appxRoot, "AppxBlockMapSignature.p7x");
            if (File.Exists(appxBundleBlockMapSignatureFile2))
            {
                Logging.Log($"Deleting {appxBundleBlockMapSignatureFile2}");
                File.Delete(appxBundleBlockMapSignatureFile2);
            }

            string appxBundleBlockMapFile3 = Path.Combine(appxRoot, "AppxBlockMap.xml");
            if (File.Exists(appxBundleBlockMapFile3))
            {
                Logging.Log($"Deleting {appxBundleBlockMapFile3}");
                File.Delete(appxBundleBlockMapFile3);
            }

            string appxBundleSignatureFile3 = Path.Combine(appxRoot, "AppxSignature.p7x");
            if (File.Exists(appxBundleSignatureFile3))
            {
                Logging.Log($"Deleting {appxBundleSignatureFile3}");
                File.Delete(appxBundleSignatureFile3);
            }

            string appxBundleMetadataFile3 = Path.Combine(appxRoot, "AppxMetadata.xml");
            if (File.Exists(appxBundleMetadataFile3))
            {
                Logging.Log $"Deleting {appxBundleMetadataFile3}");
                File.Delete(appxBundleMetadataFile3);
            }

            string appxBundleMetadataSignatureFile3
                = Path.Combine(appxRoot, "AppxMetadataSignature.p7x");
            if (File.Exists(appxBundleMetadataSignatureFile3))
            {
                Logging.Log($"Deleting {appxBundleMetadataSignatureFile3}");
                File.Delete(appxBundleMetadataSignatureFile3);
            }

            string appxBundleManifestSignatureFile3
                = Path.Combine(appxRoot, "AppxManifestSignature.p7x");
            if (File.Exists(appxBundleManifestSignatureFile3))
            {
                Logging.Log($"Deleting {appxBundleManifestSignatureFile3}");
                File.Delete(appxBundleManifestSignatureFile3);
            }

        private static async Task CheckAndDownloadUpdates(OSSkuId ReportingSku,
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

            IEnumerable<UpdateData> data = await FE3Handler.GetUpdates(null, ctac, token, FileExchangeV3UpdateFilter.ProductRelease);
            //data = data.Select(x => UpdateUtils.TrimDeltasFromUpdateData(x));

            if (!data.Any())
            {
                Logging.Log("No updates found that matched the specified criteria.", Logging.LoggingLevel.Error);
            }
            else
            {
                Logging.Log($"Found {data.Count()} update(s):");

                for (int i = 0; i < data.Count(); i++)
                {
                    UpdateData update = data.ElementAt(i);

                    Logging.Log($"{i}: Title: {update.Xml.LocalizedProperties.Title}");
                    Logging.Log($"{i}: Description: {update.Xml.LocalizedProperties.Description}");
                }

                foreach (UpdateData update in data)
                {
                    Logging.Log("Title: " + update.Xml.LocalizedProperties.Title);
                    Logging.Log("Description: " + update.Xml.LocalizedProperties.Description);

                    await ProcessUpdateAsync(update, OutputFolder, MachineType, Language, Edition, true);
                }
            }
            Logging.Log("Completed.");
            if (Debugger.IsAttached)
            {
                _ = Console.ReadLine();
            }
        }

        private static async Task ProcessUpdateAsync(UpdateData update, string pOutputFolder, MachineType MachineType, string Language = "", string Edition = "", bool WriteMetadata = true)
        {
            string buildstr = "";
            IEnumerable<string> languages = null;

            Logging.Log("Gathering update metadata...");

            HashSet<CompDBXmlClass.CompDB> compDBs = await update.GetCompDBsAsync();

            await Task.WhenAll(
                Task.Run(async () => buildstr = await update.GetBuildStringAsync()),
                Task.Run(async () => languages = await update.GetAvailableLanguagesAsync()));

            buildstr ??= "";

            //
            // Windows Phone Build Lab says hi
            //
            // Quirk with Nickel+ Windows NT builds where specific binaries
            // exempted from neutral build info gets the wrong build tags
            //
            if (buildstr.Contains("GitEnlistment(winpbld)"))
            {
                // We need to fallback to CompDB (less accurate but we have no choice, due to CUs etc...

                // Loop through all CompDBs to find the highest version reported
                CompDBXmlClass.CompDB selectedCompDB = null;
                Version currentHighest = null;
                foreach (CompDBXmlClass.CompDB compDB in compDBs)
                {
                    if (compDB.TargetOSVersion != null)
                    {
                        if (Version.TryParse(compDB.TargetOSVersion, out Version currentVer))
                        {
                            if (currentHighest == null || (currentVer != null && currentVer.GreaterThan(currentHighest)))
                            {
                                if (!string.IsNullOrEmpty(compDB.TargetBuildInfo) && !string.IsNullOrEmpty(compDB.TargetOSVersion))
                                {
                                    currentHighest = currentVer;
                                    selectedCompDB = compDB;
                                }
                            }
                        }
                    }
                }

                // We found a suitable CompDB is it is not null
                if (selectedCompDB != null)
                {
                    // Example format:
                    // TargetBuildInfo="rs_prerelease_flt.22509.1011.211120-1700"
                    // TargetOSVersion="10.0.22509.1011"

                    buildstr = $"{selectedCompDB.TargetOSVersion} ({selectedCompDB.TargetBuildInfo.Split(".")[0]}.{selectedCompDB.TargetBuildInfo.Split(".")[3]})";
                }
            }

            if (string.IsNullOrEmpty(buildstr) && update.Xml.LocalizedProperties.Title.Contains("(UUP-CTv2)"))
            {
                string unformattedBase = update.Xml.LocalizedProperties.Title.Split(" ")[0];
                buildstr = $"10.0.{unformattedBase.Split(".")[0]}.{unformattedBase.Split(".")[1]} ({unformattedBase.Split(".")[2]}.{unformattedBase.Split(".")[3]})";
            }
            else if (string.IsNullOrEmpty(buildstr))
            {
                buildstr = update.Xml.LocalizedProperties.Title;
            }

            Logging.Log("Build String: " + buildstr);
            Logging.Log("Languages: " + string.Join(", ", languages));

            Logging.Log("Parsing CompDBs...");

            if (compDBs != null)
            {
                CompDBXmlClass.Package editionPackPkg = compDBs.GetEditionPackFromCompDBs();
                if (editionPackPkg != null)
                {
                    string editionPkg = await update.DownloadFileFromDigestAsync(editionPackPkg.Payload.PayloadItem.First(x => !x.Path.EndsWith(".psf")).PayloadHash);
                    BuildTargets.EditionPlanningWithLanguage[] plans = await Task.WhenAll(languages.Select(x => update.GetTargetedPlanAsync(x, editionPkg)));

                    foreach (BuildTargets.EditionPlanningWithLanguage plan in plans)
                    {
                        Logging.Log("");
                        Logging.Log("Editions available for language: " + plan.LanguageCode);
                        plan.EditionTargets.PrintAvailablePlan();
                    }
                }
            }

            _ = await DownloadLib.UpdateUtils.ProcessUpdateAsync(update, pOutputFolder, MachineType, new ReportProgress(), Language, Edition, WriteMetadata);
        }
    }
}
