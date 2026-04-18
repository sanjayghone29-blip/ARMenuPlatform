using ARMenuPlatform.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace ARMenuPlatform.Controllers
{
    [TypeFilter(typeof(SuperAdminOnlyFilter))]
    public class AdminController : Controller
    {
        private readonly DbHelper _db;

        public AdminController(DbHelper db)
        {
            _db = db;
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /Admin/Dashboard
        // ─────────────────────────────────────────────────────────────────────
        public async Task<IActionResult> Dashboard()
        {
            // ── 1. Stat card counts ──────────────────────────────────────────
            // Single query — four counts in one round-trip using conditional
            // aggregation. Much faster than four separate COUNT queries.
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

            // ── 2. Scan counts ───────────────────────────────────────────────
            // Both today and this-month in one query.
            const string scansSql = @"
                SELECT
                    SUM(CASE
                            WHEN CAST(ScanTimestamp AS DATE) = CAST(SYSUTCDATETIME() AS DATE)
                            THEN 1 ELSE 0
                        END) AS ScansToday,
                    SUM(CASE
                            WHEN YEAR(ScanTimestamp)  = YEAR(SYSUTCDATETIME())
                             AND MONTH(ScanTimestamp) = MONTH(SYSUTCDATETIME())
                            THEN 1 ELSE 0
                        END) AS ScansThisMonth
                FROM Scans";

            var scansRow = (await _db.ExecuteQueryAsync(scansSql)).Rows[0];

            ViewBag.ScansToday = scansRow["ScansToday"] == DBNull.Value ? 0 : Convert.ToInt32(scansRow["ScansToday"]);
            ViewBag.ScansThisMonth = scansRow["ScansThisMonth"] == DBNull.Value ? 0 : Convert.ToInt32(scansRow["ScansThisMonth"]);

            // ── 3. Restaurants list with per-restaurant scan count ───────────
            // LEFT JOIN ensures restaurants with zero scans still appear.
            // COALESCE turns NULL (no scans) into 0.
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
                    SELECT
                        RestaurantID,
                        COUNT(*) AS ScansThisMonth
                    FROM Scans
                    WHERE YEAR(ScanTimestamp)  = YEAR(SYSUTCDATETIME())
                      AND MONTH(ScanTimestamp) = MONTH(SYSUTCDATETIME())
                    GROUP BY RestaurantID
                ) s ON s.RestaurantID = r.RestaurantID
                ORDER BY r.CreatedDate DESC";

            var restaurants = await _db.ExecuteQueryAsync(restaurantsSql);

            ViewBag.Restaurants = restaurants;
            ViewBag.FullName = HttpContext.Session.GetString(SessionKeys.FullName);

            return View();
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /Admin/PauseRestaurant
        // Called by the Pause button in the dashboard table.
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PauseRestaurant(int restaurantId)
        {
            await _db.ExecuteNonQueryAsync(
                "UPDATE Restaurants SET Status = 'Paused' WHERE RestaurantID = @ID",
                new SqlParameter("@ID", restaurantId));

            return RedirectToAction(nameof(Dashboard));
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /Admin/ActivateRestaurant
        // Called by the Activate button in the dashboard table.
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActivateRestaurant(int restaurantId)
        {
            await _db.ExecuteNonQueryAsync(
                "UPDATE Restaurants SET Status = 'Active' WHERE RestaurantID = @ID",
                new SqlParameter("@ID", restaurantId));

            return RedirectToAction(nameof(Dashboard));
        }
    }
}