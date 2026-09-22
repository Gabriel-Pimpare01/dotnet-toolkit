using cl2j.FileStorage.Core;
using cl2j.Tooling.Exceptions;
using Microsoft.Extensions.Configuration;

namespace cl2j.FileStorage.Provider.Disk
{
    public class FileStorageProviderDisk : IFileStorageProvider
    {
        private DirectoryInfo directory = null!;
        private const int BufferSize = 4096;

        //Suffix of the temporary files WriteAsync creates. It serves twice: to name them, and to
        //keep them out of listings.
        private const string TemporarySuffix = ".tmp";

        public void Initialize(string providerName, IConfigurationSection configuration)
        {
            Name = providerName;

            var settings = new FileStorageProviderDiskConfiguration();
            configuration.Bind(settings);

            if (string.IsNullOrEmpty(settings.Path))
                throw new NotFoundException("FileStorageProviderDisk: Path configuration not defined.");

            if (Directory.Exists(settings.Path))
                directory = new DirectoryInfo(settings.Path);
            else
                directory = Directory.CreateDirectory(settings.Path);
        }

        public string Name { get; set; } = null!;

        public async Task<bool> ExistsAsync(string name)
        {
            var exists = File.Exists(GetName(name));
            await Task.CompletedTask;
            return exists;
        }

        public async Task<FileStoreFileInfo?> GetInfoAsync(string name)
        {
            try
            {
                await Task.CompletedTask;

                var fi = new FileInfo(GetName(name));
                if (!fi.Exists)
                    return null;

                return new FileStoreFileInfo
                {
                    Size = fi.Length,
                    CreatedOn = fi.CreationTimeUtc,
                    LastModified = fi.LastWriteTimeUtc,
                    Created = fi.CreationTime
                };
            }
            catch
            {
                return null;
            }
        }

        public async Task<IEnumerable<string>> ListFilesAsync(string path)
        {
            await Task.CompletedTask;

            var fullName = GetName(path);
            if (!Directory.Exists(fullName))
                return [];

            //WriteAsync temporaries are excluded: they normally live a fraction of a second, but a
            //process killed mid-write can leave one behind. It must never pass for data in the eyes
            //of a caller scanning the folder.
            var list = Directory.GetFiles(fullName).Where(n => !n.EndsWith(TemporarySuffix, StringComparison.Ordinal));

            //Names come from the path itself, not from cutting the prefix off. The cut assumed the
            //root ended without a separator, which `Path.Combine` does not guarantee: a path given
            //as "t/" produced "C:\root\t\", one character longer than the cut expected, and every
            //name lost its first character — "1.jpg" listed as ".jpg". Seen September 4th 2026
            //against a local media folder; production runs on Azure, where the other provider
            //builds its names differently, so nothing ever caught it.
            return list.Select(Path.GetFileName);
        }

        public async Task<IEnumerable<string>> ListFoldersAsync(string path)
        {
            await Task.CompletedTask;

            var fullName = GetName(path);
            if (!Directory.Exists(fullName))
                return [];

            //Same cut, same defect: a folder listed from "t/" lost its first character too.
            //`GetFileName` names a directory as well, as long as the path carries no trailing
            //separator — which `GetDirectories` never returns.
            var list = Directory.GetDirectories(fullName);
            return list.Select(Path.GetFileName);
        }

