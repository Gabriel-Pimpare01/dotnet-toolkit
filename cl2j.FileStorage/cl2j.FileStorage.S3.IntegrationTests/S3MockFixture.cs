using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace cl2j.FileStorage.S3.IntegrationTests
{
    /// <summary>
    ///     One S3 service for the whole assembly, in a container.
    ///
    ///     <para>
    ///     The provider is the one part of cl2j.FileStorage that cannot be exercised without a
    ///     server. Nothing about "a HEAD on a missing key answers 404 with no error code", "listing
    ///     with a delimiter groups keys below a sub-prefix", or "the SDK reads the stream twice, so
    ///     a forward-only one has to be buffered" is visible by reading the code — each is a
    ///     property of the service, and the only way to learn it is to ask one.
    ///     </para>
    ///
    ///     <para>
    ///     S3Mock rather than the alternatives, and the alternatives are worth recording because
    ///     each was tried. MinIO: its open-source server was archived and its Docker Hub image
    ///     withdrawn — a suite cannot depend on an image that is no longer published. LocalStack:
    ///     from 4.15 onwards the image refuses to start without LOCALSTACK_AUTH_TOKEN, which a
    ///     contributor's pull request does not have. Amazon itself: the tests must run with no
    ///     credentials and no account, and a suite that bills someone gets turned off.
    ///     </para>
    ///
    ///     <para>
    ///     S3Mock is Apache-licensed, still released, and 85 MB against LocalStack's gigabyte.
    ///     What it cannot prove is a behaviour where a real service differs from it — Cloudflare
    ///     R2's eventually-consistent listing is the example worth naming: a write followed
    ///     immediately by a list may not show it there, and nothing here would catch that.
    ///     </para>
    ///
    ///     <para>
    ///     These fail rather than skip when Docker is unavailable, like cl2j.Database's. A skipped
    ///     test reads as a passing one in a summary line, and the skip becomes permanent.
    ///     </para>
    /// </summary>
    public sealed class S3MockFixture : IAsyncLifetime
    {
        private const ushort Port = 9090;

        //Pinned rather than floating, for the reason the SQL Server fixture gives: a suite whose
        //server version changes underneath it is a suite whose failures cannot be reproduced.
        private readonly IContainer container = new ContainerBuilder()
            .WithImage("adobe/s3mock:5.2.3")
            .WithPortBinding(Port, true)
            //The port opens before the service behind it answers, so waiting on the port alone
            //lets the first test race the startup. Any response below 500 means it is serving.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPath("/")
                .ForPort(Port)
                .ForStatusCodeMatching(code => code < HttpStatusCode.InternalServerError)))
            .Build();

        //S3Mock accepts any credentials; these exist because the SDK requires a pair, not because
        //anything checks them.
        public static string AccessKey => "test";

        public static string SecretKey => "test";

        public string ServiceUrl { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            await container.StartAsync();
            ServiceUrl = $"http://{container.Hostname}:{container.GetMappedPublicPort(Port)}";
        }

        public async Task DisposeAsync() => await container.DisposeAsync();
    }

    [CollectionDefinition(Name)]
    public sealed class S3MockFixtureCollection : ICollectionFixture<S3MockFixture>
    {
        public const string Name = "s3mock";
    }
}
