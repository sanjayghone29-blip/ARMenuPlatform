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

        public AdminController(DbHelper db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        // =====================================================================
        // GET /Admin/Dashboard
        // =====================================================================
        public async Task<IActionResult> Dashboard()
        {
            // Four status counts in one round-trip
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

            // Today + this-month scans in one round-trip
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

            // All restaurants with per-restaurant scan count this month
            const string restaurantsSql = @"
                SELECT
                    r.RestaurantID,
                    r.RestaurantName,
                    r.OwnerName,
                    r.OwnerPhone,
                    r.City,
                    r.PlanType,
                    r.Status,
                    r.TrialEndDate,
                    r.CreatedDate,
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

            // Credentials banner — populated when redirected from AddRestaurant POST
            ViewBag.SuccessMessage = TempData["SuccessMessage"];
            ViewBag.NewOwnerEmail = TempData["NewOwnerEmail"];
            ViewBag.NewOwnerPassword = TempData["NewOwnerPassword"];
            ViewBag.NewRestaurantName = TempData["NewRestaurantName"];

            return View();
        }

        // =====================================================================
        // GET /Admin/AddRestaurant
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> AddRestaurant()
        {
            ViewBag.Plans = await LoadPlans();
            return View();
        }

        // =====================================================================
        // POST /Admin/AddRestaurant
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddRestaurant(
            string restaurantName,
            string ownerName,
            string ownerPhone,
            string ownerEmail,
            string address,
            string city,
            string planType,
            int totalTables)
        {
            // Always reload plans so the dropdown is populated if we return the form
            ViewBag.Plans = await LoadPlans();

            // ── Server-side validation ───────────────────────────────────────
            if (string.IsNullOrWhiteSpace(restaurantName) ||
                string.IsNullOrWhiteSpace(ownerName) ||
                string.IsNullOrWhiteSpace(ownerPhone) ||
                string.IsNullOrWhiteSpace(ownerEmail) ||
                string.IsNullOrWhiteSpace(planType) ||
                totalTables < 0)
            {
                ViewBag.Error = "All required fields must be filled in correctly.";
                return View();
            }

            if (planType != "Basic" && planType != "Pro" && planType != "Enterprise")
            {
                ViewBag.Error = "Invalid plan type selected.";
                return View();
            }

            // Duplicate email check
            var emailExists = Convert.ToInt32(await _db.ExecuteScalarAsync(
                "SELECT COUNT(1) FROM Users WHERE Email = @Email",
                new SqlParameter("@Email", ownerEmail.Trim().ToLower())));

            if (emailExists > 0)
            {
                ViewBag.Error = $"A login already exists for {ownerEmail.Trim()}. Please use a different email address.";
                return View();
            }

            // ── Prepare values ───────────────────────────────────────────────
            var today = DateTime.UtcNow.Date;
            var trialEndDate = today.AddDays(30);
            const string defaultPassword = "Welcome@123";
            var passwordHash = PasswordHelper.HashPassword(defaultPassword);

            // ── Transactional insert: Restaurant + User ──────────────────────
            // A raw SqlConnection is used here so both INSERTs share one
            // transaction. If the Users insert fails, the Restaurants row is
            // rolled back — no orphaned restaurant records with no owner login.
            var connectionString = _config.GetConnectionString("ARMenuDB")!;
            int newRestaurantId;

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

            try
            {
                // 1. Insert restaurant, capture new PK via SCOPE_IDENTITY()
                const string insertRestaurantSql = @"
                    INSERT INTO Restaurants
                        (RestaurantName, OwnerName, OwnerPhone, OwnerEmail,
                         Address, City, PlanType, Status,
                         TrialStartDate, TrialEndDate,
                         TotalTables, CreatedDate)
                    VALUES
                        (@RestaurantName, @OwnerName, @OwnerPhone, @OwnerEmail,
                         @Address, @City, @PlanType, 'Trial',
                         @TrialStartDate, @TrialEndDate,
                         @TotalTables, SYSUTCDATETIME());
                    SELECT SCOPE_IDENTITY();";

                await using var rCmd = new SqlCommand(insertRestaurantSql, conn, tx);
                rCmd.Parameters.AddRange(new SqlParameter[]
                {
                    new("@RestaurantName", restaurantName.Trim()),
                    new("@OwnerName",      ownerName.Trim()),
                    new("@OwnerPhone",     ownerPhone.Trim()),
                    new("@OwnerEmail",     ownerEmail.Trim().ToLower()),
                    new("@Address",        string.IsNullOrWhiteSpace(address)
                                               ? DBNull.Value : (object) address.Trim()),
                    new("@City",           string.IsNullOrWhiteSpace(city)
                                               ? DBNull.Value : (object) city.Trim()),
                    new("@PlanType",       planType),
                    new("@TrialStartDate", today),
                    new("@TrialEndDate",   trialEndDate),
                    new("@TotalTables",    totalTables),
                });

                newRestaurantId = Convert.ToInt32(await rCmd.ExecuteScalarAsync());

                // 2. Create owner login in Users
                const string insertUserSql = @"
                    INSERT INTO Users
                        (RestaurantID, FullName, Email, PasswordHash,
                         Role, IsActive, CreatedDate)
                    VALUES
                        (@RestaurantID, @FullName, @Email, @PasswordHash,
                         'RestaurantOwner', 1, SYSUTCDATETIME())";

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
                // Unique constraint violation — race condition safety net
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

            // ── Pass credentials to dashboard via TempData ───────────────────
            // TempData persists across exactly one redirect then self-destructs.
            TempData["SuccessMessage"] = $"'{restaurantName.Trim()}' has been added successfully on a 30-day trial.";
            TempData["NewOwnerEmail"] = ownerEmail.Trim().ToLower();
            TempData["NewOwnerPassword"] = defaultPassword;
            TempData["NewRestaurantName"] = restaurantName.Trim();

            return RedirectToAction(nameof(Dashboard));
        }

        // =====================================================================
        // POST /Admin/PauseRestaurant
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
        // POST /Admin/ActivateRestaurant
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

        // ── Private helpers ──────────────────────────────────────────────────

        private async Task<DataTable> LoadPlans() =>
            await _db.ExecuteQueryAsync(
                @"SELECT PlanName, MonthlyPriceINR, MaxDishes,
                         Allows3DModels, AllowsSelfServiceUpdates
                  FROM   SubscriptionPlans
                  ORDER  BY MonthlyPriceINR");
    }
}