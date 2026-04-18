using ARMenuPlatform.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;

namespace ARMenuPlatform.Controllers
{
    [TypeFilter(typeof(SuperAdminOnlyFilter))]
    public class AdminController : Controller
    {
        private readonly DbHelper _db;
        private readonly IConfiguration _config;
        private readonly AzureBlobHelper _blob;

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
            // Load restaurant info + plan limits in one query
            var restaurant = await GetRestaurantWithPlan(restaurantId);
            if (restaurant == null) return NotFound();

            ViewBag.Restaurant = restaurant;
            ViewBag.DishCount = Convert.ToInt32(await _db.ExecuteScalarAsync(
                "SELECT COUNT(*) FROM Dishes WHERE RestaurantID = @ID AND IsActive = 1",
                new SqlParameter("@ID", restaurantId)));

            // ── Server-side validation ─────

            // Load all dishes for this restaurant
            const string dishesSql = @"
                SELECT
                    DishID, DishName, Description, Category,
                    Price, IsVegetarian, SpiceLevel,
                    IsActive, DisplayOrder,
                    ContentType, ContentURL, ThumbnailURL,
                    CreatedDate, UpdatedDate
                FROM  Dishes
                WHERE RestaurantID = @RestaurantID
                ORDER BY DisplayOrder ASC, CreatedDate DESC";

            ViewBag.Dishes = await _db.ExecuteQueryAsync(dishesSql,
                new SqlParameter("@RestaurantID", restaurantId));

            // Active dish count for plan limit display
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

            // Check if restaurant has already hit the dish limit
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
        [RequestSizeLimit(524_288_000)]         // 500 MB max upload per request
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
            // Reload restaurant info so we can return the form on error
            var restaurant = await GetRestaurantWithPlan(restaurantId);
            if (restaurant == null) return NotFound();

            ViewBag.Restaurant = restaurant;

            // ── Server-side validation ───────────────────────────────────────
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(dishName)) errors.Add("Dish name is required.");
            if (price < 0) errors.Add("Price cannot be negative.");
            if (contentFile == null || contentFile.Length == 0)
                errors.Add("Please upload a content file (video or 3D model).");

            // Validate category
            var validCategories = new[] { "Starter", "Main Course", "Bread", "Dessert", "Drink", "Special" };
            if (!validCategories.Contains(category)) errors.Add("Invalid category selected.");

            // Validate spice level
            var validSpice = new[] { "None", "Mild", "Medium", "Hot" };
            if (!validSpice.Contains(spiceLevel)) errors.Add("Invalid spice level selected.");

            // Validate content type
            if (contentType != "Video" && contentType != "3DModel")
                errors.Add("Invalid content type.");

            // ── Plan limit checks ────────────────────────────────────────────
            var planType = restaurant["PlanType"].ToString()!;
            var maxDishes = Convert.ToInt32(restaurant["MaxDishes"]);
            var allows3D = Convert.ToBoolean(restaurant["Allows3DModels"]);

            // 3D model restriction
            if (contentType == "3DModel" && !allows3D)
                errors.Add($"The {planType} plan does not allow 3D model content. Upgrade to Pro or Enterprise to use 3D models.");

            // Dish count limit (check active dishes only)
            if (maxDishes < 999999)
            {
                var currentCount = Convert.ToInt32(await _db.ExecuteScalarAsync(
                    "SELECT COUNT(*) FROM Dishes WHERE RestaurantID = @ID AND IsActive = 1",
                    new SqlParameter("@ID", restaurantId)));

                if (currentCount >= maxDishes)
                    errors.Add($"This restaurant has reached the {planType} plan limit of {maxDishes} active dishes.");
            }

            // ── File type validation ─────────────────────────────────────────
            if (contentFile != null && contentFile.Length > 0)
            {
                var ext = Path.GetExtension(contentFile.FileName).ToLower();
                if (contentType == "Video" && ext != ".mp4")
                    errors.Add("Video content must be an MP4 file (.mp4).");
                if (contentType == "3DModel" && ext != ".glb" && ext != ".gltf")
                    errors.Add("3D model content must be a GLB or GLTF file (.glb / .gltf).");
            }

            if (thumbnailFile != null && thumbnailFile.Length > 0)
            {
                var ext = Path.GetExtension(thumbnailFile.FileName).ToLower();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp")
                    errors.Add("Thumbnail must be a JPG, PNG, or WEBP image.");
            }

            if (errors.Count > 0)
            {
                ViewBag.Error = string.Join(" ", errors);
                return View();
            }

            // ── Upload files to Azure Blob Storage ───────────────────────────
            var folderPath = $"dishes/restaurant-{restaurantId}";
            string contentUrl = "";
            string thumbnailUrl = "";

