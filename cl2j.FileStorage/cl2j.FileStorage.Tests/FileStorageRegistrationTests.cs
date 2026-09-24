using cl2j.FileStorage.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace cl2j.FileStorage.Tests
{
    /// <summary>
    ///     What happens when an application wants two provider types at once.
    /// </summary>
    /// <remarks>
    ///     Each provider package registers itself by calling AddFileStorage with its own callback,
    ///     so an application storing in two places calls it twice. The factory is registered with
    ///     TryAdd — there must only be one — which used to mean the second callback was thrown away
    ///     without a word. Nothing failed at startup; the provider simply was not there, and the
    ///     first read of it threw, in production, at the first file.
    /// </remarks>
    public sealed class FileStorageRegistrationTests
    {
        private sealed class ProviderOne : StubProvider { }

        private sealed class ProviderTwo : StubProvider { }

        [Fact]
        public void Two_providers_registered_separately_are_both_there()
        {
            var services = Build();
            services.AddFileStorage(f => f.Register<ProviderOne>("One"));
            services.AddFileStorage(f => f.Register<ProviderTwo>("Two"));

            var factory = services.BuildServiceProvider().GetRequiredService<IFileStorageFactory>();

            Assert.IsType<ProviderOne>(factory.GetProvider("first"));
            Assert.IsType<ProviderTwo>(factory.GetProvider("second"));
        }

        [Fact]
        public void The_disk_provider_is_still_there_when_another_is_added()
        {
            var services = Build();
            services.AddFileStorage(f => f.Register<ProviderOne>("One"));

            var factory = services.BuildServiceProvider().GetRequiredService<IFileStorageFactory>();

            Assert.NotNull(factory.GetProvider("onDisk"));
        }

        private static ServiceCollection Build()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["cl2j:FileStorage:Storages:first:Type"] = "One",
                ["cl2j:FileStorage:Storages:second:Type"] = "Two",
                ["cl2j:FileStorage:Storages:onDisk:Type"] = "Disk",
                ["cl2j:FileStorage:Storages:onDisk:Path"] = Path.GetTempPath()
            }).Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationRoot>(configuration);
            services.AddSingleton<IConfiguration>(configuration);
            return services;
        }

        private abstract class StubProvider : IFileStorageProvider
        {
            public string Name { get; set; } = null!;

            public void Initialize(string providerName, IConfigurationSection configuration) => Name = providerName;

            public Task<bool> ExistsAsync(string name) => Task.FromResult(false);

            public Task<FileStoreFileInfo?> GetInfoAsync(string name) => Task.FromResult<FileStoreFileInfo?>(null);

            public Task<IEnumerable<string>> ListFilesAsync(string path) => Task.FromResult(Enumerable.Empty<string>());

            public Task<IEnumerable<string>> ListFoldersAsync(string path) => Task.FromResult(Enumerable.Empty<string>());

            public Task<bool> ReadAsync(string name, Stream stream) => Task.FromResult(false);

            public Task WriteAsync(string name, Stream stream, string? contentType = null) => Task.CompletedTask;

            public Task AppendAsync(string name, Stream stream) => Task.CompletedTask;

            public Task DeleteAsync(string name) => Task.CompletedTask;
        }
    }
}
