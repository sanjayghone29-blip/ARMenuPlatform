using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace ARMenuPlatform.Helpers
{
    /// <summary>
    /// Handles file uploads to Azure Blob Storage.
    /// Reads the connection string from appsettings.json key "AzureStorageConnection".
    ///
    /// NuGet required: Azure.Storage.Blobs
    ///   Install-Package Azure.Storage.Blobs
    /// </summary>
    public class AzureBlobHelper
    {
        private readonly string _connectionString;

        public AzureBlobHelper(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("AzureStorageConnection")
                ?? throw new InvalidOperationException(
                    "Azure Storage connection string 'AzureStorageConnection' not found in appsettings.json.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // UploadFileAsync
        //
        // Uploads a file to Azure Blob Storage and returns the public URL.
        //
        // Parameters:
        //   containerName  — the blob container (e.g. "ar-content", "thumbnails")
        //   folderPath     — optional subfolder inside the container (e.g. "dishes/restaurant-5")
        //                    pass null or "" to upload directly into the container root
        //   file           — the IFormFile received from the MVC form upload
        //   overwrite      — if true, an existing blob with the same name is replaced
        //
        // Returns:
        //   The full public HTTPS URL of the uploaded blob.
        //   e.g. https://youraccount.blob.core.windows.net/ar-content/dishes/r5/paneer.mp4
        //
        // Usage example:
        //   string url = await _blob.UploadFileAsync("ar-content", $"dishes/r{restaurantId}", file);
        // ─────────────────────────────────────────────────────────────────────
        public async Task<string> UploadFileAsync(
            string containerName,
            string? folderPath,
            IFormFile file,
            bool overwrite = true)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("File is null or empty.", nameof(file));

            // Build the blob name: "folderPath/timestamp_originalfilename"
            // Prepending a timestamp prevents collisions if the same filename
            // is uploaded twice for different dishes.
            var safeFileName = Path.GetFileName(file.FileName)         // strip directory traversal
                                   .Replace(" ", "_");                 // no spaces in blob names
            var blobFileName = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{safeFileName}";
            var blobName = string.IsNullOrWhiteSpace(folderPath)
                                   ? blobFileName
                                   : $"{folderPath.TrimEnd('/')}/{blobFileName}";

            // Get or create the container
            var containerClient = new BlobContainerClient(_connectionString, containerName);

            // CreateIfNotExistsAsync is a no-op if the container already exists.
            // PublicAccessType.Blob = individual blobs are publicly readable (no SAS needed).
            // Change to PublicAccessType.None + SAS tokens for private content.
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);

            var blobClient = containerClient.GetBlobClient(blobName);

            // Set the content type so browsers handle the file correctly
            // (e.g. video/mp4 streams in-browser rather than forcing a download)
            var uploadOptions = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = file.ContentType
                },
                Conditions = overwrite ? null : new BlobRequestConditions
                {
                    // Only upload if the blob does NOT already exist
                    IfNoneMatch = new Azure.ETag("*")
                }
            };

            await using var stream = file.OpenReadStream();
            await blobClient.UploadAsync(stream, uploadOptions);

            // Return the full public URL
            return blobClient.Uri.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        // DeleteFileAsync
        //
        // Deletes a blob by its full URL.
        // Silently succeeds if the blob does not exist.
        //
        // Usage example:
        //   await _blob.DeleteFileAsync("https://....blob.core.windows.net/ar-content/dishes/r5/paneer.mp4");
        // ─────────────────────────────────────────────────────────────────────
        public async Task DeleteFileAsync(string blobUrl)
        {
            if (string.IsNullOrWhiteSpace(blobUrl)) return;

            try
            {
                var blobClient = new BlobClient(new Uri(blobUrl),
                    new Azure.Storage.StorageSharedKeyCredential(
                        GetAccountName(), GetAccountKey()));

                await blobClient.DeleteIfExistsAsync();
            }
            catch
            {
                // Deletion failure is non-critical — log and continue.
                // A failed delete leaves an orphaned blob but doesn't break the app.
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Private helpers — parse account name / key from connection string
        // ─────────────────────────────────────────────────────────────────────
        private string GetAccountName()
        {
            var parts = _connectionString.Split(';');
            var namePart = parts.FirstOrDefault(p => p.StartsWith("AccountName=", StringComparison.OrdinalIgnoreCase));
            return namePart?.Split('=', 2)[1] ?? "";
        }

        private string GetAccountKey()
        {
            var parts = _connectionString.Split(';');
            var keyPart = parts.FirstOrDefault(p => p.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase));
            return keyPart?.Split('=', 2)[1] ?? "";
        }
    }
}