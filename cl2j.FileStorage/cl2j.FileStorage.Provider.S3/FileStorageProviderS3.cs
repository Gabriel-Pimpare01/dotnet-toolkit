using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using cl2j.FileStorage.Core;
using cl2j.Tooling.Exceptions;
using Microsoft.Extensions.Configuration;

namespace cl2j.FileStorage.Provider.S3
{
    /// <summary>
    ///     Stores files as objects in an S3-compatible bucket — Amazon S3, Cloudflare R2, MinIO,
    ///     and anything else that speaks the same API.
    ///
    ///     <para>
    ///     Object storage has no directories: a key containing slashes only looks like a path. The
    ///     listing operations reproduce the hierarchy the interface expects by asking S3 to group
    ///     on the delimiter, so <see cref="ListFilesAsync"/> returns what sits directly at a prefix
    ///     rather than everything beneath it.
    ///     </para>
    /// </summary>
    public class FileStorageProviderS3 : IFileStorageProvider
    {
        private const string Delimiter = "/";

        private IAmazonS3? client;
        private string bucket = null!;

        public string Name { get; set; } = null!;

        public void Initialize(string providerName, IConfigurationSection configuration)
        {
            Name = providerName;

            var settings = new FileStorageProviderS3Configuration();
            configuration.Bind(settings);

            if (string.IsNullOrEmpty(settings.Bucket))
                throw new NotFoundException("FileStorageProviderS3: Bucket configuration not defined.");
            if (string.IsNullOrEmpty(settings.AccessKey) || string.IsNullOrEmpty(settings.SecretKey))
                throw new NotFoundException($"FileStorageProviderS3 '{providerName}': AccessKey and SecretKey are both required.");

            var config = new AmazonS3Config { ForcePathStyle = settings.ForcePathStyle };
            if (!string.IsNullOrEmpty(settings.ServiceUrl))
                config.ServiceURL = settings.ServiceUrl;
            else if (!string.IsNullOrEmpty(settings.Region))
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);

            var s3 = new AmazonS3Client(new BasicAWSCredentials(settings.AccessKey, settings.SecretKey), config);

            // A bucket that is not there is a configuration error until someone says otherwise —
            // the same rule the Azure Blob provider settled on, and for the same reason: a
            // misspelled name that creates its own bucket writes everything where nobody will look
            // for it, and nothing anywhere says so.
            if (settings.CreateIfMissing)
            {
                if (!BucketExists(s3, settings.Bucket))
                    s3.PutBucketAsync(new PutBucketRequest { BucketName = settings.Bucket }).GetAwaiter().GetResult();
            }
            else if (!BucketExists(s3, settings.Bucket))
            {
                throw new NotFoundException(
                    $"FileStorageProviderS3 '{providerName}': bucket '{settings.Bucket}' does not exist. "
                    + "Create it, or set CreateIfMissing to true for this provider.");
            }

            bucket = settings.Bucket;
            client = s3;
        }

        public async Task<bool> ExistsAsync(string name)
        {
            return await GetMetadataAsync(name) != null;
        }

        public async Task<FileStoreFileInfo?> GetInfoAsync(string name)
        {
            var metadata = await GetMetadataAsync(name);
            if (metadata == null)
                return null;

            // S3 keeps one timestamp per object. An overwrite replaces the object rather than
            // editing it, so the moment it was last written is also the moment this version came
            // into being: reporting it as both is accurate, not a stand-in for something missing.
            var lastModified = metadata.LastModified ?? DateTime.UtcNow;
            return new FileStoreFileInfo
            {
                Size = metadata.ContentLength,
                CreatedOn = lastModified,
                Created = lastModified,
                LastModified = lastModified
            };
        }

        public async Task<IEnumerable<string>> ListFilesAsync(string path)
        {
            var prefix = Prefix(path);
            var names = new List<string>();

            await EachPage(prefix, page =>
            {
                foreach (var entry in page.S3Objects)
                {
                    // The prefix itself comes back as an object when something created it as an
                    // explicit empty marker. It is not a file in the directory it names.
                    if (entry.Key.Length <= prefix.Length)
                        continue;

                    names.Add(entry.Key[prefix.Length..]);
                }
            });

            return names;
        }

