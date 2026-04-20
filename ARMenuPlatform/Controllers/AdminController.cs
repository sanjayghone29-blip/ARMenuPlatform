using ARMenuPlatform.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using System.IO.Compression;

namespace ARMenuPlatform.Controllers
{
    [TypeFilter(typeof(SuperAdminOnlyFilter))]
    public class AdminController : Controller
    {
        private readonly DbHelper _db;
        private readonly IConfiguration _config;
        private readonly AzureBlobHelper _blob;

        // Base URL used when building the AR menu URL embedded in each QR code.
        // Change this to your production domain before going live.
        // e.g. "https://armenu.in"
        private string ArMenuBaseUrl =>
            $"{Request.Scheme}://{Request.Host}";
        public AdminController(DbHelper db, IConfiguration config, AzureBlobHelper blob)
        {
            _db = db;
            _config = config;
            _blob = blob;
        }

        // =====================================================================
        // DASHBOARD
        // =====================================================================
        public async Task<IActionResult> Dashboard()
        {
            const string statsSql = @"
                SELECT
                    SUM(CASE WHEN Status = 'Active'  THEN 1 ELSE 0 END) AS ActiveCount,
                    SUM(CASE WHEN Status = 'Trial'   THEN 1 ELSE 0 END) AS TrialCount,
                    SUM(CASE WHEN Status = 'Paused'  THEN 1 ELSE 0 END) AS PausedCount
                FROM Restaurants";
            var statsRow = (await _db.ExecuteQueryAsync(statsSql)).Rows[0];
            ViewBag.ActiveCount = Convert.ToInt32(statsRow["ActiveCount"]);
            ViewBag.TrialCount = Convert.ToInt32(statsRow["TrialCount"]);
            ViewBag.PausedCount = Convert.ToInt32(statsRow["PausedCount"]);

            const string scansSql = @"
                SELECT
                    SUM(CASE WHEN CAST(ScanTimestamp AS DATE) = CAST(SYSUTCDATETIME() AS DATE)
                             THEN 1 ELSE 0 END) AS ScansToday,
                    SUM(CASE WHEN YEAR(ScanTimestamp)  = YEAR(SYSUTCDATETIME())
                              AND MONTH(ScanTimestamp) = MONTH(SYSUTCDATETIME())
                             THEN 1 ELSE 0 END) AS ScansThisMonth
                FROM Scans";
            var scansRow = (await _db.ExecuteQueryAsync(scansSql)).Rows[0];
            ViewBag.ScansToday = scansRow["ScansToday"] == DBNull.Value ? 0 : Convert.ToInt32(scansRow["ScansToday"]);
            ViewBag.ScansThisMonth = scansRow["ScansThisMonth"] == DBNull.Value ? 0 : Convert.ToInt32(scansRow["ScansThisMonth"]);

            const string restaurantsSql = @"
                SELECT
                    r.RestaurantID, r.RestaurantName, r.OwnerName, r.OwnerPhone,
                    r.City, r.PlanType, r.Status, r.TrialEndDate, r.CreatedDate,
                    COALESCE(s.ScansThisMonth, 0) AS ScansThisMonth
                FROM Restaurants r
                LEFT JOIN (
                    SELECT RestaurantID, COUNT(*) AS ScansThisMonth
                    FROM   Scans
                    WHERE  YEAR(ScanTimestamp)  = YEAR(SYSUTCDATETIME())
                      AND  MONTH(ScanTimestamp) = MONTH(SYSUTCDATETIME())
                    GROUP  BY RestaurantID
                ) s ON s.RestaurantID = r.RestaurantID
                ORDER BY r.CreatedDate DESC";
            ViewBag.Restaurants = await _db.ExecuteQueryAsync(restaurantsSql);
            ViewBag.FullName = HttpContext.Session.GetString(SessionKeys.FullName);
            ViewBag.SuccessMessage = TempData["SuccessMessage"];
            ViewBag.NewOwnerEmail = TempData["NewOwnerEmail"];
            ViewBag.NewOwnerPassword = TempData["NewOwnerPassword"];
            ViewBag.NewRestaurantName = TempData["NewRestaurantName"];
            return View();
        }

