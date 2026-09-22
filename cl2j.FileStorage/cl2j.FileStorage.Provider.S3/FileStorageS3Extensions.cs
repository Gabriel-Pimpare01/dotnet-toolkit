using Microsoft.Extensions.DependencyInjection;

namespace cl2j.FileStorage.Provider.S3
{
    public static class FileStorageS3Extensions
    {
        public static void AddS3FileStorage(this IServiceCollection services)
        {
            services.AddFileStorage(factory => factory.Register<FileStorageProviderS3>("S3"));
        }
    }
}
