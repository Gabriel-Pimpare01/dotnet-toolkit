using Testcontainers.LocalStack;
using Xunit;

namespace cl2j.FileStorage.S3.IntegrationTests
{
    /// <summary>
    ///     One LocalStack container for the whole assembly, standing in for an S3 service.
    ///
    ///     <para>
    ///     The provider is the one part of cl2j.FileStorage that cannot be exercised without a
    ///     server. Nothing about "a HEAD on a missing key answers 404 with no error code", "listing
    ///     with a delimiter groups keys below a sub-prefix", or "the SDK reads the stream twice
    ///     unless payload signing is off" is visible by reading the code — each is a property of
    ///     the service, and the only way to learn it is to ask one.
    ///     </para>
    ///
    ///     <para>
    ///     LocalStack rather than Amazon: the tests must run on a pull request with no credentials
    ///     and no account, and a suite that bills someone gets turned off. Rather than MinIO, whose
    ///     open-source server was archived and whose image was withdrawn from Docker Hub — a test
    ///     suite cannot depend on an image that is no longer published.
    ///     </para>
    ///
    ///     <para>
    ///     What it cannot prove is a behaviour where a real implementation differs from LocalStack.
    ///     Cloudflare R2's eventually-consistent listing is the example worth naming: a write
    ///     followed immediately by a list may not show it there, and nothing here would catch that.
    ///     </para>
    ///
    ///     <para>
    ///     These fail rather than skip when Docker is unavailable, like cl2j.Database's. A skipped
    ///     test reads as a passing one in a summary line, and the skip becomes permanent.
    ///     </para>
    /// </summary>
    public sealed class LocalStackFixture : IAsyncLifetime
    {
        //Pinned rather than floating, for the reason the SQL Server fixture gives: a suite whose
        //server version changes underneath it is a suite whose failures cannot be reproduced.
        private readonly LocalStackContainer container =
            new LocalStackBuilder().WithImage("localstack/localstack:2026.08.3").Build();

        //LocalStack accepts any credentials; these exist because the SDK requires a pair, not
        //because anything checks them.
        public string AccessKey => "test";

        public string SecretKey => "test";

        public string ServiceUrl { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            await container.StartAsync();
            ServiceUrl = container.GetConnectionString();
        }

        public async Task DisposeAsync() => await container.DisposeAsync();
    }

    [CollectionDefinition(Name)]
    public sealed class LocalStackCollection : ICollectionFixture<LocalStackFixture>
    {
        public const string Name = "localstack";
    }
}
