`cl2j.FileStorage` is a multi-providers .NET library written in C# that abstract file operations like read, write, delete and more. It's an open and extensible framework based on interfaces and Dependency Injection.

Providers supported:

- `FileStorageProviderDisk` provider for Local file system
- `FileStorageProviderAzureBlobStorage` provider for Azure Blob Storage
- `FileStorageProviderS3` provider for S3-compatible object storage (Amazon S3, Cloudflare R2, MinIO)

# Getting started

The setup is simple.

Add the nuget package to your project:

```powershell
Install-Package cl2j.FileStorage
```

1. Add the following lines in the appsettings.json:

```json
"cl2j": {
  "FileStorage": {
    "Storages": {
      "Data": {
        "Type": "Disk",
        "Path": "c:\folder>"
      }
    }
  }
}
```

In the previous example, Data is the name of the FileStorage.
Type is the type of the provider, Disk indicate that you want to use a provider that will use the File.IO operations.
Path is the local path, i.e. c:\dev, where the file operations will be done.

2. Configures the services by calling **AddFileStorage()** and then **UseFileStorageDisk()**
3. Get the IFileProvider configured and perform file operations

IFileStorageProvider works with Streams. The library offer utilities, through extensions methods, to simplify text manipulations.

## Web Application

```cs
public void ConfigureServices(IServiceCollection services)
{
  ...

  //Bootstrap the FileStorage to be available from DependencyInjection.
  //This will allow accessing IFileStorageProviderFactory instance
  services.AddFileStorage();
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
  ...

  //Add the Disk FileStorage provider
  app.ApplicationServices.UseFileStorageDisk();
}
```

## Console Application

```cs
var services = new ServiceCollection();
...

//Bootstrap the FileStorage to be available from DependencyInjection.
//This will allow accessing IFileStorageProviderFactory instance
services.AddFileStorage();

//Add the Disk FileStorage provider
var serviceProvider = services.BuildServiceProvider();
serviceProvider.UseFileStorageDisk();
```

# Sample code

Here's a class that execute common file operations.

The class receive, by Dependency Injection, the IFileStorageProviderFactory. It request the IFileStorageProvider, represented by the name Data, and execute file operations.

```cs
internal class FileOperationSample
{
    private IFileStorageProvider fileStorageProvider;

    public FileOperationExecutor(IFileStorageProviderFactory fileStorageFactory)
    {
      //Retreive the FileStorage provider
      fileStorageProvider = fileStorageFactory.Get("Data");
    }

    public async Task ExecuteAsync()
    {
      //Create a file with the specified text
      await fileStorageProvider.WriteTextAsync("file1.txt", "First part of the text");

      //Read the file content
      var text = await fileStorageProvider.ReadTextAsync("file1.txt");

      //Append text to the existing file
      await fileStorageProvider.AppendTextAsync("file1.txt", "Second part of the text");

      //Delete the file
      await fileStorageProvider.DeleteAsync("file1.txt");
    }
}
```

# Operations

## Get the File Storage Provider

To perform file operations, you need to get the IFileStorageProviderFactory and request the provider by passing it's name like:

```cs
var fileStorageProviderFactory = serviceProvider.GetRequiredService<IFileStorageProviderFactory>();
var fileStorageProvider = fileStorageProviderFactory.Get("Data");
```

Here's the operations available from the `IFileStorageProvider` interface:

```cs
public interface IFileStorageProvider
{
  // Write content
  Task WriteAsync(string name, Stream stream);

  // Read content
  Task<bool> ReadAsync(string name, Stream stream);

  // Append content
  Task AppendAsync(string name, Stream stream);

  // Validate if a file exists
  Task<bool> ExistsAsync(string name);

  // List files of directory
  Task<IEnumerable<string>> ListAsync(string path);

  // Get file information
  Task<FileStoreFileInfo> GetInfoAsync(string name);

  // Delete file
  Task DeleteAsync(string name);
}
```

# Azure Blob Storage

To use a Azure Blob Storage in youf project, add the following package:

```powershell
Install-Package cl2j.FileStorage.Provider.AzureBlobStorage
```

In the application configuration, call `UseFileStorageAzureBlobStorage()` like this:

```cs
serviceProvider.UseFileStorageAzureBlobStorage();
```

Add an entry in the appsetttings.json as follow:

```json
"cl2j": {
  "FileStorage": {
    "Storages": {
      "Azure": {
        "Type": "AzureBlobStorage",
        "ConnectionString": "<ConnectionStringToYourStorage>",
        "Container": "<NameOfYourContainer>"
      }
    }
  }
}
```

# S3-compatible object storage

Works with Amazon S3 and with anything that speaks the same API — Cloudflare R2, MinIO, Backblaze B2.

```powershell
Install-Package cl2j.FileStorage.Provider.S3
```

```cs
services.AddS3FileStorage();
```

For Amazon S3, name the region:

```json
"cl2j": {
  "FileStorage": {
    "Storages": {
      "Medias": {
        "Type": "S3",
        "Bucket": "<YourBucket>",
        "AccessKey": "<YourAccessKey>",
        "SecretKey": "<YourSecretKey>",
        "Region": "us-east-1"
      }
    }
  }
}
```

For every other implementation, give the endpoint instead of the region:

```json
"ServiceUrl": "https://<AccountId>.r2.cloudflarestorage.com"
```

| Setting | Default | |
| --- | --- | --- |
| `Bucket` | — | Required. |
| `AccessKey`, `SecretKey` | — | Both required. |
| `ServiceUrl` | — | The endpoint, for an implementation that is not Amazon's. |
| `Region` | — | The AWS region. Ignored when `ServiceUrl` is set. |
| `ForcePathStyle` | `true` | Addresses objects as `endpoint/bucket/key`. Required by MinIO and most self-hosted implementations; accepted by R2. |
| `CreateIfMissing` | `false` | Creates the bucket when it is not there. Off by default: a missing bucket is nearly always a name spelled wrong, and creating it hides the mistake behind data written where no one will look. |

## Two things to know before choosing it

**`AppendAsync` reads, concatenates, and writes the whole object back**, because S3 has no append.
The cost of appending therefore grows with the size of what is already there, and two overlapping
appends leave only one of them with no error anywhere. Azure Blob has a real append block and does
not have either problem — worth knowing when deciding where a log goes.

**Listings are grouped on `/`**, so `ListFilesAsync` returns what sits directly under a prefix
rather than everything beneath it, the same way the other providers behave. Object storage itself
is flat: a key containing slashes only looks like a path.

# Feedback & Community

We look forward to hearing your comments.
Feel free to submit your opinion, any problems you have encountered as well as ideas for improvements, we would love to hear it.

If you have a technical question or issue, please either:

- Submit an issue
- Ask a question on StackOverflow
- Contact us directly

# Roadmap

We expect to add `Google Cloud Storage` in the coming months.

We will also like to add:

- `FTP` FileStorageProvider
- Copy and move utilities to simplify the operations
