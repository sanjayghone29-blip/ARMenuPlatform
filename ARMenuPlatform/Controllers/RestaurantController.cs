using ARMenuPlatform.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;

namespace ARMenuPlatform.Controllers
{
    [TypeFilter(typeof(RestaurantOwnerFilter))]
    public class RestaurantController : Controller
    {
        private readonly DbHelper _db;

        public RestaurantController(DbHelper db)
        {
            _db = db;
        }

        // =====================================================================
        // GET /Restaurant/Dashboard
        // =====================================================================
        public async Task<IActionResult> Dashboard()
        {
            // ── Resolve the restaurant from session ──────────────────────────
            var restaurantIdStr = HttpContext.Session.GetString(SessionKeys.RestaurantID);
            if (string.IsNullOrEmpty(restaurantIdStr) || !int.TryParse(restaurantIdStr, out int restaurantId))
                return RedirectToAction("Index", "Login");

            // ── 1. Restaurant core info ──────────────────────────────────────
            const string restaurantSql = @"
                SELECT RestaurantID, RestaurantName, OwnerName, PlanType, Status,
                       TrialStartDate, TrialEndDate, SubscriptionStartDate
                FROM   Restaurants
                WHERE  RestaurantID = @ID";

            var restaurantDt = await _db.ExecuteQueryAsync(restaurantSql,
                new SqlParameter("@ID", restaurantId));

            if (restaurantDt.Rows.Count == 0)
                return RedirectToAction("Index", "Login");

            var restaurant = restaurantDt.Rows[0];
            ViewBag.Restaurant = restaurant;

            // Trial days remaining
            int trialDaysLeft = 0;
            if (restaurant["TrialEndDate"] != DBNull.Value)
            {
                var trialEnd = Convert.ToDateTime(restaurant["TrialEndDate"]).Date;
                trialDaysLeft = Math.Max(0, (trialEnd - DateTime.UtcNow.Date).Days);
            }
            ViewBag.TrialDaysLeft = trialDaysLeft;

            // ── 2. Scan stats — today / this week / this month ───────────────
            const string statsSql = @"
                SELECT
                    SUM(CASE WHEN CAST(ScanTimestamp AS DATE) = CAST(SYSUTCDATETIME() AS DATE)
                             THEN 1 ELSE 0 END) AS ScansToday,
                    SUM(CASE WHEN ScanTimestamp >= DATEADD(DAY, -6, CAST(SYSUTCDATETIME() AS DATE))
                             THEN 1 ELSE 0 END) AS ScansThisWeek,
                    SUM(CASE WHEN YEAR(ScanTimestamp)  = YEAR(SYSUTCDATETIME())
                              AND MONTH(ScanTimestamp) = MONTH(SYSUTCDATETIME())
                             THEN 1 ELSE 0 END) AS ScansThisMonth
                FROM Scans
                WHERE RestaurantID = @ID";

            var statsRow = (await _db.ExecuteQueryAsync(statsSql,
                new SqlParameter("@ID", restaurantId))).Rows[0];

            ViewBag.ScansToday = statsRow["ScansToday"] == DBNull.Value ? 0 : Convert.ToInt32(statsRow["ScansToday"]);
            ViewBag.ScansThisWeek = statsRow["ScansThisWeek"] == DBNull.Value ? 0 : Convert.ToInt32(statsRow["ScansThisWeek"]);
            ViewBag.ScansThisMonth = statsRow["ScansThisMonth"] == DBNull.Value ? 0 : Convert.ToInt32(statsRow["ScansThisMonth"]);

            // ── 3. Top 5 most viewed dishes this week ────────────────────────
            const string topDishesSql = @"
                SELECT TOP 5
                    d.DishName,
                    COUNT(*) AS ViewCount
                FROM  Scans s
                JOIN  Dishes d ON d.DishID = s.DishID
                WHERE s.RestaurantID = @ID
                  AND s.ScanType     = 'DishView'
                  AND s.ScanTimestamp >= DATEADD(DAY, -6, CAST(SYSUTCDATETIME() AS DATE))
                  AND s.DishID IS NOT NULL
                GROUP BY d.DishID, d.DishName
                ORDER BY ViewCount DESC";

            ViewBag.TopDishes = await _db.ExecuteQueryAsync(topDishesSql,
                new SqlParameter("@ID", restaurantId));

            // Most viewed dish name for the stat card
            var topDt = ViewBag.TopDishes as DataTable;
            ViewBag.MostViewedDish = topDt != null && topDt.Rows.Count > 0
                ? topDt.Rows[0]["DishName"].ToString()
                : "—";

            // ── 4. Daily scan counts for last 7 days (Chart.js data) ─────────
            // Generate a guaranteed 7-row series including days with 0 scans.
            // A LEFT JOIN on a date series ensures gaps don't vanish from the chart.
            const string chartSql = @"
                WITH DateSeries AS (
                    SELECT CAST(DATEADD(DAY, -6, CAST(SYSUTCDATETIME() AS DATE)) AS DATE) AS ScanDate
                    UNION ALL
                    SELECT DATEADD(DAY, 1, ScanDate)
                    FROM   DateSeries
                    WHERE  ScanDate < CAST(SYSUTCDATETIME() AS DATE)
                )
                SELECT
                    ds.ScanDate,
                    COALESCE(COUNT(s.ScanID), 0) AS ScanCount
                FROM DateSeries ds
                LEFT JOIN Scans s
                    ON  CAST(s.ScanTimestamp AS DATE) = ds.ScanDate
                    AND s.RestaurantID = @ID
                GROUP BY ds.ScanDate
                ORDER BY ds.ScanDate";

            ViewBag.ChartData = await _db.ExecuteQueryAsync(chartSql,
                new SqlParameter("@ID", restaurantId));

            // ── 5. Dishes list ───────────────────────────────────────────────
            const string dishesSql = @"
                SELECT DishID, DishName, Category, Price,
                       IsVegetarian, IsActive, ContentType, ThumbnailURL, DisplayOrder
                FROM   Dishes
                WHERE  RestaurantID = @ID
                ORDER  BY DisplayOrder ASC, DishName ASC";

            ViewBag.Dishes = await _db.ExecuteQueryAsync(dishesSql,
                new SqlParameter("@ID", restaurantId));

            // ── 6. Tables and QR codes ───────────────────────────────────────
            const string tablesSql = @"
                SELECT TableID, TableNumber, QRCodeImageURL, IsActive
                FROM   Tables
                WHERE  RestaurantID = @ID AND IsActive = 1
                ORDER  BY CAST(TableNumber AS INT)";

            ViewBag.Tables = await _db.ExecuteQueryAsync(tablesSql,
                new SqlParameter("@ID", restaurantId));

            // ── 7. Pending menu update requests ─────────────────────────────
            const string requestsSql = @"
                SELECT TOP 5 RequestID, RequestDescription, Status, RequestedDate, CompletedDate
                FROM   MenuUpdateRequests
                WHERE  RestaurantID = @ID
                ORDER  BY RequestedDate DESC";

            ViewBag.UpdateRequests = await _db.ExecuteQueryAsync(requestsSql,
                new SqlParameter("@ID", restaurantId));

            // ── TempData messages ────────────────────────────────────────────
            ViewBag.SuccessMessage = TempData["OwnerSuccess"];
            ViewBag.ErrorMessage = TempData["OwnerError"];

            ViewBag.FullName = HttpContext.Session.GetString(SessionKeys.FullName);

            return View();
        }