        public async Task<bool> ReadAsync(string name, Stream stream)
        {
            try
            {
                //FileShare.Delete on top, ever since WriteAsync replaces the target by renaming:
                //on Windows, `MoveFileEx` fails if the file being replaced is open without that
                //share. Without it, a read in progress would block a write that used to go
                //through — truncation, for its part, required nothing.
                var fs = new FileStream(GetName(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                await fs.CopyToAsync(stream);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Atomic write: the target file is either the old one or the new one, never in between.
        ///
        /// **Why, since September 3rd 2026.** The previous implementation opened the target with
        /// `FileMode.Create`, which **truncates it to zero before writing**. A process interrupted
        /// between the two — on Appartogo, a VM deallocated by its Logic App while a crawl cycle
        /// was writing — left an empty or partial file. The Azure storage of the same project does
        /// not have this problem: a blob only becomes visible there once the upload is committed.
        /// Local disk had no such guarantee; it does now.
        ///
        /// The temporary is created **in the same folder** as the target, therefore on the same
        /// volume: that is the condition for `File.Move` to be atomic — `MoveFileEx` with
        /// `MOVEFILE_REPLACE_EXISTING` on Windows, `rename` on Linux.
        ///
        /// `Flush(flushToDisk: true)` pushes the bytes all the way to disk before the rename.
        /// Without it, atomicity would only hold against the death of the process, not against an
        /// abrupt loss of the machine: NTFS journals metadata, not content.
        /// </summary>
        public async Task WriteAsync(string name, Stream stream, string? contentType)
        {
            var fileName = GetName(name);
            CreateDirectory(fileName);

            var temporaryFileName = $"{fileName}.{Guid.NewGuid():N}{TemporarySuffix}";
            try
            {
                using (var outputStream = new FileStream(temporaryFileName, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
                {
                    var bytes = new byte[stream.Length];
                    stream.Seek(0, SeekOrigin.Begin);
                    var actualCount = await stream.ReadAsync(bytes);
                    await outputStream.WriteAsync(bytes.AsMemory(0, actualCount));

                    await outputStream.FlushAsync();
                    outputStream.Flush(flushToDisk: true);
                }

                ReplaceAtomically(temporaryFileName, fileName);
            }
            catch
            {
                //Never leave a temporary behind: it does not carry the expected name, so nobody
                //will ever read it, and it would grow the folder on every failure.
                TryDelete(temporaryFileName);
                throw;
            }
        }

        //Attempts and the wait between them. Five tries spaced 50, 100, 150 and 200 ms cover half
        //a second — well beyond how long an antivirus holds a file open, and short enough not to
        //freeze a loop that writes often.
        private const int TentativesDeRemplacement = 5;
        private const int AttenteEntreTentativesMs = 50;

        /// <summary>
        /// Replaces the target with the temporary, in one gesture.
        ///
        /// `File.Replace` rather than `File.Move(overwrite: true)`: on Windows, Move fails with
        /// "Access to the path is denied" as soon as a reader holds the target open, even in share
        /// mode. `ReplaceFile`, which Replace builds on, is designed for that case. The detail is
        /// not theoretical — a test covers it, and the old truncating implementation did not have
        /// this constraint: losing it would have been a regression.
        ///
        /// **But Replace has a weakness of its own, seen in production on September 4th 2026:**
        /// "Unable to remove the file to be replaced" — Windows error 1175. `ReplaceFile` has to
        /// *delete* the old target, and that fails as long as a third party holds it open without
        /// allowing deletion. An antivirus scanning the file just written is enough, which is why
        /// the failure is intermittent rather than systematic.
        ///
        /// It showed up on `Results/GeocodingAddresses.json`, rewritten in full — 11.6 MB — on
        /// every geocoded address. The bigger the file and the more often it is rewritten, the
        /// wider the window opens.
        ///
        /// So we retry, then fall back to Move as a last resort: the two fail under different
        /// conditions, and what blocks Replace does not necessarily block Move. If both fail, the
        /// exception propagates — losing the write silently would be worse.
        /// </summary>
        private static void ReplaceAtomically(string temporaryFileName, string fileName)
        {
            for (var tentative = 1; ; ++tentative)
            {
                try
                {
                    File.Replace(temporaryFileName, fileName, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (FileNotFoundException)
                {
                    //Replace requires an existing target, Move does not. This is the first write.
                    File.Move(temporaryFileName, fileName, overwrite: true);
                    return;
                }
                catch (IOException) when (tentative < TentativesDeRemplacement)
                {
                    Thread.Sleep(AttenteEntreTentativesMs * tentative);
                }
                catch (IOException)
                {
                    File.Move(temporaryFileName, fileName, overwrite: true);
                    return;
                }
            }
        }

        private static void TryDelete(string fileName)
        {
            try
            {
                if (File.Exists(fileName))
                    File.Delete(fileName);
            }
            catch
            {
                //Nothing more to do: we are already on an error path, and masking the original
                //exception with the cleanup one would be worse.
            }
        }

        public async Task AppendAsync(string name, Stream stream)
        {
            var fileName = GetName(name);
            CreateDirectory(fileName);

            using var outputStream = new FileStream(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, BufferSize, true);
            var bytes = new byte[stream.Length];
            stream.Seek(0, SeekOrigin.Begin);
            var actualCount = await stream.ReadAsync(bytes);
            await outputStream.WriteAsync(bytes.AsMemory(0, actualCount));
        }

        public async Task DeleteAsync(string name)
        {
            await Task.CompletedTask;
            File.Delete(GetName(name));
        }

        #region Private

        private static void CreateDirectory(string fileName)
        {
            var directory = Path.GetDirectoryName(fileName);
            if (directory != null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }

        private string GetName(string key)
        {
            return Path.Combine(directory.FullName, key);
        }

        #endregion Private
    }
}