        // =====================================================================
        // ADD RESTAURANT — GET
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> AddRestaurant()
        {
            ViewBag.Plans = await LoadPlans();
            return View();
        }

        // =====================================================================
        // ADD RESTAURANT — POST
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddRestaurant(
            string restaurantName, string ownerName, string ownerPhone,
            string ownerEmail, string address, string city,
            string planType, int totalTables)
        {
            ViewBag.Plans = await LoadPlans();

            if (string.IsNullOrWhiteSpace(restaurantName) || string.IsNullOrWhiteSpace(ownerName) ||
                string.IsNullOrWhiteSpace(ownerPhone) || string.IsNullOrWhiteSpace(ownerEmail) ||
                string.IsNullOrWhiteSpace(planType) || totalTables < 0)
            {
                ViewBag.Error = "All required fields must be filled in correctly.";
                return View();
            }

            if (planType != "Basic" && planType != "Pro" && planType != "Enterprise")
            {
                ViewBag.Error = "Invalid plan type selected.";
                return View();
            }

            var emailExists = Convert.ToInt32(await _db.ExecuteScalarAsync(
                "SELECT COUNT(1) FROM Users WHERE Email = @Email",
                new SqlParameter("@Email", ownerEmail.Trim().ToLower())));

            if (emailExists > 0)
            {
                ViewBag.Error = $"A login already exists for {ownerEmail.Trim()}. Please use a different email address.";
                return View();
            }

            var today = DateTime.UtcNow.Date;
            var trialEndDate = today.AddDays(30);
            const string defaultPassword = "Welcome@123";
            var passwordHash = PasswordHelper.HashPassword(defaultPassword);
            var connectionString = _config.GetConnectionString("ARMenuDB")!;
            int newRestaurantId;

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

            try
            {
                const string insertRestaurantSql = @"
                    INSERT INTO Restaurants
                        (RestaurantName, OwnerName, OwnerPhone, OwnerEmail,
                         Address, City, PlanType, Status,
                         TrialStartDate, TrialEndDate, TotalTables, CreatedDate)
                    VALUES
                        (@RestaurantName, @OwnerName, @OwnerPhone, @OwnerEmail,
                         @Address, @City, @PlanType, 'Trial',
                         @TrialStartDate, @TrialEndDate, @TotalTables, SYSUTCDATETIME());
                    SELECT SCOPE_IDENTITY();";

                await using var rCmd = new SqlCommand(insertRestaurantSql, conn, tx);
                rCmd.Parameters.AddRange(new SqlParameter[]
                {
                    new("@RestaurantName", restaurantName.Trim()),
                    new("@OwnerName",      ownerName.Trim()),
                    new("@OwnerPhone",     ownerPhone.Trim()),
                    new("@OwnerEmail",     ownerEmail.Trim().ToLower()),
                    new("@Address",        string.IsNullOrWhiteSpace(address) ? DBNull.Value : (object) address.Trim()),
                    new("@City",           string.IsNullOrWhiteSpace(city)    ? DBNull.Value : (object) city.Trim()),
                    new("@PlanType",       planType),
                    new("@TrialStartDate", today),
                    new("@TrialEndDate",   trialEndDate),
                    new("@TotalTables",    totalTables),
                });

                newRestaurantId = Convert.ToInt32(await rCmd.ExecuteScalarAsync());

                const string insertUserSql = @"
                    INSERT INTO Users (RestaurantID, FullName, Email, PasswordHash, Role, IsActive, CreatedDate)
                    VALUES (@RestaurantID, @FullName, @Email, @PasswordHash, 'RestaurantOwner', 1, SYSUTCDATETIME())";

                await using var uCmd = new SqlCommand(insertUserSql, conn, tx);
                uCmd.Parameters.AddRange(new SqlParameter[]
                {
                    new("@RestaurantID", newRestaurantId),
                    new("@FullName",     ownerName.Trim()),
                    new("@Email",        ownerEmail.Trim().ToLower()),
                    new("@PasswordHash", passwordHash),
                });

                await uCmd.ExecuteNonQueryAsync();
                await tx.CommitAsync();
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                await tx.RollbackAsync();
                ViewBag.Error = "That email address is already in use. Please choose a different one.";
                return View();
            }
            catch
            {
                await tx.RollbackAsync();
                ViewBag.Error = "An unexpected error occurred. Please try again.";
                return View();
            }

            TempData["SuccessMessage"] = $"'{restaurantName.Trim()}' has been added successfully on a 30-day trial.";
            TempData["NewOwnerEmail"] = ownerEmail.Trim().ToLower();
            TempData["NewOwnerPassword"] = defaultPassword;
            TempData["NewRestaurantName"] = restaurantName.Trim();
            return RedirectToAction(nameof(Dashboard));
        }