        // =====================================================================
        // POST /Restaurant/ToggleDish
        // Toggles IsActive for a dish — only for dishes owned by this restaurant.
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleDish(int dishId)
        {
            var restaurantIdStr = HttpContext.Session.GetString(SessionKeys.RestaurantID);
            if (!int.TryParse(restaurantIdStr, out int restaurantId))
                return RedirectToAction("Index", "Login");

            // Verify this dish belongs to the session restaurant — prevents
            // a restaurant owner from toggling another owner's dishes by
            // crafting a request with a foreign dishId.
            var ownerCheck = await _db.ExecuteScalarAsync(
                "SELECT COUNT(1) FROM Dishes WHERE DishID = @DishID AND RestaurantID = @RestaurantID",
                new SqlParameter("@DishID", dishId),
                new SqlParameter("@RestaurantID", restaurantId));

            if (Convert.ToInt32(ownerCheck) == 0)
            {
                TempData["OwnerError"] = "Dish not found or does not belong to your restaurant.";
                return RedirectToAction(nameof(Dashboard));
            }

            // Read current state then flip it
            var current = await _db.ExecuteScalarAsync(
                "SELECT IsActive FROM Dishes WHERE DishID = @ID",
                new SqlParameter("@ID", dishId));

            var currentlyActive = Convert.ToBoolean(current);

            await _db.ExecuteNonQueryAsync(
                "UPDATE Dishes SET IsActive = @NewState, UpdatedDate = SYSUTCDATETIME() WHERE DishID = @ID",
                new SqlParameter("@NewState", !currentlyActive),
                new SqlParameter("@ID", dishId));

            TempData["OwnerSuccess"] = currentlyActive
                ? "Dish deactivated — it will no longer appear on your menu."
                : "Dish activated — it is now visible to customers.";

            return RedirectToAction(nameof(Dashboard));
        }