            try
            {
                // Content file (video or 3D model)
                contentUrl = await _blob.UploadFileAsync("dish-content", folderPath, contentFile!);

                // Thumbnail (optional but strongly recommended)
                if (thumbnailFile != null && thumbnailFile.Length > 0)
                    thumbnailUrl = await _blob.UploadFileAsync("thumbnails", folderPath, thumbnailFile);
            }
            catch (Exception ex)
            {
                ViewBag.Error = "File upload failed: " + ex.Message +
                                " — Check your Azure Storage connection string in appsettings.json.";
                return View();
            }

            // ── Insert dish into database ────────────────────────────────────
            const string insertSql = @"
                INSERT INTO Dishes
                    (RestaurantID, DishName, Description, Category,
                     Price, IsVegetarian, SpiceLevel, IsActive, DisplayOrder,
                     ContentType, ContentURL, ThumbnailURL,
                     CreatedDate, UpdatedDate)
                VALUES
                    (@RestaurantID, @DishName, @Description, @Category,
                     @Price, @IsVegetarian, @SpiceLevel, 1, @DisplayOrder,
                     @ContentType, @ContentURL, @ThumbnailURL,
                     SYSUTCDATETIME(), SYSUTCDATETIME())";

            await _db.ExecuteNonQueryAsync(insertSql,
                new SqlParameter("@RestaurantID", restaurantId),
                new SqlParameter("@DishName", dishName.Trim()),
                new SqlParameter("@Description", string.IsNullOrWhiteSpace(description)
                                                      ? DBNull.Value : (object)description.Trim()),
                new SqlParameter("@Category", category),
                new SqlParameter("@Price", price),
                new SqlParameter("@IsVegetarian", isVegetarian),
                new SqlParameter("@SpiceLevel", spiceLevel),
                new SqlParameter("@DisplayOrder", displayOrder),
                new SqlParameter("@ContentType", contentType),
                new SqlParameter("@ContentURL", string.IsNullOrEmpty(contentUrl)
                                                      ? DBNull.Value : (object)contentUrl),
                new SqlParameter("@ThumbnailURL", string.IsNullOrEmpty(thumbnailUrl)
                                                      ? DBNull.Value : (object)thumbnailUrl));

            TempData["DishSuccess"] = $"'{dishName.Trim()}' has been added successfully.";
            return RedirectToAction(nameof(Dishes), new { restaurantId });
        }

        // =====================================================================
        // TOGGLE DISH ACTIVE/INACTIVE — POST /Admin/ToggleDish
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleDish(int dishId, int restaurantId)
        {
            // Read current state
            var current = await _db.ExecuteScalarAsync(
                "SELECT IsActive FROM Dishes WHERE DishID = @ID",
                new SqlParameter("@ID", dishId));

            if (current == null) return NotFound();

            var currentlyActive = Convert.ToBoolean(current);

            // If trying to activate, check plan limit first
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
                        TempData["DishError"] = $"Cannot activate dish — the {restaurant["PlanType"]} plan " +
                                                $"limit of {maxDishes} active dishes has been reached.";
                        return RedirectToAction(nameof(Dishes), new { restaurantId });
                    }
                }
            }

            // Flip the bit
            await _db.ExecuteNonQueryAsync(
                "UPDATE Dishes SET IsActive = @NewState, UpdatedDate = SYSUTCDATETIME() WHERE DishID = @ID",
                new SqlParameter("@NewState", !currentlyActive),
                new SqlParameter("@ID", dishId));

            return RedirectToAction(nameof(Dishes), new { restaurantId });
        }

        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        /// <summary>
        /// Loads a single restaurant row joined to its SubscriptionPlan.
        /// Returns null if the restaurant is not found.
        /// </summary>
        private async Task<DataRow?> GetRestaurantWithPlan(int restaurantId)
        {
            const string sql = @"
                SELECT
                    r.RestaurantID, r.RestaurantName, r.PlanType, r.Status,
                    sp.MaxDishes, sp.Allows3DModels, sp.AllowsSelfServiceUpdates,
                    sp.MonthlyPriceINR
                FROM  Restaurants      r
                JOIN  SubscriptionPlans sp ON sp.PlanName = r.PlanType
                WHERE r.RestaurantID = @ID";

            var dt = await _db.ExecuteQueryAsync(sql, new SqlParameter("@ID", restaurantId));
            return dt.Rows.Count > 0 ? dt.Rows[0] : null;
        }

        private async Task<DataTable> LoadPlans() =>
            await _db.ExecuteQueryAsync(
                @"SELECT PlanName, MonthlyPriceINR, MaxDishes, Allows3DModels, AllowsSelfServiceUpdates
                  FROM   SubscriptionPlans
                  ORDER  BY MonthlyPriceINR");
    }
}