        public async Task<IEnumerable<string>> ListFoldersAsync(string path)
        {
            var prefix = Prefix(path);
            var names = new HashSet<string>();

            await EachPage(prefix, page =>
            {
                foreach (var common in page.CommonPrefixes)
                {
                    var name = common[prefix.Length..].TrimEnd('/');
                    if (name.Length > 0)
                        names.Add(name);
                }
            });

            return names;
        }

        public async Task<bool> ReadAsync(string name, Stream stream)
        {
            try
            {
                using var response = await Client.GetObjectAsync(bucket, name);
                await response.ResponseStream.CopyToAsync(stream);
                return true;
            }
            catch (AmazonS3Exception ex) when (IsMissing(ex))
            {
                return false;
            }
        }

        public async Task WriteAsync(string name, Stream stream, string? contentType = null)
        {
            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = name,
                InputStream = stream,

                // The SDK computes a checksum by reading the stream twice, which a forward-only
                // stream cannot serve. Disabling it costs the integrity check on the wire, which
                // TLS already covers, and buys the ability to write whatever the caller hands over.
                DisablePayloadSigning = true
            };
            if (contentType != null)
                request.ContentType = contentType;

            await Client.PutObjectAsync(request);

            // Rewound for the caller, the same way the Azure Blob provider leaves it: a caller that
            // writes the same stream to two providers should not have to know which one moved it.
            if (stream.CanSeek)
                stream.Seek(0, SeekOrigin.Begin);
        }

        /// <summary>
        ///     Appends by reading what is there, adding to it, and writing the whole object back.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///     S3 has no append. Every implementation of it on object storage is this, and it
        ///     carries two costs the caller has to know about rather than discover.
        ///     </para>
        ///     <para>
        ///     It transfers the whole object each time, so the cost of appending grows with the
        ///     size of what is already there — a log file appended to all day is re-uploaded in
        ///     full on every line. Use one object per period rather than one that grows.
        ///     </para>
        ///     <para>
        ///     And it is last-writer-wins: two appends that overlap leave only one of them, with no
        ///     error anywhere. Azure Blob has a real append block and does not have this problem,
        ///     which is worth knowing when choosing where a log goes.
        ///     </para>
        /// </remarks>
        public async Task AppendAsync(string name, Stream stream)
        {
            using var combined = new MemoryStream();

            using (var existing = new MemoryStream())
            {
                if (await ReadAsync(name, existing))
                {
                    existing.Seek(0, SeekOrigin.Begin);
                    await existing.CopyToAsync(combined);
                }
            }

            await stream.CopyToAsync(combined);
            combined.Seek(0, SeekOrigin.Begin);

            await WriteAsync(name, combined);
        }

        public async Task DeleteAsync(string name)
        {
            await Client.DeleteObjectAsync(bucket, name);
        }

        #region Private

        private IAmazonS3 Client => client ?? throw new BadRequestException("FileStorageProviderS3: the provider was not initialized.");

        /// <summary>The key prefix a path names, always ending in the delimiter, empty for the root.</summary>
        private static string Prefix(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return path.EndsWith(Delimiter) ? path : path + Delimiter;
        }

        private async Task EachPage(string prefix, Action<ListObjectsV2Response> handle)
        {
            var request = new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,

                // Grouping on the delimiter is what makes a flat namespace answer questions about
                // one level of it: keys below a sub-prefix come back as that prefix instead of one
                // entry each. Without it, listing a bucket's root would walk every object in it.
                Delimiter = Delimiter
            };

            ListObjectsV2Response response;
            do
            {
                response = await Client.ListObjectsV2Async(request);
                handle(response);
                request.ContinuationToken = response.NextContinuationToken;
            }
            while (response.IsTruncated == true);
        }

        private async Task<GetObjectMetadataResponse?> GetMetadataAsync(string name)
        {
            try
            {
                return await Client.GetObjectMetadataAsync(bucket, name);
            }
            catch (AmazonS3Exception ex) when (IsMissing(ex))
            {
                return null;
            }
        }

        // A HEAD on an absent key answers 404 with no error code, where a GET names NoSuchKey.
        private static bool IsMissing(AmazonS3Exception ex) =>
            ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey" || ex.ErrorCode == "NotFound";

        private static bool BucketExists(IAmazonS3 s3, string name)
        {
            try
            {
                s3.GetBucketLocationAsync(name).GetAwaiter().GetResult();
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket" || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        #endregion Private
    }
}
