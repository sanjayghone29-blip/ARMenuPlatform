using ARMenuPlatform.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;

namespace ARMenuPlatform.Controllers
{
    // =========================================================================
    // MenuApiController
    // Public-facing JSON API consumed by the AR menu browser experience.
    // No session/auth required — these endpoints are hit by customer phones.
    //
    // Routes:
    //   GET  /api/menu/{qrCodeString}   → full menu JSON
    //   POST /api/scan/log              → log a scan event
    // =========================================================================
    [ApiController]
    [Route("api")]
    public class MenuApiController : ControllerBase
    {
        private readonly DbHelper _db;

        public MenuApiController(DbHelper db)
        {
            _db = db;
        }

        // =====================================================================
        // GET /api/menu/{qrCodeString}
        //
        // Looks up the restaurant via the QRCodeString on the Tables table.
        // Returns full menu grouped by category, active dishes only.
        // Returns allowed:false if restaurant is Paused or Cancelled.
        // =====================================================================
        [HttpGet("menu/{qrCodeString}")]
        public async Task<IActionResult> GetMenu(string qrCodeString)
        {
            // ── Parse and validate the GUID ──────────────────────────────────
            if (!Guid.TryParse(qrCodeString, out var qrGuid))
                return BadRequest(new { allowed = false, error = "Invalid QR code." });

            // ── Look up the table → restaurant ───────────────────────────────
            const string lookupSql = @"
                SELECT
                    t.TableID,
                    t.TableNumber,
                    t.QRCodeImageURL,
                    r.RestaurantID,
                    r.RestaurantName,
                    r.PlanType,
                    r.Status,
                    r.LogoURL
                FROM  Tables      t
                JOIN  Restaurants r ON r.RestaurantID = t.RestaurantID
                WHERE t.QRCodeString = @QRCode
                  AND t.IsActive     = 1";

            var lookupDt = await _db.ExecuteQueryAsync(lookupSql,
                new SqlParameter("@QRCode", qrGuid));

            if (lookupDt.Rows.Count == 0)
                return NotFound(new { allowed = false, error = "QR code not recognised." });

            var tableRow = lookupDt.Rows[0];
            var restaurantId = Convert.ToInt32(tableRow["RestaurantID"]);
            var restaurantName = tableRow["RestaurantName"].ToString()!;
            var planType = tableRow["PlanType"].ToString()!;
            var status = tableRow["Status"].ToString()!;
            var logoUrl = tableRow["LogoURL"] == DBNull.Value ? "" : tableRow["LogoURL"].ToString();
            var qrImageUrl = tableRow["QRCodeImageURL"] == DBNull.Value ? "" : tableRow["QRCodeImageURL"].ToString();

            // ── Status gate: Paused / Cancelled → menu unavailable ───────────
            if (status == "Paused" || status == "Cancelled")
                return Ok(new { allowed = false, restaurantName, status });

            // ── Load active dishes ───────────────────────────────────────────
            const string dishesSql = @"
                SELECT
                    DishID, DishName, Description, Category,
                    Price, IsVegetarian, SpiceLevel,
                    ContentType, ContentURL, ThumbnailURL,
                    DisplayOrder
                FROM  Dishes
                WHERE RestaurantID = @RestaurantID
                  AND IsActive     = 1
                ORDER BY
                    CASE Category
                        WHEN 'Starter'     THEN 1
                        WHEN 'Main Course' THEN 2
                        WHEN 'Bread'       THEN 3
                        WHEN 'Drink'       THEN 4
                        WHEN 'Dessert'     THEN 5
                        WHEN 'Special'     THEN 6
                        ELSE                    7
                    END,
                    DisplayOrder ASC,
                    DishName     ASC";

            var dishesDt = await _db.ExecuteQueryAsync(dishesSql,
                new SqlParameter("@RestaurantID", restaurantId));

            // ── Group dishes by category ─────────────────────────────────────
            var grouped = new Dictionary<string, List<object>>();

            foreach (DataRow row in dishesDt.Rows)
            {
                var category = row["Category"].ToString()!;

                if (!grouped.ContainsKey(category))
                    grouped[category] = new List<object>();

                grouped[category].Add(new
                {
                    dishId = Convert.ToInt32(row["DishID"]),
                    dishName = row["DishName"].ToString(),
                    description = row["Description"] == DBNull.Value ? "" : row["Description"].ToString(),
                    price = Convert.ToDecimal(row["Price"]),
                    isVegetarian = Convert.ToBoolean(row["IsVegetarian"]),
                    spiceLevel = row["SpiceLevel"].ToString(),
                    contentType = row["ContentType"].ToString(),
                    contentURL = row["ContentURL"] == DBNull.Value ? "" : row["ContentURL"].ToString(),
                    thumbnailURL = row["ThumbnailURL"] == DBNull.Value ? "" : row["ThumbnailURL"].ToString(),
                });
            }

            var categories = grouped.Select(kvp => new
            {
                name = kvp.Key,
                dishes = kvp.Value
            }).ToList();

            return Ok(new
            {
                allowed = true,
                restaurantName,
                planType,
                logoUrl,
                qrImageUrl,     // QR code PNG URL — used by VideoAR to derive .mind file path
                tableNumber = tableRow["TableNumber"].ToString(),
                categories
            });
        }