        // =====================================================================
        // POST /Restaurant/RequestMenuUpdate
        // Inserts a menu update request. Available on Pro plan only.
        // Enterprise owners manage their own menu directly.
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestMenuUpdate(string requestDescription)
        {
            var restaurantIdStr = HttpContext.Session.GetString(SessionKeys.RestaurantID);
            if (!int.TryParse(restaurantIdStr, out int restaurantId))
                return RedirectToAction("Index", "Login");

            // Verify plan — only Pro allowed to submit requests via this form
            var planType = (await _db.ExecuteScalarAsync(
                "SELECT PlanType FROM Restaurants WHERE RestaurantID = @ID",
                new SqlParameter("@ID", restaurantId)))?.ToString();

            if (planType != "Pro")
            {
                TempData["OwnerError"] = "Menu update requests are available on the Pro plan only.";
                return RedirectToAction(nameof(Dashboard));
            }

            if (string.IsNullOrWhiteSpace(requestDescription) || requestDescription.Trim().Length < 10)
            {
                TempData["OwnerError"] = "Please describe your requested changes in at least 10 characters.";
                return RedirectToAction(nameof(Dashboard));
            }

            await _db.ExecuteNonQueryAsync(
                @"INSERT INTO MenuUpdateRequests (RestaurantID, RequestDescription, Status, RequestedDate)
                  VALUES (@RestaurantID, @Description, 'Pending', SYSUTCDATETIME())",
                new SqlParameter("@RestaurantID", restaurantId),
                new SqlParameter("@Description", requestDescription.Trim()));

            TempData["OwnerSuccess"] = "Your menu update request has been submitted. We will process it within 2 business days.";
            return RedirectToAction(nameof(Dashboard));
        }

        // =====================================================================
        // GET /Restaurant/DownloadQR
        // Fetches the QR image from Azure and forces the browser to download it.
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> DownloadQR(int tableId)
        {
            var restaurantIdStr = HttpContext.Session.GetString(SessionKeys.RestaurantID);
            if (!int.TryParse(restaurantIdStr, out int restaurantId))
                return RedirectToAction("Index", "Login");

            // Look up the QR image URL AND verify the table belongs to this restaurant
            // (security — stops one owner downloading another owner's QR codes)
            var dt = await _db.ExecuteQueryAsync(
                @"SELECT QRCodeImageURL, TableNumber
                  FROM Tables
                  WHERE TableID = @TableID AND RestaurantID = @RestaurantID",
                new SqlParameter("@TableID", tableId),
                new SqlParameter("@RestaurantID", restaurantId));

            if (dt.Rows.Count == 0) return NotFound();

            var imageUrl = dt.Rows[0]["QRCodeImageURL"]?.ToString();
            var tableNumber = dt.Rows[0]["TableNumber"]?.ToString();

            if (string.IsNullOrEmpty(imageUrl)) return NotFound();

            // Fetch the PNG from Azure, then return it with a download header
            using var httpClient = new HttpClient();
            var bytes = await httpClient.GetByteArrayAsync(imageUrl);

            return File(bytes, "image/png", $"Table-{tableNumber}.png");
        }
    }

}