        // =====================================================================
        // PAUSE RESTAURANT — POST
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PauseRestaurant(int restaurantId)
        {
            await _db.ExecuteNonQueryAsync(
                "UPDATE Restaurants SET Status = 'Paused' WHERE RestaurantID = @ID",
                new SqlParameter("@ID", restaurantId));
            return RedirectToAction(nameof(Dashboard));
        }

        // =====================================================================
        // ACTIVATE RESTAURANT — POST
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActivateRestaurant(int restaurantId)
        {
            await _db.ExecuteNonQueryAsync(
                "UPDATE Restaurants SET Status = 'Active' WHERE RestaurantID = @ID",
                new SqlParameter("@ID", restaurantId));
            return RedirectToAction(nameof(Dashboard));
        }

        // =====================================================================
        // DISHES LIST — GET /Admin/Dishes/{restaurantId}
        // =====================================================================
        [HttpGet]
        [Route("Admin/Dishes/{restaurantId:int}")]
        public async Task<IActionResult> Dishes(int restaurantId)
        {
            var restaurant = await GetRestaurantWithPlan(restaurantId);
            if (restaurant == null) return NotFound();
            ViewBag.Restaurant = restaurant;

            const string dishesSql = @"
                SELECT DishID, DishName, Description, Category,
                       Price, IsVegetarian, SpiceLevel,
                       IsActive, DisplayOrder,
                       ContentType, ContentURL, ThumbnailURL,
                       CreatedDate, UpdatedDate
                FROM   Dishes
                WHERE  RestaurantID = @RestaurantID
                ORDER  BY DisplayOrder ASC, CreatedDate DESC";

            ViewBag.Dishes = await _db.ExecuteQueryAsync(dishesSql,
                new SqlParameter("@RestaurantID", restaurantId));

            ViewBag.ActiveDishCount = Convert.ToInt32(await _db.ExecuteScalarAsync(
                "SELECT COUNT(*) FROM Dishes WHERE RestaurantID = @ID AND IsActive = 1",
                new SqlParameter("@ID", restaurantId)));

            ViewBag.SuccessMessage = TempData["DishSuccess"];
            ViewBag.ErrorMessage = TempData["DishError"];
            return View();
        }

        // =====================================================================
        // ADD DISH — GET /Admin/AddDish/{restaurantId}
        // =====================================================================
        [HttpGet]
        [Route("Admin/AddDish/{restaurantId:int}")]
        public async Task<IActionResult> AddDish(int restaurantId)
        {
            var restaurant = await GetRestaurantWithPlan(restaurantId);
            if (restaurant == null) return NotFound();

            var currentDishCount = Convert.ToInt32(await _db.ExecuteScalarAsync(
                "SELECT COUNT(*) FROM Dishes WHERE RestaurantID = @ID AND IsActive = 1",
                new SqlParameter("@ID", restaurantId)));

            var maxDishes = Convert.ToInt32(restaurant["MaxDishes"]);
            if (maxDishes < 999999 && currentDishCount >= maxDishes)
            {
                TempData["DishError"] = $"This restaurant is on the {restaurant["PlanType"]} plan " +
                                        $"which allows a maximum of {maxDishes} active dishes. " +
                                        $"Deactivate an existing dish before adding a new one.";
                return RedirectToAction(nameof(Dishes), new { restaurantId });
            }

            ViewBag.Restaurant = restaurant;
            ViewBag.DishCount = currentDishCount;
            return View();
        }