        // =====================================================================
        // POST /api/scan/log
        //
        // Logs a customer scan event to the Scans table.
        // Called on: page load (MenuOpen) and when a dish AR is launched (DishView).
        //
        // Request body (JSON):
        // {
        //   "qrCodeString": "guid-here",
        //   "dishId": null,               // null for MenuOpen
        //   "scanType": "MenuOpen"        // "MenuOpen" | "DishView"
        // }
        // =====================================================================
        [HttpPost("scan/log")]
        public async Task<IActionResult> LogScan([FromBody] ScanLogRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.QrCodeString))
                return BadRequest(new { success = false });

            if (!Guid.TryParse(request.QrCodeString, out var qrGuid))
                return BadRequest(new { success = false, error = "Invalid QR code format." });

            // Validate scan type
            if (request.ScanType != "MenuOpen" && request.ScanType != "DishView")
                return BadRequest(new { success = false, error = "Invalid scan type." });

            // DishView must have a dishId
            if (request.ScanType == "DishView" && (request.DishId == null || request.DishId <= 0))
                return BadRequest(new { success = false, error = "DishId required for DishView." });

            // ── Resolve TableID and RestaurantID from QR GUID ─────────────────
            const string lookupSql = @"
                SELECT t.TableID, t.RestaurantID, r.Status
                FROM   Tables      t
                JOIN   Restaurants r ON r.RestaurantID = t.RestaurantID
                WHERE  t.QRCodeString = @QRCode AND t.IsActive = 1";

            var dt = await _db.ExecuteQueryAsync(lookupSql,
                new SqlParameter("@QRCode", qrGuid));

            if (dt.Rows.Count == 0)
                return Ok(new { success = false, allowed = false });

            var row = dt.Rows[0];
            var tableId = Convert.ToInt32(row["TableID"]);
            var restaurantId = Convert.ToInt32(row["RestaurantID"]);
            var status = row["Status"].ToString()!;

            // Detect device type from User-Agent header
            var userAgent = Request.Headers["User-Agent"].ToString();
            var deviceType = userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
                           : userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                          || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iOS"
                           : "Unknown";

            // ── Insert scan record ───────────────────────────────────────────
            await _db.ExecuteNonQueryAsync(
                @"INSERT INTO Scans (RestaurantID, TableID, DishID, ScanTimestamp, DeviceType, ScanType)
                  VALUES (@RestaurantID, @TableID, @DishID, SYSUTCDATETIME(), @DeviceType, @ScanType)",
                new SqlParameter("@RestaurantID", restaurantId),
                new SqlParameter("@TableID", tableId),
                new SqlParameter("@DishID", request.DishId.HasValue ? (object)request.DishId.Value : DBNull.Value),
                new SqlParameter("@DeviceType", deviceType),
                new SqlParameter("@ScanType", request.ScanType));

            return Ok(new
            {
                success = true,
                allowed = status != "Paused" && status != "Cancelled"
            });
        }
    }

    // ── Request model for scan log body ──────────────────────────────────────
    public class ScanLogRequest
    {
        public string QrCodeString { get; set; } = "";
        public int? DishId { get; set; }
        public string ScanType { get; set; } = "MenuOpen";
    }
}