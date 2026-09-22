using Testcontainers.Minio;
using Xunit;

namespace cl2j.FileStorage.S3.IntegrationTests
{
    /// <summary>
    ///     One MinIO container for the whole assembly, standing in for any S3 implementation.
    ///
    ///     <para>
    ///     The provider is the one part of cl2j.FileStorage that cannot be exercised without a
    ///     server. Nothing about "list what is directly under this prefix", "a HEAD on a missing
    ///     key answers 404 with no error code", or "the SDK reads the stream twice unless payload
    ///     signing is off" is visible by reading the code — each of those is a property of the
    ///     service, and the only way to learn it is to ask one.
    ///     </para>
    ///
    ///     <para>
    ///     MinIO rather than Amazon: the contract is the S3 API, the tests must run on a pull
    ///     request with no credentials and no account, and a suite that bills someone gets turned
    ///     off. What it cannot prove is a behaviour where a real implementation differs from MinIO
    ///     — Cloudflare R2's eventual listing, for one.
    ///     </para>
    ///
    ///     <para>
    ///     These fail rather than skip when Docker is unavailable, like cl2j.Database's. A skipped
    ///     test reads as a passing one in a summary line, and the skip becomes permanent.
    ///     </para>
    /// </summary>
    public sealed class MinioFixture : IAsyncLifetime
    {
        //Pinned rather than floating, for the reason the SQL Server fixture gives: a suite whose
        //server version changes underneath it is a suite whose failures cannot be reproduced.
        private readonly MinioContainer container = new MinioBuilder().WithImage("minio/minio:RELEASE.2025-04-22T22-12-26Z").Build();

        public string ServiceUrl { get; private set; } = string.Empty;

        public string AccessKey { get; private set; } = string.Empty;

        public string SecretKey { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            await container.StartAsync();
            ServiceUrl = container.GetConnectionString();
            AccessKey = container.GetAccessKey();
            SecretKey = container.GetSecretKey();
        }

        public async Task DisposeAsync() => await container.DisposeAsync();
    }

    [CollectionDefinition(Name)]
    public sealed class MinioCollection : ICollectionFixture<MinioFixture>
    {
        public const string Name = "minio";
    }
}
