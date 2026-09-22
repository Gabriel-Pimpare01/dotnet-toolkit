using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using cl2j.FileStorage.Core;
using cl2j.Tooling.Exceptions;
using Microsoft.Extensions.Configuration;

namespace cl2j.FileStorage.Provider.AzureBlobStorage
{
    public class FileStorageProviderAzureBlobStorage : IFileStorageProvider
    {
        private BlobContainerClient? container;

        public void Initialize(string providerName, IConfigurationSection configuration)
        {
            Name = providerName;

            var settings = new FileStorageProviderAzureBlobStorageConfiguration();
            configuration.Bind(settings);

            if (string.IsNullOrEmpty(settings.Container))
                throw new NotFoundException("FileStorageProviderAzureBlobStorage: Container configuration not defined.");

            var client = new BlobContainerClient(settings.ConnectionString, settings.Container);

            //A container that is not there is a configuration error until someone says otherwise.
            //
            //This used to call `CreateIfNotExists`, so a misspelled name created a container of
            //that name and everything written went there: nothing failed, nothing said anything,
            //and the data sat where no one would look for it. Verified September 4th 2026 — a
            //write to "conteneur-qui-nexiste-pas" succeeded, container and all.
            //
            //`CreateIfMissing` keeps the old behaviour for whoever wants it — bootstrapping a new
            //environment, a test run against a throwaway account — but it now has to be asked for.
            if (settings.CreateIfMissing)
                client.CreateIfNotExists();
            else if (!client.Exists())
                throw new NotFoundException(
                    $"FileStorageProviderAzureBlobStorage '{providerName}': container '{settings.Container}' does not exist. "
                    + "Create it, or set CreateIfMissing to true for this provider.");

            container = client;
        }

        public string Name { get; set; } = null!;

        public async Task<bool> ExistsAsync(string name)
        {
            try
            {
                return await GetBlob(name).ExistsAsync();
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
            {
                return false;
            }
        }

        public async Task<FileStoreFileInfo?> GetInfoAsync(string name)
        {
            var prop = await GetBlobPropertiesAsync(name);
            if (prop == null)
                return null;

            return new FileStoreFileInfo
            {
                Size = prop.ContentLength,
                CreatedOn = prop.CreatedOn.DateTime,
                LastModified = prop.LastModified.DateTime,
                Created = prop.CreatedOn.DateTime
            };
        }

        public async Task<IEnumerable<string>> ListFilesAsync(string path)
        {
            if (container == null)
                throw new BadRequestException("Container not defined");

            var list = new List<string>();

            var blobs = container.GetBlobs(prefix: path);
            if (path != null && path.Length > 0)
            {
                foreach (var blob in blobs)
                {
                    var name = blob.Name;
                    if (name.StartsWith(path))
                    {
                        name = name[(path.Length)..];
                        if (name.StartsWith('/'))
                            name = name[1..];
                        if (name.IndexOf('/') < 0)
                            list.Add(name);
                    }
                }
            }
            else
            {
                foreach (var blob in blobs)
                {
                    var name = blob.Name;
                    if (name.IndexOf('/') < 0)
                        list.Add(name);
                }
            }

            await Task.CompletedTask;

            return list;
        }

        public async Task<IEnumerable<string>> ListFoldersAsync(string path)
        {
            if (container == null)
                throw new BadRequestException("Container not defined");

            var list = new HashSet<string>();

            var blobs = container.GetBlobs();
            foreach (var blob in blobs)
            {
                var name = blob.Name;
                if (name.StartsWith(path))
                {
                    name = name[(path.Length + 1)..];

                    var n = name.IndexOf('/');
                    if (n > 0)
                    {
                        name = name[..n];
                        list.Add(name);
                    }
                }
            }

            await Task.CompletedTask;

            return list;
        }

        public async Task<bool> ReadAsync(string name, Stream stream)
        {
            try
            {
                await GetBlob(name).DownloadToAsync(stream);
                return true;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
            {
                return false;
            }
        }

        public async Task WriteAsync(string name, Stream stream, string? contentType)
        {
            if (container == null)
                throw new BadRequestException("Container not defined");

            var blobClient = container.GetBlobClient(name);
            var blobHttpHeader = new BlobHttpHeaders();
            if (contentType != null)
                blobHttpHeader.ContentType = contentType;

            await blobClient.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = blobHttpHeader, Conditions = null });
            stream.Seek(0, SeekOrigin.Begin);
        }

        public async Task AppendAsync(string name, Stream stream)
        {
            var appendBlobClient = container.GetAppendBlobClient(name);

            try
            {
                await appendBlobClient.AppendBlockAsync(stream);
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
            {
                await appendBlobClient.CreateIfNotExistsAsync();

                stream.Seek(0, SeekOrigin.Begin);
                await appendBlobClient.AppendBlockAsync(stream);
            }
        }

        public async Task DeleteAsync(string name)
        {
            await GetBlob(name).DeleteAsync();
        }

        #region Private

        private BlobClient GetBlob(string name)
        {
            if (container == null)
                throw new BadRequestException("Container not defined");

            return container.GetBlobClient(name);
        }

        public async Task<BlobProperties?> GetBlobPropertiesAsync(string name)
        {
            var blob = GetBlob(name);
            if (!await blob.ExistsAsync())
                return null;

            return (await blob.GetPropertiesAsync()).Value;
        }

        #endregion Private
    }
}