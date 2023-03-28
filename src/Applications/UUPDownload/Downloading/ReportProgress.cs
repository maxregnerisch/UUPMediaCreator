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
using DownloadLib;
using System;
using System.Collections.Generic;
using System.Threading;

namespace UUPDownload.Downloading
{
    public class ConsoleProgress : IProgress<DownloadStatus>, IDisposable

    {
        private readonly Mutex mutex = new Mutex();
       
        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }

        public void Report(DownloadStatus status)
        {
            mutex.WaitOne();
                Console.WriteLine($"{DateTime.Now:'['HH':'mm':'ss']'}[{e.NumFilesDownloadedSuccessfully}/{e.NumFiles}][E:{e.NumFilesDownloadedUnsuccessfully}][{msg}] {status.File.FileName} ({FormatBytes(status.File.FileSize)})");
            }

            mutex.ReleaseMutex();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