        // =====================================================================
        // ADD DISH — POST /Admin/AddDish
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(524_288_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 524_288_000)]
        public async Task<IActionResult> AddDish(
            int restaurantId,
            string dishName,
            string? description,
            string category,
            decimal price,
            bool isVegetarian,
            string spiceLevel,
            int displayOrder,
            string contentType,
            IFormFile? contentFile,
            IFormFile? thumbnailFile)
        {
            var restaurant = await GetRestaurantWithPlan(restaurantId);
            if (restaurant == null) return NotFound();

            ViewBag.Restaurant = restaurant;
            ViewBag.DishCount = Convert.ToInt32(await _db.ExecuteScalarAsync(
                "SELECT COUNT(*) FROM Dishes WHERE RestaurantID = @ID AND IsActive = 1",
                new SqlParameter("@ID", restaurantId)));

            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(dishName)) errors.Add("Dish name is required.");
            if (price < 0) errors.Add("Price cannot be negative.");
            if (contentFile == null || contentFile.Length == 0) errors.Add("Please upload a content file.");

            var validCategories = new[] { "Starter", "Main Course", "Bread", "Dessert", "Drink", "Special" };
            if (!validCategories.Contains(category)) errors.Add("Invalid category selected.");

            var validSpice = new[] { "None", "Mild", "Medium", "Hot" };
            if (!validSpice.Contains(spiceLevel)) errors.Add("Invalid spice level selected.");

            if (contentType != "Video" && contentType != "3DModel") errors.Add("Invalid content type.");

            var planType = restaurant["PlanType"].ToString()!;
            var maxDishes = Convert.ToInt32(restaurant["MaxDishes"]);
            var allows3D = Convert.ToBoolean(restaurant["Allows3DModels"]);

            if (contentType == "3DModel" && !allows3D)
                errors.Add($"The {planType} plan does not allow 3D model content.");

            if (maxDishes < 999999)
            {
                var currentCount = Convert.ToInt32(await _db.ExecuteScalarAsync(
                    "SELECT COUNT(*) FROM Dishes WHERE RestaurantID = @ID AND IsActive = 1",
                    new SqlParameter("@ID", restaurantId)));
                if (currentCount >= maxDishes)
                    errors.Add($"Plan limit of {maxDishes} active dishes reached.");
            }

            if (contentFile != null && contentFile.Length > 0)
            {
                var ext = Path.GetExtension(contentFile.FileName).ToLower();
                if (contentType == "Video" && ext != ".mp4") errors.Add("Video must be MP4.");
                if (contentType == "3DModel" && ext != ".glb" && ext != ".gltf") errors.Add("3D model must be GLB or GLTF.");
            }

            if (thumbnailFile != null && thumbnailFile.Length > 0)
            {
                var ext = Path.GetExtension(thumbnailFile.FileName).ToLower();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp")
                    errors.Add("Thumbnail must be JPG, PNG, or WEBP.");
            }

            if (errors.Count > 0)
            {
                ViewBag.Error = string.Join(" ", errors);
                return View();
            }

            var folderPath = $"dishes/restaurant-{restaurantId}";
            string contentUrl = "";
            string thumbnailUrl = "";

            try
            {
                contentUrl = await _blob.UploadFileAsync("ar-content", folderPath, contentFile!);
                if (thumbnailFile != null && thumbnailFile.Length > 0)
                    thumbnailUrl = await _blob.UploadFileAsync("thumbnails", folderPath, thumbnailFile);
            }
            catch (Exception ex)
            {
                ViewBag.Error = "File upload failed: " + ex.Message;
                return View();
            }

            const string insertSql = @"
                INSERT INTO Dishes
                    (RestaurantID, DishName, Description, Category,
                     Price, IsVegetarian, SpiceLevel, IsActive, DisplayOrder,
                     ContentType, ContentURL, ThumbnailURL, CreatedDate, UpdatedDate)
                VALUES
                    (@RestaurantID, @DishName, @Description, @Category,
                     @Price, @IsVegetarian, @SpiceLevel, 1, @DisplayOrder,
                     @ContentType, @ContentURL, @ThumbnailURL, SYSUTCDATETIME(), SYSUTCDATETIME())";

