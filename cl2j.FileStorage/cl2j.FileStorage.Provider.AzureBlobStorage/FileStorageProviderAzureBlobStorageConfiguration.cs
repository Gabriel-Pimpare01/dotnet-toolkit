using cl2j.FileStorage.Core;

namespace cl2j.FileStorage.Provider.AzureBlobStorage
{
    public class FileStorageProviderAzureBlobStorageConfiguration : FileStorageConfiguration
    {
        public string Container { get; set; } = null!;
        public string ConnectionString { get; set; } = null!;

        /// <summary>
        /// Create the container when it is not there. Off by default: a container that is missing
        /// is nearly always a name spelled wrong, and creating it hides the mistake behind data
        /// written where no one will look for it.
        ///
        /// Turn it on to bootstrap a new environment, or to run against a throwaway account.
        /// </summary>
        public bool CreateIfMissing { get; set; }
    }
}