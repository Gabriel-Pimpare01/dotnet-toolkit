namespace cl2j.FileStorage.Provider.S3
{
    public class FileStorageProviderS3Configuration
    {
        /// <summary>The bucket every name in this provider is resolved against.</summary>
        public string Bucket { get; set; } = null!;

        public string AccessKey { get; set; } = null!;

        public string SecretKey { get; set; } = null!;

        /// <summary>
        /// The endpoint to talk to, for an S3 implementation that is not Amazon's — Cloudflare R2,
        /// MinIO, Backblaze B2. Leave it empty for Amazon S3 and set <see cref="Region"/> instead.
        /// </summary>
        public string? ServiceUrl { get; set; }

        /// <summary>The AWS region, for Amazon S3. Ignored when <see cref="ServiceUrl"/> is set.</summary>
        public string? Region { get; set; }

        /// <summary>
        /// Address objects as <c>endpoint/bucket/key</c> rather than <c>bucket.endpoint/key</c>.
        /// Required by MinIO and by most self-hosted implementations; harmless on R2, which accepts
        /// both. Defaults to true because a custom <see cref="ServiceUrl"/> is nearly always one of
        /// those, and the virtual-host form fails there in a way that reads as a DNS error rather
        /// than a configuration one.
        /// </summary>
        public bool ForcePathStyle { get; set; } = true;

        /// <summary>
        /// Create the bucket when it is not there. Off by default, for the reason the Azure Blob
        /// provider gives: a bucket that is missing is nearly always a name spelled wrong, and
        /// creating it hides the mistake behind data written where no one will look for it.
        ///
        /// Turn it on to bootstrap a new environment, or to run against a throwaway account.
        /// </summary>
        public bool CreateIfMissing { get; set; }
    }
}
