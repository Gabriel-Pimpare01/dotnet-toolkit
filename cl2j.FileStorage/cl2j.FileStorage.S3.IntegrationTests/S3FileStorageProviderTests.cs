using cl2j.FileStorage.Core;
using cl2j.FileStorage.Extensions;
using cl2j.FileStorage.Provider.S3;
using cl2j.FileStorage.Tests;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace cl2j.FileStorage.S3.IntegrationTests
{
    /// <summary>
    ///     The contract, run against a real S3 implementation.
    ///
    ///     <para>
    ///     A bucket of its own per class, created on initialize: the contract writes at fixed
    ///     names, so sharing one between runs would let a leftover object decide a later result.
    ///     </para>
    /// </summary>
    [Collection(S3MockFixtureCollection.Name)]
    public sealed class S3FileStorageProviderTests : FileStorageProviderContract
    {
        private readonly FileStorageProviderS3 provider;

        public S3FileStorageProviderTests(S3MockFixture s3mock)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["S3:Bucket"] = "contract-" + Guid.NewGuid().ToString("N")[..12],
                    ["S3:AccessKey"] = S3MockFixture.AccessKey,
                    ["S3:SecretKey"] = S3MockFixture.SecretKey,
                    ["S3:ServiceUrl"] = s3mock.ServiceUrl,
                    ["S3:CreateIfMissing"] = "true"
                })
                .Build();

            provider = new FileStorageProviderS3();
            provider.Initialize("S3", configuration.GetSection("S3"));
        }

        protected override IFileStorageProvider Provider => provider;
    }

    /// <summary>
    ///     What the contract does not cover, because it is specific to object storage.
    /// </summary>
    [Collection(S3MockFixtureCollection.Name)]
    public sealed class S3ProviderBehaviourTests(S3MockFixture s3mock)
    {
        private FileStorageProviderS3 Provider(bool createIfMissing = true, string? bucket = null)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["S3:Bucket"] = bucket ?? "behaviour-" + Guid.NewGuid().ToString("N")[..12],
                    ["S3:AccessKey"] = S3MockFixture.AccessKey,
                    ["S3:SecretKey"] = S3MockFixture.SecretKey,
                    ["S3:ServiceUrl"] = s3mock.ServiceUrl,
                    ["S3:CreateIfMissing"] = createIfMissing ? "true" : "false"
                })
                .Build();

            var provider = new FileStorageProviderS3();
            provider.Initialize("S3", configuration.GetSection("S3"));
            return provider;
        }

        // The rule the Azure Blob provider settled on in 5.3.0, held here too: a bucket that is not
        // there is a name spelled wrong far more often than it is a bucket waiting to be made, and
        // creating it puts the data somewhere nobody will look.
        [Fact]
        public void A_missing_bucket_is_refused_rather_than_created()
        {
            var ex = Assert.ThrowsAny<Exception>(() => Provider(createIfMissing: false, bucket: "absent-" + Guid.NewGuid().ToString("N")[..12]));

            Assert.Contains("does not exist", ex.Message);
        }

        // Object storage is flat: a key with slashes in it only looks like a path. Listing has to
        // answer about one level, or every caller walking a store would walk all of it.
        [Fact]
        public async Task Listing_a_prefix_returns_what_sits_directly_under_it()
        {
            var provider = Provider();
            await provider.WriteTextAsync("photos/one.txt", "1");
            await provider.WriteTextAsync("photos/two.txt", "2");
            await provider.WriteTextAsync("photos/deeper/three.txt", "3");
            await provider.WriteTextAsync("elsewhere/four.txt", "4");

            var files = (await provider.ListFilesAsync("photos")).ToList();

            Assert.Equal(["one.txt", "two.txt"], files.Order());
        }

        [Fact]
        public async Task Listing_folders_returns_the_prefixes_below_it()
        {
            var provider = Provider();
            await provider.WriteTextAsync("root/first/a.txt", "a");
            await provider.WriteTextAsync("root/second/b.txt", "b");
            await provider.WriteTextAsync("root/c.txt", "c");

            var folders = (await provider.ListFoldersAsync("root")).ToList();

            Assert.Equal(["first", "second"], folders.Order());
        }

        // S3 has no append; this is read-concatenate-write. Worth a test precisely because the
        // implementation is not the operation the name suggests.
        [Fact]
        public async Task Appending_adds_to_what_is_already_there()
        {
            var provider = Provider();
            await provider.WriteTextAsync("log.txt", "first\n");

            using var addition = new MemoryStream("second\n"u8.ToArray());
            await provider.AppendAsync("log.txt", addition);

            Assert.Equal("first\nsecond\n", await provider.ReadTextAsync("log.txt"));
        }

        [Fact]
        public async Task Appending_to_a_name_that_does_not_exist_creates_it()
        {
            var provider = Provider();

            using var content = new MemoryStream("only\n"u8.ToArray());
            await provider.AppendAsync("new.txt", content);

            Assert.Equal("only\n", await provider.ReadTextAsync("new.txt"));
        }

        // The caller may hand the same stream to two providers. Leaving it rewound is what the
        // Azure Blob provider does, and a caller should not have to know which one moved it.
        [Fact]
        public async Task A_written_stream_is_left_rewound()
        {
            var provider = Provider();
            using var content = new MemoryStream("payload"u8.ToArray());

            await provider.WriteAsync("rewound.txt", content, "text/plain");

            Assert.Equal(0, content.Position);
        }

        [Fact]
        public async Task Asking_about_a_name_that_is_not_there_is_not_an_error()
        {
            var provider = Provider();

            Assert.False(await provider.ExistsAsync("absent.txt"));
            Assert.Null(await provider.GetInfoAsync("absent.txt"));
            Assert.False(await provider.ReadAsync("absent.txt", new MemoryStream()));
        }
    }
}
