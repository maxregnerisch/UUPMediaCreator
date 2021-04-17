﻿// Copyright (c) Gustave Monce and Contributors
// Copyright (c) ADeltaX and Contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UUPDownload;
using WindowsUpdateLib;

namespace Download.Downloading
{
    public class GeneralDownloadProgress
    {
        public long EstimatedTotalBytes;
        public long DownloadedTotalBytes;
        public int NumFilesDownloadedSuccessfully;
        public int NumFilesDownloadedUnsuccessfully;
        public int NumFiles;

        public FileDownloadStatus[] DownloadedStatus;
    }

    public class ReportProgress : IProgress<GeneralDownloadProgress>
    {
        private Dictionary<string,FileStatus> files = new Dictionary<string, FileStatus>();

        private Mutex mutex = new Mutex();

        private static string FormatBytes(double bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            int i;
            double dblSByte = bytes;
            for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblSByte = bytes / 1024.0;
            }

            return $"{dblSByte:0.##}{suffix[i]}";
        }

        private static string GetProgressBarString(int perc)
        {
            if (perc < 0)
                perc = 0;
            if (perc > 100)
                perc = 100;

            int eqsLength = (int)((double)perc / 100 * 55);
            string bases = new string('=', eqsLength) + new string(' ', 55 - eqsLength);
            bases = bases.Insert(28, perc + "%");
            if (perc == 100)
                bases = bases[1..];
            else if (perc < 10)
                bases = bases.Insert(28, " ");
            return "[" + bases + "]";
        }

