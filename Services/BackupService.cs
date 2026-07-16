using Azure.Data.Tables;
using Azure.Storage.Blobs;
using CsvHelper;
using System.Globalization;
using System.IO.Compression;

namespace CurriculumPortal;

public class BackupService
{
  private readonly BlobServiceClient _blobServiceClient;
  private readonly TableServiceClient _tableServiceClient;

  public BackupService(BlobServiceClient blobServiceClient, TableServiceClient tableServiceClient)
  {
    ArgumentNullException.ThrowIfNull(blobServiceClient);
    ArgumentNullException.ThrowIfNull(tableServiceClient);
    _blobServiceClient = blobServiceClient;
    _tableServiceClient = tableServiceClient;
  }

  public async Task<(FileStream Stream, string FileName)> CreateBackupAsync(CancellationToken cancellationToken = default)
  {
    var fileName = $"curriculumportal-{DateTime.UtcNow:yyyy-MM-dd}.zip";
    var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    FileStream stream = null;

    try
    {
      stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
      using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
      {
        await AddBlobsAsync(archive, cancellationToken);
        await AddTablesAsync(archive, cancellationToken);
      }

      stream.Position = 0;
      return (stream, fileName);
    }
    catch
    {
      if (stream is not null)
      {
        await stream.DisposeAsync();
      }

      throw;
    }
  }

  private async Task AddBlobsAsync(ZipArchive archive, CancellationToken cancellationToken)
  {
    await foreach (var container in _blobServiceClient.GetBlobContainersAsync(cancellationToken: cancellationToken))
    {
      var containerClient = _blobServiceClient.GetBlobContainerClient(container.Name);
      await foreach (var blob in containerClient.GetBlobsAsync(cancellationToken: cancellationToken))
      {
        if (container.Name == "config" && blob.Name is "serviceaccount.json" or "keys.xml") continue;

        var entry = archive.CreateEntry($"blobs/{container.Name}/{Uri.EscapeDataString(blob.Name)}", CompressionLevel.SmallestSize);
        await using var entryStream = await entry.OpenAsync(cancellationToken);
        await containerClient.GetBlobClient(blob.Name).DownloadToAsync(entryStream, cancellationToken);
      }
    }
  }

  private async Task AddTablesAsync(ZipArchive archive, CancellationToken cancellationToken)
  {
    await foreach (var table in _tableServiceClient.QueryAsync(cancellationToken: cancellationToken))
    {
      var entry = archive.CreateEntry($"tables/{table.Name}.csv", CompressionLevel.SmallestSize);
      await using var entryStream = await entry.OpenAsync(cancellationToken);
      await using var writer = new StreamWriter(entryStream);
      await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
      var propertyNames = new SortedSet<string>(StringComparer.Ordinal);
      var propertyTypes = new Dictionary<string, string>(StringComparer.Ordinal);
      var tableClient = _tableServiceClient.GetTableClient(table.Name);

      await foreach (var entity in tableClient.QueryAsync<TableEntity>(cancellationToken: cancellationToken))
      {
        foreach (var property in entity.Keys.Where(o => o is not "PartitionKey" and not "RowKey" and not "Timestamp"))
        {
          propertyNames.Add(property);
          if (!propertyTypes.ContainsKey(property) && entity[property] is not null)
          {
            propertyTypes[property] = GetTypeName(entity[property]);
          }
        }
      }

      WriteHeader(csv, propertyNames);
      await csv.NextRecordAsync();

      var isFirstEntity = true;
      await foreach (var entity in tableClient.QueryAsync<TableEntity>(cancellationToken: cancellationToken))
      {
        WriteEntity(csv, entity, propertyNames, propertyTypes, isFirstEntity);
        await csv.NextRecordAsync();
        isFirstEntity = false;
      }
    }
  }

  private static void WriteHeader(CsvWriter csv, IEnumerable<string> propertyNames)
  {
    csv.WriteField("PartitionKey");
    csv.WriteField("RowKey");
    csv.WriteField("Timestamp");
    foreach (var propertyName in propertyNames)
    {
      csv.WriteField(propertyName);
      csv.WriteField($"{propertyName}@type");
    }
  }

  private static void WriteEntity(CsvWriter csv, TableEntity entity, IEnumerable<string> propertyNames, Dictionary<string, string> propertyTypes, bool includeTypes)
  {
    csv.WriteField(entity.PartitionKey);
    csv.WriteField(entity.RowKey);
    csv.WriteField(FormatValue(entity.Timestamp));
    foreach (var propertyName in propertyNames)
    {
      entity.TryGetValue(propertyName, out var value);
      csv.WriteField(FormatValue(value));
      csv.WriteField(includeTypes && propertyTypes.TryGetValue(propertyName, out var propertyType) ? propertyType : string.Empty);
    }
  }

  private static string FormatValue(object value)
  {
    return value switch
    {
      null => string.Empty,
      string text => text,
      bool flag => flag.ToString().ToLowerInvariant(),
      int number => number.ToString(CultureInfo.InvariantCulture),
      long number => number.ToString(CultureInfo.InvariantCulture),
      double number => number.ToString(CultureInfo.InvariantCulture),
      Guid guid => guid.ToString("D"),
      DateTimeOffset date => date.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
      DateTime date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
      byte[] bytes => Convert.ToBase64String(bytes),
      BinaryData data => Convert.ToBase64String(data.ToArray()),
      _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
  }

  private static string GetTypeName(object value)
  {
    return value switch
    {
      byte[] => "Edm.Binary",
      BinaryData => "Edm.Binary",
      bool => "Edm.Boolean",
      DateTime => "Edm.DateTime",
      DateTimeOffset => "Edm.DateTime",
      double => "Edm.Double",
      Guid => "Edm.Guid",
      int => "Edm.Int32",
      long => "Edm.Int64",
      _ => string.Empty
    };
  }
}