            await _db.ExecuteNonQueryAsync(insertSql,
                new SqlParameter("@RestaurantID", restaurantId),
                new SqlParameter("@DishName", dishName.Trim()),
                new SqlParameter("@Description", string.IsNullOrWhiteSpace(description) ? DBNull.Value : (object)description.Trim()),
                new SqlParameter("@Category", category),
                new SqlParameter("@Price", price),
                new SqlParameter("@IsVegetarian", isVegetarian),
                new SqlParameter("@SpiceLevel", spiceLevel),
                new SqlParameter("@DisplayOrder", displayOrder),
                new SqlParameter("@ContentType", contentType),
                new SqlParameter("@ContentURL", string.IsNullOrEmpty(contentUrl) ? DBNull.Value : (object)contentUrl),
                new SqlParameter("@ThumbnailURL", string.IsNullOrEmpty(thumbnailUrl) ? DBNull.Value : (object)thumbnailUrl));

            TempData["DishSuccess"] = $"'{dishName.Trim()}' has been added successfully.";
            return RedirectToAction(nameof(Dishes), new { restaurantId });
        }

        // =====================================================================
        // TOGGLE DISH — POST /Admin/ToggleDish
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleDish(int dishId, int restaurantId)
        {
            var current = await _db.ExecuteScalarAsync(
                "SELECT IsActive FROM Dishes WHERE DishID = @ID",
                new SqlParameter("@ID", dishId));

            if (current == null) return NotFound();
            var currentlyActive = Convert.ToBoolean(current);

            if (!currentlyActive)
            {
                var restaurant = await GetRestaurantWithPlan(restaurantId);
                if (restaurant != null)
                {
                    var maxDishes = Convert.ToInt32(restaurant["MaxDishes"]);
                    var activeCount = Convert.ToInt32(await _db.ExecuteScalarAsync(
                        "SELECT COUNT(*) FROM Dishes WHERE RestaurantID = @ID AND IsActive = 1",
                        new SqlParameter("@ID", restaurantId)));

                    if (maxDishes < 999999 && activeCount >= maxDishes)
                    {
                        TempData["DishError"] = $"Cannot activate — {restaurant["PlanType"]} plan limit of {maxDishes} active dishes reached.";
                        return RedirectToAction(nameof(Dishes), new { restaurantId });
                    }
                }
            }

            await _db.ExecuteNonQueryAsync(
                "UPDATE Dishes SET IsActive = @NewState, UpdatedDate = SYSUTCDATETIME() WHERE DishID = @ID",
                new SqlParameter("@NewState", !currentlyActive),
                new SqlParameter("@ID", dishId));

            return RedirectToAction(nameof(Dishes), new { restaurantId });
        }

        // =====================================================================
        // TABLES LIST — GET /Admin/Tables/{restaurantId}
        // =====================================================================
        [HttpGet]
        [Route("Admin/Tables/{restaurantId:int}")]
        public async Task<IActionResult> Tables(int restaurantId)
        {
            var restaurant = await GetRestaurantBasic(restaurantId);
            if (restaurant == null) return NotFound();

            ViewBag.Restaurant = restaurant;

            var tables = await _db.ExecuteQueryAsync(
                @"SELECT TableID, TableNumber, QRCodeString, QRCodeImageURL, IsActive, CreatedDate
                  FROM   Tables
                  WHERE  RestaurantID = @ID
                  ORDER  BY CAST(TableNumber AS INT)",
                new SqlParameter("@ID", restaurantId));

            ViewBag.Tables = tables;
            ViewBag.ArMenuBaseUrl = ArMenuBaseUrl;
            ViewBag.SuccessMessage = TempData["QRSuccess"];
            ViewBag.ErrorMessage = TempData["QRError"];
            return View();
        }

        // =====================================================================
        // GENERATE QR CODES — POST /Admin/GenerateQRCodes/{restaurantId}
        //
        // For each table number 1..TotalTables:
        //   - Skip if a Tables row already exists for that table number
        //   - Generate a new GUID as the QRCodeString
        //   - Build the AR menu URL
        //   - Render a 300×300 PNG via QRCodeHelper
        //   - Upload PNG to Azure Blob "qr-codes/{restaurantId}/Table-N.png"
        //   - Insert row into Tables
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("Admin/GenerateQRCodes/{restaurantId:int}")]
        public async Task<IActionResult> GenerateQRCodes(int restaurantId)
        {
            var restaurant = await GetRestaurantBasic(restaurantId);
            if (restaurant == null) return NotFound();

            var totalTables = Convert.ToInt32(restaurant["TotalTables"]);

            if (totalTables <= 0)
            {
                TempData["QRError"] = "TotalTables is 0. Update the restaurant's table count before generating QR codes.";
                return RedirectToAction(nameof(Tables), new { restaurantId });
            }

            // Load already-existing table numbers so we can skip them
            var existingDt = await _db.ExecuteQueryAsync(
                "SELECT TableNumber FROM Tables WHERE RestaurantID = @ID",
                new SqlParameter("@ID", restaurantId));

            var existingNumbers = new HashSet<string>(
                existingDt.Rows.Cast<DataRow>().Select(r => r["TableNumber"].ToString()!),
                StringComparer.OrdinalIgnoreCase);

            int generated = 0;
            int skipped = 0;

            for (int tableNum = 1; tableNum <= totalTables; tableNum++)
            {
                var tableNumberStr = tableNum.ToString();

                if (existingNumbers.Contains(tableNumberStr))
                {
                    skipped++;
                    continue;
                }

                // ── New GUID = unique, unguessable QR identifier ─────────────
                var qrGuid = Guid.NewGuid();
                var menuUrl = $"{ArMenuBaseUrl}/ar/menu/{qrGuid}";

                // ── Generate PNG bytes ───────────────────────────────────────
                byte[] pngBytes;
                try
                {
                    pngBytes = QRCodeHelper.GenerateQRCodePng(menuUrl);
                }
                catch (Exception ex)
                {
                    TempData["QRError"] = $"QR generation failed for Table {tableNum}: {ex.Message}";
                    return RedirectToAction(nameof(Tables), new { restaurantId });
                }

                // ── Upload PNG to Azure Blob Storage ─────────────────────────
                // We wrap the bytes in a MemoryStream and an adapter so
                // AzureBlobHelper.UploadFileAsync can accept it as an IFormFile.
                string imageUrl;
                try
                {
                    using var ms = new MemoryStream(pngBytes);
                    var formFile = new InMemoryFile(ms, $"Table-{tableNum}.png", "image/png");
                    imageUrl = await _blob.UploadFileAsync("qr-codes", $"{restaurantId}", formFile);
                }
                catch (Exception ex)
                {
                    TempData["QRError"] = $"Upload failed for Table {tableNum}: {ex.Message}";
                    return RedirectToAction(nameof(Tables), new { restaurantId });
                }

                // ── Insert into Tables ───────────────────────────────────────
                await _db.ExecuteNonQueryAsync(
                    @"INSERT INTO Tables
                          (RestaurantID, TableNumber, QRCodeString, QRCodeImageURL, IsActive, CreatedDate)
                      VALUES
                          (@RestaurantID, @TableNumber, @QRCodeString, @QRCodeImageURL, 1, SYSUTCDATETIME())",
                    new SqlParameter("@RestaurantID", restaurantId),
                    new SqlParameter("@TableNumber", tableNumberStr),
                    new SqlParameter("@QRCodeString", qrGuid),
                    new SqlParameter("@QRCodeImageURL", imageUrl));

                generated++;
            }

            TempData["QRSuccess"] = $"Done. {generated} QR code(s) generated, {skipped} already existed and were skipped.";
            return RedirectToAction(nameof(Tables), new { restaurantId });
        }

        // =====================================================================
        // DOWNLOAD ALL QR CODES — GET /Admin/DownloadQRCodes/{restaurantId}
        //
        // Fetches all QR PNG bytes from Azure Blob and packages them into a
        // ZIP file returned as a file download. Named "Table-1.png" etc.
        // =====================================================================
        [HttpGet]
        [Route("Admin/DownloadQRCodes/{restaurantId:int}")]
        public async Task<IActionResult> DownloadQRCodes(int restaurantId)
        {
            var restaurant = await GetRestaurantBasic(restaurantId);
            if (restaurant == null) return NotFound();

            var tables = await _db.ExecuteQueryAsync(
                @"SELECT TableNumber, QRCodeImageURL
                  FROM   Tables
                  WHERE  RestaurantID = @ID AND IsActive = 1 AND QRCodeImageURL IS NOT NULL
                  ORDER  BY CAST(TableNumber AS INT)",
                new SqlParameter("@ID", restaurantId));

            if (tables.Rows.Count == 0)
            {
                TempData["QRError"] = "No QR codes found. Generate them first.";
                return RedirectToAction(nameof(Tables), new { restaurantId });
            }

            // Build the ZIP in memory — for most restaurants (<200 tables)
            // this is perfectly fine. At very large scale, stream to disk first.
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var httpClient = new HttpClient();

                foreach (DataRow row in tables.Rows)
                {
                    var tableNumber = row["TableNumber"].ToString();
                    var imageUrl = row["QRCodeImageURL"].ToString();

                    if (string.IsNullOrEmpty(imageUrl)) continue;

                    try
                    {
                        // Download the PNG bytes from Azure Blob public URL
                        var pngBytes = await httpClient.GetByteArrayAsync(imageUrl);

                        var entry = archive.CreateEntry($"Table-{tableNumber}.png", CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        await entryStream.WriteAsync(pngBytes);
                    }
                    catch
                    {
                        // Skip a single failed image rather than aborting the whole ZIP
                        continue;
                    }
                }
            }

            zipStream.Position = 0;
            var restaurantName = restaurant["RestaurantName"].ToString()!
                                      .Replace(" ", "_").Replace("/", "-");
            var fileName = $"QRCodes_{restaurantName}.zip";

            return File(zipStream.ToArray(), "application/zip", fileName);
        }

        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        /// <summary>
        /// Loads restaurant + subscription plan info.
        /// Used by dish actions that need plan limits.
        /// </summary>
        private async Task<DataRow?> GetRestaurantWithPlan(int restaurantId)
        {
            const string sql = @"
                SELECT r.RestaurantID, r.RestaurantName, r.PlanType, r.Status,
                       sp.MaxDishes, sp.Allows3DModels, sp.AllowsSelfServiceUpdates, sp.MonthlyPriceINR
                FROM   Restaurants       r
                JOIN   SubscriptionPlans sp ON sp.PlanName = r.PlanType
                WHERE  r.RestaurantID = @ID";

            var dt = await _db.ExecuteQueryAsync(sql, new SqlParameter("@ID", restaurantId));
            return dt.Rows.Count > 0 ? dt.Rows[0] : null;
        }

        /// <summary>
        /// Loads basic restaurant columns only (no plan join needed).
        /// Used by table/QR actions.
        /// </summary>
        private async Task<DataRow?> GetRestaurantBasic(int restaurantId)
        {
            const string sql = @"
                SELECT RestaurantID, RestaurantName, TotalTables, Status
                FROM   Restaurants
                WHERE  RestaurantID = @ID";

            var dt = await _db.ExecuteQueryAsync(sql, new SqlParameter("@ID", restaurantId));
            return dt.Rows.Count > 0 ? dt.Rows[0] : null;
        }

        private async Task<DataTable> LoadPlans() =>
            await _db.ExecuteQueryAsync(
                @"SELECT PlanName, MonthlyPriceINR, MaxDishes, Allows3DModels, AllowsSelfServiceUpdates
                  FROM   SubscriptionPlans
                  ORDER  BY MonthlyPriceINR");
    }

    // =========================================================================
    // InMemoryFile
    // A lightweight IFormFile adapter that wraps a MemoryStream.
    // Used to pass in-memory byte arrays (e.g. generated QR PNGs) to
    // AzureBlobHelper.UploadFileAsync without writing to disk first.
    // =========================================================================
    internal sealed class InMemoryFile : IFormFile
    {
        private readonly Stream _stream;

        public InMemoryFile(Stream stream, string fileName, string contentType)
        {
            _stream = stream;
            FileName = fileName;
            ContentType = contentType;
            Name = fileName;
        }

        public string ContentType { get; }
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{FileName}\"";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => _stream.Length;
        public string Name { get; }
        public string FileName { get; }

        public void CopyTo(Stream target) => _stream.CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken ct) => _stream.CopyToAsync(target, ct);
        public Stream OpenReadStream() => _stream;
    }
}