        public void Report(GeneralDownloadProgress e)
        {
            mutex.WaitOne();

            foreach (FileDownloadStatus status in e.DownloadedStatus)
            {
                if (status == null)
                    continue;

                bool shouldReport = !files.ContainsKey(status.File.FileName) || files[status.File.FileName] != status.FileStatus;

                if (!shouldReport)
                    continue;

                files[status.File.FileName] = status.FileStatus;

                string msg = "U";

                switch(status.FileStatus)
                {
                    case FileStatus.Completed:
                        msg = "C";
                        break;
                    case FileStatus.Downloading:
                        msg = "D";
                        break;
                    case FileStatus.Expired:
                        msg = "E";
                        break;
                    case FileStatus.Failed:
                        msg = "F";
                        break;
                    case FileStatus.Verifying:
                        msg = "V";
                        break;
                }

                Console.WriteLine($"{DateTime.Now.ToString("'['HH':'mm':'ss']'")}[{e.NumFilesDownloadedSuccessfully}/{e.NumFiles}][E:{e.NumFilesDownloadedUnsuccessfully}][{msg}] {status.File.FileName} ({FormatBytes(status.File.FileSize)})");
            }

            mutex.ReleaseMutex();
        }
    }

    public enum FileStatus
    {
        Downloading,
        Verifying,
        Completed,
        Expired,
        Failed
    }

    public class FileDownloadStatus
    {
        public FileStatus FileStatus;
        public long DownloadedBytes;
        public long HashedBytes;
        public UUPFile File;
        public FileDownloadStatus(UUPFile file)
        {
            this.File = file;
        }
    }

    public class UUPFile
    {
        public FileExchangeV3FileDownloadInformation WUFile { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string SHA256 { get; set; }

        public UUPFile(FileExchangeV3FileDownloadInformation WUFile, string FileName, long FileSize, string SHA256)
        {
            this.WUFile = WUFile;
            this.FileName = FileName;
            this.FileSize = FileSize;
            this.SHA256 = SHA256;
        }
    }

    public class HttpDownloader : IDisposable
    {
        private const long CHUNK_SIZE = 8_388_608 + 65_536; //Slice 8MB+64KB
        private const string TEMP_DOWNLOAD_EXTENSION = ".dlTmp";

        private readonly HttpClient _hc;
        public string DownloadFolderPath { get; set; }
        public int DownloadThreads { get; set; }
        public int DownloadRetries { get; set; }
        public bool VerifyFiles { get; set; }

        public HttpDownloader(string downloadFolderPath, int downloadThreads = 4, bool verifyFiles = true, IWebProxy proxy = null, bool useSystemProxy = true)
        {
            var filter = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = 512
            };

            if (proxy != null || !useSystemProxy)
                filter.Proxy = proxy;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                filter.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator; //For Linux, MS cert isn't trusted lol ¯\_(ツ)_/¯

            _hc = new HttpClient(filter)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _hc.DefaultRequestHeaders.Add("User-Agent", "Windows-Update-Agent/10.0.10011.16384 Client-Protocol/2.1");
            _hc.DefaultRequestHeaders.Connection.Add("keep-alive");

            DownloadThreads = downloadThreads;
            DownloadFolderPath = downloadFolderPath;
            VerifyFiles = verifyFiles;
        }

        public async Task<bool> DownloadAsync(List<UUPFile> Files, IProgress<GeneralDownloadProgress> generalDownloadProgress, CancellationToken cancellationToken = default)
            => await ParallelQueue(Files, DownloadFile, generalDownloadProgress, DownloadThreads, cancellationToken).ConfigureAwait(false);

        public async Task<bool> DownloadAsync(UUPFile File, IProgress<FileDownloadStatus> downloadProgress = null, CancellationToken cancellationToken = default)
            => await DownloadFile(File, downloadProgress, cancellationToken).ConfigureAwait(false);

        private static async ValueTask<bool> ParallelQueue(List<UUPFile> items, Func<UUPFile, IProgress<FileDownloadStatus>, CancellationToken, ValueTask<bool>> func,
            IProgress<GeneralDownloadProgress> generalProgress, int threads, CancellationToken cancellationToken)
        {
            Queue<UUPFile> pending = new(items);
            Task<bool>[] workingSlots = new Task<bool>[threads];
            int workingThreadsCount = 0;

            GeneralDownloadProgress generalDownloadProgress = new()
            {
                DownloadedStatus = new FileDownloadStatus[threads],
                NumFiles = items.Count
            };

            var result = true;
            while (pending.Count + workingThreadsCount != 0)
            {
                if (workingThreadsCount < threads && pending.Count != 0)
                {
                    var item = pending.Dequeue();
                    FileDownloadStatus fileStatus = new(item);
                    Progress<FileDownloadStatus> progress = new();

                    workingThreadsCount++;
                    GetFreeSlotIndex(workingSlots, out int freeSlotIndex);
                    generalDownloadProgress.DownloadedStatus[freeSlotIndex] = fileStatus;

                    progress.ProgressChanged += (s, e) =>
                    {
                        generalDownloadProgress.DownloadedTotalBytes += e.DownloadedBytes - generalDownloadProgress.DownloadedStatus[freeSlotIndex].DownloadedBytes;
                        generalDownloadProgress.DownloadedStatus[freeSlotIndex].DownloadedBytes = e.DownloadedBytes;
                        generalDownloadProgress.DownloadedStatus[freeSlotIndex].HashedBytes = e.HashedBytes;
                        generalDownloadProgress.DownloadedStatus[freeSlotIndex].FileStatus = e.FileStatus;
                        generalProgress?.Report(generalDownloadProgress);
                    };

                    workingSlots[freeSlotIndex] = Task.Run(async () => await func(item, progress, cancellationToken).ConfigureAwait(false));
                }
                else
                {
                    await Task.WhenAny(workingSlots.Where(t => t != null));

                    for (int i = 0; i < workingSlots.Length; i++)
                    {
                        if (workingSlots[i] != null && workingSlots[i].IsCompleted)
                        {
                            if (workingSlots[i].Result)
                                generalDownloadProgress.NumFilesDownloadedSuccessfully++;
                            else
                            {
                                generalDownloadProgress.NumFilesDownloadedUnsuccessfully++;
                                result = false;
                            }

                            workingThreadsCount--;
                            workingSlots[i].Dispose();
                            workingSlots[i] = null;
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            return result;
        }

        private async ValueTask<bool> DownloadFile(UUPFile downloadFile, IProgress<FileDownloadStatus> progress, CancellationToken cancellationToken)
            => await HttpDownload(DownloadFolderPath, downloadFile, _hc, VerifyFiles, progress, cancellationToken: cancellationToken).ConfigureAwait(false);

        private async static ValueTask<bool> HttpDownload(string basePath, UUPFile downloadFile, HttpClient httpClient, bool verifyFiles,
            IProgress<FileDownloadStatus> downloadProgress = null, int bufferSize = 65_536, CancellationToken cancellationToken = default)
        {
            long currRange = 0;
            long chunk = CHUNK_SIZE;
            long totalBytesRead = 0;
            long hashedBytes = 0;
            var blockBufferSize = bufferSize;

            //These variables are used to decrypt files if esrp is available.
            EsrpDecryptor esrpDecrypter = null; //Implements IDisposable
            byte[] backBuffer = null;
            int backBufferLength = 0;
            long blockCount = 0;
            EsrpDecryptionInformation esrp = null;

            //If esrp is available, setup all required variable
            if (downloadFile.WUFile.EsrpDecryptionInformation != null)
            {
                esrp = downloadFile.WUFile.EsrpDecryptionInformation;
                backBuffer = new byte[blockBufferSize];
                esrpDecrypter = new EsrpDecryptor(esrp);
                blockBufferSize = (int)esrp.EncryptionBufferSize;
            }

            try
            {
                //This path will be used for downloaded and validated files, moved/renamed
                var filePath = Path.Combine(basePath, downloadFile.FileName);

                //This path will be used for downloading files
                var tempFilePath = filePath + TEMP_DOWNLOAD_EXTENSION;

                //If we have an already completed file, prefer this under certain conditions.
                if (File.Exists(tempFilePath) && File.Exists(filePath))
                {
                    var tmpFileInfo = new FileInfo(tempFilePath);
                    var fileInfo = new FileInfo(filePath);
                    if (tmpFileInfo.Length == downloadFile.FileSize)
                        File.Delete(filePath);
                    else if (fileInfo.Length == downloadFile.FileSize)
                        File.Delete(tempFilePath);
                    else
                        File.Delete(filePath);
                }

                if (File.Exists(tempFilePath))
                {
                    var tmpFileInfo = new FileInfo(tempFilePath);

                    if (tmpFileInfo.Length == downloadFile.FileSize)
                    {
                        //Decrypted file should match estimated bytes.
                        //Imagine if it crashed during hashing, the file may be valid.
                        //So... lets rename this file so it can be verified.
                        File.Move(tempFilePath, filePath);
                        totalBytesRead = tmpFileInfo.Length;
                    }
                    else if (downloadFile.WUFile.EsrpDecryptionInformation != null)
                    {
                        //Imagine if it crashed during decryption. We can only take the good part, 
                        //so we need to set the position to the last good block.
                        //This will round the last good block for us (until proven otherwise).
                        currRange = (tmpFileInfo.Length / esrp.EncryptionBufferSize) * esrp.EncryptionBufferSize;
                        totalBytesRead = currRange;

                        if (currRange > downloadFile.FileSize)
                        {
                            //If it's bigger then something with the file must have gone wrong.
                            //Reset currRange and delete the file.
                            currRange = 0;
                            totalBytesRead = 0;
                            File.Delete(tempFilePath);
                        }
                    }
                    else if (tmpFileInfo.Length < downloadFile.FileSize)
                    {
                        //Download the remaining part
                        currRange = tmpFileInfo.Length;
                        totalBytesRead = tmpFileInfo.Length;
                    }
                    else
                    {
                        //If it's bigger then we have a problem.
                        //Just... delete the file.
                        File.Delete(tempFilePath);
                    }
                }

                if (File.Exists(filePath))
                {
                    //If the filename was renamed then the download must have been completed successfully,
                    //we just hash to be sure that the file hasn't been tampered
                    if (verifyFiles)
                    {
                        var fileStreamToHash = File.Open(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                        totalBytesRead = fileStreamToHash.Length;
                        var hashResult = await HashWithProgress(fileStreamToHash).ConfigureAwait(false);
                        await fileStreamToHash.DisposeAsync().ConfigureAwait(false);

                        if (hashResult)
                        {
                            //Nice, the file is ok, return success.
                            return true;
                        }
                        else
                        {
                            //The file is not ok.
                            //It's better to delete this file and redownload from scratch 
                            //instead of partially downloading the missing parts and found later that the entire file was corrupted.
                            File.Delete(filePath);

                            //This range may have been changed before (e.g. when assuming for temp. file), so let's set it to 0
                            currRange = 0;
                            totalBytesRead = 0;
                            hashedBytes = 0;
                        }
                    }
                    else
                    {
                        //At your own risk lol
                        var tmpFileInfo = new FileInfo(tempFilePath);
                        downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                        {
                            DownloadedBytes = tmpFileInfo.Length,
                            FileStatus = FileStatus.Completed
                        });
                        return true;
                    }
                }


                //Before we need to create a directory.
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));

                //Open the file as stream.
                await using var streamToWriteTo = File.Open(tempFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

                //Set the seek position to current range position (via totalBytesRead or currRange). This is needed.
                streamToWriteTo.Seek(totalBytesRead, SeekOrigin.Begin);

                using var httpRequestMessageHead = new HttpRequestMessage(HttpMethod.Head, new Uri(downloadFile.WUFile.DownloadUrl));
                using var response = await httpClient.SendAsync(httpRequestMessageHead, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                //Technically the  server reports both content-length and Accept-Ranges.
                var contentLength = response.Content.Headers.ContentLength;
                var hasAcceptRanges = response.Headers.AcceptRanges.Contains("bytes");

                //This is just a fallback in case the file delivery server goes nuts.
                if (!hasAcceptRanges)
                {
                    //We don't have a way to download from a specific range, hence we set the position to 0
                    //and download everything from scratch... Sadly.
                    //TODO
                    streamToWriteTo.Seek(0, SeekOrigin.Begin);

                    downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                    {
                        DownloadedBytes = 0,
                        FileStatus = FileStatus.Downloading
                    });

                    //TODO: add download reporting for this
                    //TODO: may throw an exception if the server suddently closes the connection (e.g. file expired.)

                    using var fullFileResp = await httpClient.GetAsync(downloadFile.WUFile.DownloadUrl, cancellationToken).ConfigureAwait(false);
                    await using var streamToReadFrom = await fullFileResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                    if (esrpDecrypter != null)
                        await esrpDecrypter.DecryptStreamFullAsync(streamToReadFrom, streamToWriteTo, (ulong)contentLength.Value, cancellationToken).ConfigureAwait(false);
                    else
                        await streamToReadFrom.CopyToAsync(streamToWriteTo, cancellationToken).ConfigureAwait(false);

                    downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                    {
                        DownloadedBytes = contentLength.Value,
                        FileStatus = FileStatus.Downloading
                    });

                    //just an assumption 

                    streamToWriteTo.Seek(0, SeekOrigin.Begin);
                    return await HashWithProgress(streamToWriteTo).ConfigureAwait(false);
                }

                while (currRange < contentLength.Value)
                {
                    //Calculate range values
                    if (currRange + chunk >= contentLength.Value)
                        chunk = contentLength.Value - currRange - 1;

                    //Create request for range and send it, return asap (we just need the header to see if the status code is ok)
                    using var requestMessageRange = CreateRequestHeaderForRange(HttpMethod.Get, downloadFile.WUFile.DownloadUrl, currRange, currRange + chunk);
                    using var filePartResp = await httpClient.SendAsync(requestMessageRange, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                    //increment to the next range
                    currRange += chunk + 1;

                    //If the server has replied with 200 ok/206 partial content
                    if (filePartResp.IsSuccessStatusCode)
                    {
                        //get the underlying stream
                        await using var streamToReadFrom = await filePartResp.Content.ReadAsStreamAsync(cancellationToken);

                        int bytesRead;
                        var buffer = new byte[blockBufferSize];

                        //read the content
                        //TODO: it may throw an exception (stream closed because file expired?)
                        //In that case we would wrap into another try catch and try to read the reason behind this
                        while ((bytesRead = await streamToReadFrom.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            totalBytesRead += bytesRead;

                            //If esrp is not null, we must use a backbuffer
                            //Basically we need exactly (bufferSize) to transform a block for (symmetric crypto algorithm)
                            //This does all the calculation to store in a back buffer because (bytesRead) does NOT always equal to (bufferSize)
                            if (esrpDecrypter != null)
                            {
                                int diff = blockBufferSize - (backBufferLength + bytesRead);
                                diff = Math.Min(diff, 0);

                                Array.Copy(buffer, 0, backBuffer, backBufferLength, bytesRead + diff);
                                backBufferLength += bytesRead + diff;

                                if (!(backBufferLength < blockBufferSize))
                                {
                                    //The last block is always padded.
                                    bool needsPadding = totalBytesRead == contentLength.Value;
                                    long previousSumBlockLength = blockBufferSize * blockCount;
                                    await esrpDecrypter.DecryptBufferToStreamAsync(backBuffer, streamToWriteTo, backBufferLength,
                                                previousSumBlockLength, needsPadding, cancellationToken).ConfigureAwait(false);

                                    //if there is still data in buffer, copy the remaining one into the backbuffer
                                    if (diff < 0)
                                    {
                                        Array.Copy(buffer, bytesRead + diff, backBuffer, 0, -diff);
                                        backBufferLength = -diff;
                                    }
                                    else
                                    {
                                        backBufferLength = 0;
                                    }

                                    //increment the block count, this is used for IV transform for the encryption.
                                    blockCount++;
                                }
                            }
                            else
                            {
                                //simply write to the file
                                await streamToWriteTo.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                            }

                            //report progress
                            downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                            {
                                DownloadedBytes = totalBytesRead,
                                FileStatus = FileStatus.Downloading
                            });
                        }
                    }
                    else
                    {
                        if (filePartResp.StatusCode == HttpStatusCode.Forbidden ||
                            filePartResp.StatusCode == HttpStatusCode.NotFound ||
                            filePartResp.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            //The url is expired.
                            //Report that is expired and return false
                            //We need to keep the file, because it's just incomplete, not corrupted.
                            downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                            {
                                DownloadedBytes = totalBytesRead,
                                FileStatus = FileStatus.Expired
                            });

                            return false;
                        }
                        else
                        {
                            throw new Exception(filePartResp.ReasonPhrase);
                        }
                    }
                }

                //last left block if any

                if (esrpDecrypter != null && backBufferLength > 0)
                    await esrpDecrypter.DecryptBufferToStreamAsync(backBuffer, streamToWriteTo, backBufferLength,
                                            blockBufferSize * blockCount, true, cancellationToken).ConfigureAwait(false);

                if (verifyFiles)
                {
                    streamToWriteTo.Seek(0, SeekOrigin.Begin);
                    var hashResult = await HashWithProgress(streamToWriteTo).ConfigureAwait(false);

                    if (hashResult)
                    {
                        streamToWriteTo.Close();
                        File.Move(tempFilePath, filePath, true);
                    }

                    return hashResult;
                }
                else
                {
                    streamToWriteTo.Close();
                    File.Move(tempFilePath, filePath, true);

                    downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                    {
                        DownloadedBytes = totalBytesRead,
                        FileStatus = FileStatus.Completed
                    });

                    return true;
                }
            }
            catch //(Exception ex)
            {
                downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                {
                    DownloadedBytes = totalBytesRead,
                    FileStatus = FileStatus.Failed
                });

                return false;
            }
            finally
            {
                esrpDecrypter?.Dispose();
            }

            //C# 7 - Local Functions
            async ValueTask<bool> HashWithProgress(Stream strm)
            {
                Progress<long> progressHashedBytes = new();
                progressHashedBytes.ProgressChanged += (s, e) =>
                {
                    hashedBytes = e;
                    downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                    {
                        DownloadedBytes = totalBytesRead,
                        HashedBytes = hashedBytes,
                        FileStatus = FileStatus.Verifying
                    });
                };

                var hashMatches = await IsDownloadedFileValidSHA256(strm, downloadFile.SHA256,
                                                        progressHashedBytes, cancellationToken).ConfigureAwait(false);

                if (hashMatches)
                {
                    downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                    {
                        DownloadedBytes = totalBytesRead,
                        HashedBytes = hashedBytes,
                        FileStatus = FileStatus.Completed
                    });
                    return true;
                }
                else
                {
                    downloadProgress?.Report(new FileDownloadStatus(downloadFile)
                    {
                        DownloadedBytes = totalBytesRead,
                        HashedBytes = hashedBytes,
                        FileStatus = FileStatus.Failed
                    });
                    return false;
                }
            }
        }


        #region Helpers

        private static async ValueTask<bool> IsDownloadedFileValidSHA256(Stream fileStream, string base64Hash, IProgress<long> progress = null, CancellationToken cancellationToken = default)
        {
            using var hashAlgo = SHA256.Create();
            var hashByte = await ComputeHashAsyncT(hashAlgo, fileStream, progress, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ByteArraySpanCompare(Convert.FromBase64String(base64Hash), hashByte);
        }

        public static async ValueTask<byte[]> ComputeHashAsyncT(HashAlgorithm hashAlgorithm, Stream fileStream, IProgress<long> progress = null,
                    int bufferSize = 1_048_576, CancellationToken cancellationToken = default)
        {
            int readBytes;
            long totalBytesRead = 0;
            var bufSizeEffective = Math.Min(bufferSize, fileStream.Length);
            var buffer = new byte[bufSizeEffective];
            using var ms = new MemoryStream(buffer);
            using var cs = new CryptoStream(ms, hashAlgorithm, CryptoStreamMode.Write);
            while ((readBytes = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await cs.WriteAsync(buffer.AsMemory(0, readBytes), cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
                totalBytesRead += readBytes;
                progress?.Report(totalBytesRead);
                cancellationToken.ThrowIfCancellationRequested();
            }
            await cs.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
            return hashAlgorithm.Hash;
        }

        private static bool ByteArraySpanCompare(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
            => a1.SequenceEqual(a2);

        private static void GetFreeSlotIndex<T>(T[] array, out int firstFreeTaskIndex)
        {
            firstFreeTaskIndex = -1;
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] == null)
                {
                    firstFreeTaskIndex = i;
                    break;
                }
            }
        }

        private static HttpRequestMessage CreateRequestHeaderForRange(HttpMethod method, string url, long from, long to)
        {
            var request = new HttpRequestMessage(method, new Uri(url));
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
            return request;
        }

        #endregion

        public void Dispose()
        {
            //Release all resources that have been instanced and used
            _hc.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
