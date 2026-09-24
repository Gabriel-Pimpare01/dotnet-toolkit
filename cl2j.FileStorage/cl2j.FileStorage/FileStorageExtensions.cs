using cl2j.FileStorage.Core;
using cl2j.FileStorage.Provider.Disk;
using cl2j.Tooling.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace cl2j.FileStorage
{
    public static class FileStorageExtensions
    {
        /// <summary>Registers the file storage factory, and the providers a caller adds to it.</summary>
        /// <remarks>
        ///     Every provider package calls this with its own callback, so an application wanting
        ///     two of them calls it twice. The callbacks are collected rather than passed straight
        ///     through: the factory itself is registered with TryAdd so that there is only ever one,
        ///     which means a callback handed to a later call would otherwise be dropped — silently,
        ///     and only noticed at the first GetProvider for the type that went missing.
        /// </remarks>
        public static void AddFileStorage(this IServiceCollection services, Action<FileStorageFactory>? factoryCallback = null)
        {
            if (factoryCallback is not null)
                services.AddSingleton(new FileStorageProviderRegistration(factoryCallback));

            services.TryAddSingleton<IFileStorageFactory>(x =>
            {
                var configuration = x.GetRequiredService<IConfigurationRoot>();
                var fileStorageFactory = new FileStorageFactory(configuration);

                fileStorageFactory.Register<FileStorageProviderDisk>("Disk");

                foreach (var registration in x.GetServices<FileStorageProviderRegistration>())
                    registration.Apply(fileStorageFactory);

                return fileStorageFactory;
            });
        }

        /// <summary>One caller's contribution to the factory, kept so that later ones do not replace it.</summary>
        private sealed class FileStorageProviderRegistration(Action<FileStorageFactory> register)
        {
            public void Apply(FileStorageFactory factory) => register(factory);
        }

        public static IFileStorageProvider GetFileStorageProvider(this IServiceProvider builder, string datastoreName)
        {
            var fileStorageFactory = builder.GetRequiredService<IFileStorageFactory>();
            var fileStorageProvider = fileStorageFactory.GetProvider(datastoreName) ?? throw new NotFoundException($"DataStore '{datastoreName}' not found");
            return fileStorageProvider;
        }
    }
}